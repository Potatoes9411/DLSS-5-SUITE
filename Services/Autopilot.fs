namespace DLSS_5_MANAGER.Services

open System
open System.IO
open System.Threading
open DLSS_5_MANAGER.Models

/// Autopilot: install a route, start the game, look at what actually loaded
/// in it, and when ours is not in there, close-and-retry with the next route
/// that fits the game. At most three routes per run.
///
/// What decides "loaded" is the game's own DLL list - the files this install
/// put in the folder have to be among the modules the running game holds.
/// The log reading of "did it work?" comes afterwards, once the person has
/// played; autopilot only gets the right files into the game.
module Autopilot =

    type Route =
        { Mode: ModInstaller.InstallMode
          Arch: ModInstaller.InstallArch
          OptiApi: ModInstaller.OptiScalerApi
          Neural: bool
          Feeder: bool
          Label: string }

    [<Literal>]
    let private MaxAttempts = 3

    let private startSeconds = 300.0
    let private closeSeconds = 600.0

    /// The routes worth trying for this game, best first, from the graphics
    /// API and bitness read off its executable.
    let plan (game: GameItem) : Route list =
        let exe = game.TargetExecutablePath
        let api = try GameAnalyzer.detectGraphicsApi exe with _ -> ""
        let arch =
            if (try GameAnalyzer.detectArchitecture exe with _ -> "64") = "32" then ModInstaller.Bit32 else ModInstaller.Bit64
        let r mode optiApi neural feeder label =
            { Mode = mode; Arch = arch; OptiApi = optiApi; Neural = neural; Feeder = feeder; Label = label }
        let opti = ModInstaller.OptiScalerMode
        match api with
        | "vulkan"
        | "opengl" -> [ r opti ModInstaller.OptiVulkan false false "OptiScaler (Vulkan)" ]
        | "dx12" ->
            [ r opti ModInstaller.OptiDx12 false false "OptiScaler"
              r ModInstaller.Dx12Auto ModInstaller.OptiDx12 false true "ReShade + RenoDX with the Feeder"
              r opti ModInstaller.OptiNeural false false "OptiScaler Neural (DLSS-NR)" ]
        | "dx11"
        | "dx10" ->
            [ yield r ModInstaller.Dx11 ModInstaller.OptiDx12 false true "ReShade + Feeder (DX11)"
              if arch = ModInstaller.Bit64 then yield r opti ModInstaller.OptiNeural false false "OptiScaler Neural (DX11 bridge)"
              yield r ModInstaller.Dx11 ModInstaller.OptiDx12 false false "ReShade + RenoDX (DX11)" ]
        | "dx9" -> [ { (r ModInstaller.Dx9 ModInstaller.OptiDx12 false true "ReShade + Feeder (DX9)") with Arch = ModInstaller.Bit32 } ]
        | _ ->
            [ r ModInstaller.Dx11 ModInstaller.OptiDx12 false true "ReShade + Feeder"
              r opti ModInstaller.OptiDx12 false false "OptiScaler" ]
        |> List.truncate MaxAttempts

    /// Why autopilot will not run on this game, or None.
    let refusal (game: GameItem) : string option =
        if String.IsNullOrWhiteSpace game.TargetExecutablePath || not (File.Exists game.TargetExecutablePath) then
            Some "This game has no executable SUITE can start."
        elif GameAnalyzer.isLikelyLauncherExe game.TargetExecutablePath then
            Some "The executable here is a launcher, not the game - pick the game's own exe first."
        else
            match AntiCheat.detect game.Title game.InstallDirectory game.TargetExecutablePath with
            | Some ac -> Some(sprintf "%s is installed with this game. Autopilot never starts a game with an anti-cheat." ac)
            | None ->
                if GameLauncher.isRunning game then Some "The game is already running - close it first, then start autopilot."
                else None

    type Outcome =
        { Worked: bool
          Summary: string
          /// The route the folder holds now, if any.
          Installed: string option }

    let private waitFor (cond: unit -> bool) (seconds: float) (token: CancellationToken) =
        let deadline = DateTime.UtcNow.AddSeconds(seconds)
        let mutable hit = cond ()
        while not hit && not token.IsCancellationRequested && DateTime.UtcNow < deadline do
            token.WaitHandle.WaitOne(1000) |> ignore
            hit <- cond ()
        hit

    /// Our files the running game holds, and ones it loaded from elsewhere.
    let private inspect (game: GameItem) =
        let wanted =
            ModInstaller.installedTargets game
            |> List.filter (fun f ->
                let e = Path.GetExtension(f).ToLowerInvariant()
                (e = ".dll" || e = ".addon64" || e = ".addon32")
                && not (f.ToLowerInvariant().Contains("\\host64\\")))
        match Diagnosis.loadedModules game with
        | None -> None
        | Some(_, mods) ->
            let loaded = mods |> List.map (fun m -> m.ToLowerInvariant())
            let ours = wanted |> List.filter (fun w -> loaded |> List.contains (w.ToLowerInvariant()))
            let elsewhere =
                wanted
                |> List.filter (fun w -> not (List.contains w ours))
                |> List.filter (fun w ->
                    let n = Path.GetFileName(w).ToLowerInvariant()
                    loaded |> List.exists (fun m ->
                        Path.GetFileName(m) = n && not (m.Contains("\\windows\\"))))
            Some(ours |> List.map Path.GetFileName, elsewhere |> List.map Path.GetFileName)

    let private names (xs: string list) =
        match xs with
        | [] -> ""
        | _ when xs.Length > 3 -> String.Join(", ", xs |> List.truncate 3) + sprintf " and %d more" (xs.Length - 3)
        | _ -> String.Join(", ", xs)

    /// Runs the whole loop on the caller's thread (call it from a worker).
    /// progress gets one line per step for the UI.
    let run
        (game: GameItem)
        (routes: Route list)
        (overlay: ModInstaller.OverlayOptions)
        (progress: string -> unit)
        (token: CancellationToken)
        : Outcome =
        let mutable result = None
        let mutable installed: string option = None
        let tried = ResizeArray<string>()
        let mutable elsewhereSeen = []
        let routes = routes |> List.truncate MaxAttempts
        let exeName = Path.GetFileName(game.TargetExecutablePath)
        for (i, route) in List.indexed routes do
            if result.IsNone then
                if token.IsCancellationRequested then
                    result <- Some { Worked = false; Summary = "Stopped: you pressed stop."; Installed = installed }
                else
                    tried.Add(route.Label)
                    progress (sprintf "Route %d of %d: installing %s..." (i + 1) routes.Length route.Label)
                    let swap =
                        try
                            if ModInstaller.isInstalled game then
                                let removal = ModInstaller.uninstall game game.TargetExecutablePath None (fun _ _ -> ())
                                if not removal.Success then Error("Could not remove the previous install: " + removal.Message)
                                else Ok()
                            else Ok()
                        with ex -> Error ex.Message
                    let outcome =
                        match swap with
                        | Error why -> Error why
                        | Ok() ->
                            try
                                let o =
                                    ModInstaller.install game game.TargetExecutablePath None route.Mode route.Arch route.OptiApi route.Neural route.Feeder overlay (fun s _ -> progress s)
                                if o.Success then Ok() else Error o.Message
                            with ex -> Error ex.Message
                    match outcome with
                    | Error why ->
                        installed <- None
                        AppLog.warn (sprintf "Autopilot: %s install failed in %s: %s" route.Label game.Title why)
                        if i = routes.Length - 1 then
                            result <- Some { Worked = false; Summary = sprintf "Stopped: the %s install did not finish - %s" route.Label why; Installed = None }
                    | Ok() ->
                        installed <- Some route.Label
                        progress (sprintf "Starting %s..." exeName)
                        let launch =
                            match GameLauncher.routeOf (GameLauncher.prefsFor game) with
                            | Some r -> GameLauncher.start game r (GameLauncher.prefsFor game).Arguments
                            | None ->
                                let r = if GameLauncher.canLaunchViaSteam game then GameLauncher.ViaSteam else GameLauncher.ViaExe
                                GameLauncher.start game r ""
                        match launch with
                        | Error why -> progress (sprintf "Could not start it (%s) - start the game now, autopilot is watching for it." why)
                        | Ok() -> ()
                        let seen = waitFor (fun () -> GameLauncher.isRunning game) startSeconds token
                        if token.IsCancellationRequested then
                            result <- Some { Worked = false; Summary = "Stopped: you pressed stop."; Installed = installed }
                        elif not seen then
                            result <- Some { Worked = false; Summary = "Stopped: the game was never seen running."; Installed = installed }
                        else
                            progress "The game is up - letting it settle..."
                            token.WaitHandle.WaitOne(8000) |> ignore
                            match inspect game with
                            | None ->
                                result <-
                                    Some
                                        { Worked = false
                                          Summary = sprintf "%s is protected, so the DLLs it loaded cannot be read. Play, then press 'Did it work?'." exeName
                                          Installed = installed }
                            | Some(ours, elsewhere) when not ours.IsEmpty && elsewhere.IsEmpty ->
                                AppLog.info (sprintf "Autopilot: %s loaded in %s (%s)" route.Label game.Title (names ours))
                                result <-
                                    Some
                                        { Worked = true
                                          Summary = sprintf "The %s install is loaded in the game (%s). Play for a few minutes, then press 'Did it work?'." route.Label (names ours)
                                          Installed = installed }
                            | Some(_, elsewhere) ->
                                elsewhereSeen <- elsewhere
                                let what =
                                    if elsewhere.IsEmpty then "the game ran and loaded none of this install's files"
                                    else sprintf "the game loaded %s from somewhere else, not ours" (names elsewhere)
                                AppLog.info (sprintf "Autopilot: %s did not load in %s: %s" route.Label game.Title what)
                                if i < routes.Length - 1 then
                                    progress (sprintf "%s - close the game and autopilot carries on with the next route." (string (Char.ToUpper what.[0]) + what.Substring(1)))
                                    let closed = waitFor (fun () -> not (GameLauncher.isRunning game)) closeSeconds token
                                    if not closed then
                                        result <-
                                            Some
                                                { Worked = false
                                                  Summary =
                                                    if token.IsCancellationRequested then "Stopped: you pressed stop."
                                                    else "Stopped: the game is still running - the files it has open cannot be replaced while it is up."
                                                  Installed = installed }
        match result with
        | Some r -> r
        | None ->
            let why =
                if elsewhereSeen.IsEmpty then "nothing of ours ended up running in the game"
                else sprintf "the game loaded %s from another folder, so the copy beside it is never reached" (names elsewhereSeen)
            let where =
                match installed with
                | Some r -> sprintf " The %s route is what is installed now - Uninstall takes it back out." r
                | None -> " Nothing of SUITE's is in the folder."
            { Worked = false
              Summary = sprintf "Tried %s - %s.%s" (String.Join(", ", tried)) why where
              Installed = installed }
