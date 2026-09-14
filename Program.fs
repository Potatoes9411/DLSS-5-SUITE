namespace DLSS_5_MANAGER

open System
open System.IO
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

    /// Brings data over from the folder older builds used, exactly once.
    ///
    /// Builds before the rename kept everything in %LOCALAPPDATA%\DLSS5Manager.
    /// That is also where the original DLSS 5 MANAGER keeps its data, so the two
    /// apps were reading and writing one another's installs - which is why the
    /// folder moved to DLSS5Suite. But the move copied nothing, so a SUITE build
    /// on the new folder saw an empty install list: no checkmarks, and no record
    /// of mods it had put on games, so it could not uninstall them either.
    ///
    /// What is imported:
    ///   * everything except Backups, and nothing that already exists here - a
    ///     settings file this build has already written is newer and wins.
    ///
    /// Why Backups stay put: each install record stores the absolute path of
    /// its backups, inside DLSS5Manager\Backups. Restoring reads that path
    /// directly, so the records work from here while the 5 GB of original game
    /// files stay where they are - and the original MANAGER, which is installed
    /// alongside this and shares those backups, keeps working too.
    ///
    /// Copied, never moved, for the same reason. And only once: after this the
    /// two apps are separate, so MANAGER's future installs do not leak in.
    let private migrateLegacyData () =
        try
            let local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            let oldRoot = Path.Combine(local, "DLSS5Manager")
            let newRoot = Path.Combine(local, "DLSS5Suite")
            let marker = Path.Combine(newRoot, "migrated_from_dlss5manager.txt")

            if Directory.Exists(oldRoot) && not (File.Exists(marker)) then
                Directory.CreateDirectory(newRoot) |> ignore
                let prefix = Path.GetFullPath(oldRoot).TrimEnd('\\') + "\\"
                let mutable copied = 0

                for source in Directory.EnumerateFiles(oldRoot, "*", SearchOption.AllDirectories) do
                    let relative = source.Substring(prefix.Length)
                    let top = relative.Split([| '\\'; '/' |]).[0]

                    if not (top.Equals("Backups", StringComparison.OrdinalIgnoreCase)) then
                        let target = Path.Combine(newRoot, relative)

                        if not (File.Exists(target)) then
                            try
                                Directory.CreateDirectory(Path.GetDirectoryName(target)) |> ignore
                                File.Copy(source, target, false)
                                copied <- copied + 1
                            with _ ->
                                ()

                File.WriteAllText(
                    marker,
                    sprintf "Imported %d file(s) from %s on %s" copied oldRoot (DateTime.Now.ToString("u")))
        with _ ->
            // A failed import must never stop the app from starting.
            ()

    [<EntryPoint; STAThread>]
    let main argv =
        migrateLegacyData ()
        buildAvaloniaApp().StartWithClassicDesktopLifetime(argv)
