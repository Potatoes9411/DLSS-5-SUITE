module DataMigrationTests

// DataMigration.migrate copies the old DLSS5Manager folder into DLSS5Suite once.
// Getting it wrong either loses install records (no checkmarks, no uninstall)
// or duplicates gigabytes of backups, so each rule is pinned down here against
// throwaway folders.

open System
open System.IO
open Xunit
open DLSS_5_MANAGER.Services

let private tempRoots () =
    let root = Path.Combine(Path.GetTempPath(), "dlss5-migration-test-" + Guid.NewGuid().ToString("N"))
    let oldRoot = Path.Combine(root, "DLSS5Manager")
    let newRoot = Path.Combine(root, "DLSS5Suite")
    Directory.CreateDirectory(oldRoot) |> ignore
    root, oldRoot, newRoot

let private write (path: string) (text: string) =
    Directory.CreateDirectory(Path.GetDirectoryName(path)) |> ignore
    File.WriteAllText(path, text)

let private cleanup (root: string) =
    try Directory.Delete(root, true) with _ -> ()

[<Fact>]
let ``install records and nested files are copied`` () =
    let root, oldRoot, newRoot = tempRoots ()
    try
        write (Path.Combine(oldRoot, "Installs", "steam_1.json")) "{}"
        write (Path.Combine(oldRoot, "Cache", "Posters", "a.jpg")) "x"

        let outcome = DataMigration.migrate oldRoot newRoot

        Assert.Equal(DataMigration.Imported 2, outcome)
        Assert.True(File.Exists(Path.Combine(newRoot, "Installs", "steam_1.json")))
        Assert.True(File.Exists(Path.Combine(newRoot, "Cache", "Posters", "a.jpg")))
    finally
        cleanup root

[<Fact>]
let ``backups are left where they are`` () =
    // Install records point at their backups by absolute path, so copying them
    // would only duplicate gigabytes of original game files.
    let root, oldRoot, newRoot = tempRoots ()
    try
        write (Path.Combine(oldRoot, "Backups", "steam_1", "dxgi.dll")) "original"
        write (Path.Combine(oldRoot, "Installs", "steam_1.json")) "{}"

        DataMigration.migrate oldRoot newRoot |> ignore

        Assert.False(Directory.Exists(Path.Combine(newRoot, "Backups")))
        Assert.True(File.Exists(Path.Combine(oldRoot, "Backups", "steam_1", "dxgi.dll")))
    finally
        cleanup root

[<Fact>]
let ``files already in the new folder are never overwritten`` () =
    // A settings file this build already wrote is newer than the old one.
    let root, oldRoot, newRoot = tempRoots ()
    try
        write (Path.Combine(oldRoot, "settings.json")) "old"
        write (Path.Combine(newRoot, "settings.json")) "new"

        DataMigration.migrate oldRoot newRoot |> ignore

        Assert.Equal("new", File.ReadAllText(Path.Combine(newRoot, "settings.json")))
    finally
        cleanup root

[<Fact>]
let ``the old folder is copied, not moved`` () =
    // The original DLSS 5 MANAGER may still be using it.
    let root, oldRoot, newRoot = tempRoots ()
    try
        write (Path.Combine(oldRoot, "Installs", "steam_1.json")) "{}"

        DataMigration.migrate oldRoot newRoot |> ignore

        Assert.True(File.Exists(Path.Combine(oldRoot, "Installs", "steam_1.json")))
    finally
        cleanup root

[<Fact>]
let ``it runs only once`` () =
    // After the first import the apps are separate: installs made later by the
    // original MANAGER must not leak across.
    let root, oldRoot, newRoot = tempRoots ()
    try
        write (Path.Combine(oldRoot, "Installs", "steam_1.json")) "{}"
        DataMigration.migrate oldRoot newRoot |> ignore

        write (Path.Combine(oldRoot, "Installs", "steam_2.json")) "{}"
        let second = DataMigration.migrate oldRoot newRoot

        Assert.Equal(DataMigration.AlreadyDone, second)
        Assert.False(File.Exists(Path.Combine(newRoot, "Installs", "steam_2.json")))
    finally
        cleanup root

[<Fact>]
let ``no old folder means nothing to do`` () =
    let root, oldRoot, newRoot = tempRoots ()
    try
        Directory.Delete(oldRoot)
        Assert.Equal(DataMigration.NoLegacyFolder, DataMigration.migrate oldRoot newRoot)
        Assert.False(File.Exists(Path.Combine(newRoot, DataMigration.MarkerFileName)))
    finally
        cleanup root
