module InstallerMergeTests

// What came over from the 1.3.8 installer, pinned without a game: the RenoDX
// options per route, the emulator API pick, proxy names and renames, the
// hotkey ini edits, the RTX 40 payload choice and the excluded-games list.

open System
open System.IO
open System.Collections.Generic
open System.Text
open System.Text.Json
open Xunit
open DLSS_5_MANAGER.Models
open DLSS_5_MANAGER.Services
open DLSS_5_MANAGER.Services.ModInstaller

let private allOn =
    { MfgUnlockAddon = true
      MultipassBuild = true
      DeepFriedChicken = false
      EmulatorReShadeApi = "" }

let private tempDir () =
    let d = Path.Combine(Path.GetTempPath(), "suite-merge-tests", Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(d) |> ignore
    d

/// A file the AMD proxy check recognises: the payload's marker in the middle.
let private writeAmdProxy (path: string) =
    let bytes = Array.concat [ Array.create 64 0uy; Encoding.ASCII.GetBytes("dlssnr_on_amd"); Array.create 64 7uy ]
    File.WriteAllBytes(path, bytes)

let private amdManifest (exeDir: string) (files: InstalledFile[]) : InstallManifest =
    { GameId = "test"
      GameTitle = "Test"
      ExecutablePath = Path.Combine(exeDir, "game.exe")
      InstalledAtUtc = ""
      Mode = "amd"
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

[<Fact>]
let ``RenoDX options apply only on the 64-bit DX12 / DX11 / DX9 routes`` () =
    Assert.Equal((true, true, false, false), effectiveRenoDxOptions Dx12Auto Bit64 allOn)
    Assert.Equal((true, true, false, false), effectiveRenoDxOptions Dx9 Bit64 allOn)
    Assert.Equal((false, false, false, false), effectiveRenoDxOptions Dx11 Bit32 allOn)
    Assert.Equal((false, false, false, false), effectiveRenoDxOptions OptiScalerMode Bit64 allOn)
    Assert.Equal((false, false, false, false), effectiveRenoDxOptions Emulator Bit64 allOn)
    Assert.Equal((false, false, false, false), effectiveRenoDxOptions AmdMode Bit64 allOn)

[<Fact>]
let ``deep-fried chicken replaces RenoDX on 64-bit, rides in host64 on 32-bit, and drops Multipass`` () =
    let fried = { allOn with DeepFriedChicken = true }
    Assert.Equal((true, false, true, false), effectiveRenoDxOptions Dx12Auto Bit64 fried)
    Assert.Equal((false, false, false, true), effectiveRenoDxOptions Dx9 Bit32 fried)
    Assert.Equal((false, false, false, true), effectiveRenoDxOptions Dx11 Bit32 fried)
    Assert.Equal((false, false, false, false), effectiveRenoDxOptions Emulator Bit64 fried)

[<Fact>]
let ``the emulator API is the user's pick when ReShade knows it, else the catalogue's`` () =
    Assert.Equal("dxgi", emulatorReShadeApiFor "dxgi" (fun () -> "vulkan"))
    Assert.Equal("vulkan", emulatorReShadeApiFor " Vulkan " (fun () -> "dxgi"))
    Assert.Equal("vulkan", emulatorReShadeApiFor "" (fun () -> "vulkan"))
    Assert.Equal("d3d11", emulatorReShadeApiFor "opengl" (fun () -> "d3d11"))
    Assert.Equal("vulkan", emulatorReShadeApiFor null (fun () -> "vulkan"))

[<Fact>]
let ``a typed proxy name becomes a DLL name or nothing`` () =
    Assert.Equal("winmm.dll", normalizeProxyName "winmm")
    Assert.Equal("winmm.dll", normalizeProxyName "  winmm.DLL ")
    Assert.Equal("", normalizeProxyName "")
    Assert.Equal("", normalizeProxyName ".dll")
    Assert.Equal("", normalizeProxyName "bad|name")
    Assert.Equal("", normalizeProxyName "trailing.")

[<Fact>]
let ``renaming the AMD proxy moves the file and the manifest with it`` () =
    let dir = tempDir ()
    try
        let oldPath = Path.Combine(dir, "version.dll")
        writeAmdProxy oldPath
        let manifest = amdManifest dir [| { TargetPath = oldPath; BackupPath = ""; WasExisting = false } |]

        match renameProxyIn manifest AmdProxyDll "winmm" with
        | Error e -> failwith e
        | Ok(updated, message) ->
            let newPath = Path.Combine(dir, "winmm.dll")
            Assert.False(File.Exists(oldPath))
            Assert.True(File.Exists(newPath))
            Assert.Equal<string[]>([| newPath |], updated.Files |> Array.map (fun f -> f.TargetPath))
            Assert.Contains("version.dll -> winmm.dll", message)
    finally
        try Directory.Delete(dir, true) with _ -> ()

[<Fact>]
let ``renaming puts the game's own backed-up file back under the old name`` () =
    let dir = tempDir ()
    try
        let oldPath = Path.Combine(dir, "version.dll")
        let backup = Path.Combine(dir, "backup_version.dll")
        writeAmdProxy oldPath
        File.WriteAllText(backup, "the game's own version.dll")

        let manifest = amdManifest dir [| { TargetPath = oldPath; BackupPath = backup; WasExisting = true } |]

        match renameProxyIn manifest AmdProxyDll "dxgi.dll" with
        | Error e -> failwith e
        | Ok(updated, _) ->
            Assert.Equal("the game's own version.dll", File.ReadAllText(oldPath))
            Assert.True(File.Exists(Path.Combine(dir, "dxgi.dll")))
            // The old entry is gone: the file there is the game's again.
            Assert.DoesNotContain(updated.Files, fun f -> f.TargetPath = oldPath)
    finally
        try Directory.Delete(dir, true) with _ -> ()

[<Fact>]
let ``a rename never lands on a file the game already has`` () =
    let dir = tempDir ()
    try
        let oldPath = Path.Combine(dir, "version.dll")
        writeAmdProxy oldPath
        File.WriteAllText(Path.Combine(dir, "winmm.dll"), "game file")
        let manifest = amdManifest dir [| { TargetPath = oldPath; BackupPath = ""; WasExisting = false } |]

        match renameProxyIn manifest AmdProxyDll "winmm.dll" with
        | Ok _ -> failwith "expected a refusal"
        | Error e -> Assert.Contains("already a file in the game folder", e)

        Assert.True(File.Exists(oldPath))
        Assert.Equal("game file", File.ReadAllText(Path.Combine(dir, "winmm.dll")))
    finally
        try Directory.Delete(dir, true) with _ -> ()

[<Fact>]
let ``only an AMD install has an AMD proxy`` () =
    let dir = tempDir ()
    try
        let path = Path.Combine(dir, "version.dll")
        writeAmdProxy path
        let manifest = { amdManifest dir [| { TargetPath = path; BackupPath = ""; WasExisting = false } |] with Mode = "dx12" }
        Assert.True((proxyEntryIn manifest AmdProxyDll).IsNone)
        Assert.True((proxyEntryIn { manifest with Mode = "amd" } AmdProxyDll).IsSome)
    finally
        try Directory.Delete(dir, true) with _ -> ()

[<Fact>]
let ``an ini value is set inside its own section and nowhere else`` () =
    let lines = [ "[GENERAL]"; "KeyOverlay=1"; "[INPUT]"; "KeyOverlay=36,0,0,0"; "Other=1" ]
    let updated = setIniValueInLines lines "INPUT" "KeyOverlay" "35,0,0,0"
    Assert.Equal<string list>([ "[GENERAL]"; "KeyOverlay=1"; "[INPUT]"; "KeyOverlay=35,0,0,0"; "Other=1" ], updated)

[<Fact>]
let ``a missing key goes to the top of its section, a missing section to the end`` () =
    let lines = [ "[Menu]"; "Scale=1" ]
    Assert.Equal<string list>([ "[Menu]"; "ShortcutKey=0x24"; "Scale=1" ], setIniValueInLines lines "Menu" "ShortcutKey" "0x24")
    Assert.Equal<string list>([ "[Menu]"; "Scale=1"; ""; "[Log]"; "LogLevel=2" ], setIniValueInLines lines "Log" "LogLevel" "2")
    Assert.Equal<string list>([ "Top=1"; "[Menu]"; "Scale=1" ], setIniValueInLines lines "" "Top" "1")

[<Fact>]
let ``the hotkey names map to the codes the inis store`` () =
    Assert.Equal(0x24, reshadeKeyCode "Home")
    Assert.Equal(0x23, reshadeKeyCode "End")
    Assert.Equal(0x24, reshadeKeyCode "something else")
    Assert.Equal("auto", optiMenuKeyCode "Auto")
    Assert.Equal("0x7A", optiMenuKeyCode "F11")
    Assert.Equal("0x08", optiMenuKeyCode "Backspace")
    Assert.All(reshadeKeys, fun k -> Assert.NotEqual(0, reshadeKeyCode k))

[<Fact>]
let ``applying the ReShade key rewrites only an existing ReShade ini`` () =
    let dir = tempDir ()
    try
        applyReShadeKey dir "End"
        Assert.False(File.Exists(Path.Combine(dir, "ReShade.ini")))

        File.WriteAllLines(Path.Combine(dir, "ReShade.ini"), [| "[GENERAL]"; "PresetPath=.\\ReShadePreset.ini" |])
        applyReShadeKey dir "Page Up"
        let text = File.ReadAllLines(Path.Combine(dir, "ReShade.ini"))
        Assert.Contains("KeyOverlay=33,0,0,0", text)
        Assert.Contains("PresetPath=.\\ReShadePreset.ini", text)
    finally
        try Directory.Delete(dir, true) with _ -> ()

[<Fact>]
let ``the RTX 40 build is used only on an RTX 40 card and only when it ships`` () =
    let root = tempDir ()
    try
        Assert.Equal(optiScalerNeuralDirName, neuralPayloadDirFor true root)
        Directory.CreateDirectory(Path.Combine(root, optiScalerRtx40DirName)) |> ignore
        Assert.Equal(optiScalerRtx40DirName, neuralPayloadDirFor true root)
        Assert.Equal(optiScalerNeuralDirName, neuralPayloadDirFor false root)
        Assert.Equal(optiScalerNeuralDirName, neuralPayloadDirFor true "")
    finally
        try Directory.Delete(root, true) with _ -> ()

[<Fact>]
let ``a manifest written before the new options reads them back as off`` () =
    let json = """{ "GameId": "x", "Mode": "dx12", "Arch": "64", "Files": [] }"""
    let options = JsonSerializerOptions()
    options.PropertyNameCaseInsensitive <- true
    let m = JsonSerializer.Deserialize<InstallManifest>(json, options)
    Assert.True(String.IsNullOrWhiteSpace(m.MfgUnlock))
    Assert.True(String.IsNullOrWhiteSpace(m.Multipass))
    Assert.True(String.IsNullOrWhiteSpace(m.DeepFried))
    Assert.True(isNull m.EmuApi || m.EmuApi = "")

[<Fact>]
let ``the new add-ons are swept on removal like every other file of ours`` () =
    for name in [ mfgUnlockAddonName; renodxMultipassName; overlayAddon32Name; installRecordName ] do
        Assert.Contains(name, exclusiveArtifacts)

[<Fact>]
let ``an excluded game stays out by its executable or its title`` () =
    let set = HashSet<string>(GameScanner.excludedKeysFor @"C:\Games\A\a.exe" " Alpha ", StringComparer.OrdinalIgnoreCase)

    let game (exe: string) (title: string) : GameItem =
        { AppId = ""
          Title = title
          LauncherTypeName = "CUSTOM"
          InstallDirectory = ""
          TargetExecutablePath = exe
          TargetExecutableSize = 0L
          LocalBannerPath = ""
          LastManifestTimestamp = 0L
          UpscaleStatus = ""
          DlssVersion = ""
          FsrVersion = ""
          XessVersion = "" }

    Assert.True(GameScanner.isExcluded set (game @"c:\games\a\A.EXE" "Other"))
    Assert.True(GameScanner.isExcluded set (game "" "alpha"))
    Assert.False(GameScanner.isExcluded set (game @"C:\Games\B\b.exe" "Beta"))
    Assert.Empty(GameScanner.excludedKeysFor "" "  ")
