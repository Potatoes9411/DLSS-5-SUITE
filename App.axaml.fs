namespace DLSS_5_MANAGER

open Avalonia
open Avalonia.Controls
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.Markup.Xaml
open DLSS_5_MANAGER.ViewModels
open DLSS_5_MANAGER.Views

type App() =
    inherit Application()

    override this.Initialize() =
        AvaloniaXamlLoader.Load(this)

    member private this.SetUpTray(desktop: IClassicDesktopStyleApplicationLifetime, window: MainWindow, vm: MainViewModel) =
        try
            let bringBack () =
                window.Show()
                if window.WindowState = Avalonia.Controls.WindowState.Minimized then
                    window.WindowState <- Avalonia.Controls.WindowState.Normal
                window.Activate()

            let openItem = NativeMenuItem("Open DLSS 5 SUITE")
            openItem.Click.Add(fun _ -> bringBack ())
            let quitItem = NativeMenuItem("Quit")
            quitItem.Click.Add(fun _ -> desktop.Shutdown())
            let menu = NativeMenu()
            menu.Items.Add(openItem)
            menu.Items.Add(quitItem)

            let tray = new TrayIcon()
            tray.ToolTipText <- "DLSS 5 SUITE"
            tray.Menu <- menu
            tray.IsVisible <- vm.IsMinimizeToTray
            try
                use stream = Avalonia.Platform.AssetLoader.Open(System.Uri("avares://DLSS 5 SUITE/Assets/logo.ico"))
                tray.Icon <- WindowIcon(stream)
            with _ -> ()
            tray.Clicked.Add(fun _ -> bringBack ())
            let icons = TrayIcons()
            icons.Add(tray)
            TrayIcon.SetIcons(this, icons)

            vm.PropertyChanged.Add(fun e ->
                if e.PropertyName = "IsMinimizeToTray" then tray.IsVisible <- vm.IsMinimizeToTray)

            window.PropertyChanged.Add(fun e ->
                if e.Property = Window.WindowStateProperty
                   && window.WindowState = Avalonia.Controls.WindowState.Minimized
                   && vm.IsMinimizeToTray then
                    window.Hide())

            desktop.Exit.Add(fun _ -> try tray.Dispose() with _ -> ())
        with ex ->
            DLSS_5_MANAGER.Services.AppLog.error "Setting up the tray icon" ex

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
                        (fun () ->
                            window.WindowState <- Avalonia.Controls.WindowState.Minimized
                            if vm.IsMinimizeToTray then window.Hide()),
                        Avalonia.Threading.DispatcherPriority.Background))
            // Minimise to the notification area, when that is switched on in
            // Settings: the window hides and the tray icon brings it back.
            this.SetUpTray(desktop, window, vm)
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
