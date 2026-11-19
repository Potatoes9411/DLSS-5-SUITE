namespace DLSS_5_MANAGER

open Avalonia
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.Markup.Xaml
open DLSS_5_MANAGER.ViewModels
open DLSS_5_MANAGER.Views

type App() =
    inherit Application()

    override this.Initialize() =
        AvaloniaXamlLoader.Load(this)

    override this.OnFrameworkInitializationCompleted() =
        match this.ApplicationLifetime with
        | :? IClassicDesktopStyleApplicationLifetime as desktop ->
            let vm = MainViewModel()
            if not (isNull desktop.Args) then
                let args = desktop.Args
                let updateIndex = Array.tryFindIndex (fun (a: string) -> a.Equals("--updated", System.StringComparison.OrdinalIgnoreCase)) args
                if updateIndex.IsSome && updateIndex.Value + 1 < args.Length then
                    vm.ShowUpdateSuccessMessage <- true
                    vm.UpdateSuccessVersion <- args.[updateIndex.Value + 1]
            // The Screen Engine window lives on its own, so closing the main
            // window leaves it running; the app ends with the last window.
            desktop.ShutdownMode <- Avalonia.Controls.ShutdownMode.OnLastWindowClose
            let window = MainWindow(DataContext = vm)
            vm.ScreenEngine.ControlsRequested.Add(fun () ->
                Avalonia.Threading.Dispatcher.UIThread.Post(fun () -> window.ToggleScreenEngineOverlay()))
            // Started by the Start with Windows shortcut: come up minimized,
            // and keep that shortcut pointing at this executable.
            if DLSS_5_MANAGER.Services.WindowsStartup.launchedAtStartup desktop.Args then
                window.WindowState <- Avalonia.Controls.WindowState.Minimized
                // Restoring the saved placement can bring the window back up,
                // so minimise once more after it has opened.
                window.Opened.Add(fun _ ->
                    Avalonia.Threading.Dispatcher.UIThread.Post(
                        (fun () -> window.WindowState <- Avalonia.Controls.WindowState.Minimized),
                        Avalonia.Threading.DispatcherPriority.Background))
            // "--screen": open straight into the Screen Engine, for a shortcut
            // that starts DLSS 5 on the desktop.
            if not (isNull desktop.Args)
               && desktop.Args |> Array.exists (fun a -> a.Equals("--screen", System.StringComparison.OrdinalIgnoreCase)) then
                window.Opened.Add(fun _ ->
                    try vm.ScreenEngine.Start()
                    with ex -> DLSS_5_MANAGER.Services.AppLog.error "Starting the Screen Engine from --screen" ex)
            // An exception on the window's thread is written to the log, the
            // same as the ones Program.fs already catches off it.
            Avalonia.Threading.Dispatcher.UIThread.UnhandledException.Add(fun e ->
                DLSS_5_MANAGER.Services.AppLog.error "Unhandled exception on the window's thread" e.Exception)
            System.Threading.Tasks.Task.Run(fun () ->
                try DLSS_5_MANAGER.Services.WindowsStartup.refresh () with _ -> ()) |> ignore
            desktop.MainWindow <- window
        | _ -> ()

        base.OnFrameworkInitializationCompleted()
