namespace DLSS_5_MANAGER.ViewModels

open System
open System.Collections.ObjectModel
open System.Globalization
open System.Threading.Tasks
open Avalonia.Media
open Avalonia.Threading
open DLSS_5_MANAGER.Models
open DLSS_5_MANAGER.Services

/// One game Adaptive Motion Learning keeps something for.
type LearnedRowViewModel(game: LearnedGame) =
    inherit ViewModelBase()
    member _.Game = game
    member _.Name = game.Game
    member _.State =
        let state = if game.Learned then "Learned" else sprintf "Learning %d%%" game.Percent
        if game.Updated = "" then state else sprintf "%s · %s" state game.Updated
    member _.Percent = if game.Learned then 100.0 else float game.Percent

/// The Motion Forge edition: MFG SUPER MOTION's Super Motion page (NODIX
/// TECH's ViewModels/MainViewModel.fs), its profiles and its learning cache,
/// over the engine in Services/MotionEngine.fs.
///
/// Every setting moves the selected profile; a running engine is sent only
/// the lines that changed, a moment after the last change, so a slider
/// dragged across its range is one change for it and not fifty.
type MotionForgeViewModel() as this =
    inherit ViewModelBase()

    let host = new MotionEngineHost()
    let hotkey = new MotionHotkey()
    let mutable prefs = MotionPrefs.load ()
    let mutable engineStatus = MotionStatus.idle
    let mutable isActive = false
    let mutable cardsAsked = false
    let mutable preparingShaders = false
    let mutable systemVrr: bool option = None
    let mutable note = ""
    let mutable newProfileName = ""
    let mutable aiBusy = false
    let mutable aiProgress = 0.0
    let mutable aiText = ""
    let mutable gpuChoices: string array = [| "Auto" |]
    let learned = ObservableCollection<LearnedRowViewModel>()
    // While a list is handed over anew (the profiles' names, the cards), its box
    // puts back whatever it stood at - the first entry - as if that had been
    // picked. What a box says then is not a choice: it is passed over, and the
    // box is told its place again once it has taken the list in.
    let mutable swappingLists = false
    /// The profiles' names, changed in place: a box whose list is replaced
    /// whole loses its place, and with it the profile shown.
    let profileNames = ObservableCollection<string>()
    let mutable indexShown: int option = None

    let sendTimer = DispatcherTimer(Interval = TimeSpan.FromMilliseconds 200.0)
    let saveTimer = DispatcherTimer(Interval = TimeSpan.FromMilliseconds 400.0)

    let selected () =
        prefs.Profiles |> Array.tryFind (fun p -> p.Id = prefs.SelectedProfile) |> Option.defaultValue prefs.Profiles[0]

    let options () = (selected ()).Options

    let persist () =
        saveTimer.Stop()
        saveTimer.Start()

    let brush (hex: string) = SolidColorBrush(Color.Parse hex) :> IBrush

    // The page's lists. The upscalers in the order the page shows them; the
    // engine's own numbers are upscalerOrder (his app put SMSR second).
    let generatorNames = [| "DLSS MFG"; "XeSS MFG"; "SMFG 1.3"; "SMAI 1.0"; "SUITE FG"; "AMD FSR 3.1 FG" |]
    let upscalerOrder = [| 0; 5; 1; 2; 3; 4 |]
    let upscalerNames = [| "Off"; "SMSR 1.0"; "Sharpness only"; "Integer"; "Nearest Neighbor"; "Anime4K" |]
    let multiplierNames = [| "x2"; "x3"; "x4"; "x5"; "x6"; "Custom" |]
    let gameTypeNames = [| "Auto"; "2D"; "2.5D"; "First person"; "Third person"; "Racing"; "Open world"; "Custom" |]
    let presetNames = [| "Driver's choice"; "Performance · Preset C"; "Quality · Preset B"; "Balanced · Preset A" |]
    let syncNames = [| "Off (allow tearing)"; "Default"; "VSync"; "VSync ½"; "VSync ⅓" |]
    let queueNames = [| "0 · least delay"; "1 · balanced"; "2 · smoothest" |]
    let motionNames = [| "DLSS MFG's own"; "NVIDIA Optical Flow"; "GPU estimate" |]
    let xessMotionNames = [| "XeSS MFG's own"; "GPU estimate"; "NVIDIA Optical Flow" |]
    let suiteMotionNames = [| "GPU estimate"; "NVIDIA Optical Flow" |]
    let fsrMotionNames = [| "GPU estimate"; "FSR's own optical flow"; "NVIDIA Optical Flow" |]
    let searchNames = [| "SMFG (full search)"; "SMFG · Fast" |]
    let captureNames = [| "DXGI (Desktop Duplication)"; "WGC (Windows Graphics Capture)" |]
    let windowNames = [| "Borderless"; "Scaled to screen"; "Windowed (as it is)" |]
    let countdownNames = MotionPrefs.countdowns |> Array.map (sprintf "%d s")
    let priorityNames = [| "Realtime"; "Normal" |]
    let gameFpsNames = MotionPrefs.gameFpsRates |> Array.map (fun r -> if r = 0 then "Auto" else string r)
    let counterNames = [| "Simple"; "Advanced (frame time graph)" |]
    // The shortcut: modifiers * 65536 + virtual key (Alt 1, Ctrl 2, Shift 4).
    let hotkeys =
        [| "Shift + Alt + M", (5 <<< 16) ||| 0x4D
           "Ctrl + Alt + M", (3 <<< 16) ||| 0x4D
           "Ctrl + Shift + M", (6 <<< 16) ||| 0x4D
           "Shift + Alt + F", (5 <<< 16) ||| 0x46
           "Ctrl + Alt + F", (3 <<< 16) ||| 0x46
           "F6", 0x75
           "F8", 0x77
           "F9", 0x79
           "None", -1 |]

    let effectiveHotkey () = if prefs.Hotkey = 0 then MotionPrefs.defaultHotkey else prefs.Hotkey

    /// Every option-backed property, raised together when a whole profile changes.
    static let optionProperties =
        [ "FrameGeneration"; "GeneratorIndex"; "FrameModeIndex"; "MultiplierIndex"; "CustomMultiplier"; "CustomMultiplierText"
          "AdaptiveTargetText"; "GameTypeIndex"; "Flow"; "FlowText"; "DlssPresetIndex"; "TriangleMesh"; "SmtAssist"
          "TriPitchIndex"; "TriRoundsIndex"; "TriFineIndex"; "TriFullIndex"; "TriHomeIndex"; "TriRangeIndex"; "TriExactIndex"; "TriRelaxIndex"
          "UltraSmooth"; "EvenPacing"; "LowLatency"; "AdaptiveLearning"; "AdaptiveFlow"; "AmlGuide"
          "UpscalerIndex"; "Stretch"; "SmsrBoost"; "Sharpness"; "OverSharpness"
          "Vrr"; "Hdr"; "StillPixels"; "LimitToScreen"; "MultiDisplay"; "FpsCounter"; "FpsCounterModeIndex"; "FpsCounterSize"
          "LockRefresh"; "QueueTargetIndex"; "FrameLimit"; "FrameLimitText"; "GameFpsIndex"; "SyncModeIndex"; "Bloom"; "BloomWide"
          "MotionSourceIndex"; "XessMotionSourceIndex"; "SuiteMotionSourceIndex"; "FsrMotionSourceIndex"; "MsmSearchIndex"; "CaptureApiIndex"; "WindowModeIndex"; "CountdownIndex"; "Spout"
          "GpuIndex"; "GpuPriorityIndex"; "CropLeft"; "CropTop"; "CropRight"; "CropBottom"
          "CursorClip"; "CursorSpeed"; "CursorHide"; "CursorScale"
          "IsDlss"; "IsXess"; "IsSmfg"; "IsSmai"; "IsSuiteFg"; "IsFsr"; "IsDlssOrXess"; "HasGeneratorFiles"; "IsFixed"; "IsAdaptive"; "IsCustomMultiplier"; "IsCustomGame"
          "IsUpscaling"; "IsSmsr"; "ShowSmtGear"; "FlowNote"; "MultiplierNote"; "HasMultiplierNote"; "NeedsFiles"; "FilesText"; "CanStart"; "GeneratorNote" ]

    let notify (names: string list) =
        if names |> List.contains "ProfileNames" then
            swappingLists <- true
            try
                let now = prefs.Profiles |> Array.map (fun p -> p.Name)
                while profileNames.Count > now.Length do profileNames.RemoveAt(profileNames.Count - 1)
                now |> Array.iteri (fun i n -> if i >= profileNames.Count then profileNames.Add n elif profileNames[i] <> n then profileNames[i] <- n)
            finally swappingLists <- false
            // (A box whose selected entry was renamed in place keeps no place at all, and a raise of the same value is not
            // passed on to it: it is shown -1 first, then the profile's own place.)
            Dispatcher.UIThread.Post((fun () ->
                indexShown <- Some -1
                this.RaisePropertyChanged "ProfileIndex"
                indexShown <- None
                this.RaisePropertyChanged "ProfileIndex"), DispatcherPriority.Background)
        let lists, rest = names |> List.partition (fun n -> n = "GpuChoices")
        if not lists.IsEmpty then
            swappingLists <- true
            try for n in lists do this.RaisePropertyChanged n
            finally swappingLists <- false
            Dispatcher.UIThread.Post((fun () -> this.RaisePropertyChanged "ProfileIndex"; this.RaisePropertyChanged "GpuIndex"), DispatcherPriority.Background)
        for n in rest do this.RaisePropertyChanged n

    let refreshLearned () =
        let games = LearnedGames.list ()
        learned.Clear()
        for g in games do learned.Add(LearnedRowViewModel g)
        notify [ "HasLearned"; "NoLearned" ]

    do
        for p in prefs.Profiles do profileNames.Add p.Name
        sendTimer.Tick.Add(fun _ ->
            sendTimer.Stop()
            host.Send(options ()))
        saveTimer.Tick.Add(fun _ ->
            saveTimer.Stop()
            let snapshot = prefs
            Task.Run(fun () -> MotionPrefs.save snapshot |> ignore) |> ignore)
        host.Changed.Add(fun s ->
            Dispatcher.UIThread.Post(fun () ->
                let wasIdle = engineStatus.Phase = Idle
                engineStatus <- s
                notify [ "IsRunning"; "IsIdle"; "StartText"; "StartBrush"; "StatusLine"; "StatusNote"; "HasStatusNote"; "LearningText" ]
                // A study that moved on, or an engine that ended: the cache list follows.
                if s.Phase = Idle && not wasIdle then refreshLearned ()))
        hotkey.Pressed.Add(fun () -> Dispatcher.UIThread.Post(fun () -> this.StartOrStop(true)))
        hotkey.Set(effectiveHotkey ())
        // Another overlay of SUITE's starting stops this one (OverlayArbiter).
        OverlayArbiter.register "motion" (fun () ->
            let running = host.IsActive
            if running then
                host.Stop()
                Dispatcher.UIThread.Post(fun () ->
                    note <- "Stopped: the Screen Engine was started, and only one overlay runs at a time."
                    notify [ "StatusNote"; "HasStatusNote" ])
            running)
        // The engine is its own process; it must not outlive SUITE (it holds
        // the game's window borderless until it is told to stop).
        AppDomain.CurrentDomain.ProcessExit.Add(fun _ -> host.StopNow(); (hotkey :> IDisposable).Dispose())

    /// Changes the selected profile's options and tells whoever needs telling.
    member private this.Update(names: string list, change: MotionOptions -> MotionOptions) =
        let before = options ()
        let next = MotionPrefs.normalizeOptions (change before)
        if next <> before then
            let id = prefs.SelectedProfile
            prefs <- { prefs with Profiles = prefs.Profiles |> Array.map (fun p -> if p.Id = id then { p with Options = next } else p) }
            persist ()
            // The generator is read once, when the engine starts: a new one needs a new start.
            if host.IsActive && next.Generator <> before.Generator then
                host.Stop()
                note <- "The generator changed - press START MOTION to run it."
                notify [ "StatusNote"; "HasStatusNote" ]
            elif host.IsActive then
                sendTimer.Stop()
                sendTimer.Start()
        notify names

    // ---------------------------------------------------------------------
    // Page life
    // ---------------------------------------------------------------------

    /// The page is shown: the cache is read, and once per run the engine is
    /// asked for the cards, the shaders and VRR - on a worker thread, as each
    /// starts a process of the engine's.
    member this.Activate() =
        isActive <- true
        refreshLearned ()
        notify [ "IsEngineAvailable"; "HasStreamline"; "HasXessFiles"; "IsAiInstalled"; "FilesText"; "NeedsFiles"; "CanStart"; "CanImport" ]
        if not cardsAsked && MotionFiles.isEngineAvailable () then
            cardsAsked <- true
            Task.Run(fun () ->
                let cards = host.LoadCards()
                Dispatcher.UIThread.Post(fun () -> this.TakeCards cards)
                if host.ShadersNeedWarm() then
                    Dispatcher.UIThread.Post(fun () -> preparingShaders <- true; notify [ "StatusNote"; "HasStatusNote" ])
                    host.WarmShaders()
                    Dispatcher.UIThread.Post(fun () -> preparingShaders <- false; notify [ "StatusNote"; "HasStatusNote" ])
                let vrr = host.SystemVrr()
                Dispatcher.UIThread.Post(fun () -> this.TakeSystemVrr vrr))
            |> ignore

    member _.Deactivate() = isActive <- false

    member private this.TakeCards(cards: MotionCard array) =
        gpuChoices <- Array.append [| "Auto" |] (cards |> Array.map (fun c -> c.Name))
        notify [ "GpuChoices"; "GpuIndex"; "CardsText" ]

    /// VRR follows the PC (G-SYNC / FreeSync for windows) in every profile
    /// the person has not set it in themselves.
    member private this.TakeSystemVrr(vrr: bool option) =
        systemVrr <- vrr
        match vrr with
        | Some on ->
            let changed = prefs.Profiles |> Array.exists (fun p -> p.Options.Vrr <> on && not p.Options.VrrChosen)
            if changed then
                prefs <- { prefs with Profiles = prefs.Profiles |> Array.map (fun p -> if p.Options.VrrChosen then p else { p with Options = { p.Options with Vrr = on } }) }
                persist ()
                notify [ "Vrr" ]
        | None -> ()

    // ---------------------------------------------------------------------
    // START / STOP and the engine's status
    // ---------------------------------------------------------------------

    member _.IsEngineAvailable = MotionFiles.isEngineAvailable ()
    member _.IsRunning = engineStatus.Phase <> Idle
    member _.IsIdle = engineStatus.Phase = Idle

    /// `pickNow`: the shortcut, pressed in the game - that window is taken at
    /// once. Pressed with SUITE in front there is no game to take, so the
    /// countdown runs as for the button.
    member this.StartOrStop(pickNow: bool) =
        if host.IsActive then host.Stop()
        else
            note <- ""
            if not (MotionFiles.isEngineAvailable ()) then
                note <- "Motion Forge's engine is not in this build of SUITE (engine\\motion\\MotionForgeEngine.exe)."
            else
                let stopped = OverlayArbiter.claim "motion"
                if not stopped.IsEmpty then note <- "The Screen Engine was stopped - only one overlay runs at a time."
                host.Start(options (), prefs.EngineLog, pickNow) |> ignore
        notify [ "StatusNote"; "HasStatusNote"; "IsRunning"; "IsIdle"; "StartText"; "StartBrush" ]

    /// The START MOTION button.
    member this.ToggleMotion() = this.StartOrStop(false)
    member this.StopMotion() = if host.IsActive then host.Stop()

    member _.StartText =
        match engineStatus.Phase with
        | Idle -> "START MOTION"
        | Starting -> "STARTING…"
        | Picking -> sprintf "CLICK YOUR GAME · %d" engineStatus.Countdown
        | Running -> "STOP MOTION"

    /// Blue while idle, amber while it waits for the game, green running (as his app has it).
    member _.StartBrush =
        match engineStatus.Phase with
        | Idle -> brush "#2563EB"
        | Starting | Picking -> brush "#D97706"
        | Running -> brush "#4D8C00"

    member _.StatusLine =
        let s = engineStatus
        match s.Phase with
        | Idle -> if MotionFiles.isEngineAvailable () then "Ready" else "Engine missing"
        | Starting -> "Starting…"
        | Picking -> sprintf "Switch to your game… %d s" s.Countdown
        | Running ->
            let fps = if s.BaseFps >= 0 && s.OutputFps >= 0 then sprintf "%d → %d fps" s.BaseFps s.OutputFps else "measuring"
            let api = if s.Api = "" then "" else " · " + s.Api.ToUpperInvariant()
            let place = if s.Placement = "" then "" else " · " + s.Placement
            let size = if s.Width > 0 then sprintf " · %dx%d" s.Width s.Height else ""
            let mult = if s.Multiplier > 0 then sprintf " · x%d" s.Multiplier elif s.MfgUnavailable then " · no frame generation" else ""
            fps + api + place + size + mult

    member _.LearningText =
        match engineStatus.Learning with
        | 101 -> "Adaptive Motion Learning: this game is learned"
        | n when n >= 0 -> sprintf "Adaptive Motion Learning: learning %d%%" n
        | _ -> ""

    /// What the person should know: an error, why frame generation is off, a
    /// screen VRR does not reach, or the shaders being made.
    member _.StatusNote =
        let s = engineStatus
        let error =
            match s.Error with
            | "" -> ""
            | "nogpu" -> "No compatible graphics card was found."
            | "busy" -> "Another Motion Forge engine is still running."
            | "missing" -> "Motion Forge's engine is not in this build of SUITE."
            | "nowindow" -> "No game was in front. Press START MOTION, then switch to your game."
            | "closed" -> "The game window was closed."
            | "failed" -> "The engine stopped unexpectedly. Turn on the engine log (Advanced) and look at engine.log."
            | other -> sprintf "The engine stopped: %s." other
        [ (if preparingShaders then "Preparing frame generation for this PC (first time only, about 15 seconds)…" else "")
          error
          (if s.MfgUnavailable then "Frame generation is unavailable:" + s.MfgWhy + " The game is shown as it is." else "")
          (if s.VrrFixed then "The screen stays at its fixed refresh (VRR is not reaching it). SMFG and SMAI fit themselves to it; with DLSS MFG or XeSS MFG pick Adaptive at the screen's refresh." else "")
          note ]
        |> List.filter (fun t -> t <> "")
        |> String.concat "  "

    member this.HasStatusNote = this.StatusNote <> ""

    // ---------------------------------------------------------------------
    // Profiles
    // ---------------------------------------------------------------------

    member _.ProfileNames = profileNames

    member _.ProfileIndex
        with get () =
            match indexShown with
            | Some i -> i
            | None -> prefs.Profiles |> Array.tryFindIndex (fun p -> p.Id = prefs.SelectedProfile) |> Option.defaultValue 0
        and set (i: int) =
            if not swappingLists && indexShown.IsNone && i >= 0 && i < prefs.Profiles.Length && prefs.Profiles[i].Id <> prefs.SelectedProfile then
                let before = options ()
                prefs <- { prefs with SelectedProfile = prefs.Profiles[i].Id }
                persist ()
                if host.IsActive then
                    if (options ()).Generator <> before.Generator then host.Stop()
                    else host.Send(options ())
                notify ([ "ProfileIndex"; "CanDeleteProfile"; "ProfileName" ] @ optionProperties)

    member _.ProfileName = (selected ()).Name
    member _.CanDeleteProfile = prefs.SelectedProfile <> "default"
    member _.CanAddProfile = prefs.Profiles.Length < MotionPrefs.maxProfiles

    /// The name typed for Add / Rename.
    member _.NewProfileName
        with get () = newProfileName
        and set (v: string) = newProfileName <- (if isNull v then "" else v)

    member private _.NameOk(name: string, except: string) =
        let n = name.Trim()
        n.Length >= 1 && n.Length <= 40
        && not (prefs.Profiles |> Array.exists (fun p -> p.Id <> except && p.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))

    /// A copy of the selected profile under the typed name (or "Profile N").
    member this.AddProfile() =
        if this.CanAddProfile then
            let typed = newProfileName.Trim()
            let name =
                if typed <> "" then typed
                else Seq.initInfinite (fun i -> sprintf "Profile %d" (i + 2)) |> Seq.find (fun n -> this.NameOk(n, ""))
            if not (this.NameOk(name, "")) then
                note <- "Use a unique name of 1–40 characters."
            else
                let p = { Id = Guid.NewGuid().ToString("N"); Name = name; Options = options () }
                prefs <- { prefs with Profiles = Array.append prefs.Profiles [| p |]; SelectedProfile = p.Id }
                newProfileName <- ""
                note <- ""
                persist ()
            notify ([ "ProfileNames"; "ProfileIndex"; "NewProfileName"; "CanDeleteProfile"; "CanAddProfile"; "ProfileName"; "StatusNote"; "HasStatusNote" ] @ optionProperties)

    member this.RenameProfile() =
        let name = newProfileName.Trim()
        if not (this.NameOk(name, prefs.SelectedProfile)) then note <- "Use a unique name of 1–40 characters."
        else
            prefs <- { prefs with Profiles = prefs.Profiles |> Array.map (fun p -> if p.Id = prefs.SelectedProfile then { p with Name = name } else p) }
            newProfileName <- ""
            note <- ""
            persist ()
        notify [ "ProfileNames"; "ProfileIndex"; "NewProfileName"; "ProfileName"; "StatusNote"; "HasStatusNote" ]

    /// Default is always kept.
    member this.DeleteProfile() =
        if this.CanDeleteProfile then
            let before = options ()
            prefs <- { prefs with Profiles = prefs.Profiles |> Array.filter (fun p -> p.Id <> prefs.SelectedProfile); SelectedProfile = "default" }
            persist ()
            if host.IsActive && (options ()).Generator <> before.Generator then host.Stop() elif host.IsActive then host.Send(options ())
            notify ([ "ProfileNames"; "ProfileIndex"; "CanDeleteProfile"; "CanAddProfile"; "ProfileName" ] @ optionProperties)

    member this.ResetProfile() = this.Update(optionProperties, fun _ -> MotionPrefs.defaultOptions)

    member _.CanImport = IO.File.Exists(MotionPrefs.superMotionFile ())

    /// MFG SUPER MOTION's own profiles and learnt games, if it is installed.
    member this.ImportFromSuperMotion() =
        let games = LearnedGames.importFromSuperMotion ()
        match MotionPrefs.importFromSuperMotion prefs with
        | Some(next, added) ->
            prefs <- next
            persist ()
            note <- sprintf "Imported from MFG SUPER MOTION: %d profile%s, %d learnt game%s." added (if added = 1 then "" else "s") games (if games = 1 then "" else "s")
        | None -> note <- sprintf "Imported from MFG SUPER MOTION: %d learnt game%s (no profiles found)." games (if games = 1 then "" else "s")
        refreshLearned ()
        notify [ "ProfileNames"; "ProfileIndex"; "CanAddProfile"; "StatusNote"; "HasStatusNote" ]

    // ---------------------------------------------------------------------
    // Frame generation
    // ---------------------------------------------------------------------

    member _.Generators = generatorNames
    member _.FrameModes = [| "Fixed"; "Adaptive" |]
    member _.Multipliers = multiplierNames
    member _.GameTypes = gameTypeNames
    member _.DlssPresets = presetNames

    member _.FrameGeneration
        with get () = (options ()).FrameGeneration
        and set v = this.Update([ "FrameGeneration" ], fun o -> { o with FrameGeneration = v })

    member _.GeneratorIndex
        with get () = (options ()).Generator
        and set v =
            if v >= 0 && v < MotionPrefs.generators then
                this.Update([ "GeneratorIndex"; "IsDlss"; "IsXess"; "IsSmfg"; "IsSmai"; "IsSuiteFg"; "IsFsr"; "IsDlssOrXess"; "HasGeneratorFiles"; "FlowNote"; "MultiplierNote"; "HasMultiplierNote"
                              "NeedsFiles"; "FilesText"; "CanStart"; "GeneratorNote"; "GpuIndex"; "ShowSmtGear" ],
                            fun o -> { o with Generator = v })

    member _.IsDlss = (options ()).Generator = 0
    member _.IsXess = (options ()).Generator = 1
    member _.IsSmfg = (options ()).Generator = 2
    member _.IsSmai = (options ()).Generator = 3
    member _.IsSuiteFg = (options ()).Generator = MotionPrefs.suiteFg
    member _.IsFsr = (options ()).Generator = MotionPrefs.fsrFg
    member _.IsDlssOrXess = (options ()).Generator <= 1
    /// The generators that come with files to pick, download or check (SMFG and SUITE FG have none).
    member _.HasGeneratorFiles = let g = (options ()).Generator in g <> 2 && g <> MotionPrefs.suiteFg

    /// What the picked generator runs on, in a line.
    member _.GeneratorNote =
        match (options ()).Generator with
        | 0 -> "NVIDIA DLSS Multi Frame Generation through NVIDIA Streamline, which comes with SUITE - nothing to pick. Needs an NVIDIA RTX 40 (x2) or RTX 50 (up to x6) card; a higher multiplier runs at the card's most. Older cards: use SMFG."
        | 1 -> "Intel's frame generation, from your own XeSS SDK files. Runs on many cards; one frame in between a pair outside Intel Arc."
        | 2 -> "Motion Forge's own generator. Any graphics card, the lightest of all."
        | 4 -> "The Screen Engine's own frame generation: each frame in between drawn from the two real frames along the motion SUITE works out. Any graphics card, x2 to x6."
        | 5 -> "AMD FidelityFX Super Resolution 3.1 Frame Generation, which comes with SUITE. Any graphics card (AMD, NVIDIA or Intel); one frame in between a pair, so x2."
        | _ -> "A neural network (SMFG Net) on your card through DirectML - best on cards with AI units. Heavier than SMFG; a one-time download."

    member _.FrameModeIndex
        with get () = if (options ()).Adaptive then 1 else 0
        and set v = this.Update([ "FrameModeIndex"; "IsFixed"; "IsAdaptive"; "IsCustomMultiplier"; "MultiplierNote"; "HasMultiplierNote" ], fun o -> { o with Adaptive = (v = 1) })

    member _.IsFixed = not (options ()).Adaptive
    member _.IsAdaptive = (options ()).Adaptive

    member _.MultiplierIndex
        with get () = let o = options () in if o.MultiplierCustom > 0 then 5 else o.Mode
        and set v =
            if v = 5 then
                this.Update([ "MultiplierIndex"; "IsCustomMultiplier"; "CustomMultiplier"; "CustomMultiplierText"; "MultiplierNote"; "HasMultiplierNote" ],
                            fun o -> { o with MultiplierCustom = (if o.MultiplierCustom > 0 then o.MultiplierCustom else (o.Mode + 2) * 10 + 5) })
            elif v >= 0 && v <= 4 then
                this.Update([ "MultiplierIndex"; "IsCustomMultiplier"; "CustomMultiplier"; "CustomMultiplierText"; "MultiplierNote"; "HasMultiplierNote" ], fun o -> { o with Mode = v; MultiplierCustom = 0 })

    member _.IsCustomMultiplier = not (options ()).Adaptive && (options ()).MultiplierCustom > 0

    /// A generator that cannot go as high as the multiplier picked says so (FSR 3.1: one frame in between a pair).
    member _.MultiplierNote =
        let o = options ()
        let most = MotionPrefs.mostMultiplier o.Generator
        if most < 6 && (o.Adaptive || MotionPrefs.multiplierTenths o > most * 10) then
            sprintf "FSR 3.1 makes one frame in between each pair: it runs at x%d whatever is picked here." most
        else ""

    member this.HasMultiplierNote = this.MultiplierNote <> ""

    /// x1.1 .. x6.0, in tenths underneath.
    member _.CustomMultiplier
        with get () = float (MotionPrefs.multiplierTenths (options ())) / 10.0
        and set (v: float) =
            let tenths = Math.Clamp(int (Math.Round(v * 10.0)), 11, MotionPrefs.multiplierCustomMost)
            this.Update([ "CustomMultiplier"; "CustomMultiplierText"; "MultiplierNote"; "HasMultiplierNote" ], fun o -> { o with MultiplierCustom = tenths })

    member this.CustomMultiplierText = sprintf "x%.1f" this.CustomMultiplier

    /// Empty is the screen's own refresh.
    member _.AdaptiveTargetText
        with get () = let t = (options ()).AdaptiveFps in if t = 0 then "" else string t
        and set (v: string) =
            let text = if isNull v then "" else v.Trim()
            match text with
            | "" -> this.Update([ "AdaptiveTargetText" ], fun o -> { o with AdaptiveFps = 0 })
            | t ->
                match Int32.TryParse t with
                | true, n when n >= 30 && n <= 1000 -> this.Update([ "AdaptiveTargetText" ], fun o -> { o with AdaptiveFps = n })
                | _ -> notify [ "AdaptiveTargetText" ]

    member _.ScreenRefreshText = sprintf "Screen (%d)" (MotionScreen.refreshRate ())

    member _.GameTypeIndex
        with get () = (options ()).GameType
        and set v = this.Update([ "GameTypeIndex"; "IsCustomGame"; "FlowNote" ], fun o -> { o with GameType = v })

    member _.IsCustomGame = (options ()).GameType = MotionPrefs.gameCustom

    member _.Flow
        with get () = float (options ()).Flow
        and set (v: float) = this.Update([ "Flow"; "FlowText"; "FlowNote" ], fun o -> { o with Flow = int (Math.Round v) })

    member _.FlowText = sprintf "%d%%" (options ()).Flow

    member _.FlowNote =
        let o = options ()
        if o.Generator = 2 || o.Generator = 3 then "SMFG and SMAI make their frames at the picture's full size."
        elif o.Generator = MotionPrefs.suiteFg || o.Generator = MotionPrefs.fsrFg then
            sprintf "Frames in between are made at the picture's full size; the motion is worked out at %d%% of it." (MotionPrefs.effectiveFlow o)
        else sprintf "Frames in between are made at %d%% of the picture, then rebuilt at full size." (MotionPrefs.effectiveFlow o)

    member _.DlssPresetIndex
        with get () = (options ()).DlssPreset
        and set v = this.Update([ "DlssPresetIndex" ], fun o -> { o with DlssPreset = v })

    // The chips.
    member _.TriangleMesh
        with get () = (options ()).TriangleMesh
        and set v = this.Update([ "TriangleMesh"; "ShowSmtGear" ], fun o -> { o with TriangleMesh = v })
    /// SMT's own settings: with SMFG, while SMT is on.
    member _.ShowSmtGear = let o = options () in o.Generator = 2 && o.TriangleMesh
    member _.SmtAssist
        with get () = (options ()).SmtAssist
        and set v = this.Update([ "SmtAssist" ], fun o -> { o with SmtAssist = v })
    member _.UltraSmooth
        with get () = (options ()).UltraSmooth
        and set v = this.Update([ "UltraSmooth" ], fun o -> { o with UltraSmooth = v })
    member _.EvenPacing
        with get () = not (options ()).PairPacing
        and set v = this.Update([ "EvenPacing" ], fun o -> { o with PairPacing = not v })
    member _.LowLatency
        with get () = (options ()).LowLatency
        and set v = this.Update([ "LowLatency" ], fun o -> { o with LowLatency = v })
    member _.AdaptiveLearning
        with get () = (options ()).AdaptiveLearning
        and set v = this.Update([ "AdaptiveLearning" ], fun o -> { o with AdaptiveLearning = v })
    member _.AdaptiveFlow
        with get () = (options ()).AdaptiveFlow
        and set v = this.Update([ "AdaptiveFlow" ], fun o -> { o with AdaptiveFlow = v })
    member _.AmlGuide
        with get () = not (options ()).AmlGuideOff
        and set v = this.Update([ "AmlGuide" ], fun o -> { o with AmlGuideOff = not v })

    // SMT's gear: each list, and the place the profile keeps (+1, 0 = default).
    member _.TriPitches = MotionPrefs.triPitches |> Array.map (sprintf "%d px")
    member _.TriRoundChoices = MotionPrefs.triRounds |> Array.map string
    member _.TriFineChoices = MotionPrefs.triFines |> Array.map string
    member _.TriFullChoices = MotionPrefs.triFulls |> Array.map string
    member _.TriHomeChoices = MotionPrefs.triHomes |> Array.map (fun p -> if p = 0 then "Off" else sprintf "%d px" p)
    member _.TriRangeChoices = [| "1"; "2"; "3" |]
    member _.TriExactChoices = MotionPrefs.triExacts |> Array.map string
    member _.TriRelaxChoices = MotionPrefs.triRelaxes |> Array.map string
    member _.TriPitchIndex with get () = MotionPrefs.triPlace MotionPrefs.triPitches 0 (options ()).TriPitch and set v = this.Update([ "TriPitchIndex" ], fun o -> { o with TriPitch = v + 1 })
    member _.TriRoundsIndex with get () = MotionPrefs.triPlace MotionPrefs.triRounds 1 (options ()).TriRounds and set v = this.Update([ "TriRoundsIndex" ], fun o -> { o with TriRounds = v + 1 })
    member _.TriFineIndex with get () = MotionPrefs.triPlace MotionPrefs.triFines 2 (options ()).TriFine and set v = this.Update([ "TriFineIndex" ], fun o -> { o with TriFine = v + 1 })
    member _.TriFullIndex with get () = MotionPrefs.triPlace MotionPrefs.triFulls 3 (options ()).TriFull and set v = this.Update([ "TriFullIndex" ], fun o -> { o with TriFull = v + 1 })
    member _.TriHomeIndex with get () = MotionPrefs.triPlace MotionPrefs.triHomes 4 (options ()).TriHome and set v = this.Update([ "TriHomeIndex" ], fun o -> { o with TriHome = v + 1 })
    member _.TriRangeIndex with get () = MotionPrefs.triPlace MotionPrefs.triRanges 5 (options ()).TriRange and set v = this.Update([ "TriRangeIndex" ], fun o -> { o with TriRange = v + 1 })
    member _.TriExactIndex with get () = MotionPrefs.triPlace MotionPrefs.triExacts 6 (options ()).TriExact and set v = this.Update([ "TriExactIndex" ], fun o -> { o with TriExact = v + 1 })
    member _.TriRelaxIndex with get () = MotionPrefs.triPlace MotionPrefs.triRelaxes 7 (options ()).TriRelax and set v = this.Update([ "TriRelaxIndex" ], fun o -> { o with TriRelax = v + 1 })

    // ---------------------------------------------------------------------
    // Upscaling
    // ---------------------------------------------------------------------

    member _.Upscalers = upscalerNames

    member _.UpscalerIndex
        with get () = Array.IndexOf(upscalerOrder, (options ()).Upscaler) |> max 0
        and set v =
            if v >= 0 && v < upscalerOrder.Length then
                this.Update([ "UpscalerIndex"; "IsUpscaling"; "IsSmsr" ], fun o -> { o with Upscaler = upscalerOrder[v] })

    member _.IsUpscaling = (options ()).Upscaler <> 0
    member _.IsSmsr = (options ()).Upscaler = 5
    member _.Stretch with get () = (options ()).Stretch and set v = this.Update([ "Stretch" ], fun o -> { o with Stretch = v })
    member _.SmsrBoost with get () = float (options ()).SmsrBoost and set (v: float) = this.Update([ "SmsrBoost" ], fun o -> { o with SmsrBoost = int (Math.Round v) })
    member _.Sharpness with get () = float (options ()).Sharpness and set (v: float) = this.Update([ "Sharpness" ], fun o -> { o with Sharpness = int (Math.Round v) })
    member _.OverSharpness with get () = float (options ()).OverSharpness and set (v: float) = this.Update([ "OverSharpness" ], fun o -> { o with OverSharpness = int (Math.Round v) })

    // ---------------------------------------------------------------------
    // Quick settings
    // ---------------------------------------------------------------------

    /// Set here, VRR stops following what the PC says at each start.
    member _.Vrr with get () = (options ()).Vrr and set v = this.Update([ "Vrr" ], fun o -> { o with Vrr = v; VrrChosen = true })
    member _.Hdr with get () = (options ()).Hdr and set v = this.Update([ "Hdr" ], fun o -> { o with Hdr = v })
    member _.StillPixels with get () = (options ()).StillPixels and set v = this.Update([ "StillPixels" ], fun o -> { o with StillPixels = v })
    member _.LimitToScreen with get () = (options ()).LimitToScreen and set v = this.Update([ "LimitToScreen" ], fun o -> { o with LimitToScreen = v })
    member _.MultiDisplay with get () = (options ()).MultiDisplay and set v = this.Update([ "MultiDisplay" ], fun o -> { o with MultiDisplay = v })
    member _.FpsCounter with get () = (options ()).FpsCounter and set v = this.Update([ "FpsCounter" ], fun o -> { o with FpsCounter = v })
    member _.FpsCounterModes = counterNames
    member _.FpsCounterModeIndex with get () = (if (options ()).FpsCounterAdvanced then 1 else 0) and set v = this.Update([ "FpsCounterModeIndex" ], fun o -> { o with FpsCounterAdvanced = (v = 1) })
    member _.FpsCounterSize with get () = float (options ()).FpsCounterSize and set (v: float) = this.Update([ "FpsCounterSize" ], fun o -> { o with FpsCounterSize = int (Math.Round v) })

    // ---------------------------------------------------------------------
    // Advanced
    // ---------------------------------------------------------------------

    member _.AdvancedOpen
        with get () = prefs.AdvancedOpen
        and set v =
            prefs <- { prefs with AdvancedOpen = v }
            persist ()
            notify [ "AdvancedOpen" ]

    member _.QueueTargets = queueNames
    member _.SyncModes = syncNames
    member _.MotionSources = motionNames
    member _.XessMotionSources = xessMotionNames
    member _.SuiteMotionSources = suiteMotionNames
    member _.FsrMotionSources = fsrMotionNames
    member _.MsmSearches = searchNames
    member _.CaptureApis = captureNames
    member _.WindowModes = windowNames
    member _.Countdowns = countdownNames
    member _.GpuPriorities = priorityNames
    member _.GameFpsChoices = gameFpsNames
    member _.Hotkeys = hotkeys |> Array.map fst

    member _.LockRefresh with get () = (options ()).LockRefresh and set v = this.Update([ "LockRefresh" ], fun o -> { o with LockRefresh = v })
    member _.QueueTargetIndex with get () = (options ()).QueueTarget and set v = this.Update([ "QueueTargetIndex" ], fun o -> { o with QueueTarget = v })

    /// 0 is off; anything below 20 is off.
    member _.FrameLimit
        with get () = float (options ()).FrameLimit
        and set (v: float) = this.Update([ "FrameLimit"; "FrameLimitText" ], fun o -> { o with FrameLimit = int (Math.Round v) })
    member _.FrameLimitText = let l = (options ()).FrameLimit in if l = 0 then "Off" else sprintf "%d fps" l

    member _.GameFpsIndex
        with get () = Array.IndexOf(MotionPrefs.gameFpsRates, (options ()).GameFps) |> max 0
        and set v = if v >= 0 && v < MotionPrefs.gameFpsRates.Length then this.Update([ "GameFpsIndex" ], fun o -> { o with GameFps = MotionPrefs.gameFpsRates[v] })

    member _.SyncModeIndex with get () = (options ()).SyncMode and set v = this.Update([ "SyncModeIndex" ], fun o -> { o with SyncMode = v })
    member _.Bloom with get () = float (options ()).Bloom and set (v: float) = this.Update([ "Bloom" ], fun o -> { o with Bloom = int (Math.Round v) })
    member _.BloomWide with get () = float (options ()).BloomWide and set (v: float) = this.Update([ "BloomWide" ], fun o -> { o with BloomWide = int (Math.Round v) })
    member _.MotionSourceIndex with get () = (options ()).MotionSource and set v = this.Update([ "MotionSourceIndex" ], fun o -> { o with MotionSource = v })
    member _.XessMotionSourceIndex with get () = (options ()).XessMotionSource and set v = this.Update([ "XessMotionSourceIndex" ], fun o -> { o with XessMotionSource = v })
    member _.SuiteMotionSourceIndex with get () = (options ()).SuiteMotionSource and set v = this.Update([ "SuiteMotionSourceIndex" ], fun o -> { o with SuiteMotionSource = v })
    member _.FsrMotionSourceIndex with get () = (options ()).FsrMotionSource and set v = this.Update([ "FsrMotionSourceIndex" ], fun o -> { o with FsrMotionSource = v })
    member _.MsmSearchIndex with get () = (options ()).MsmSearch and set v = this.Update([ "MsmSearchIndex" ], fun o -> { o with MsmSearch = v })
    member _.CaptureApiIndex with get () = (options ()).CaptureApi and set v = this.Update([ "CaptureApiIndex" ], fun o -> { o with CaptureApi = v })
    member _.WindowModeIndex with get () = (options ()).WindowMode and set v = this.Update([ "WindowModeIndex" ], fun o -> { o with WindowMode = v })
    member _.CountdownIndex with get () = (options ()).Countdown and set v = this.Update([ "CountdownIndex" ], fun o -> { o with Countdown = v })
    member _.Spout with get () = (options ()).Spout and set v = this.Update([ "Spout" ], fun o -> { o with Spout = v })
    member _.GpuPriorityIndex with get () = (options ()).GpuPriority and set v = this.Update([ "GpuPriorityIndex" ], fun o -> { o with GpuPriority = v })

    /// "Auto", then the cards the engine found.
    member _.GpuChoices = gpuChoices
    member _.CardsText =
        if host.Cards.Length = 0 then "" else
        host.Cards |> Array.map (fun c -> c.Name + (if c.Dlss then " (DLSS MFG)" else "")) |> String.concat ", "

    member _.GpuIndex
        with get () =
            let name = (options ()).Gpu
            if name = "" then 0 else Array.IndexOf(gpuChoices, name) |> max 0
        and set v =
            // (A list handed over anew puts its box back on -1 for a moment: that is not a choice.)
            if not swappingLists && v >= 0 && v < gpuChoices.Length then
                this.Update([ "GpuIndex" ], fun o -> { o with Gpu = (if v = 0 then "" else gpuChoices[v]) })

    member private this.SetCrop(value: string, put: MotionOptions -> int -> MotionOptions, name: string) =
        let text = if isNull value then "" else value.Trim()
        match (if text = "" then Some 0 else match Int32.TryParse text with | true, n when n >= 0 -> Some(min n MotionPrefs.cropMax) | _ -> None) with
        | Some n -> this.Update([ name ], fun o -> put o n)
        | None -> notify [ name ]

    member _.CropLeft with get () = string (options ()).CropLeft and set (v: string) = this.SetCrop(v, (fun o n -> { o with CropLeft = n }), "CropLeft")
    member _.CropTop with get () = string (options ()).CropTop and set (v: string) = this.SetCrop(v, (fun o n -> { o with CropTop = n }), "CropTop")
    member _.CropRight with get () = string (options ()).CropRight and set (v: string) = this.SetCrop(v, (fun o n -> { o with CropRight = n }), "CropRight")
    member _.CropBottom with get () = string (options ()).CropBottom and set (v: string) = this.SetCrop(v, (fun o n -> { o with CropBottom = n }), "CropBottom")

    member _.CursorClip with get () = (options ()).CursorClip and set v = this.Update([ "CursorClip" ], fun o -> { o with CursorClip = v })
    member _.CursorSpeed with get () = (options ()).CursorSpeed and set v = this.Update([ "CursorSpeed" ], fun o -> { o with CursorSpeed = v })
    member _.CursorHide with get () = (options ()).CursorHide and set v = this.Update([ "CursorHide" ], fun o -> { o with CursorHide = v })
    member _.CursorScale with get () = (options ()).CursorScale and set v = this.Update([ "CursorScale" ], fun o -> { o with CursorScale = v })

    member _.HotkeyIndex
        with get () = hotkeys |> Array.tryFindIndex (fun (_, k) -> k = effectiveHotkey ()) |> Option.defaultValue 0
        and set v =
            if v >= 0 && v < hotkeys.Length then
                prefs <- { prefs with Hotkey = snd hotkeys[v] }
                persist ()
                hotkey.Set(effectiveHotkey ())
                notify [ "HotkeyIndex" ]

    /// The engine keeps engine.log (taken at the next start).
    member _.EngineLog
        with get () = prefs.EngineLog
        and set v =
            prefs <- { prefs with EngineLog = v }
            persist ()
            notify [ "EngineLog" ]

    member _.OpenLogFolder() =
        try
            IO.Directory.CreateDirectory(MotionFiles.engineDataDir ()) |> ignore
            Diagnostics.Process.Start(Diagnostics.ProcessStartInfo("explorer.exe", "\"" + MotionFiles.engineDataDir () + "\"", UseShellExecute = true)) |> ignore
        with _ -> ()

    // ---------------------------------------------------------------------
    // The generators' files
    // ---------------------------------------------------------------------

    member _.HasStreamline = MotionFiles.hasStreamline ()
    member _.HasXessFiles = MotionFiles.hasXess ()
    member _.IsAiInstalled = MotionAi.isInstalled ()
    member _.IsAiBusy = aiBusy
    member _.AiProgress = aiProgress * 100.0
    member _.AiText = aiText
    member _.AiSizeText = sprintf "about %.0f MB" (float MotionAi.totalBytes / 1048576.0)

    /// The picked generator still needs files before it can make frames.
    member _.NeedsFiles =
        match (options ()).Generator with
        | 0 -> not (MotionFiles.hasStreamline ())
        | 1 -> not (MotionFiles.hasXess ())
        | 3 -> not (MotionAi.isInstalled ())
        | 5 -> not (MotionFiles.hasFidelityFx ())
        | _ -> false

    member _.FilesText =
        match (options ()).Generator with
        | 0 when MotionFiles.hasStreamline () ->
            "DLSS MFG runs through NVIDIA Streamline, installed with SUITE (its engine folder). It needs an NVIDIA RTX 40 card (x2) or RTX 50 card (x2 to x6), and a current NVIDIA driver."
        | 0 ->
            sprintf "DLSS MFG needs NVIDIA Streamline in SUITE's engine folder, and %s is missing. Reinstall SUITE to put it back."
                (MotionFiles.missingStreamline System.IO.File.Exists (MotionFiles.streamlineDir ())).Head
        | 1 when MotionFiles.hasXess () -> "XeSS Frame Generation's libraries are in place."
        | 1 -> "XeSS MFG needs libxess_fg.dll and libxell.dll from Intel's XeSS SDK (2.1 or later). Pick both."
        | 3 when MotionAi.isInstalled () -> "SMAI's runtime and network are installed."
        | 3 -> "SMAI needs ONNX Runtime, DirectML and its network - a one-time download."
        | 5 when MotionFiles.hasFidelityFx () ->
            "FSR FG runs through AMD's FidelityFX runtime (SDK 1.1.4), installed with SUITE (its engine folder). Any DirectX 12 card."
        | 5 -> sprintf "FSR FG needs AMD's FidelityFX runtime in SUITE's engine folder, and %s is missing. Reinstall SUITE to put it back." MotionFiles.FidelityFxFile
        | _ -> ""

    /// Starting is possible when the engine is there; a generator that still
    /// needs files is started anyway (the engine shows the game as it is and
    /// says why), but the page says so first.
    member _.CanStart = MotionFiles.isEngineAvailable ()

    member private this.FilesChanged(message: string) =
        note <- message
        notify [ "HasStreamline"; "HasXessFiles"; "IsAiInstalled"; "NeedsFiles"; "FilesText"; "StatusNote"; "HasStatusNote" ]

    member this.AcceptXess(paths: string list) =
        match MotionFiles.pickXess paths with
        | Ok m ->
            this.FilesChanged m
            // Intel's library can now say which cards it runs on.
            Task.Run(fun () -> let c = host.ReloadCards() in Dispatcher.UIThread.Post(fun () -> this.TakeCards c)) |> ignore
        | Error m -> this.FilesChanged m

    member this.RemoveXess() =
        MotionFiles.removeXess ()
        this.FilesChanged "XeSS's libraries were removed from SUITE's folder."

    member this.InstallAi() =
        if not aiBusy then
            aiBusy <- true
            aiProgress <- 0.0
            aiText <- "Starting the download…"
            notify [ "IsAiBusy"; "AiProgress"; "AiText" ]
            let progress (text: string) (fraction: float) =
                Dispatcher.UIThread.Post(fun () ->
                    aiText <- text
                    aiProgress <- Math.Clamp(fraction, 0.0, 1.0)
                    notify [ "AiProgress"; "AiText" ])
            task {
                let! result = MotionAi.install progress
                Dispatcher.UIThread.Post(fun () ->
                    aiBusy <- false
                    match result with
                    | Ok() -> aiText <- "SMAI is ready."
                    | Error e -> aiText <- "The download did not finish: " + e
                    notify [ "IsAiBusy"; "AiText" ]
                    this.FilesChanged "")
            }
            |> ignore

    member this.RemoveAi() =
        if not aiBusy then
            MotionAi.remove ()
            aiText <- ""
            notify [ "AiText" ]
            this.FilesChanged "SMAI's files were removed."

    // ---------------------------------------------------------------------
    // Learning cache
    // ---------------------------------------------------------------------

    member _.Learned = learned
    member _.HasLearned = learned.Count > 0
    member _.NoLearned = learned.Count = 0

    /// Deleted here and, when the engine is playing that game, forgotten there too.
    member _.ForgetGame(row: LearnedRowViewModel) =
        host.Forget row.Game.Game
        LearnedGames.delete row.Game
        refreshLearned ()

    member _.RefreshLearned() = refreshLearned ()

    member _.Host = host
