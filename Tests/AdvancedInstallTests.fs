module AdvancedInstallTests

// Advanced install, pinned without a game: which part a file belongs to,
// what a route ticks, what Apply would do, and that taking a part off or
// adding a file of the user's own stays an ordinary, reversible manifest edit.

open System
open System.IO
open Xunit
open DLSS_5_MANAGER.Services
open DLSS_5_MANAGER.Services.ModInstaller

let private tempDir () =
    let d = Path.Combine(Path.GetTempPath(), "suite-advanced-tests", Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(d) |> ignore
    d

let private manifestIn (exeDir: string) (mode: string) (files: InstalledFile[]) : InstallManifest =
    { GameId = "test"
      GameTitle = "Test"
      ExecutablePath = Path.Combine(exeDir, "game.exe")
      InstalledAtUtc = ""
      Mode = mode
      Arch = "64"
      Api = ""
      Neural = ""
      Feeder = ""
      MfgUnlock = ""
      Multipass = ""
      DeepFried = ""
      EmuApi = ""
      Advanced = ""
      UserFiles = [||]
      Files = files }

let private state (entries: (string * bool) list) : Map<string, AdvancedInstall.PartState> =
    entries
    |> List.map (fun (id, installed) ->
        (id, { AdvancedInstall.PartState.Id = id
               AdvancedInstall.PartState.Files = [| id |]
               AdvancedInstall.PartState.Installed = installed
               AdvancedInstall.PartState.Incomplete = not installed }))
    |> Map.ofList

[<Fact>]
let ``files are sorted into the parts the sheet shows`` () =
    let dir = @"C:\Games\Test"
    let c = AdvancedInstall.classify "dx12" dir []
    Assert.Equal("model", c (Path.Combine(dir, "nvngx_dlssnr.dll")))
    Assert.Equal("model", c (Path.Combine(dir, "host64", "nvngx_dlssnr.dll")))
    Assert.Equal("modelspread", c (Path.Combine(dir, "Engine", "Plugins", "nvngx_dlssnr.dll")))
    Assert.Equal("effects", c (Path.Combine(dir, "reshade-shaders", "Shaders", "UIMask.fx")))
    Assert.Equal("renodx", c (Path.Combine(dir, "renodx-dlss5.addon64")))
    Assert.Equal("renodx", c (Path.Combine(dir, "renodx-dlss5-multipass.addon64")))
    Assert.Equal("mfgunlock", c (Path.Combine(dir, "renodx-mfgunlock.addon64")))
    Assert.Equal("feed", c (Path.Combine(dir, "dlss5-feed.addon64")))
    Assert.Equal("overlay", c (Path.Combine(dir, "dlss5-overlay.ini")))
    Assert.Equal("neural", c (Path.Combine(dir, "nvngx.dll.addon64")))
    Assert.Equal("neural", c (Path.Combine(dir, "host64", "nvngx.dll.addon64")))
    Assert.Equal("deepfried", c (Path.Combine(dir, "host64", "deep-fried-chicken.addon64")))
    Assert.Equal("host64", c (Path.Combine(dir, "host64", "dlss5-feed-host64.exe")))
    Assert.Equal("reshade", c (Path.Combine(dir, "dxgi.dll")))
    Assert.Equal("reshade", c (Path.Combine(dir, "ReShade.ini")))
    Assert.Equal("dlss", c (Path.Combine(dir, "Engine", "nvngx_dlss.dll")))
    Assert.Equal("streamline", c (Path.Combine(dir, "sl.interposer.dll")))
    Assert.Equal("extras", c (Path.Combine(dir, "something.txt")))

[<Fact>]
let ``the proxy slot is OptiScaler's on its route and ReShade's elsewhere`` () =
    let dir = @"C:\Games\Test"
    Assert.Equal("optiscaler", AdvancedInstall.classify "optiscaler" dir [] (Path.Combine(dir, "dxgi.dll")))
    Assert.Equal("optiscaler", AdvancedInstall.classify "optiscaler" dir [] (Path.Combine(dir, "OptiScaler.ini")))
    Assert.Equal("reshade", AdvancedInstall.classify "dx11" dir [] (Path.Combine(dir, "dxgi.dll")))
    Assert.Equal("amd", AdvancedInstall.classify "amd" dir [] (Path.Combine(dir, "version.dll")))
    Assert.Equal("dgvoodoo", AdvancedInstall.classify "dx9" dir [] (Path.Combine(dir, "d3d9.dll")))

[<Fact>]
let ``a file the user added is theirs whatever its name`` () =
    let dir = @"C:\Games\Test"
    let mine = Path.Combine(dir, "dxgi.dll")
    Assert.Equal("userextra", AdvancedInstall.classify "dx12" dir [ mine ] mine)

[<Fact>]
let ``each route ticks what its one-click install puts in`` () =
    let opts = defaultInstallOptions
    let opti = AdvancedInstall.recommend OptiScalerMode Bit64 false false opts false
    Assert.Contains("optiscaler", opti)
    Assert.DoesNotContain("reshade", opti)
    Assert.DoesNotContain("overlay", opti)

    let dx12 = AdvancedInstall.recommend Dx12Auto Bit64 false true opts true
    Assert.Contains("reshade", dx12)
    Assert.Contains("renodx", dx12)
    Assert.Contains("feed", dx12)
    Assert.Contains("overlay", dx12)
    Assert.DoesNotContain("host64", dx12)

    let fried = AdvancedInstall.recommend Dx12Auto Bit64 false false { opts with DeepFriedChicken = true } false
    Assert.Contains("deepfried", fried)
    Assert.DoesNotContain("renodx", fried)

    let dx9x32 = AdvancedInstall.recommend Dx9 Bit32 false false opts false
    Assert.Contains("dgvoodoo", dx9x32)
    Assert.Contains("host64", dx9x32)
    Assert.DoesNotContain("renodx", dx9x32)

    let amd = AdvancedInstall.recommend AmdMode Bit64 false false opts true
    Assert.Equal<Set<string>>(set [ "amd"; "model"; "extras" ], amd)

[<Fact>]
let ``Apply installs what is ticked and missing and removes only shown unticked parts`` () =
    let st = state [ ("reshade", true); ("renodx", true); ("model", true); ("feed", false) ]
    let shown = set [ "reshade"; "renodx"; "model"; "feed"; "overlay" ]
    let wanted = set [ "reshade"; "model"; "feed"; "overlay" ]
    let (add, remove) = AdvancedInstall.changes shown wanted st
    Assert.Equal<Set<string>>(set [ "feed"; "overlay" ], add)
    Assert.Equal<Set<string>>(set [ "renodx" ], remove)

[<Fact>]
let ``Apply never takes off the model or a part the sheet did not show`` () =
    let st = state [ ("model", true); ("modelspread", true); ("extras", true) ]
    let (add, remove) = AdvancedInstall.changes (set [ "model"; "modelspread" ]) Set.empty st
    Assert.True(add.IsEmpty)
    Assert.True(remove.IsEmpty)

[<Fact>]
let ``parts that depend on the game are no reason to install`` () =
    let st = state [ ("reshade", true) ]
    let (add, _) = AdvancedInstall.changes (set [ "reshade"; "dlss"; "streamline"; "extras" ]) (set [ "reshade"; "dlss"; "streamline"; "extras" ]) st
    Assert.True(add.IsEmpty)

[<Fact>]
let ``taking a part off restores the original and deletes what was ours`` () =
    let dir = tempDir ()
    try
        let backupDir = Path.Combine(dir, "backup")
        Directory.CreateDirectory(backupDir) |> ignore
        let dxgi = Path.Combine(dir, "dxgi.dll")
        let backup = Path.Combine(backupDir, "dxgi.dll")
        File.WriteAllText(dxgi, "reshade")
        File.WriteAllText(backup, "original")
        let addon = Path.Combine(dir, "renodx-dlss5.addon64")
        File.WriteAllText(addon, "renodx")
        let model = Path.Combine(dir, "nvngx_dlssnr.dll")
        File.WriteAllText(model, "model")

        let manifest =
            manifestIn dir "dx12"
                [| { TargetPath = dxgi; BackupPath = backup; WasExisting = true }
                   { TargetPath = addon; BackupPath = ""; WasExisting = false }
                   { TargetPath = model; BackupPath = ""; WasExisting = false } |]

        let pick (ids: string list) (e: InstalledFile) =
            List.contains (AdvancedInstall.classify manifest.Mode dir [] e.TargetPath) ids

        let (updated, count) = revertEntriesIn manifest (pick [ "reshade"; "renodx" ])
        Assert.Equal(2, count)
        Assert.Equal("original", File.ReadAllText(dxgi))
        Assert.False(File.Exists(addon))
        Assert.True(File.Exists(model))
        Assert.Equal(1, updated.Files.Length)
        Assert.Equal(model, updated.Files.[0].TargetPath)
    finally
        try Directory.Delete(dir, true) with _ -> ()

[<Fact>]
let ``a file of the user's own is recorded, backed up and comes off cleanly`` () =
    let dir = tempDir ()
    try
        let backupDir = Path.Combine(dir, "backup")
        let source = Path.Combine(dir, "src", "my.addon64")
        Directory.CreateDirectory(Path.GetDirectoryName(source)) |> ignore
        File.WriteAllText(source, "mine")
        let target = Path.Combine(dir, "my.addon64")
        File.WriteAllText(target, "game's own")

        let manifest = manifestIn dir "dx12" [||]

        match addUserFileIn manifest backupDir source target with
        | Error e -> failwith e
        | Ok updated ->
            Assert.Equal("mine", File.ReadAllText(target))
            Assert.Equal("1", updated.Advanced)
            Assert.Single(updated.UserFiles) |> ignore
            Assert.Equal("userextra", AdvancedInstall.classify updated.Mode dir updated.UserFiles target)
            let state = AdvancedInstall.stateOf updated
            Assert.True(state.["userextra"].Installed)

            let (after, count) = revertEntriesIn updated (fun e -> e.TargetPath = target)
            Assert.Equal(1, count)
            Assert.Equal("game's own", File.ReadAllText(target))
            Assert.Empty(after.UserFiles)
            Assert.Empty(after.Files)
    finally
        try Directory.Delete(dir, true) with _ -> ()

[<Fact>]
let ``a file of the user's own must stay inside the game and is never the model`` () =
    let dir = tempDir ()
    try
        let source = Path.Combine(dir, "x.dll")
        File.WriteAllText(source, "x")
        let manifest = manifestIn (Path.Combine(dir, "game")) "dx12" [||]
        Directory.CreateDirectory(Path.Combine(dir, "game")) |> ignore

        match addUserFileIn manifest (Path.Combine(dir, "b")) source (Path.Combine(dir, "outside.dll")) with
        | Ok _ -> failwith "outside the game folder was accepted"
        | Error _ -> ()

        match addUserFileIn manifest (Path.Combine(dir, "b")) source (Path.Combine(dir, "game", "nvngx_dlssnr.dll")) with
        | Ok _ -> failwith "the model was accepted as a user file"
        | Error _ -> ()

        Assert.False(File.Exists(Path.Combine(dir, "game", "nvngx_dlssnr.dll")))
    finally
        try Directory.Delete(dir, true) with _ -> ()
