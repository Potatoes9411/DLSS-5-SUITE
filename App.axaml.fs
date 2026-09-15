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
            desktop.MainWindow <- window
        | _ -> ()

        base.OnFrameworkInitializationCompleted()
