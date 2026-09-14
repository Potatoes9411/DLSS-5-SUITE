namespace DLSS_5_MANAGER.ViewModels

open System
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
    let mutable settings = load ()
    let mutable monitors: MonitorEntry list = []
    let mutable status = ""
    let mutable hasPendingChanges = false
    let mutable isAdvancedOpen = false

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
        AppDomain.CurrentDomain.ProcessExit.Add(fun _ -> host.Stop())
        monitors <- listMonitors ()

        host.StateChanged.Add(fun message ->
            Dispatcher.UIThread.Post(fun () ->
                status <- message
                this.RaisePropertyChanged("Status")
                this.RaisePropertyChanged("IsRunning")
                this.RaisePropertyChanged("CanStart")))

    member val IsAvailable = isAvailable ()

    member _.Status =
        if status <> "" then status
        elif not (isAvailable ()) then "The screen engine is not included in this build."
        else "Stopped"

    member _.IsRunning = host.IsRunning
    member this.CanStart = this.IsAvailable && not host.IsRunning
    member _.HasPendingChanges = hasPendingChanges
    member this.IsAdvancedOpen
        with get () = isAdvancedOpen
        and set value = this.SetProperty(&isAdvancedOpen, value) |> ignore

    member private this.Change(updated: Settings) =
        settings <- normalize updated
        save settings
        if host.IsRunning && not hasPendingChanges then
            hasPendingChanges <- true
            this.RaisePropertyChanged("HasPendingChanges")

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
            if value <> settings.Intensity then
                this.Change { settings with Intensity = value }
                this.RaisePropertyChanged("Intensity")
                this.RaisePropertyChanged("IntensityText")

    member _.IntensityText = sprintf "%.0f%%" (settings.Intensity * 100.0)

    member _.LocalStructure
        with get () = settings.LocalStructure
        and set value =
            if value <> settings.LocalStructure then
                this.Change { settings with LocalStructure = value }
                this.RaisePropertyChanged("LocalStructure")
                this.RaisePropertyChanged("LocalStructureText")

    member _.LocalStructureText = settings.LocalStructure.ToString("0.00")

    member _.LocalTone
        with get () = settings.LocalTone
        and set value =
            if value <> settings.LocalTone then
                this.Change { settings with LocalTone = value }
                this.RaisePropertyChanged("LocalTone")
                this.RaisePropertyChanged("LocalToneText")

    member _.LocalToneText = settings.LocalTone.ToString("0.00")

    member _.Passes
        with get () = float settings.Passes
        and set (value: float) =
            let passes = int (Math.Round value)
            if passes <> settings.Passes then
                this.Change { settings with Passes = passes }
                this.RaisePropertyChanged("Passes")
                this.RaisePropertyChanged("PassesText")

    member _.PassesText = string settings.Passes

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
            if v <> settings.Advanced.NrPreset then this.ChangeAdvanced((fun a -> { a with NrPreset = v }), "NrPreset")

    member _.SrPreset
        with get () = float settings.Advanced.SrPreset
        and set (value: float) =
            let v = int (Math.Round value)
            if v <> settings.Advanced.SrPreset then this.ChangeAdvanced((fun a -> { a with SrPreset = v }), "SrPreset")

    member _.MvLevel
        with get () = float settings.Advanced.MvLevel
        and set (value: float) =
            let v = int (Math.Round value)
            if v <> settings.Advanced.MvLevel then this.ChangeAdvanced((fun a -> { a with MvLevel = v }), "MvLevel")

    member _.ResetThreshold
        with get () = settings.Advanced.ResetThreshold
        and set value = if value <> settings.Advanced.ResetThreshold then this.ChangeAdvanced((fun a -> { a with ResetThreshold = value }), "ResetThreshold")

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

    member _.SkinStrength
        with get () = match settings.Skin with FollowStructure -> 1.0 | SkinStrength v -> v
        and set value =
            if not this.SkinFollowsStructure && settings.Skin <> SkinStrength value then
                this.Change { settings with Skin = SkinStrength value }
                this.RaisePropertyChanged("SkinStrength")

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
    member this.Start() =
        hasPendingChanges <- false
        host.Start(settings)
        this.RaisePropertyChanged("HasPendingChanges")
        this.RaisePropertyChanged("IsRunning")
        this.RaisePropertyChanged("CanStart")

    member this.Stop() =
        host.Stop()
        hasPendingChanges <- false
        status <- "Stopped"
        this.RaisePropertyChanged("Status")
        this.RaisePropertyChanged("HasPendingChanges")
        this.RaisePropertyChanged("IsRunning")
        this.RaisePropertyChanged("CanStart")

    member this.Apply() = this.Start()

    member this.ResetToDefaults() =
        settings <- defaults
        save settings
        for name in
            [ "SelectedSourceIndex"; "SelectedTargetIndex"; "CanPickTarget"; "Window"; "NeuralRendering"
              "Intensity"; "IntensityText"; "LocalStructure"; "LocalStructureText"; "LocalTone"; "LocalToneText"
              "Passes"; "PassesText"; "AutoMask"; "SelectedStyleIndex"; "SelectedSuperResolutionIndex"
              "SelectedCompareIndex"; "VSync"; "ShowPanel"; "NrPreset"; "SrPreset"; "MvLevel"
              "ResetThreshold"; "MvScaleAuto"; "UseManualMvScale"; "MvScaleX"; "MvScaleY"
              "CaptureBorder"; "Affinity"; "Topmost"; "ClickThrough"; "ExcludeOwnWindows"
              "Indicator"; "CubinCache"; "DebugLayer"; "ExclusionLog"; "RedirectionBitmap"
              "ShowInert"; "UiCorrection"; "DepthValue"; "DepthInverted"; "SelectedNvofGridIndex"
              "SelectedNvofPerfIndex"; "SelectedNgxLogIndex"; "SelectedLogLevelIndex"
              "SelectedAdapterIndex"; "NgxAppId"; "NgxProjectId"; "SkinFollowsStructure"
              "SkinStrength"; "SelectedMotionIndex"; "SelectedCursorIndex"; "HighPrecisionColour" ] do
            this.RaisePropertyChanged(name)
        if host.IsRunning then
            hasPendingChanges <- true
            this.RaisePropertyChanged("HasPendingChanges")

    interface IDisposable with
        member _.Dispose() = (host :> IDisposable).Dispose()
