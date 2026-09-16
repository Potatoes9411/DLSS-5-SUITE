namespace DLSS_5_MANAGER.Services

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading.Tasks
open DLSS_5_MANAGER.Models

/// Starting and stopping a game from its card.
///
/// A launch through Steam returns before the game exists - steam.exe is what
/// was started - so a game is always followed by its own executable's name,
/// not by the process that was launched. That also covers games whose launcher
/// stub starts the real executable and exits.
module GameLauncher =

    type LaunchRoute =
        | ViaSteam
        | ViaExe

    /// What the user chose for one game. Route is "" until they have been asked.
    [<CLIMutable>]
    type LaunchPrefs = { Route: string; Arguments: string }

    let private emptyPrefs = { Route = ""; Arguments = "" }

    // =====================================================================
    // REMEMBERED CHOICES
    // =====================================================================
    let private storePath () =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DLSS5Suite", "launch.json")

    let private gate = obj ()

    let private readAll () : Dictionary<string, LaunchPrefs> =
        try
            if File.Exists(storePath ()) then
                match JsonSerializer.Deserialize<Dictionary<string, LaunchPrefs>>(File.ReadAllText(storePath ())) with
                | null -> Dictionary()
                | d -> d
            else
                Dictionary()
        with _ ->
            Dictionary()

    let prefsFor (game: GameItem) =
        lock gate (fun () ->
            match readAll().TryGetValue(game.AppId) with
            | true, p when not (isNull (box p)) ->
                { Route = (if isNull p.Route then "" else p.Route)
                  Arguments = (if isNull p.Arguments then "" else p.Arguments) }
            | _ -> emptyPrefs)

    /// Written whole to a temporary file and renamed, so a crash mid-write
    /// cannot leave every game's choice unreadable.
    let savePrefs (game: GameItem) (prefs: LaunchPrefs) =
        lock gate (fun () ->
            try
                let all = readAll ()
                all.[game.AppId] <- prefs
                let path = storePath ()
                Directory.CreateDirectory(Path.GetDirectoryName(path)) |> ignore
                let tmp = path + ".tmp"
                File.WriteAllText(tmp, JsonSerializer.Serialize(all, JsonSerializerOptions(WriteIndented = true)))
                File.Move(tmp, path, true)
                true
            with _ ->
                false)

    let routeOf (prefs: LaunchPrefs) =
        match prefs.Route with
        | "steam" -> Some ViaSteam
        | "exe" -> Some ViaExe
        | _ -> None

    let routeKey (route: LaunchRoute) =
        match route with
        | ViaSteam -> "steam"
        | ViaExe -> "exe"

    // =====================================================================
    // WHICH GAMES CAN BE STARTED HOW
    // =====================================================================
    /// The Steam app id the library stored the game under, when it came from Steam.
    let steamAppId (game: GameItem) =
        if not (isNull game.AppId) && game.AppId.StartsWith("steam_", StringComparison.OrdinalIgnoreCase) then
            let id = game.AppId.Substring(6)
            if id <> "" && id |> Seq.forall Char.IsDigit then Some id else None
        else
            None

    let canLaunchViaSteam (game: GameItem) = (steamAppId game).IsSome

    let hasExecutable (game: GameItem) =
        not (String.IsNullOrWhiteSpace(game.TargetExecutablePath)) && File.Exists(game.TargetExecutablePath)

    /// Only a Steam title has two ways in; anything else is its executable.
    let needsChoice (game: GameItem) =
        canLaunchViaSteam game && hasExecutable game && (routeOf (prefsFor game)).IsNone

    // =====================================================================
    // RUNNING
    // =====================================================================
    let private processName (game: GameItem) =
        if String.IsNullOrWhiteSpace(game.TargetExecutablePath) then ""
        else Path.GetFileNameWithoutExtension(game.TargetExecutablePath)

    /// Every live process running this game's executable. Matched by name and
    /// then, where Windows lets us read it, by full path - two games can ship
    /// an executable with the same name.
    let running (game: GameItem) : Process list =
        let name = processName game
        if name = "" then []
        else
            let wanted = try Path.GetFullPath(game.TargetExecutablePath) with _ -> game.TargetExecutablePath
            Process.GetProcessesByName(name)
            |> Array.filter (fun p ->
                let path = try p.MainModule.FileName with _ -> null
                isNull path || String.Equals(path, wanted, StringComparison.OrdinalIgnoreCase))
            |> List.ofArray

    let isRunning (game: GameItem) =
        let found = running game
        let alive = not found.IsEmpty
        for p in found do p.Dispose()
        alive

    /// Whether the game has put a window up yet, which is when "starting" ends.
    let hasWindow (game: GameItem) =
        let found = running game
        let shown = found |> List.exists (fun p -> try p.MainWindowHandle <> 0n with _ -> false)
        for p in found do p.Dispose()
        shown

    /// Starts the game. Error carries a sentence for the card, never an exception.
    let start (game: GameItem) (route: LaunchRoute) (arguments: string) : Result<unit, string> =
        try
            match route with
            | ViaSteam ->
                match steamAppId game with
                | None -> Error "This game did not come from Steam."
                | Some id ->
                    // Steam's own URL takes no arguments; they are passed with
                    // -applaunch through steam.exe when the user has set some.
                    if String.IsNullOrWhiteSpace(arguments) then
                        Process.Start(ProcessStartInfo("steam://rungameid/" + id, UseShellExecute = true)) |> ignore
                    else
                        Process.Start(ProcessStartInfo("steam://run/" + id + "//" + Uri.EscapeDataString(arguments.Trim()) + "/", UseShellExecute = true)) |> ignore
                    Ok()
            | ViaExe ->
                if not (hasExecutable game) then
                    Error "The game's executable was not found. Set it again in Manage."
                else
                    let psi =
                        ProcessStartInfo(
                            game.TargetExecutablePath,
                            Arguments = (if isNull arguments then "" else arguments),
                            WorkingDirectory = Path.GetDirectoryName(game.TargetExecutablePath),
                            UseShellExecute = true)
                    use _ = Process.Start(psi)
                    Ok()
        with ex ->
            Error("The game could not be started: " + ex.Message)

    /// Asks the game to close the way its own X button would, and only ends it
    /// by force if it is still there after the grace period - a game that is
    /// saving on exit should be allowed to finish.
    let stop (game: GameItem) : Task<unit> =
        task {
            let found = running game
            for p in found do
                try p.CloseMainWindow() |> ignore with _ -> ()

            let deadline = DateTime.UtcNow.AddSeconds(8.0)
            while DateTime.UtcNow < deadline && isRunning game do
                do! Task.Delay(250)

            for p in running game do
                try p.Kill(entireProcessTree = true) with _ -> ()
                p.Dispose()

            for p in found do p.Dispose()
        }

    /// The folder the game lives in, opened in Explorer.
    let openFolder (game: GameItem) =
        try
            let dir =
                if hasExecutable game then Path.GetDirectoryName(game.TargetExecutablePath)
                else game.InstallDirectory
            if not (String.IsNullOrWhiteSpace(dir)) && Directory.Exists(dir) then
                Process.Start(ProcessStartInfo("explorer.exe", "\"" + dir + "\"", UseShellExecute = true)) |> ignore
                true
            else
                false
        with _ ->
            false
