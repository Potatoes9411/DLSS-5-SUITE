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
              "SelectedCompareIndex"; "VSync" ] do
            this.RaisePropertyChanged(name)
        if host.IsRunning then
            hasPendingChanges <- true
            this.RaisePropertyChanged("HasPendingChanges")

    interface IDisposable with
        member _.Dispose() = (host :> IDisposable).Dispose()
