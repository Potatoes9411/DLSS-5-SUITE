namespace DLSS_5_MANAGER.ViewModels

open System
open System.IO
open System.Text.RegularExpressions
open Avalonia.Media.Imaging
open Avalonia.Threading
open DLSS_5_MANAGER.Models
open DLSS_5_MANAGER.Services

type GameCardViewModel(game: GameItem) =
    inherit ViewModelBase()

    /// The record is swapped whenever the user corrects the detected executable.
    let mutable currentGame = game

    let mutable bannerBitmap : Bitmap option = None
    let mutable isBitmapLoaded = false
    let mutable isVerticalCover = false
    let mutable isDragging = false
    let mutable isDragTarget = false

    // ---- launching ----
    // 0 idle, 1 starting, 2 running. Watched once a second while not idle;
    // nothing is polled for a card nobody has pressed play on.
    let mutable launchState = 0
    let mutable launchMessage = ""
    let mutable isChoosingLaunch = false
    let mutable startedAt = DateTime.MinValue
    let mutable seenProcess = false
    let launchWatch = DispatcherTimer(Interval = TimeSpan.FromSeconds(1.0))
    let mutable watchAttached = false

    let cleanDisplayTitle (raw: string) =
        let cleaned = Regex.Replace(raw, @"\[.*?\]|\(.*?\)", "").Trim()
        if String.IsNullOrWhiteSpace(cleaned) then raw else cleaned

    let loadBitmap () =
        if not isBitmapLoaded then
            isBitmapLoaded <- true
            try
                if not (String.IsNullOrWhiteSpace(currentGame.LocalBannerPath)) && File.Exists(currentGame.LocalBannerPath) then
                    use stream = File.OpenRead(currentGame.LocalBannerPath)
                    // Decoding at card width keeps an extracted icon as sharp as
                    // its source allows instead of scaling a thumbnail up later.
                    let bmp = Bitmap.DecodeToWidth(stream, 432)
                    isVerticalCover <- (bmp.Size.Height > bmp.Size.Width && bmp.Size.Width >= 120.0)
                    bannerBitmap <- Some bmp
            with _ ->
                bannerBitmap <- None

    /// Reads the install manifest once and turns it into the short badge the
    /// card wears. Empty when this app did not install anything on the game.
    let readBadge () =
        try
            let (route, arch) = ModInstaller.installedRouteAndArch currentGame

            match route with
            | "optiscaler" ->
                if ModInstaller.installedOptiApi currentGame = "vulkan" then "OPTI-VK" else "OPTI"
            | "dx12" -> "DX12"
            | "dx11" -> if arch = "32" then "DX11-32" else "DX11"
            | "dx9" -> if arch = "32" then "DX9-32" else "DX9-64"
            | "amd" -> "AMD"
            | "emulator" -> "EMU"
            | _ -> ""
        with _ ->
            ""

    let mutable badge = readBadge ()

    member this.Game = currentGame

    /// Replaces the auto-detected executable with a user supplied one.
    member this.SetExecutable(path: string) =
        let size =
            try FileInfo(path).Length
            with _ -> 0L

        currentGame <-
            { currentGame with
                TargetExecutablePath = path
                TargetExecutableSize = size }

        this.RaisePropertyChanged("ExecutablePath")

    /// Points the card at artwork the user picked themselves and redraws it.
    member this.SetBanner(path: string) =
        currentGame <- { currentGame with LocalBannerPath = path }

        // Force the next read to decode the new file.
        bannerBitmap <- None
        isBitmapLoaded <- false
        isVerticalCover <- false

        this.RaisePropertyChanged("BannerImage")
        this.RaisePropertyChanged("HasBannerImage")
        this.RaisePropertyChanged("IsVerticalCover")
        this.RaisePropertyChanged("IsAppIcon")


    /// Refreshed after an install or a removal, so the grid stays truthful
    /// without re-reading every manifest on every repaint.
    member this.RefreshModBadge() =
        badge <- readBadge ()
        this.RaisePropertyChanged("ModBadgeText")
        this.RaisePropertyChanged("HasModBadge")
        this.RaisePropertyChanged("DetectedApiText")
        this.RaisePropertyChanged("HasDetectedApi")

    member this.ModBadgeText = badge
    member this.HasModBadge = badge <> ""

    /// The graphics API, read from the analysis the library scan already
    /// cached. Working it out here instead would mean reading the executable's
    /// import tables and scanning its strings on every repaint.
    member this.DetectedApiText =
        match AnalysisStore.tryGet currentGame with
        | Some a when not (isNull (box a.GraphicsApi)) ->
            match a.GraphicsApi with
            | "dx12" -> "DX12"
            | "dx11" -> "DX11"
            | "dx10" -> "DX10"
            | "dx9" -> "DX9"
            | "vulkan" -> "VULKAN"
            | "opengl" -> "OPENGL"
            | _ -> ""
        | _ -> ""

    member this.HasDetectedApi = this.DetectedApiText <> ""

    /// The chip is abbreviated to fit the corner, so the tooltip spells the
    /// API out. "DX12" is obvious to someone who already knows what it means,
    /// which is not who the label is for.
    member this.ApiTooltip =
        let full =
            match AnalysisStore.tryGet currentGame with
            | Some a when not (isNull (box a.GraphicsApi)) ->
                match a.GraphicsApi with
                | "dx12" -> "DirectX 12"
                | "dx11" -> "DirectX 11"
                | "dx10" -> "DirectX 10"
                | "dx9" -> "DirectX 9"
                | "vulkan" -> "Vulkan"
                | "opengl" -> "OpenGL"
                | _ -> ""
            | _ -> ""

        if full = "" then Localization.current.ApiTooltip + "  \u00B7  " + Localization.current.ApiUnknown
        else Localization.current.ApiTooltip + "  \u00B7  " + full

    /// The card's own copy of the installed badge text. The template cannot
    /// bind to MainViewModel.Loc from in here - see Localization.current.
    member _.BadgeInstalled = Localization.current.BadgeInstalled

    /// The corner mark is a bare tick, so the tooltip has to carry the whole
    /// meaning - and the route badge with it, which is the part a user who
    /// already knows it is installed actually wants.
    member this.InstalledTooltip =
        let route = this.ModBadgeText
        if String.IsNullOrWhiteSpace(route) then Localization.current.InstalledTooltip
        else Localization.current.InstalledTooltip + "  \u00B7  " + route

    member this.Title = cleanDisplayTitle currentGame.Title
    member this.RawTitle = currentGame.Title
    member this.LauncherType = currentGame.LauncherTypeName
    member this.InstallDirectory = currentGame.InstallDirectory
    member this.ExecutablePath = currentGame.TargetExecutablePath
    member this.UpscaleStatus = currentGame.UpscaleStatus

    member this.HasDlss = not (String.IsNullOrWhiteSpace(currentGame.DlssVersion))
    member this.HasFsr = not (String.IsNullOrWhiteSpace(currentGame.FsrVersion))
    member this.HasXess = not (String.IsNullOrWhiteSpace(currentGame.XessVersion))

    member this.IsLongTitle: bool = this.Title.Length > 28

    member this.MarqueeString: string =
        if this.Title.Length > 28 then
            sprintf "%s      â€¢      %s      â€¢      " this.Title this.Title
        else
            this.Title

    member this.BannerImage: Bitmap =
        loadBitmap ()
        match bannerBitmap with
        | Some bmp -> bmp
        | None -> null

    member this.HasBannerImage: bool =
        loadBitmap ()
        bannerBitmap.IsSome

    member this.IsVerticalCover: bool =
        loadBitmap ()
        isVerticalCover

    member this.IsAppIcon: bool =
        loadBitmap ()
        bannerBitmap.IsSome && not isVerticalCover

    member this.IsDragging
        with get () = isDragging
        and set value =
            if this.SetProperty(&isDragging, value) then
                this.RaisePropertyChanged("IsDragging")

    member this.IsDragTarget
        with get () = isDragTarget
        and set value =
            if this.SetProperty(&isDragTarget, value) then
                this.RaisePropertyChanged("IsDragTarget")

    // =====================================================================
    // PLAY / CANCEL / STOP
    // =====================================================================
    member private this.RaiseLaunch() =
        for name in
            [ "IsLaunchIdle"; "IsLaunchStarting"; "IsLaunchRunning"; "IsLaunchActive"; "PlayGlyph"; "PlayBrush"
              "PlayTooltip"; "LaunchMessage"; "HasLaunchMessage"; "IsChoosingLaunch" ] do
            this.RaisePropertyChanged(name)

    member private this.SetLaunchState(state: int, message: string) =
        launchState <- state
        launchMessage <- message
        if not watchAttached then
            watchAttached <- true
            launchWatch.Tick.Add(fun _ -> this.WatchTick())
        if state = 0 then launchWatch.Stop()
        elif not launchWatch.IsEnabled then launchWatch.Start()
        this.RaiseLaunch()

    member _.IsLaunchIdle = launchState = 0
    member _.IsLaunchStarting = launchState = 1
    member _.IsLaunchRunning = launchState = 2
    /// Keeps the button on show after the pointer leaves, so a starting or
    /// running game never hides the way to stop it.
    member _.IsLaunchActive = launchState <> 0 || isChoosingLaunch

    /// Play, a square while it is starting (press to cancel), a cross to stop.
    member _.PlayGlyph =
        match launchState with
        | 1 -> "■"
        | 2 -> "✕"
        | _ -> "▶"

    member _.PlayBrush =
        match launchState with
        | 1 -> "#475569"
        | 2 -> "#2563EB"
        | _ -> "#22A31B"

    member _.PlayTooltip =
        match launchState with
        | 1 -> "Starting... click to cancel"
        | 2 -> "Stop"
        | _ -> "Play"

    member _.LaunchMessage = launchMessage
    member _.HasLaunchMessage = launchMessage <> ""
    member _.IsChoosingLaunch = isChoosingLaunch

    /// The command line the game is started with, remembered per game.
    member this.LaunchArguments
        with get () = (GameLauncher.prefsFor currentGame).Arguments
        and set (value: string) =
            let prefs = GameLauncher.prefsFor currentGame
            GameLauncher.savePrefs currentGame { prefs with Arguments = (if isNull value then "" else value) } |> ignore
            this.RaisePropertyChanged("LaunchArguments")

    member _.CanLaunchViaSteam = GameLauncher.canLaunchViaSteam currentGame

    member _.LaunchRouteText =
        match GameLauncher.routeOf (GameLauncher.prefsFor currentGame) with
        | Some GameLauncher.ViaSteam -> "Starts through Steam"
        | Some GameLauncher.ViaExe -> "Starts the executable directly"
        | None when GameLauncher.canLaunchViaSteam currentGame -> "Asks how to start it the first time"
        | None -> "Starts the executable directly"

    /// Forgets the Steam-or-executable answer, so the next play asks again.
    member this.ResetLaunchRoute() =
        let prefs = GameLauncher.prefsFor currentGame
        GameLauncher.savePrefs currentGame { prefs with Route = "" } |> ignore
        this.RaisePropertyChanged("LaunchRouteText")

    member this.OpenGameFolder() = GameLauncher.openFolder currentGame |> ignore

    member private this.Launch(route: GameLauncher.LaunchRoute) =
        isChoosingLaunch <- false
        match GameLauncher.start currentGame route (GameLauncher.prefsFor currentGame).Arguments with
        | Ok() ->
            startedAt <- DateTime.UtcNow
            seenProcess <- false
            this.SetLaunchState(1, "")
        | Error message -> this.SetLaunchState(0, message)

    /// The first press on a Steam title asks how to start it; the answer is kept.
    member this.ChooseLaunch(viaSteam: bool) =
        let route = if viaSteam then GameLauncher.ViaSteam else GameLauncher.ViaExe
        let prefs = GameLauncher.prefsFor currentGame
        GameLauncher.savePrefs currentGame { prefs with Route = GameLauncher.routeKey route } |> ignore
        this.RaisePropertyChanged("LaunchRouteText")
        this.Launch route

    member this.CancelLaunchChoice() =
        isChoosingLaunch <- false
        this.RaiseLaunch()

    member this.PressPlay() =
        match launchState with
        | 0 when isChoosingLaunch -> this.CancelLaunchChoice()
        | 0 ->
            if GameLauncher.isRunning currentGame then
                // Already open, started from somewhere else: show it as running.
                seenProcess <- true
                this.SetLaunchState(2, "")
            elif GameLauncher.needsChoice currentGame then
                isChoosingLaunch <- true
                launchMessage <- ""
                this.RaiseLaunch()
            else
                let route =
                    match GameLauncher.routeOf (GameLauncher.prefsFor currentGame) with
                    | Some r -> r
                    | None -> if GameLauncher.hasExecutable currentGame then GameLauncher.ViaExe else GameLauncher.ViaSteam
                this.Launch route
        | _ ->
            // Cancelling a start and stopping a game are the same request: close
            // whatever of it exists, politely first.
            let wasStarting = launchState = 1
            this.SetLaunchState(1, if wasStarting then "Cancelling..." else "Closing...")
            async {
                do! GameLauncher.stop currentGame |> Async.AwaitTask
                Dispatcher.UIThread.Post(fun () -> this.SetLaunchState(0, ""))
            }
            |> Async.Start

    member private this.WatchTick() =
        match launchState with
        | 1 when launchMessage = "Cancelling..." || launchMessage = "Closing..." -> ()
        | 1 ->
            let alive = GameLauncher.isRunning currentGame
            if alive then seenProcess <- true
            if alive && (GameLauncher.hasWindow currentGame || (DateTime.UtcNow - startedAt).TotalSeconds > 20.0) then
                this.SetLaunchState(2, "")
            elif not alive && seenProcess then
                // Came up and went away again before it was ever shown.
                this.SetLaunchState(0, "The game closed while it was starting.")
            elif (DateTime.UtcNow - startedAt).TotalSeconds > 90.0 then
                this.SetLaunchState(0, "The game did not start within 90 seconds.")
        | 2 ->
            if not (GameLauncher.isRunning currentGame) then this.SetLaunchState(0, "")
        | _ -> launchWatch.Stop()

    member this.LauncherBadgeBackground: string =
        match currentGame.LauncherTypeName with
        | "STEAM" -> "#1E3A8A"
        | "EPIC GAMES" -> "#1E293B"
        | "GOG GALAXY" -> "#581C87"
        | "REPACK" -> "#831843"
        | _ -> "#334155"

    member this.HasUpscaleStatus: bool =
        let s = currentGame.UpscaleStatus
        not (String.IsNullOrWhiteSpace(s)) && not (s.Contains("Direct3D")) && not (s.Contains("Custom")) && not (s.Contains("Default"))

    member this.UpscaleBadgeBackground: string =
        if currentGame.UpscaleStatus.Contains("OptiScaler") then "#0891B2"
        elif currentGame.UpscaleStatus.Contains("DLSS") then "#16A34A"
        elif currentGame.UpscaleStatus.Contains("FSR") then "#EA580C"
        elif currentGame.UpscaleStatus.Contains("XeSS") then "#2563EB"
        else "#334155"

