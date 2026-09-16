namespace DLSS_5_MANAGER.Views

open System
open System.Diagnostics
open System.IO
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Interactivity
open Avalonia.Markup.Xaml
open Avalonia.Platform.Storage
open Avalonia.Threading
open DLSS_5_MANAGER.Services
open DLSS_5_MANAGER.ViewModels

/// The Screen Engine's own window, opened from Settings. Its DataContext is
/// the MainViewModel, so the backdrop follows the app's theme; the controls
/// bind to ScreenEngine inside it.
type ScreenEngineWindow() as this =
    inherit Window()

    let mutable isPicking = false
    let mutable wasDragged = false
    let mutable pickerArmed = false
    let mutable waitForButtonRelease = false
    let mutable pickerTicksRemaining = 0
    let mutable pressPosition = Avalonia.Point()
    let pickerTimer = DispatcherTimer(Interval = TimeSpan.FromMilliseconds(30.0))
    let mutable isOverlay = false
    let reopenSuiteRequested = Event<unit>()

    do
        AvaloniaXamlLoader.Load(this)
        pickerTimer.Tick.Add(fun _ ->
            if pickerArmed then
                pickerTicksRemaining <- pickerTicksRemaining - 1
                let down = ScreenEngine.isLeftMouseButtonDown ()
                if waitForButtonRelease then
                    if not down then waitForButtonRelease <- false
                elif down then
                    match ScreenEngine.windowAtCursor () with
                    | Some _ ->
                        pickerArmed <- false
                        pickerTimer.Stop()
                        this.WithEngine(fun engine -> engine.TrackWindowUnderCursor true)
                    | None -> ()
                elif pickerTicksRemaining <= 0 then
                    pickerArmed <- false
                    pickerTimer.Stop()
                    this.WithEngine(fun engine -> engine.CancelWindowPicker()))

    member private this.Engine =
        match this.DataContext with
        | :? MainViewModel as vm -> Some vm.ScreenEngine
        | _ -> None

    member private this.WithEngine(action: ScreenEngineViewModel -> unit) =
        this.Engine |> Option.iter action

    member this.OnHeaderPointerPressed(sender: obj, e: PointerPressedEventArgs) =
        if e.GetCurrentPoint(this).Properties.IsLeftButtonPressed then this.BeginMoveDrag(e)

    member this.OnCloseClicked(sender: obj, e: RoutedEventArgs) = if isOverlay then this.Hide() else this.Close()

    /// Raised by the "Reopen DLSS 5 SUITE window" button.
    member _.ReopenSuiteRequested = reopenSuiteRequested.Publish
    member _.IsOverlay = isOverlay

    /// Overlay: above everything, including DLSS 5's own picture, with the
    /// button that goes back to the app. Otherwise an ordinary window.
    member this.SetOverlay(on: bool) =
        isOverlay <- on
        this.Topmost <- on
        match this.FindControl<Control>("OverlayBar") with
        | null -> ()
        | bar -> bar.IsVisible <- on
        if on && this.WindowState = WindowState.Minimized then this.WindowState <- WindowState.Normal

    member this.OnReopenSuiteClicked(sender: obj, e: RoutedEventArgs) = reopenSuiteRequested.Trigger()
    member this.OnMinimizeClicked(sender: obj, e: RoutedEventArgs) = this.WindowState <- WindowState.Minimized

    member private this.ArmClickPicker() =
        pickerArmed <- true
        waitForButtonRelease <- true
        pickerTicksRemaining <- 2000 // one minute at 30 ms
        this.WithEngine(fun engine -> engine.ArmWindowPicker())
        pickerTimer.Start()

    // ----- crosshair: click, Alt+Tab, then click; dragging still works -----
    member this.OnCrosshairPressed(sender: obj, e: PointerPressedEventArgs) =
        if e.GetCurrentPoint(this).Properties.IsLeftButtonPressed then
            if pickerArmed then
                pickerArmed <- false
                pickerTimer.Stop()
                this.WithEngine(fun engine -> engine.CancelWindowPicker())
            isPicking <- true
            wasDragged <- false
            pressPosition <- e.GetPosition(this)
            e.Pointer.Capture(sender :?> IInputElement)
            e.Handled <- true

    member this.OnCrosshairMoved(sender: obj, e: PointerEventArgs) =
        if isPicking then
            let p = e.GetPosition(this)
            if abs (p.X - pressPosition.X) >= 5.0 || abs (p.Y - pressPosition.Y) >= 5.0 then
                wasDragged <- true
                this.WithEngine(fun engine -> engine.TrackWindowUnderCursor false)

    member this.OnCrosshairReleased(sender: obj, e: PointerReleasedEventArgs) =
        if isPicking then
            isPicking <- false
            e.Pointer.Capture(null)
            if wasDragged then this.WithEngine(fun engine -> engine.TrackWindowUnderCursor true)
            else this.ArmClickPicker()
            e.Handled <- true

    member this.OnCrosshairCaptureLost(sender: obj, e: PointerCaptureLostEventArgs) =
        // Releasing capture is part of both valid paths. Do not cancel the
        // armed picker when SUITE loses focus to Alt+Tab.
        isPicking <- false

    member this.OnMethodScreenEngineClicked(sender: obj, e: RoutedEventArgs) = this.WithEngine(fun engine -> engine.SelectMethod false)
    member this.OnMethodNeuralScreenClicked(sender: obj, e: RoutedEventArgs) = this.WithEngine(fun engine -> engine.SelectMethod true)

    member this.OnInstallNeuralScreenClicked(sender: obj, e: RoutedEventArgs) =
        async {
            let zip = FilePickerFileType("NeuralScreen add-on", Patterns = [| "*.zip" |])
            let options = FilePickerOpenOptions(Title = "Choose the downloaded NeuralScreen add-on zip", AllowMultiple = false, FileTypeFilter = [| zip |])
            let! files = this.StorageProvider.OpenFilePickerAsync(options) |> Async.AwaitTask
            if files.Count > 0 then
                this.WithEngine(fun engine -> engine.InstallNeuralScreenAddon files.[0].Path.LocalPath)
        }
        |> Async.StartImmediate

    member this.OnGetNeuralScreenClicked(sender: obj, e: RoutedEventArgs) =
        try
            Process.Start(ProcessStartInfo("https://github.com/Potatoes9411/DLSS-5-SUITE/releases/latest", UseShellExecute = true)) |> ignore
        with _ -> ()

    member this.OnClearWindowClicked(sender: obj, e: RoutedEventArgs) = this.WithEngine(fun engine -> engine.ClearWindow())

    // ----- engine commands -----
    member this.OnScreenEngineStartClicked(sender: obj, e: RoutedEventArgs) = this.WithEngine(fun engine -> engine.Start())
    member this.OnScreenEngineApplyClicked(sender: obj, e: RoutedEventArgs) = this.WithEngine(fun engine -> engine.Apply())
    member this.OnScreenEngineStopClicked(sender: obj, e: RoutedEventArgs) = this.WithEngine(fun engine -> engine.Stop())
    member this.OnScreenEngineResetClicked(sender: obj, e: RoutedEventArgs) = this.WithEngine(fun engine -> engine.ResetToDefaults())
    member this.OnScreenEngineScreenshotClicked(sender: obj, e: RoutedEventArgs) = this.WithEngine(fun engine -> engine.SaveScreenshot())
    member this.OnScreenEngineRecordClicked(sender: obj, e: RoutedEventArgs) = this.WithEngine(fun engine -> engine.ToggleRecording())
    member this.OnScreenEngineSweepClicked(sender: obj, e: RoutedEventArgs) = this.WithEngine(fun engine -> engine.StartComparisonSweep())
    member this.OnScreenEngineRefreshWindowsClicked(sender: obj, e: RoutedEventArgs) = this.WithEngine(fun engine -> engine.RefreshWindows())

    member this.OnScreenEngineCaptureFolderClicked(sender: obj, e: RoutedEventArgs) =
        async {
            let options = FolderPickerOpenOptions(Title = "Choose the Screen Engine capture folder", AllowMultiple = false)
            let! folders = this.StorageProvider.OpenFolderPickerAsync(options) |> Async.AwaitTask
            if folders.Count > 0 then
                this.WithEngine(fun engine -> engine.CaptureFolder <- folders.[0].Path.LocalPath)
        }
        |> Async.StartImmediate

    member this.OnScreenEngineModelFolderClicked(sender: obj, e: RoutedEventArgs) =
        async {
            let options = FolderPickerOpenOptions(Title = "Choose the folder holding nvngx_dlssnr.dll", AllowMultiple = false)
            let! folders = this.StorageProvider.OpenFolderPickerAsync(options) |> Async.AwaitTask
            if folders.Count > 0 then
                this.WithEngine(fun engine -> engine.ModelFolder <- folders.[0].Path.LocalPath)
        }
        |> Async.StartImmediate

    member this.OnScreenEngineBundledModelClicked(sender: obj, e: RoutedEventArgs) =
        this.WithEngine(fun engine -> engine.ModelFolder <- "")

    member this.OnScreenEngineOpenCaptureFolderClicked(sender: obj, e: RoutedEventArgs) =
        this.WithEngine(fun engine ->
            try
                Directory.CreateDirectory(engine.CaptureFolder) |> ignore
                Process.Start(ProcessStartInfo(engine.CaptureFolder, UseShellExecute = true)) |> ignore
            with _ -> ())
