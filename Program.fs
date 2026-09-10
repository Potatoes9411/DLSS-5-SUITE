namespace DLSS_5_MANAGER

open System
open Avalonia
open Avalonia.Win32

module Program =

    [<CompiledName "BuildAvaloniaApp">]
    let buildAvaloniaApp () =
        AppBuilder
            .Configure<App>()
            .UsePlatformDetect()
            // Presentation, spelled out rather than left to detection.
            //
            // WinUIComposition hands frames to DirectComposition, which presents
            // on the monitor's own vblank - on a 144/180 Hz panel that is what
            // lets the window actually run at panel rate. The default redirection
            // surface path presents through the legacy GDI-backed surface and
            // tops out well below it, which is what made scrolling on a
            // high-refresh display feel like it was stepping.
            //
            // Both lists are ordered fallbacks: if the composition path or ANGLE
            // is unavailable the next entry is used, so this cannot leave the app
            // unable to start.
            .With(
                Win32PlatformOptions(
                    CompositionMode =
                        [| Win32CompositionMode.WinUIComposition
                           Win32CompositionMode.RedirectionSurface |],
                    RenderingMode =
                        [| Win32RenderingMode.AngleEgl
                           Win32RenderingMode.Software |]
                )
            )
            .WithInterFont()
#if DEBUG
            .WithDeveloperTools()
#endif
            .LogToTrace(areas = Array.empty)

    [<EntryPoint; STAThread>]
    let main argv =
        buildAvaloniaApp().StartWithClassicDesktopLifetime(argv)
