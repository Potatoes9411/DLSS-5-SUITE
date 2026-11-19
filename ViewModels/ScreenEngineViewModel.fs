namespace DLSS_5_MANAGER.ViewModels

open System
open System.IO
open System.IO.Compression
open Avalonia.Threading
open DLSS_5_MANAGER.Services
open DLSS_5_MANAGER.Services.ScreenEngine

/// The Screen Engine card: DLSS 5 over the whole screen or one window.
///
/// Every change is saved straight away. A running engine only reads its
/// options when it starts, so changes made while it runs wait for Apply,
/// which restarts it; restarting on every slider tick would black the screen
/// out for each one.
type ScreenEngineViewModel() as this =
    inherit ViewModelBase()

    let host = new Host()
    let nsHost = new NeuralScreen.Host()
    let robloxHost = new RobloxShadeHost.Host()
    let gpuName, gpuTier = NeuralScreen.detect ()

    // Which program runs DLSS 5 over the screen. The Screen Engine needs an
    // RTX 50 card; NeuralScreen covers RTX 30/40 (and 50, as a second choice).
    let canScreenEngine = (gpuTier = NeuralScreen.Rtx50) && isAvailable ()
    let cardRunsNeuralScreen = (match gpuTier with NeuralScreen.Rtx50 | NeuralScreen.RtxOlder _ -> true | _ -> false)
    let mutable canNeuralScreen = cardRunsNeuralScreen && NeuralScreen.isAvailable ()
    let methodPath () = Path.Combine(NeuralScreen.dataDir (), "method.txt")
    let savedMethod = (try File.ReadAllText(methodPath ()).Trim() with _ -> "")
    let mutable useNeuralScreen =
        if savedMethod = "neuralscreen" && canNeuralScreen then true
        elif savedMethod = "engine" && canScreenEngine then false
        else not canScreenEngine && canNeuralScreen
    // RobloxShadeHost is Roblox's own method now, started from its card in
    // Settings rather than chosen here, so this page never selects it.
    let mutable useRobloxShadeHost = false
    let mutable robloxStatus =
        if RobloxShadeHost.isAvailable () then "Ready. Start it, then open Roblox; it waits until Roblox is available."
        else "RobloxShadeHost is not included in this SUITE build."
    let mutable settings = load ()
    let mutable extras = NeuralScreen.loadExtras ()
    /// The game whose own settings are in use, as (library id, title); None
    /// for the shared defaults. Every change is saved to whichever is active.
    let mutable presetGame: (string * string) option = None
    // What local tone was before the colours were locked, so turning the lock
    // off puts the picture back where the user had it rather than at a default.
    let mutable toneBeforeColourLock = 1.0
    let mutable monitors: MonitorEntry list = []
    let mutable windows: WindowEntry list = []
    let mutable status = ""
    let mutable hasPendingChanges = false
    let mutable isAdvancedOpen = false
    let mutable isRecording = false
    let mutable selectedSweepIndex = 0
    let mutable sweepValues = 5.0
    let mutable addonProgress = 0.0
    let mutable addonStatus = ""
    let mutable isInstallingAddon = false
    let mutable pickerText = "Click, then Alt+Tab and click a window — or drag the crosshair onto it"

    // Changes apply by themselves. A running engine only reads its options at
    // start, so each change restarts it - but only once the settings have been
    // still for a moment, so dragging a slider restarts it once, not per tick.
    let applyTimer = DispatcherTimer(Interval = TimeSpan.FromMilliseconds(600.0))

    // Which DLSS Frame Generation NVIDIA frame gen runs (Services/DlssgVersions.fs, shared with Motion Forge).
    // The engine reads it at start, so a running engine with NVIDIA frame gen on is restarted.
    let dlssg =
        DlssgPickerViewModel(DlssgVersions.ScreenEngine, fun () ->
            if host.IsRunning && settings.Generator = NvidiaDlssg then
                applyTimer.Stop()
                applyTimer.Start())

    let styles = [ Standard, "Standard"; Natural, "Natural"; Cinematic, "Cinematic" ]
    let frameGens = [ 1, "Off"; 2, "2x"; 3, "3x"; 4, "4x"; 5, "5x"; 6, "6x" ]
    let generators =
        [ OwnBlend, "SUITE blend (any GPU)"
          NvidiaDlssg, "NVIDIA DLSS Frame Generation"
          Smfg, "SMFG (Motion Forge, any GPU)"
          Smt, "SMT triangle mesh (Motion Forge)"
          Smai, "SMAI neural network (DirectML)"
          XessMfg, "Intel XeSS MFG"
          Fsr31, "AMD FSR 3.1 Frame Generation"
          Memc, "SUITE MEMC block matching (any GPU)"
          Dis, "SUITE DIS optical flow (any GPU)"
          Spacewarp, "SUITE Spacewarp extrapolation (any GPU)"
          MotionBlur, "Motion-blur blend (any GPU)"
          Crossfade, "Crossfade blend (any GPU)"
          RepeatFrames, "Repeat - no frame generation (compare)" ]
    let smaiModels = [ false, "SMFG Net (Motion Forge's own)"; true, "RIFE 4.25 lite" ]
    let mutable smaiProgress = ""
    let mutable isGettingSmai = false
    let superResolutions = [ SrAuto, "Automatic"; SrDlaa, "DLAA (native resolution)"; SrOff, "Off" ]
    let compares = [ CompareOff, "Off"; CompareSplit, "Split view"; CompareOriginal, "Original only" ]
    let motionBackends = [ "cpu", "CPU (works everywhere)"; "nvofa", "NVIDIA Optical Flow" ]

    let indexIn (choices: ('a * string) list) (value: 'a) =
        choices |> List.tryFindIndex (fun (v, _) -> v = value) |> Option.defaultValue 0

    let valueAt (choices: ('a * string) list) (index: int) =
        if index >= 0 && index < choices.Length then Some(fst choices.[index]) else None

    let describe (i: int) (m: MonitorEntry) =
        sprintf "Monitor %d: %dx%d%s" (i + 1) m.Width m.Height (if m.IsPrimary then " (primary)" else "")

    do
        // The engine is a separate process and would otherwise keep running
        // after SUITE closes, including the Environment.Exit before an update,
        // where its locked executable would break the install.
        AppDomain.CurrentDomain.ProcessExit.Add(fun _ -> host.Stop(); nsHost.Stop(); robloxHost.Stop())
        // MOTION FORGE: one overlay at a time - Motion Forge starting stops this page's engines (Services/OverlayArbiter.fs).
        OverlayArbiter.register "screen" (fun () ->
            let running = this.IsRunning
            if running then Dispatcher.UIThread.Post(fun () -> this.Stop())
            running)
        monitors <- listMonitors ()
        windows <- listVisibleWindows ()

        applyTimer.Tick.Add(fun _ ->
            applyTimer.Stop()
            if this.IsRunning then this.Start())

        let onState (message: string) =
            Dispatcher.UIThread.Post(fun () ->
                status <- message
                if not (message.StartsWith("Running", StringComparison.OrdinalIgnoreCase)) then
                    isRecording <- false
                    this.RaisePropertyChanged("IsRecording")
                    this.RaisePropertyChanged("RecordButtonText")
                this.RaisePropertyChanged("Status")
                this.RaisePropertyChanged("IsRunning")
                this.RaisePropertyChanged("CanStart"))
        host.StateChanged.Add onState
        nsHost.StateChanged.Add onState
        robloxHost.StateChanged.Add(fun message ->
            Dispatcher.UIThread.Post(fun () ->
                robloxStatus <- message
                for n in [ "RobloxShadeHostStatus"; "IsRobloxShadeHostRunning"; "CanStartRobloxShadeHost" ] do
                    this.RaisePropertyChanged(n)))
        nsHost.RecordingChanged.Add(fun active ->
            Dispatcher.UIThread.Post(fun () ->
                isRecording <- active
                this.RaisePropertyChanged("IsRecording")
                this.RaisePropertyChanged("RecordButtonText")))

    member _.IsAvailable =
        if useRobloxShadeHost then RobloxShadeHost.isAvailable ()
        elif useNeuralScreen then canNeuralScreen
        else canScreenEngine

    // ----- method: Screen Engine or NeuralScreen -----
    member _.GpuName = if gpuName = "" then "No graphics card found" else gpuName
    member _.CanUseScreenEngine = canScreenEngine
    member _.CanUseNeuralScreen = canNeuralScreen
    member _.ScreenEngineLocked = not canScreenEngine
    member _.NeuralScreenLocked = not canNeuralScreen
    member _.IsNeuralScreen = useNeuralScreen && not useRobloxShadeHost
    member _.IsScreenEngine = not useNeuralScreen && not useRobloxShadeHost
    member _.IsRobloxShadeHost = useRobloxShadeHost
    member _.CanUseRobloxShadeHost = RobloxShadeHost.isAvailable ()
    member _.RobloxShadeHostLocked = not (RobloxShadeHost.isAvailable ())

    member _.ScreenEngineTip =
        if canScreenEngine then "DLSS 5 through SUITE's Screen Engine. RTX 50 series."
        elif not (isAvailable ()) then "The Screen Engine is not included in this build."
        else "The Screen Engine needs an RTX 50 series card. Use NeuralScreen instead."

    member _.NeuralScreenTip =
        match gpuTier with
        | _ when canNeuralScreen -> "DLSS 5 through NeuralScreen. RTX 30/40 series, and 50 series as an alternative."
        | NeuralScreen.Rtx20 -> "RTX 20 series cards cannot run the DLSS 5 model, with either method."
        | NeuralScreen.NoRtx -> "DLSS 5 needs an NVIDIA RTX 30, 40 or 50 series card."
        | _ -> "NeuralScreen is a separate download. Install the add-on to use it."

    // ----- RobloxShadeHost, Roblox's own card -----
    member _.RobloxShadeHostStatus = robloxStatus
    member _.IsRobloxShadeHostRunning = robloxHost.IsRunning
    member _.CanStartRobloxShadeHost = RobloxShadeHost.isAvailable () && not robloxHost.IsRunning

    /// Starts the host beside Roblox. It captures the screen as the Screen
    /// Engine does, so it takes the screen overlay's turn and stops that first.
    member this.StartRobloxShadeHost() =
        if this.IsRunning && not robloxHost.IsRunning then this.Stop()
        OverlayArbiter.claim "screen" |> ignore
        if not (robloxHost.Start()) && not robloxHost.IsRunning then
            robloxStatus <- "RobloxShadeHost is not included in this SUITE build."
        for n in [ "RobloxShadeHostStatus"; "IsRobloxShadeHostRunning"; "CanStartRobloxShadeHost"; "IsRunning"; "CanStart" ] do
            this.RaisePropertyChanged(n)

    member this.StopRobloxShadeHost() =
        robloxHost.Stop()
        for n in [ "RobloxShadeHostStatus"; "IsRobloxShadeHostRunning"; "CanStartRobloxShadeHost"; "IsRunning"; "CanStart" ] do
            this.RaisePropertyChanged(n)

    member _.RobloxShadeHostTip =
        if RobloxShadeHost.isAvailable () then
            "RobloxShadeHost captures Roblox externally and runs ReShade beside it."
        else
            "Install the optional RobloxShadeHost method to process Roblox through SUITE."

    member _.AddonProgress = addonProgress
    member _.AddonStatus = addonStatus
    member _.IsInstallingAddon = isInstallingAddon

    /// Fetches the add-on from the release this build belongs to, checks it
    /// and unpacks it, without sending anyone to a browser.
    member this.DownloadNeuralScreenAddon() =
        if not isInstallingAddon then
            isInstallingAddon <- true
            addonProgress <- 0.0
            addonStatus <- "Looking up the NeuralScreen add-on..."
            for name in [ "IsInstallingAddon"; "AddonProgress"; "AddonStatus" ] do
                this.RaisePropertyChanged(name)

            let report (message: string) (percent: float) =
                Dispatcher.UIThread.Post(fun () ->
                    addonStatus <- message
                    addonProgress <- Math.Clamp(percent, 0.0, 100.0)
                    this.RaisePropertyChanged("AddonStatus")
                    this.RaisePropertyChanged("AddonProgress"))

            async {
                let! outcome =
                    NeuralScreen.Addon.install report
                    |> Async.AwaitTask
                    |> Async.Catch
                Dispatcher.UIThread.Post(fun () ->
                    isInstallingAddon <- false
                    match outcome with
                    | Choice1Of2 () ->
                        canNeuralScreen <- cardRunsNeuralScreen && NeuralScreen.isAvailable ()
                        addonStatus <- "NeuralScreen add-on installed."
                        addonProgress <- 100.0
                    | Choice2Of2 error ->
                        addonStatus <- "The add-on could not be installed: " + error.Message
                        addonProgress <- 0.0
                    for name in
                        [ "IsInstallingAddon"; "AddonProgress"; "AddonStatus"; "CanUseNeuralScreen"; "NeuralScreenLocked"
                          "NeuralScreenNeedsInstall"; "NeuralScreenTip"; "MethodNote"; "IsAvailable"; "CanStart"; "Status" ] do
                        this.RaisePropertyChanged(name))
            }
            |> Async.Start

    /// The card can run it, but the add-on is not installed: it is a separate
    /// download because of its size and its own NVIDIA runtimes.
    member _.NeuralScreenNeedsInstall = cardRunsNeuralScreen && not canNeuralScreen

    member _.NeuralScreenInstallFolder = NeuralScreen.folder ()

    /// Unpacks "DLSS 5 SUITE NeuralScreen Add-on.zip" into mod files.
    member this.InstallNeuralScreenAddon(zipPath: string) =
        try
            let target = NeuralScreen.installRoot ()
            Directory.CreateDirectory(target) |> ignore
            status <- "Installing the NeuralScreen add-on..."
            this.RaisePropertyChanged("Status")
            ZipFile.ExtractToDirectory(zipPath, target, true)
            canNeuralScreen <- cardRunsNeuralScreen && NeuralScreen.isAvailable ()
            status <-
                if canNeuralScreen then "NeuralScreen add-on installed."
                else "That archive does not hold the NeuralScreen add-on."
        with ex ->
            status <- "The NeuralScreen add-on could not be installed: " + ex.Message
        for name in [ "Status"; "CanUseNeuralScreen"; "NeuralScreenLocked"; "NeuralScreenNeedsInstall"; "NeuralScreenTip"; "MethodNote"; "IsAvailable"; "CanStart" ] do
            this.RaisePropertyChanged(name)

    member _.MethodNote =
        if useRobloxShadeHost then
            "RobloxShadeHost is selected. Start it from SUITE, then open Roblox; it waits safely until Roblox is available."
        else
            match gpuTier with
            | NeuralScreen.Rtx50 -> "RTX 50 series detected: both methods work."
            | NeuralScreen.RtxOlder s when canNeuralScreen -> sprintf "RTX %d series detected: NeuralScreen is used. The Screen Engine is RTX 50 series only." s
            | NeuralScreen.RtxOlder s -> sprintf "RTX %d series detected: DLSS 5 needs the NeuralScreen add-on, a separate download." s
            | NeuralScreen.Rtx20 -> "RTX 20 series detected: this card cannot run DLSS 5."
            | NeuralScreen.NoRtx -> "No RTX card detected: DLSS 5 needs an RTX 30, 40 or 50 series card."

    member this.SelectMethod(neuralScreen: bool) =
        let allowed = if neuralScreen then canNeuralScreen else canScreenEngine
        if allowed && neuralScreen <> useNeuralScreen || (allowed && useRobloxShadeHost) then
            let wasRunning = this.IsRunning
            this.Stop()
            useRobloxShadeHost <- false
            useNeuralScreen <- neuralScreen
            (try
                Directory.CreateDirectory(NeuralScreen.dataDir ()) |> ignore
                File.WriteAllText(methodPath (), if neuralScreen then "neuralscreen" else "engine")
             with _ -> ())
            for name in [ "IsNeuralScreen"; "IsScreenEngine"; "IsRobloxShadeHost"; "IsAvailable"; "CanStart"; "Status"; "IsNvidiaGenerator"; "IsForgeGenerator" ] do
                this.RaisePropertyChanged(name)
            if wasRunning then this.Start()

    member this.SelectRobloxShadeHost() =
        if RobloxShadeHost.isAvailable () && not useRobloxShadeHost then
            let wasRunning = this.IsRunning
            this.Stop()
            useRobloxShadeHost <- true
            useNeuralScreen <- false
            (try
                Directory.CreateDirectory(NeuralScreen.dataDir ()) |> ignore
                File.WriteAllText(methodPath (), "robloxshadehost")
             with _ -> ())
            for name in [ "IsNeuralScreen"; "IsScreenEngine"; "IsRobloxShadeHost"; "IsAvailable"; "CanStart"; "Status"; "IsNvidiaGenerator"; "IsForgeGenerator" ] do
                this.RaisePropertyChanged(name)
            if wasRunning then this.Start()

    member this.Status =
        if status <> "" then status
        elif not this.IsAvailable then (if useNeuralScreen then this.NeuralScreenTip else this.ScreenEngineTip)
        else "Stopped"

    member _.IsRunning = host.IsRunning || nsHost.IsRunning || robloxHost.IsRunning

    /// Num2 in NeuralScreen: SUITE's control window, as an overlay.
    [<CLIEvent>]
    member _.ControlsRequested = nsHost.ControlsRequested
    member this.CanStart = this.IsAvailable && not this.IsRunning
    member _.HasPendingChanges = hasPendingChanges
    member _.IsRecording = isRecording
    member _.RecordButtonText = if isRecording then "Stop recording" else "Record video"
    member this.IsAdvancedOpen
        with get () = isAdvancedOpen
        and set value = this.SetProperty(&isAdvancedOpen, value) |> ignore

    member private this.Change(updated: Settings) =
        let previous = settings
        settings <- normalize updated
        this.Persist()
        if nsHost.IsRunning then this.PushToNeuralScreen(previous, settings)
        // The engine takes the model's own settings live. Only a change it
        // cannot take that way - a different source, output or capture option -
        // is worth restarting the session for.
        elif host.IsRunning && liveFieldsOnly previous settings && host.Tune(previous, settings) then ()
        else this.ScheduleApply()

    /// NeuralScreen takes most changes live, the way its own menu sends them;
    /// only a different monitor needs a restart.
    member private this.PushToNeuralScreen(before: Settings, after: Settings) =
        if before.Source <> after.Source then this.ScheduleApply()
        else
            if before.Intensity <> after.Intensity then nsHost.SetParam("intensity", after.Intensity) |> ignore
            if before.LocalTone <> after.LocalTone then nsHost.SetParam("local_tone", after.LocalTone) |> ignore
            if before.LocalStructure <> after.LocalStructure then nsHost.SetParam("local_structure", after.LocalStructure) |> ignore
            if before.Skin <> after.Skin then nsHost.SetParam("skin_structure", NeuralScreen.skinOf after.Skin) |> ignore
            if before.Style <> after.Style then nsHost.SetStyle(NeuralScreen.styleOf after.Style) |> ignore
            if before.Passes <> after.Passes then nsHost.SetPasses(after.Passes) |> ignore
            if before.Compare <> after.Compare then nsHost.SetSplit(NeuralScreen.splitOf after.Compare) |> ignore
            if before.NeuralRendering <> after.NeuralRendering then nsHost.ToggleNeuralRendering() |> ignore
            if before.Window <> after.Window then
                let text = after.Window.Trim()
                if text = "" then nsHost.CaptureWindow 0n |> ignore
                elif text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) then
                    match Int64.TryParse(text.Substring(2), Globalization.NumberStyles.HexNumber, null) with
                    | true, handle -> nsHost.CaptureWindow(nativeint handle) |> ignore
                    | _ -> ()

    // =====================================================================
    // NEURALSCREEN'S OWN CONTROLS
    // =====================================================================
    /// Saved straight away, and handed to a running NeuralScreen the way its
    /// own menu would. Nothing here reaches the Screen Engine, which has no
    /// equivalent for them.
    member private this.ChangeExtras(updated: NeuralScreen.Extras) =
        let before = extras
        extras <- NeuralScreen.normalizeExtras updated
        this.PersistExtras()
        if useNeuralScreen && nsHost.IsRunning then
            if before.Boost <> extras.Boost then nsHost.ToggleBoost() |> ignore
            if before.WorkScale <> extras.WorkScale then nsHost.SetWorkScale(extras.WorkScale) |> ignore
            if before.SkipStatic <> extras.SkipStatic then nsHost.ToggleSkipStatic() |> ignore
            if before.Hdr <> extras.Hdr then nsHost.ToggleHdr() |> ignore
            if before.Spout <> extras.Spout then nsHost.ToggleSpout() |> ignore
            if before.FrameGeneration <> extras.FrameGeneration then nsHost.ToggleFrameGeneration() |> ignore
            if before.FrameMultiplier <> extras.FrameMultiplier then nsHost.SetFrameMultiplier(extras.FrameMultiplier) |> ignore
            if before.RecordingIndicator <> extras.RecordingIndicator then nsHost.ToggleRecordingIndicator() |> ignore
            if before.MotionBackend <> extras.MotionBackend then nsHost.SetMotionBackend(extras.MotionBackend) |> ignore
            if before.Gpu <> extras.Gpu then nsHost.SetGpu(extras.Gpu) |> ignore
        for name in
            [ "Boost"; "WorkScale"; "WorkScaleText"; "WorkScalePercent"; "SkipStatic"; "Hdr"; "Spout"
              "FrameGeneration"; "FrameMultiplier"; "FrameMultiplierText"; "RecordingIndicator"
              "SelectedMotionBackendIndex"; "NeuralScreenGpu"; "NeuralScreenGpuText" ] do
            this.RaisePropertyChanged(name)

    member this.Boost
        with get () = extras.Boost
        and set (v: bool) = if v <> extras.Boost then this.ChangeExtras { extras with Boost = v }

    /// A fraction in the settings, a percentage in the window.
    member this.WorkScale
        with get () = extras.WorkScale * 100.0
        and set (v: float) =
            let scale = Math.Clamp(v, 10.0, 100.0) / 100.0
            if abs (scale - extras.WorkScale) > 1e-6 then this.ChangeExtras { extras with WorkScale = scale }

    member this.WorkScaleText
        with get () = sprintf "%.0f" (extras.WorkScale * 100.0)
        and set (text: string) =
            ScreenEngineViewModel.Parse text |> Option.iter (fun value -> this.WorkScale <- value)
            this.RaisePropertyChanged("WorkScaleText")

    member this.SkipStatic
        with get () = extras.SkipStatic
        and set (v: bool) = if v <> extras.SkipStatic then this.ChangeExtras { extras with SkipStatic = v }

    member this.Hdr
        with get () = extras.Hdr
        and set (v: bool) = if v <> extras.Hdr then this.ChangeExtras { extras with Hdr = v }

    member this.Spout
        with get () = extras.Spout
        and set (v: bool) = if v <> extras.Spout then this.ChangeExtras { extras with Spout = v }

    member this.FrameGeneration
        with get () = extras.FrameGeneration
        and set (v: bool) = if v <> extras.FrameGeneration then this.ChangeExtras { extras with FrameGeneration = v }

    member this.FrameMultiplier
        with get () = float extras.FrameMultiplier
        and set (v: float) =
            let times = int (Math.Round(Math.Clamp(v, 2.0, 4.0)))
            if times <> extras.FrameMultiplier then this.ChangeExtras { extras with FrameMultiplier = times }

    member this.FrameMultiplierText
        with get () = string extras.FrameMultiplier
        and set (text: string) =
            ScreenEngineViewModel.Parse text |> Option.iter (fun value -> this.FrameMultiplier <- value)
            this.RaisePropertyChanged("FrameMultiplierText")

    member this.RecordingIndicator
        with get () = extras.RecordingIndicator
        and set (v: bool) = if v <> extras.RecordingIndicator then this.ChangeExtras { extras with RecordingIndicator = v }

    member _.MotionBackendOptions = motionBackends |> List.map snd

    member this.SelectedMotionBackendIndex
        with get () = indexIn motionBackends extras.MotionBackend
        and set (index: int) =
            valueAt motionBackends index
            |> Option.iter (fun backend -> if backend <> extras.MotionBackend then this.ChangeExtras { extras with MotionBackend = backend })

    /// The adapter NeuralScreen renders on, by index. Left at 0 unless a
    /// machine hands it the wrong card.
    member this.NeuralScreenGpu
        with get () = float extras.Gpu
        and set (v: float) =
            let index = int (Math.Round(Math.Clamp(v, 0.0, 7.0)))
            if index <> extras.Gpu then this.ChangeExtras { extras with Gpu = index }

    member this.NeuralScreenGpuText
        with get () = string extras.Gpu
        and set (text: string) =
            ScreenEngineViewModel.Parse text |> Option.iter (fun value -> this.NeuralScreenGpu <- value)
            this.RaisePropertyChanged("NeuralScreenGpuText")

    member private this.ScheduleApply() =
        if this.IsRunning then
            applyTimer.Stop()
            applyTimer.Start()

    // ----- source and output -----
    member this.RefreshMonitors() =
        monitors <- listMonitors ()
        this.RaisePropertyChanged("SourceOptions")
        this.RaisePropertyChanged("TargetOptions")
        this.RaisePropertyChanged("SelectedSourceIndex")
        this.RaisePropertyChanged("SelectedTargetIndex")

    /// Primary, all, then each monitor by the engine's own numbering.
    member _.SourceOptions =
        [ yield "Primary monitor"
          yield "All monitors"
          yield! monitors |> List.mapi describe ]

    member _.SelectedSourceIndex
        with get () =
            match settings.Source with
            | PrimaryMonitor -> 0
            | AllMonitors -> 1
            | Monitor i -> i + 2
        and set (value: int) =
            let source =
                match value with
                | 0 -> Some PrimaryMonitor
                | 1 -> Some AllMonitors
                | i when i >= 2 -> Some(Monitor(i - 2))
                | _ -> None
            match source with
            | Some s when s <> settings.Source ->
                this.Change { settings with Source = s }
                this.RaisePropertyChanged("SelectedSourceIndex")
                this.RaisePropertyChanged("SelectedTargetIndex")
                this.RaisePropertyChanged("CanPickTarget")
            | _ -> ()

    member _.TargetOptions =
        [ yield "Same as the source"
          yield! monitors |> List.mapi describe ]

    /// The engine cannot show every monitor's picture on one other monitor.
    member _.CanPickTarget = settings.Source <> AllMonitors

    member _.SelectedTargetIndex
        with get () = match settings.Target with Some i -> i + 1 | None -> 0
        and set (value: int) =
            let target = if value >= 1 then Some(value - 1) else None
            if value >= 0 && target <> settings.Target then
                this.Change { settings with Target = target }
                this.RaisePropertyChanged("SelectedTargetIndex")

    member _.Window
        with get () = settings.Window
        and set (value: string) =
            let v = if isNull value then "" else value
            if v <> settings.Window then
                this.Change { settings with Window = v }
                this.RaisePropertyChanged("Window")

    member this.RefreshWindows() =
        windows <- listVisibleWindows ()
        this.RaisePropertyChanged("WindowOptions")
        this.RaisePropertyChanged("SelectedWindowIndex")

    /// What the crosshair is over while it is dragged, then what it picked.
    member _.PickerText = pickerText

    member this.ArmWindowPicker() =
        pickerText <- "Armed — Alt+Tab if needed, then click the window to capture"
        this.RaisePropertyChanged("PickerText")

    member this.CancelWindowPicker() =
        pickerText <- "Selection cancelled. Click the crosshair to try again."
        this.RaisePropertyChanged("PickerText")

    /// Called while the crosshair is dragged; the window is only taken when
    /// the button comes up, so passing over windows on the way changes nothing.
    member this.TrackWindowUnderCursor(take: bool) =
        let found = windowAtCursor ()
        pickerText <-
            match found, take with
            | Some entry, true -> "Capturing: " + entry.Title
            | Some entry, false -> "Release over: " + entry.Title
            | None, true -> "Nothing picked. Click the crosshair, then click another app's window."
            | None, false -> "Drag onto a window..."
        this.RaisePropertyChanged("PickerText")
        match found with
        | Some entry when take ->
            windows <- listVisibleWindows ()
            this.RaisePropertyChanged("WindowOptions")
            this.Window <- sprintf "0x%X" (uint64 (entry.Handle.ToInt64()))
            this.RaisePropertyChanged("SelectedWindowIndex")
        | _ -> ()

    /// Back to capturing the monitor.
    member this.ClearWindow() =
        this.Window <- ""
        pickerText <- "Capturing the monitor"
        this.RaisePropertyChanged("PickerText")
        this.RaisePropertyChanged("SelectedWindowIndex")

    member _.WindowOptions =
        [ yield "Choose a running window..."
          yield! windows |> List.map (fun entry -> entry.Title) ]

    member _.SelectedWindowIndex
        with get () =
            windows
            |> List.tryFindIndex (fun entry -> sprintf "0x%X" (uint64 (entry.Handle.ToInt64())) = settings.Window)
            |> Option.map ((+) 1)
            |> Option.defaultValue 0
        and set value =
            if value > 0 && value <= windows.Length then
                let selected = windows.[value - 1]
                let handle = sprintf "0x%X" (uint64 (selected.Handle.ToInt64()))
                if handle <> settings.Window then
                    this.Change { settings with Window = handle }
                    this.RaisePropertyChanged("Window")
                    this.RaisePropertyChanged("SelectedWindowIndex")

    // ----- DLSS 5 -----
    member _.NeuralRendering
        with get () = settings.NeuralRendering
        and set value =
            if value <> settings.NeuralRendering then
                this.Change { settings with NeuralRendering = value }
                this.RaisePropertyChanged("NeuralRendering")

    member _.Intensity
        with get () = settings.Intensity
        and set value =
            let value = Math.Clamp((if Double.IsFinite value then value else 1.0), 0.0, 1.0)
            if value <> settings.Intensity then
                this.Change { settings with Intensity = value }
                this.RaisePropertyChanged("Intensity")
                this.RaisePropertyChanged("IntensityText")


    member _.LocalStructure
        with get () = settings.LocalStructure
        and set value =
            if value <> settings.LocalStructure then
                this.Change { settings with LocalStructure = value }
                this.RaisePropertyChanged("LocalStructure")
                this.RaisePropertyChanged("LocalStructureText")
                this.RaisePropertyChanged("LocalStructureSlider")


    member _.LocalTone
        with get () = settings.LocalTone
        and set value =
            if value <> settings.LocalTone then
                this.Change { settings with LocalTone = value }
                this.RaisePropertyChanged("LocalTone")
                this.RaisePropertyChanged("LocalToneText")
                this.RaisePropertyChanged("LocalToneSlider")
                this.RaisePropertyChanged("KeepOriginalColours")
                this.RaisePropertyChanged("ColoursUnlocked")


    /// Local tone is the one control that changes colour: the model's own
    /// LocalToneStrength, which both methods pass to NVIDIA's DLL. At zero it
    /// leaves the picture's colours exactly as they came in, and style has
    /// nothing to act on either, so the lock reads as one switch.
    member _.KeepOriginalColours
        with get () = settings.LocalTone = 0.0
        and set (on: bool) =
            if on <> (settings.LocalTone = 0.0) then
                if on then
                    toneBeforeColourLock <- (if settings.LocalTone > 0.0 then settings.LocalTone else toneBeforeColourLock)
                    this.LocalTone <- 0.0
                else
                    this.LocalTone <- (if toneBeforeColourLock > 0.0 then toneBeforeColourLock else 1.0)
                this.RaisePropertyChanged("KeepOriginalColours")
                this.RaisePropertyChanged("ColoursUnlocked")

    /// The tone and style rows are dead while the colours are locked.
    member _.ColoursUnlocked = settings.LocalTone <> 0.0

    member _.Passes
        with get () = float settings.Passes
        and set (value: float) =
            let passes = int (Math.Round value)
            if passes <> settings.Passes then
                this.Change { settings with Passes = passes }
                this.RaisePropertyChanged("Passes")
                this.RaisePropertyChanged("PassesText")
                this.RaisePropertyChanged("PassesSlider")


    member _.AutoMask
        with get () = settings.AutoMask
        and set value =
            if value <> settings.AutoMask then
                this.Change { settings with AutoMask = value }
                this.RaisePropertyChanged("AutoMask")

    member _.StyleOptions = styles |> List.map snd

    member _.SelectedStyleIndex
        with get () = indexIn styles settings.Style
        and set (value: int) =
            match valueAt styles value with
            | Some s when s <> settings.Style ->
                this.Change { settings with Style = s }
                this.RaisePropertyChanged("SelectedStyleIndex")
            | _ -> ()

    member _.FrameGenOptions = frameGens |> List.map snd

    /// NVIDIA DLSS Frame Generation instead of SUITE's own; it restarts the overlay.
    /// The DLSS Frame Generation version picker (bundled, own file, or a release).
    member _.DlssgPicker = dlssg

    // ----- who makes the pictures between (Motion Forge's generators run inside the Screen Engine) -----
    member _.GeneratorOptions = generators |> List.map snd

    member this.SelectedGeneratorIndex
        with get () = indexIn generators settings.Generator
        and set (value: int) =
            match valueAt generators value with
            | Some g when g <> settings.Generator ->
                this.Change { settings with Generator = g }
                this.RaiseGeneratorProperties()
            | _ -> ()

    member this.IsNvidiaGenerator = this.IsScreenEngine && settings.Generator = NvidiaDlssg
    member this.IsForgeGenerator = this.IsScreenEngine && FrameGenerator.isForge settings.Generator
    /// SMFG and SMT look for the motion at a share of the picture; SMAI keeps SMFG's still pixels too.
    member _.UsesFlow = FrameGenerator.usesFlow settings.Generator
    member _.UsesKeepStill = (match settings.Generator with Smfg | Smt | Smai -> true | _ -> false)
    member _.IsSmfgGenerator = settings.Generator = Smfg
    member _.IsSmaiGenerator = settings.Generator = Smai

    member this.ForgeFlow
        with get () = float settings.Forge.Flow
        and set (value: float) =
            let v = Math.Clamp(int (Math.Round value), 10, 100)
            if v <> settings.Forge.Flow then
                this.Change { settings with Forge = { settings.Forge with Flow = v } }
                this.RaisePropertyChanged("ForgeFlow")
                this.RaisePropertyChanged("ForgeFlowText")

    member _.ForgeFlowText = sprintf "%d%%" settings.Forge.Flow

    member this.ForgeKeepStill
        with get () = settings.Forge.KeepStill
        and set (value: bool) =
            if value <> settings.Forge.KeepStill then
                this.Change { settings with Forge = { settings.Forge with KeepStill = value } }
                this.RaisePropertyChanged("ForgeKeepStill")

    member this.ForgeLightSearch
        with get () = settings.Forge.LightSearch
        and set (value: bool) =
            if value <> settings.Forge.LightSearch then
                this.Change { settings with Forge = { settings.Forge with LightSearch = value } }
                this.RaisePropertyChanged("ForgeLightSearch")

    member _.SmaiModelOptions = smaiModels |> List.map snd

    member this.SelectedSmaiModelIndex
        with get () = indexIn smaiModels settings.Forge.Rife
        and set (value: int) =
            match valueAt smaiModels value with
            | Some r when r <> settings.Forge.Rife ->
                this.Change { settings with Forge = { settings.Forge with Rife = r } }
                this.RaisePropertyChanged("SelectedSmaiModelIndex")
            | _ -> ()

    /// What the chosen generator needs and whether it is there; it falls back to SUITE's blend otherwise.
    member _.GeneratorNote =
        match settings.Generator with
        | OwnBlend -> "SUITE's own: drawn along the engine's motion vectors. Any GPU, lightest."
        | NvidiaDlssg -> "Needs an RTX 40 (up to 4x) or RTX 50 (up to 6x). Takes about 15 seconds to start; the DLSS-G version below is the one it runs."
        | Smfg -> "Motion Forge's own generator, plain compute: any GPU. Flow: smaller follows faster motion and costs less."
        | Smt -> "SMFG's triangle mesh: corners found by 3DRS, drawn by the GPU's rasteriser. Any GPU; sharper edges."
        | Smai ->
            if MotionAi.isInstalled () then "A network through DirectML, on the GPU's AI units where it has them. Its files are installed."
            elif settings.Forge.Rife then "RIFE needs Motion Forge's AI files: get them below (about 59 MB)."
            else "SMFG Net needs Motion Forge's AI files: get them below (about 59 MB). Without them SUITE's blend is used."
        | XessMfg ->
            if MotionFiles.hasXess () then "Intel's libraries are picked (checked for Intel's signature at start). On non-Intel GPUs Intel's library makes one frame a pair."
            else "Needs Intel's libxess_fg.dll and libxell.dll: pick them on the Motion Forge page. Without them SUITE's blend is used."
        | Fsr31 ->
            if File.Exists(Path.Combine(AppContext.BaseDirectory, "engine", "amd_fidelityfx_dx12.dll")) then "AMD's FidelityFX runtime ships with SUITE (AMD's signature is checked). FSR 3.1 makes one frame a pair: best at 2x."
            else "AMD's FidelityFX runtime (amd_fidelityfx_dx12.dll) is not in SUITE's engine folder; SUITE's blend is used."
        | RepeatFrames -> "No frame generation: the nearer real picture shown again, at the same frame rate - to compare the others with. Any GPU, x2-x6."
        | Crossfade -> "The two pictures mixed at each moment: the lightest of all, smooth brightness but doubled edges where things move. Any GPU, x2-x6."
        | Memc -> "Motion smoothing as TVs do it: 8x8 blocks matched between the two pictures, hidden and uncovered places handled. Any GPU (heavier than SMFG), x2-x6; strong on pans and scrolling."
        | Dis -> "Dense optical flow (Dense Inverse Search): a motion for every pixel, coarse to fine, hidden and uncovered places handled. Any GPU, x2-x6; good on fine and uneven motion."
        | MotionBlur -> "DIS's motion with a camera shutter's blur along it: a softer, cinematic look with less judder, not as sharp. Any GPU, x2-x6."
        | Spacewarp -> "Extrapolation, as VR's spacewarp: the pictures after each real one are drawn from it alone, so nothing waits for the next - about a frame less delay. Edges of moving things are guessed. Any GPU, x2-x6 (whole multipliers)."

    member _.SmaiNeedsFiles = settings.Generator = Smai && not (MotionAi.isInstalled ())
    member _.SmaiProgress = smaiProgress
    member _.IsGettingSmai = isGettingSmai

    /// Downloads SMAI's runtime and network into Motion Forge's data folder (shared with Motion Forge, pinned by SHA-256).
    member this.GetSmaiFiles() =
        if not isGettingSmai then
            isGettingSmai <- true
            this.RaisePropertyChanged("IsGettingSmai")
            let say text = Dispatcher.UIThread.Post(fun () -> smaiProgress <- text; this.RaisePropertyChanged("SmaiProgress"))
            task {
                let! result = MotionAi.install (fun text share -> say (sprintf "%s (%.0f%%)" text (share * 100.0)))
                say (match result with Ok () -> "SMAI's files are installed." | Error why -> "Could not get SMAI's files: " + why)
                Dispatcher.UIThread.Post(fun () ->
                    isGettingSmai <- false
                    this.RaiseGeneratorProperties()
                    this.RaisePropertyChanged("IsGettingSmai"))
            }
            |> ignore

    member private this.RaiseGeneratorProperties() =
        for name in
            [ "SelectedGeneratorIndex"; "IsNvidiaGenerator"; "IsForgeGenerator"; "UsesFlow"; "UsesKeepStill"; "IsSmfgGenerator"; "IsSmaiGenerator"
              "ForgeFlow"; "ForgeFlowText"; "ForgeKeepStill"; "ForgeLightSearch"; "SelectedSmaiModelIndex"; "GeneratorNote"; "SmaiNeedsFiles" ] do
            this.RaisePropertyChanged(name)

    member _.SelectedFrameGenIndex
        with get () = indexIn frameGens settings.FrameGen
        and set (value: int) =
            match valueAt frameGens value with
            | Some m when m <> settings.FrameGen ->
                this.Change { settings with FrameGen = m }
                this.RaisePropertyChanged("SelectedFrameGenIndex")
            | _ -> ()

    // ----- output -----
    member _.SuperResolutionOptions = superResolutions |> List.map snd

    member _.SelectedSuperResolutionIndex
        with get () = indexIn superResolutions settings.SuperResolution
        and set (value: int) =
            match valueAt superResolutions value with
            | Some s when s <> settings.SuperResolution ->
                this.Change { settings with SuperResolution = s }
                this.RaisePropertyChanged("SelectedSuperResolutionIndex")
            | _ -> ()

    member _.CompareOptions = compares |> List.map snd

    member _.SelectedCompareIndex
        with get () = indexIn compares settings.Compare
        and set (value: int) =
            match valueAt compares value with
            | Some c when c <> settings.Compare ->
                this.Change { settings with Compare = c }
                this.RaisePropertyChanged("SelectedCompareIndex")
            | _ -> ()

    member _.VSync
        with get () = settings.VSync
        and set value =
            if value <> settings.VSync then
                this.Change { settings with VSync = value }
                this.RaisePropertyChanged("VSync")

    // ----- advanced engine controls -----
    member private this.ChangeAdvanced(update: AdvancedSettings -> AdvancedSettings, name: string) =
        this.Change { settings with Advanced = update settings.Advanced }
        this.RaisePropertyChanged(name)

    member _.ShowPanel
        with get () = settings.Advanced.ShowPanel
        and set value = if value <> settings.Advanced.ShowPanel then this.ChangeAdvanced((fun a -> { a with ShowPanel = value }), "ShowPanel")

    member _.NrPreset
        with get () = float settings.Advanced.NrPreset
        and set (value: float) =
            let v = int (Math.Round value)
            if v <> settings.Advanced.NrPreset then this.ChangeAdvanced((fun a -> { a with NrPreset = v }), "NrPreset"); this.RaisePropertyChanged("NrPresetSlider"); this.RaisePropertyChanged("NrPresetText")

    member _.SrPreset
        with get () = float settings.Advanced.SrPreset
        and set (value: float) =
            let v = int (Math.Round value)
            if v <> settings.Advanced.SrPreset then this.ChangeAdvanced((fun a -> { a with SrPreset = v }), "SrPreset"); this.RaisePropertyChanged("SrPresetText")

    member _.MvLevel
        with get () = float settings.Advanced.MvLevel
        and set (value: float) =
            let v = int (Math.Round value)
            if v <> settings.Advanced.MvLevel then this.ChangeAdvanced((fun a -> { a with MvLevel = v }), "MvLevel"); this.RaisePropertyChanged("MvLevelText")

    member _.ResetThreshold
        with get () = settings.Advanced.ResetThreshold
        and set value = if value <> settings.Advanced.ResetThreshold then this.ChangeAdvanced((fun a -> { a with ResetThreshold = value }), "ResetThreshold"); this.RaisePropertyChanged("ResetThresholdText")

    member _.MvScaleAuto
        with get () = settings.Advanced.MvScaleAuto
        and set value = if value <> settings.Advanced.MvScaleAuto then this.ChangeAdvanced((fun a -> { a with MvScaleAuto = value }), "MvScaleAuto")

    member this.UseManualMvScale
        with get () = not settings.Advanced.MvScaleAuto
        and set value =
            if value <> this.UseManualMvScale then
                this.ChangeAdvanced((fun a -> { a with MvScaleAuto = not value }), "MvScaleAuto")
                this.RaisePropertyChanged("UseManualMvScale")

    member _.MvScaleX
        with get () = settings.Advanced.MvScaleX
        and set value = if value <> settings.Advanced.MvScaleX then this.ChangeAdvanced((fun a -> { a with MvScaleX = value }), "MvScaleX")

    member _.MvScaleY
        with get () = settings.Advanced.MvScaleY
        and set value = if value <> settings.Advanced.MvScaleY then this.ChangeAdvanced((fun a -> { a with MvScaleY = value }), "MvScaleY")

    member _.CaptureBorder
        with get () = settings.Advanced.CaptureBorder
        and set value = if value <> settings.Advanced.CaptureBorder then this.ChangeAdvanced((fun a -> { a with CaptureBorder = value }), "CaptureBorder")

    member _.Affinity
        with get () = settings.Advanced.Affinity
        and set value = if value <> settings.Advanced.Affinity then this.ChangeAdvanced((fun a -> { a with Affinity = value }), "Affinity")

    member _.Topmost
        with get () = settings.Advanced.Topmost
        and set value = if value <> settings.Advanced.Topmost then this.ChangeAdvanced((fun a -> { a with Topmost = value }), "Topmost")

    member _.ClickThrough
        with get () = settings.Advanced.ClickThrough
        and set value = if value <> settings.Advanced.ClickThrough then this.ChangeAdvanced((fun a -> { a with ClickThrough = value }), "ClickThrough")

    member _.ExcludeOwnWindows
        with get () = settings.Advanced.ExcludeOwnWindows
        and set value = if value <> settings.Advanced.ExcludeOwnWindows then this.ChangeAdvanced((fun a -> { a with ExcludeOwnWindows = value }), "ExcludeOwnWindows")

    member _.Indicator
        with get () = settings.Advanced.Indicator
        and set value = if value <> settings.Advanced.Indicator then this.ChangeAdvanced((fun a -> { a with Indicator = value }), "Indicator")

    member _.CubinCache
        with get () = settings.Advanced.CubinCache
        and set value = if value <> settings.Advanced.CubinCache then this.ChangeAdvanced((fun a -> { a with CubinCache = value }), "CubinCache")

    member _.DebugLayer
        with get () = settings.Advanced.DebugLayer
        and set value = if value <> settings.Advanced.DebugLayer then this.ChangeAdvanced((fun a -> { a with DebugLayer = value }), "DebugLayer")

    member _.CaptureFolder
        with get () =
            if String.IsNullOrWhiteSpace(settings.Advanced.CaptureFolder) then
                Path.Combine(dataDir (), "Captures")
            else settings.Advanced.CaptureFolder
        and set (value: string) =
            let folder = if isNull value then "" else value.Trim()
            if folder <> settings.Advanced.CaptureFolder then
                this.ChangeAdvanced((fun a -> { a with CaptureFolder = folder }), "CaptureFolder")

    /// The folder the DLSS 5 model DLL is loaded from; "" is the bundled one.
    member _.ModelFolder
        with get () = settings.Advanced.ModelFolder
        and set (value: string) =
            let folder = if isNull value then "" else value.Trim()
            if folder <> settings.Advanced.ModelFolder then
                this.ChangeAdvanced((fun a -> { a with ModelFolder = folder }), "ModelFolder")

    member _.ExclusionLog
        with get () = settings.Advanced.ExclusionLog
        and set value = if value <> settings.Advanced.ExclusionLog then this.ChangeAdvanced((fun a -> { a with ExclusionLog = value }), "ExclusionLog")

    member _.RedirectionBitmap
        with get () = settings.Advanced.RedirectionBitmap
        and set value = if value <> settings.Advanced.RedirectionBitmap then this.ChangeAdvanced((fun a -> { a with RedirectionBitmap = value }), "RedirectionBitmap")

    member _.ShowInert
        with get () = settings.Advanced.ShowInert
        and set value = if value <> settings.Advanced.ShowInert then this.ChangeAdvanced((fun a -> { a with ShowInert = value }), "ShowInert")

    member _.UiCorrection
        with get () = settings.Advanced.UiCorrection
        and set value = if value <> settings.Advanced.UiCorrection then this.ChangeAdvanced((fun a -> { a with UiCorrection = value }), "UiCorrection")

    member _.DepthValue
        with get () = settings.Advanced.DepthValue
        and set value = if value <> settings.Advanced.DepthValue then this.ChangeAdvanced((fun a -> { a with DepthValue = value }), "DepthValue")

    member _.DepthInverted
        with get () = settings.Advanced.DepthInverted
        and set value = if value <> settings.Advanced.DepthInverted then this.ChangeAdvanced((fun a -> { a with DepthInverted = value }), "DepthInverted")

    member _.NvofGridOptions = [ "1 × 1"; "2 × 2"; "4 × 4" ]

    member _.SelectedNvofGridIndex
        with get () = [ 1; 2; 4 ] |> List.tryFindIndex ((=) settings.Advanced.NvofGrid) |> Option.defaultValue 0
        and set value =
            let values = [ 1; 2; 4 ]
            if value >= 0 && value < values.Length && values.[value] <> settings.Advanced.NvofGrid then
                this.ChangeAdvanced((fun a -> { a with NvofGrid = values.[value] }), "SelectedNvofGridIndex")

    member _.NvofPerfOptions = [ "Slow"; "Medium"; "Fast" ]
    member _.SelectedNvofPerfIndex
        with get () = [ "slow"; "medium"; "fast" ] |> List.tryFindIndex ((=) settings.Advanced.NvofPerf) |> Option.defaultValue 1
        and set value =
            let values = [ "slow"; "medium"; "fast" ]
            if value >= 0 && value < values.Length then this.ChangeAdvanced((fun a -> { a with NvofPerf = values.[value] }), "SelectedNvofPerfIndex")

    member _.NgxLogOptions = [ "Off"; "On"; "Verbose" ]
    member _.SelectedNgxLogIndex
        with get () = settings.Advanced.NgxLog
        and set value = if value >= 0 && value <= 2 then this.ChangeAdvanced((fun a -> { a with NgxLog = value }), "SelectedNgxLogIndex")

    member _.LogLevelOptions = [ "Debug"; "Info"; "Warn"; "Error" ]
    member _.SelectedLogLevelIndex
        with get () = settings.Advanced.LogLevel
        and set value = if value >= 0 && value <= 3 then this.ChangeAdvanced((fun a -> { a with LogLevel = value }), "SelectedLogLevelIndex")

    member _.AdapterOptions = [ yield "First NVIDIA adapter"; yield! [ 0 .. 7 ] |> List.map (sprintf "Adapter %d") ]
    member _.SelectedAdapterIndex
        with get () = settings.Advanced.Adapter + 1
        and set value = if value >= 0 && value <= 8 then this.ChangeAdvanced((fun a -> { a with Adapter = value - 1 }), "SelectedAdapterIndex")

    member _.NgxAppId
        with get () = settings.Advanced.NgxAppId
        and set value = this.ChangeAdvanced((fun a -> { a with NgxAppId = if isNull value then "" else value }), "NgxAppId")

    member _.NgxProjectId
        with get () = settings.Advanced.NgxProjectId
        and set value = this.ChangeAdvanced((fun a -> { a with NgxProjectId = if isNull value then "" else value }), "NgxProjectId")

    member _.SkinFollowsStructure
        with get () = match settings.Skin with FollowStructure -> true | _ -> false
        and set value =
            let skin = if value then FollowStructure else SkinStrength 1.0
            if skin <> settings.Skin then
                this.Change { settings with Skin = skin }
                this.RaisePropertyChanged("SkinFollowsStructure")
                this.RaisePropertyChanged("SkinStrength")
                this.RaisePropertyChanged("SkinStrengthSlider")
                this.RaisePropertyChanged("SkinStrengthText")

    member _.SkinStrength
        with get () = match settings.Skin with FollowStructure -> 1.0 | SkinStrength v -> v
        and set value =
            if not this.SkinFollowsStructure && settings.Skin <> SkinStrength value then
                this.Change { settings with Skin = SkinStrength value }
                this.RaisePropertyChanged("SkinStrength")
                this.RaisePropertyChanged("SkinStrengthSlider")
                this.RaisePropertyChanged("SkinStrengthText")

    member _.MotionOptions = [ "Built-in"; "None" ]
    member _.SelectedMotionIndex
        with get () = match settings.Motion with NoMotion -> 1 | _ -> 0
        and set value =
            let motion = if value = 1 then NoMotion else BuiltIn
            if motion <> settings.Motion then
                this.Change { settings with Motion = motion }
                this.RaisePropertyChanged("SelectedMotionIndex")

    member _.CursorOptions = [ "Automatic"; "On"; "Off" ]
    member _.SelectedCursorIndex
        with get () = match settings.Cursor with CursorAuto -> 0 | CursorOn -> 1 | CursorOff -> 2
        and set value =
            let cursor = if value = 1 then CursorOn elif value = 2 then CursorOff else CursorAuto
            if cursor <> settings.Cursor then
                this.Change { settings with Cursor = cursor }
                this.RaisePropertyChanged("SelectedCursorIndex")

    member _.HighPrecisionColour
        with get () = settings.HighPrecisionColour
        and set value =
            if value <> settings.HighPrecisionColour then
                this.Change { settings with HighPrecisionColour = value }
                this.RaisePropertyChanged("HighPrecisionColour")

    // ----- running it -----

    // ----- typed values -----
    // Every slider has a box beside it that takes any value. Where the engine
    // caps a setting the value is still clamped to that cap; where it does not
    // (structure, tone, skin, passes, NR preset) the box can go past the
    // slider's end and the slider just sits at its end.
    static member private Parse(text: string) =
        let cleaned = if isNull text then "" else text.Trim().TrimEnd('%').Trim().Replace(',', '.')
        match Double.TryParse(cleaned, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
        | true, v when Double.IsFinite v -> Some v
        | _ -> None

    static member private Show(value: float) = value.ToString("0.###", Globalization.CultureInfo.InvariantCulture)

    /// A slider view of a wider value: shown clamped, and only a real move is
    /// written back, so a typed value past the end is not pulled in by the slider.
    static member private Within(value: float, low: float, high: float) = Math.Clamp(value, low, high)

    member this.IntensityText
        with get () = sprintf "%.0f%%" (settings.Intensity * 100.0)
        and set (text: string) =
            ScreenEngineViewModel.Parse text |> Option.iter (fun v -> this.Intensity <- Math.Clamp(v / 100.0, 0.0, 1.0))
            this.RaisePropertyChanged("IntensityText")

    member this.LocalStructureText
        with get () = ScreenEngineViewModel.Show settings.LocalStructure
        and set (text: string) =
            ScreenEngineViewModel.Parse text |> Option.iter (fun v -> this.LocalStructure <- v)
            this.RaisePropertyChanged("LocalStructureText")

    member this.LocalStructureSlider
        with get () = ScreenEngineViewModel.Within(settings.LocalStructure, 0.0, 5.0)
        and set (v: float) = if abs (v - this.LocalStructureSlider) > 1e-6 then this.LocalStructure <- v

    member this.LocalToneText
        with get () = ScreenEngineViewModel.Show settings.LocalTone
        and set (text: string) =
            ScreenEngineViewModel.Parse text |> Option.iter (fun v -> this.LocalTone <- v)
            this.RaisePropertyChanged("LocalToneText")

    member this.LocalToneSlider
        with get () = ScreenEngineViewModel.Within(settings.LocalTone, 0.0, 5.0)
        and set (v: float) = if abs (v - this.LocalToneSlider) > 1e-6 then this.LocalTone <- v

    member this.PassesText
        with get () = string settings.Passes
        and set (text: string) =
            ScreenEngineViewModel.Parse text |> Option.iter (fun v -> this.Passes <- max 1.0 v)
            this.RaisePropertyChanged("PassesText")

    member this.PassesSlider
        with get () = ScreenEngineViewModel.Within(float settings.Passes, 1.0, 4.0)
        and set (v: float) = if abs (v - this.PassesSlider) > 1e-6 then this.Passes <- v

    member this.NrPresetText
        with get () = string settings.Advanced.NrPreset
        and set (text: string) =
            ScreenEngineViewModel.Parse text |> Option.iter (fun v -> this.NrPreset <- max 0.0 v)
            this.RaisePropertyChanged("NrPresetText")

    member this.NrPresetSlider
        with get () = ScreenEngineViewModel.Within(float settings.Advanced.NrPreset, 0.0, 15.0)
        and set (v: float) = if abs (v - this.NrPresetSlider) > 1e-6 then this.NrPreset <- v

    member this.SrPresetText
        with get () = string settings.Advanced.SrPreset
        and set (text: string) =
            ScreenEngineViewModel.Parse text |> Option.iter (fun v -> this.SrPreset <- Math.Clamp(v, 0.0, 15.0))
            this.RaisePropertyChanged("SrPresetText")

    member this.MvLevelText
        with get () = string settings.Advanced.MvLevel
        and set (text: string) =
            ScreenEngineViewModel.Parse text |> Option.iter (fun v -> this.MvLevel <- Math.Clamp(v, 0.0, 7.0))
            this.RaisePropertyChanged("MvLevelText")

    member this.ResetThresholdText
        with get () = ScreenEngineViewModel.Show settings.Advanced.ResetThreshold
        and set (text: string) =
            ScreenEngineViewModel.Parse text |> Option.iter (fun v -> this.ResetThreshold <- Math.Clamp(v, 0.0, 1.0))
            this.RaisePropertyChanged("ResetThresholdText")

    member this.SkinStrengthText
        with get () = ScreenEngineViewModel.Show this.SkinStrength
        and set (text: string) =
            ScreenEngineViewModel.Parse text |> Option.iter (fun v -> this.SkinStrength <- v)
            this.RaisePropertyChanged("SkinStrengthText")

    member this.SkinStrengthSlider
        with get () = ScreenEngineViewModel.Within(this.SkinStrength, 0.0, 5.0)
        and set (v: float) = if abs (v - this.SkinStrengthSlider) > 1e-6 then this.SkinStrength <- v

    member this.Start() =
        hasPendingChanges <- false
        OverlayArbiter.claim "screen" |> ignore // MOTION FORGE: stops Motion Forge's overlay first
        if useRobloxShadeHost then
            host.Stop()
            nsHost.Stop()
            if not (robloxHost.Start()) then status <- "RobloxShadeHost is not installed in this SUITE build."
        elif useNeuralScreen then
            host.Stop()
            nsHost.Start(settings, this.CaptureFolder, settings.NeuralRendering)
            if settings.Window.StartsWith("0x", StringComparison.OrdinalIgnoreCase) then
                match Int64.TryParse(settings.Window.Substring(2), Globalization.NumberStyles.HexNumber, null) with
                | true, handle -> nsHost.CaptureWindow(nativeint handle) |> ignore
                | _ -> ()
        else
            nsHost.Stop()
            host.Start(settings)
        this.RaisePropertyChanged("HasPendingChanges")
        this.RaisePropertyChanged("IsRunning")
        this.RaisePropertyChanged("CanStart")

    member this.Stop() =
        applyTimer.Stop()
        host.Stop()
        nsHost.Stop()
        robloxHost.Stop()
        isRecording <- false
        hasPendingChanges <- false
        status <- "Stopped"
        this.RaisePropertyChanged("Status")
        this.RaisePropertyChanged("HasPendingChanges")
        this.RaisePropertyChanged("IsRunning")
        this.RaisePropertyChanged("CanStart")
        this.RaisePropertyChanged("IsRecording")
        this.RaisePropertyChanged("RecordButtonText")

    member this.Apply() = this.Start()

    member this.SaveScreenshot() =
        let sent =
            if useNeuralScreen then
                Directory.CreateDirectory(this.CaptureFolder) |> ignore
                nsHost.Screenshot(this.CaptureFolder)
            else host.RequestScreenshot()
        status <- if sent then "Screenshot requested." else "The engine is not ready for capture commands yet."
        this.RaisePropertyChanged("Status")

    member this.ToggleRecording() =
        if (if useNeuralScreen then nsHost.ToggleRecording() else host.ToggleRecording()) then
            isRecording <- not isRecording
            status <- if isRecording then "Recording started." else "Recording is being finalized."
            this.RaisePropertyChanged("IsRecording")
            this.RaisePropertyChanged("RecordButtonText")
        else
            status <- "The engine is not ready for capture commands yet."
        this.RaisePropertyChanged("Status")

    member _.SweepOptions = [ "Intensity"; "Local structure"; "Local tone"; "Skin structure"; "Style"; "Auto mask"; "Passes" ]

    member _.SelectedSweepIndex
        with get () = selectedSweepIndex
        and set value =
            if value >= 0 && value <= 6 && value <> selectedSweepIndex then
                selectedSweepIndex <- value
                this.RaisePropertyChanged("SelectedSweepIndex")

    member _.SweepValues
        with get () = sweepValues
        and set (value: float) =
            let rounded = Math.Clamp(Math.Round(value), 2.0, 50.0)
            if rounded <> sweepValues then
                sweepValues <- rounded
                this.RaisePropertyChanged("SweepValues")

    member this.StartComparisonSweep() =
        let values = if selectedSweepIndex = 4 then 3 elif selectedSweepIndex = 5 then 2 else int sweepValues
        status <- if host.StartComparison(selectedSweepIndex, values) then "Comparison sweep started." else "The engine is not ready for capture commands yet."
        this.RaisePropertyChanged("Status")

    // =====================================================================
    // PER-GAME PRESETS
    // =====================================================================
    member private this.Persist() =
        match presetGame with
        | Some(key, _) -> savePreset key settings
        | None -> save settings

    member private this.PersistExtras() =
        NeuralScreen.activeExtras <- Some extras
        match presetGame with
        | Some(key, _) -> NeuralScreen.saveExtrasTo (NeuralScreen.presetExtrasPath key) extras
        | None -> NeuralScreen.saveExtras extras

    member _.IsGamePreset = presetGame.IsSome

    member _.ActivePresetText =
        match presetGame with
        | Some(_, title) -> "Settings for " + title
        | None -> "Default settings (all games)"

    member _.HasPresetFor(gameKey: string) = hasPreset gameKey

    /// Switches every control to a new set of values and restarts a running
    /// session with them - a different game's look is not a slider tick.
    member private this.Switch(next: Settings, nextExtras: NeuralScreen.Extras) =
        settings <- normalize next
        extras <- NeuralScreen.normalizeExtras nextExtras
        NeuralScreen.activeExtras <- Some extras
        this.RaiseEverything()
        this.ScheduleApply()

    /// Used when a game with its own preset is started from its card. A game
    /// without one leaves whatever is in use alone.
    member this.UseGamePreset(gameKey: string, title: string) =
        match loadPreset gameKey with
        | Some saved ->
            presetGame <- Some(gameKey, title)
            let savedExtras = NeuralScreen.loadExtrasFrom (NeuralScreen.presetExtrasPath gameKey) |> Option.defaultValue extras
            this.Switch(saved, savedExtras)
            AppLog.info ("Screen Engine preset in use: " + title)
            true
        | None -> false

    /// Keeps the current values as this game's own, and uses them from now on.
    member this.SaveGamePreset(gameKey: string, title: string) =
        presetGame <- Some(gameKey, title)
        this.Persist()
        this.PersistExtras()
        this.RaisePropertyChanged("IsGamePreset")
        this.RaisePropertyChanged("ActivePresetText")

    member this.DeleteGamePreset(gameKey: string) =
        deletePreset gameKey
        match presetGame with
        | Some(key, _) when key = gameKey -> this.UseDefaultSettings()
        | _ -> ()

    /// Back to the shared settings every game without a preset uses.
    member this.UseDefaultSettings() =
        if presetGame.IsSome then
            presetGame <- None
            this.Switch(load (), NeuralScreen.loadExtras ())

    member private this.RaiseEverything() =
        for name in
            [ "IsGamePreset"; "ActivePresetText"; "KeepOriginalColours"; "ColoursUnlocked"
              "Boost"; "WorkScale"; "WorkScaleText"; "SkipStatic"; "Hdr"; "Spout"; "FrameGeneration"
              "FrameMultiplier"; "FrameMultiplierText"; "RecordingIndicator"; "SelectedMotionBackendIndex"
              "NeuralScreenGpu"; "NeuralScreenGpuText"; "IntensitySlider"; "Window" ] do
            this.RaisePropertyChanged(name)
        this.RaiseSettingProperties()

    member this.ResetToDefaults() =
        settings <- defaults
        this.Persist()
        this.RaiseSettingProperties()
        this.ScheduleApply()

    member private this.RaiseSettingProperties() =
        for name in
            [ "SelectedSourceIndex"; "SelectedTargetIndex"; "CanPickTarget"; "Window"; "SelectedWindowIndex"; "CaptureFolder"; "ModelFolder"; "NeuralRendering"
              "Intensity"; "IntensityText"; "LocalStructure"; "LocalStructureText"; "LocalTone"; "LocalToneText"
              "Passes"; "PassesText"; "PassesSlider"; "LocalStructureSlider"; "LocalToneSlider"; "NrPresetSlider"; "NrPresetText"; "SrPresetText"; "MvLevelText"; "ResetThresholdText"; "SkinStrengthSlider"; "SkinStrengthText"; "AutoMask"; "SelectedStyleIndex"; "SelectedFrameGenIndex"; "SelectedSuperResolutionIndex"
              "SelectedCompareIndex"; "VSync"; "ShowPanel"; "NrPreset"; "SrPreset"; "MvLevel"
              "ResetThreshold"; "MvScaleAuto"; "UseManualMvScale"; "MvScaleX"; "MvScaleY"
              "CaptureBorder"; "Affinity"; "Topmost"; "ClickThrough"; "ExcludeOwnWindows"
              "Indicator"; "CubinCache"; "DebugLayer"; "ExclusionLog"; "RedirectionBitmap"
              "ShowInert"; "UiCorrection"; "DepthValue"; "DepthInverted"; "SelectedNvofGridIndex"
              "SelectedNvofPerfIndex"; "SelectedNgxLogIndex"; "SelectedLogLevelIndex"
              "SelectedAdapterIndex"; "NgxAppId"; "NgxProjectId"; "SkinFollowsStructure"
              "SkinStrength"; "SelectedMotionIndex"; "SelectedCursorIndex"; "HighPrecisionColour" ] do
            this.RaisePropertyChanged(name)
        this.RaiseGeneratorProperties()

    interface IDisposable with
        member _.Dispose() =
            (host :> IDisposable).Dispose()
            (nsHost :> IDisposable).Dispose()
