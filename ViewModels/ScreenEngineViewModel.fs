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
    let gpuName, gpuTier = NeuralScreen.detect ()

    // Which program runs DLSS 5 over the screen. The Screen Engine needs an
    // RTX 50 card; NeuralScreen covers RTX 30/40 (and 50, as a second choice).
    let canScreenEngine = (gpuTier = NeuralScreen.Rtx50) && isAvailable ()
    let cardRunsNeuralScreen = (match gpuTier with NeuralScreen.Rtx50 | NeuralScreen.RtxOlder _ -> true | _ -> false)
    let mutable canNeuralScreen = cardRunsNeuralScreen && NeuralScreen.isAvailable ()
    let methodPath () = Path.Combine(NeuralScreen.dataDir (), "method.txt")
    let mutable useNeuralScreen =
        let saved = (try File.ReadAllText(methodPath ()).Trim() with _ -> "")
        if saved = "neuralscreen" && canNeuralScreen then true
        elif saved = "engine" && canScreenEngine then false
        else not canScreenEngine && canNeuralScreen
    let mutable settings = load ()
    let mutable monitors: MonitorEntry list = []
    let mutable windows: WindowEntry list = []
    let mutable status = ""
    let mutable hasPendingChanges = false
    let mutable isAdvancedOpen = false
    let mutable isRecording = false
    let mutable selectedSweepIndex = 0
    let mutable sweepValues = 5.0
    let mutable pickerText = "Click, then Alt+Tab and click a window — or drag the crosshair onto it"

    // Changes apply by themselves. A running engine only reads its options at
    // start, so each change restarts it - but only once the settings have been
    // still for a moment, so dragging a slider restarts it once, not per tick.
    let applyTimer = DispatcherTimer(Interval = TimeSpan.FromMilliseconds(600.0))

    let styles = [ Standard, "Standard"; Natural, "Natural"; Cinematic, "Cinematic" ]
    let superResolutions = [ SrAuto, "Automatic"; SrDlaa, "DLAA (native resolution)"; SrOff, "Off" ]
    let compares = [ CompareOff, "Off"; CompareSplit, "Split view"; CompareOriginal, "Original only" ]

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
        AppDomain.CurrentDomain.ProcessExit.Add(fun _ -> host.Stop(); nsHost.Stop())
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
        nsHost.RecordingChanged.Add(fun active ->
            Dispatcher.UIThread.Post(fun () ->
                isRecording <- active
                this.RaisePropertyChanged("IsRecording")
                this.RaisePropertyChanged("RecordButtonText")))

    member _.IsAvailable = if useNeuralScreen then canNeuralScreen else canScreenEngine

    // ----- method: Screen Engine or NeuralScreen -----
    member _.GpuName = if gpuName = "" then "No graphics card found" else gpuName
    member _.CanUseScreenEngine = canScreenEngine
    member _.CanUseNeuralScreen = canNeuralScreen
    member _.ScreenEngineLocked = not canScreenEngine
    member _.NeuralScreenLocked = not canNeuralScreen
    member _.IsNeuralScreen = useNeuralScreen
    member _.IsScreenEngine = not useNeuralScreen

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

    /// The card can run it, but the add-on is not installed: it is a separate
    /// download because of its size and its own NVIDIA runtimes.
    member _.NeuralScreenNeedsInstall = cardRunsNeuralScreen && not canNeuralScreen

    member _.NeuralScreenInstallFolder = NeuralScreen.folder ()

    /// Unpacks "DLSS 5 SUITE NeuralScreen Add-on.zip" into mod files.
    member this.InstallNeuralScreenAddon(zipPath: string) =
        try
            let target = Path.GetDirectoryName(NeuralScreen.folder ())
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
        match gpuTier with
        | NeuralScreen.Rtx50 -> "RTX 50 series detected: both methods work."
        | NeuralScreen.RtxOlder s when canNeuralScreen -> sprintf "RTX %d series detected: NeuralScreen is used. The Screen Engine is RTX 50 series only." s
        | NeuralScreen.RtxOlder s -> sprintf "RTX %d series detected: DLSS 5 needs the NeuralScreen add-on, a separate download." s
        | NeuralScreen.Rtx20 -> "RTX 20 series detected: this card cannot run DLSS 5."
        | NeuralScreen.NoRtx -> "No RTX card detected: DLSS 5 needs an RTX 30, 40 or 50 series card."

    member this.SelectMethod(neuralScreen: bool) =
        let allowed = if neuralScreen then canNeuralScreen else canScreenEngine
        if allowed && neuralScreen <> useNeuralScreen then
            let wasRunning = this.IsRunning
            this.Stop()
            useNeuralScreen <- neuralScreen
            (try
                Directory.CreateDirectory(NeuralScreen.dataDir ()) |> ignore
                File.WriteAllText(methodPath (), if neuralScreen then "neuralscreen" else "engine")
             with _ -> ())
            for name in [ "IsNeuralScreen"; "IsScreenEngine"; "IsAvailable"; "CanStart"; "Status" ] do
                this.RaisePropertyChanged(name)
            if wasRunning then this.Start()

    member this.Status =
        if status <> "" then status
        elif not this.IsAvailable then (if useNeuralScreen then this.NeuralScreenTip else this.ScreenEngineTip)
        else "Stopped"

    member _.IsRunning = host.IsRunning || nsHost.IsRunning

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
        save settings
        if nsHost.IsRunning then this.PushToNeuralScreen(previous, settings)
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
        if useNeuralScreen then
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

    member this.ResetToDefaults() =
        settings <- defaults
        save settings
        for name in
            [ "SelectedSourceIndex"; "SelectedTargetIndex"; "CanPickTarget"; "Window"; "SelectedWindowIndex"; "CaptureFolder"; "ModelFolder"; "NeuralRendering"
              "Intensity"; "IntensityText"; "LocalStructure"; "LocalStructureText"; "LocalTone"; "LocalToneText"
              "Passes"; "PassesText"; "PassesSlider"; "LocalStructureSlider"; "LocalToneSlider"; "NrPresetSlider"; "NrPresetText"; "SrPresetText"; "MvLevelText"; "ResetThresholdText"; "SkinStrengthSlider"; "SkinStrengthText"; "AutoMask"; "SelectedStyleIndex"; "SelectedSuperResolutionIndex"
              "SelectedCompareIndex"; "VSync"; "ShowPanel"; "NrPreset"; "SrPreset"; "MvLevel"
              "ResetThreshold"; "MvScaleAuto"; "UseManualMvScale"; "MvScaleX"; "MvScaleY"
              "CaptureBorder"; "Affinity"; "Topmost"; "ClickThrough"; "ExcludeOwnWindows"
              "Indicator"; "CubinCache"; "DebugLayer"; "ExclusionLog"; "RedirectionBitmap"
              "ShowInert"; "UiCorrection"; "DepthValue"; "DepthInverted"; "SelectedNvofGridIndex"
              "SelectedNvofPerfIndex"; "SelectedNgxLogIndex"; "SelectedLogLevelIndex"
              "SelectedAdapterIndex"; "NgxAppId"; "NgxProjectId"; "SkinFollowsStructure"
              "SkinStrength"; "SelectedMotionIndex"; "SelectedCursorIndex"; "HighPrecisionColour" ] do
            this.RaisePropertyChanged(name)
        this.ScheduleApply()

    interface IDisposable with
        member _.Dispose() =
            (host :> IDisposable).Dispose()
            (nsHost :> IDisposable).Dispose()
