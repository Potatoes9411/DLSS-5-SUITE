namespace DLSS_5_MANAGER.Views

open System.Diagnostics
open System.IO
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Interactivity
open Avalonia.Markup.Xaml
open Avalonia.Platform.Storage
open DLSS_5_MANAGER.ViewModels

/// The Screen Engine's own window, opened from Settings. Its DataContext is
/// the MainViewModel, so the backdrop follows the app's theme; the controls
/// bind to ScreenEngine inside it.
type ScreenEngineWindow() as this =
    inherit Window()

    let mutable isPicking = false

    do AvaloniaXamlLoader.Load(this)

    member private this.Engine =
        match this.DataContext with
        | :? MainViewModel as vm -> Some vm.ScreenEngine
        | _ -> None

    member private this.WithEngine(action: ScreenEngineViewModel -> unit) =
        this.Engine |> Option.iter action

    member this.OnHeaderPointerPressed(sender: obj, e: PointerPressedEventArgs) =
        if e.GetCurrentPoint(this).Properties.IsLeftButtonPressed then this.BeginMoveDrag(e)

    member this.OnCloseClicked(sender: obj, e: RoutedEventArgs) = this.Close()
    member this.OnMinimizeClicked(sender: obj, e: RoutedEventArgs) = this.WindowState <- WindowState.Minimized

    // ----- crosshair: press, drag onto a window, let go -----
    member this.OnCrosshairPressed(sender: obj, e: PointerPressedEventArgs) =
        if e.GetCurrentPoint(this).Properties.IsLeftButtonPressed then
            isPicking <- true
            e.Pointer.Capture(sender :?> IInputElement)
            this.WithEngine(fun engine -> engine.TrackWindowUnderCursor false)
            e.Handled <- true

    member this.OnCrosshairMoved(sender: obj, e: PointerEventArgs) =
        if isPicking then this.WithEngine(fun engine -> engine.TrackWindowUnderCursor false)

    member this.OnCrosshairReleased(sender: obj, e: PointerReleasedEventArgs) =
        if isPicking then
            isPicking <- false
            e.Pointer.Capture(null)
            this.WithEngine(fun engine -> engine.TrackWindowUnderCursor true)
            e.Handled <- true

    member this.OnCrosshairCaptureLost(sender: obj, e: PointerCaptureLostEventArgs) = isPicking <- false

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
