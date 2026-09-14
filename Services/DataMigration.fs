namespace DLSS_5_MANAGER.Services

open System
open System.IO

/// Brings data over from the folder older builds used, exactly once.
///
/// Builds before the rename kept everything in %LOCALAPPDATA%\DLSS5Manager.
/// That is also where the original DLSS 5 MANAGER keeps its data, so the two
/// apps were reading and writing one another's installs - which is why the
/// folder moved to DLSS5Suite. But the move copied nothing, so a SUITE build on
/// the new folder saw an empty install list: no checkmarks, and no record of
/// mods it had put on games, so it could not uninstall them either.
///
/// What is imported:
///   * everything except Backups, and nothing that already exists at the
///     destination - a settings file this build has already written is newer
///     and wins.
///
/// Why Backups stay put: each install record stores the absolute path of its
/// backups, inside DLSS5Manager\Backups. Restoring reads that path directly, so
/// the records work from the new folder while the gigabytes of original game
/// files stay where they are - and the original MANAGER, which may be installed
/// alongside and shares those backups, keeps working too.
///
/// Copied, never moved, for the same reason. And only once: after this the two
/// apps are separate, so MANAGER's future installs do not leak across.
module DataMigration =

    [<Literal>]
    let MarkerFileName = "migrated_from_dlss5manager.txt"

    type Outcome =
        /// The import ran and copied this many files.
        | Imported of copied: int
        /// There was nothing to import from.
        | NoLegacyFolder
        /// The import had already run on an earlier launch.
        | AlreadyDone

    /// The import itself, against explicit folders so it can be exercised
    /// without touching the real ones.
    let migrate (oldRoot: string) (newRoot: string) : Outcome =
        let marker = Path.Combine(newRoot, MarkerFileName)

        if not (Directory.Exists(oldRoot)) then
            NoLegacyFolder
        elif File.Exists(marker) then
            AlreadyDone
        else
            Directory.CreateDirectory(newRoot) |> ignore
            let prefix = Path.GetFullPath(oldRoot).TrimEnd('\\', '/') + string Path.DirectorySeparatorChar
            let mutable copied = 0

            for source in Directory.EnumerateFiles(oldRoot, "*", SearchOption.AllDirectories) do
                let relative = Path.GetFullPath(source).Substring(prefix.Length)
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

            Imported copied

    /// The import as the app runs it at startup, on the real folders. A failure
    /// here must never stop the app from starting.
    let run () =
        try
            let local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            migrate (Path.Combine(local, "DLSS5Manager")) (Path.Combine(local, "DLSS5Suite")) |> ignore
        with _ ->
            ()
