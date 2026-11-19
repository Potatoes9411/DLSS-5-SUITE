module SuiteCleanerTests

// Space Press's CLEAN UP tab, against a fake SUITE data folder in temp: what
// it offers, what it never offers, and that cleaning removes only that.

open System
open System.IO
open Xunit
open DLSS_5_MANAGER.Services

let private tempDir () =
    let d = Path.Combine(Path.GetTempPath(), "suite-cleaner-tests", Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(d) |> ignore
    d

let private write (path: string) (bytes: int) (age: TimeSpan) =
    Directory.CreateDirectory(Path.GetDirectoryName(path)) |> ignore
    File.WriteAllBytes(path, Array.create bytes 1uy)
    File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age)

let private old = TimeSpan.FromDays(30.0)
let private fresh = TimeSpan.FromMinutes(1.0)

[<Fact>]
let ``stale caches, old logs and old downloads are offered with their size`` () =
    let dir = tempDir ()
    try
        let root = Path.Combine(dir, "DLSS5Suite")
        let logs = Path.Combine(dir, "logs")
        write (Path.Combine(root, "library_cache_v2.json")) 100 old
        write (Path.Combine(root, "library_cache_v3.json")) 100 old
        write (Path.Combine(logs, "suite.old.log")) 50 fresh
        write (Path.Combine(logs, "suite.log")) 50 old
        write (Path.Combine(root, "Downloads", "remix", "remix.zip")) 400 old
        write (Path.Combine(root, "Downloads", "remix", "new.zip")) 400 fresh

        let items = SuiteCleaner.findIn root logs DateTime.UtcNow
        let byId id = items |> List.find (fun i -> i.Id = id)

        Assert.Equal(100L, (byId "stale").Bytes)
        Assert.Equal<string list>([ Path.Combine(logs, "suite.old.log") ], (byId "logs").Paths)
        Assert.Equal<string list>([ Path.Combine(root, "Downloads", "remix", "remix.zip") ], (byId "downloads").Paths)
    finally
        try Directory.Delete(dir, true) with _ -> ()

[<Fact>]
let ``install records, backups and anything holding a model are never offered`` () =
    let dir = tempDir ()
    try
        let root = Path.Combine(dir, "DLSS5Suite")
        let logs = Path.Combine(dir, "logs")
        write (Path.Combine(root, "Installs", "game.json")) 10 old
        write (Path.Combine(root, "Backups", "game", "abc_nvngx_dlss.dll")) 10 old
        write (Path.Combine(root, "Downloads", "GtaEnhanced", "backups", "x", "file.dll")) 10 old
        // A leftover extraction folder that happens to hold a model.
        let extract = Path.Combine(root, "Downloads", "GtaEnhanced", "extract-1")
        write (Path.Combine(extract, "nvngx_dlssnr.dll")) 10 old
        Directory.SetLastWriteTimeUtc(extract, DateTime.UtcNow - old)
        write (Path.Combine(root, "Downloads", "mfg", "my_nvngx_dlssnr_mod.dll")) 10 old

        let items = SuiteCleaner.findIn root logs DateTime.UtcNow
        let all = items |> List.collect (fun i -> i.Paths)

        Assert.DoesNotContain(all, fun p -> p.Contains("Installs") || p.Contains("Backups") || p.Contains("backups"))
        Assert.DoesNotContain(all, fun p -> p.ToLowerInvariant().Contains("dlssnr"))
        Assert.DoesNotContain(extract, all)
        Assert.True(SuiteCleaner.isProtected extract)
    finally
        try Directory.Delete(dir, true) with _ -> ()

[<Fact>]
let ``only legacy icons are offered and current ones stay`` () =
    let dir = tempDir ()
    try
        let root = Path.Combine(dir, "DLSS5Suite")
        write (Path.Combine(root, "Cache", "Icons", "aa.png")) 10 old
        write (Path.Combine(root, "Cache", "Icons", "bb_hq.png")) 10 old
        let items = SuiteCleaner.findIn root (Path.Combine(dir, "logs")) DateTime.UtcNow
        let icons = items |> List.find (fun i -> i.Id = "icons")
        Assert.Equal<string list>([ Path.Combine(root, "Cache", "Icons", "aa.png") ], icons.Paths)
    finally
        try Directory.Delete(dir, true) with _ -> ()

[<Fact>]
let ``cleaning removes exactly the listed paths and never anything outside`` () =
    let dir = tempDir ()
    try
        let root = Path.Combine(dir, "DLSS5Suite")
        let logs = Path.Combine(dir, "logs")
        let stale = Path.Combine(root, "analysis_cache_v3.json")
        write stale 100 old
        let outside = Path.Combine(dir, "Game", "game.exe")
        write outside 100 old
        let model = Path.Combine(root, "Downloads", "remix", "nvngx_dlssnr.dll")
        write model 100 old

        let forged : SuiteCleaner.CleanItem =
            { Id = "x"; Label = "x"; Detail = ""; Paths = [ stale; outside; model ]; Bytes = 300L; DefaultOn = true }

        let freed = SuiteCleaner.cleanIn root logs [ forged ]
        Assert.Equal(100L, freed)
        Assert.False(File.Exists(stale))
        Assert.True(File.Exists(outside))
        Assert.True(File.Exists(model))
    finally
        try Directory.Delete(dir, true) with _ -> ()

[<Fact>]
let ``sizes read the way the rest of Space Press writes them`` () =
    Assert.Equal("512 B", SuiteCleaner.formatBytes 512L)
    Assert.Equal("2 KB", SuiteCleaner.formatBytes 2048L)
    Assert.Equal("5 MB", SuiteCleaner.formatBytes (5L * 1048576L))
    Assert.Equal("1.5 GB", SuiteCleaner.formatBytes (1610612736L))
