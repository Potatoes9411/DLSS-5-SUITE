module DlssEnablerTests

// Enabler Dock's decisions, pinned without a game: the catalog line format
// the original's Worker serves, the proxy name, the settings merge, and the
// uninstall plans - including the three ways the original lost files.

open System
open System.IO
open Xunit
open DLSS_5_MANAGER.Services
open DLSS_5_MANAGER.Services.DlssEnabler

let private workerText =
    String.Join(
        "\n",
        [ "_dlss-enabler-versions:"
          "-Dlss Enabler 4.7.13.0 : \"https://pub-057af5d8cd1e4e03a7a66d12aa8bec9c.r2.dev/Dlss%20Enabler%204.7.13.0.rar\""
          "-Dlss Enabler 4.9.0.0 BETA : \"https://pub-057af5d8cd1e4e03a7a66d12aa8bec9c.r2.dev/Dlss%20Enabler%204.9.0.0%20BETA.rar\""
          "-Dlss Enabler 4.8.11.0 : \"https://pub-057af5d8cd1e4e03a7a66d12aa8bec9c.r2.dev/Dlss%20Enabler%204.8.11.0.rar\""
          "-Evil : \"https://example.com/evil.rar\""
          "-Sneaky : \"http://pub-057af5d8cd1e4e03a7a66d12aa8bec9c.r2.dev/x.rar\""
          "_streamline-versions:"
          "-streamline2.11.1 : \"https://pub-96c761d8f940430f9936a9cc0df5d829.r2.dev/streamline2.11.1.rar\""
          "_custom games config :"
          "-game : Pragmata dll: dbghelp.dll note: You must download Reframework"
          "-game : Fort Solis dll: winmm.dll note: no note"
          "-game : Bad dll: d3d12.dll note: not a proxy name" ]
    )

[<Fact>]
let ``the catalog reads the Worker's line format and drops untrusted lines`` () =
    let c = Catalog.parse workerText
    Assert.Equal<string list>([ "Dlss Enabler 4.7.13.0"; "Dlss Enabler 4.9.0.0 BETA"; "Dlss Enabler 4.8.11.0" ], c.Enablers |> List.map (fun e -> e.Name))
    Assert.Equal("Dlss Enabler 4.8.11.0.rar", c.Enablers.[2].FileName)
    Assert.Equal<string list>([ "streamline2.11.1" ], c.Streamlines |> List.map (fun e -> e.Name))
    Assert.Equal(2, c.Fixes.Length)
    Assert.Equal("", (c.Fixes |> List.find (fun f -> f.Game = "Fort Solis")).Note)
    Assert.Equal("dbghelp.dll", (c.Fixes |> List.find (fun f -> f.Game = "Pragmata")).Dll)

[<Fact>]
let ``latest skips betas and sorts by version, not text`` () =
    let c = Catalog.parse workerText
    Assert.Equal("Dlss Enabler 4.8.11.0", (Catalog.latestStable c.Enablers).Value.Name)
    Assert.Equal(Version(4, 7, 6, 10), Catalog.versionOf "Dlss Enabler 4.7.6.10")
    Assert.True(Catalog.versionOf "Dlss Enabler 4.7.12.2" > Catalog.versionOf "Dlss Enabler 4.7.6.10")

[<Fact>]
let ``the built-in catalog has the shipped versions and pins the newest`` () =
    Assert.Equal(5, Catalog.builtIn.Enablers.Length)
    Assert.True(Catalog.isPinned (Catalog.latestStable Catalog.builtIn.Enablers).Value)
    Assert.Equal(5, Catalog.builtIn.Fixes.Length)

[<Fact>]
let ``game fixes match the original's way but never on an empty title`` () =
    Assert.True(Fixes.matches "Resident Evil 9" "RESIDENT EVIL 9 Requiem" @"D:\Games\RE9")
    Assert.True(Fixes.matches "Grand Theft Auto V Enhanced" "GTA5 Enhanced" @"D:\Games\GTA5")
    Assert.False(Fixes.matches "Pragmata" "" "")
    Assert.False(Fixes.matches "Pragmata" "Elden Ring" @"D:\Games\ELDEN RING")

let private slots (taken: (string * Slot) list) name =
    taken |> List.tryFind (fun (n, _) -> n = name) |> Option.map snd |> Option.defaultValue Free

[<Fact>]
let ``the proxy follows the executable's imports`` () =
    let imports = set [ "kernel32.dll"; "winmm.dll"; "dxgi.dll" ]
    Assert.Equal(Ok("winmm.dll", "the game loads it"), chooseProxy None None imports (slots []))

[<Fact>]
let ``a name the game already uses for its own DLL is never taken`` () =
    let imports = set [ "version.dll"; "winmm.dll" ]
    let result = chooseProxy None None imports (slots [ "version.dll", Foreign ])
    Assert.Equal(Ok("winmm.dll", "the game loads it"), result)
    match chooseProxy (Some "version.dll") None imports (slots [ "version.dll", Foreign ]) with
    | Error _ -> ()
    | Ok r -> failwithf "took the game's own DLL: %A" r

[<Fact>]
let ``the order is choice, installed, fix, imports, first free`` () =
    let imports = set [ "dxgi.dll" ]
    Assert.Equal(Ok("dbghelp.dll", "your choice"), chooseProxy (Some "dbghelp.dll") (Some "winmm.dll") imports (slots [ "version.dll", Ours ]))
    Assert.Equal(Ok("version.dll", "already installed"), chooseProxy None (Some "winmm.dll") imports (slots [ "version.dll", Ours ]))
    Assert.Equal(Ok("winmm.dll", "game fix"), chooseProxy None (Some "winmm.dll") imports (slots []))
    Assert.Equal(Ok("version.dll", "first free name"), chooseProxy None None Set.empty (slots []))
    match chooseProxy None None Set.empty (slots [ for n in proxyNames -> n, Foreign ]) with
    | Error _ -> ()
    | Ok r -> failwithf "expected no free name, got %A" r

/// A minimal PE32+ image whose import table names VERSION.dll and KERNEL32.dll.
let private tinyPe () =
    let b = Array.zeroCreate<byte> 0x400
    let u16 (at: int) (v: int) = BitConverter.GetBytes(uint16 v).CopyTo(b, at)
    let u32 (at: int) (v: int) = BitConverter.GetBytes(uint32 v).CopyTo(b, at)
    u16 0 0x5A4D
    u32 0x3C 0x40
    u32 0x40 0x4550
    u16 0x44 0x8664 // machine
    u16 0x46 1 // sections
    u16 0x54 0xF0 // optional header size
    let opt = 0x58
    u16 opt 0x20B
    u32 (opt + 112 + 8) 0x1000 // directory 1: imports
    let sec = opt + 0xF0
    u32 (sec + 8) 0x200 // virtual size
    u32 (sec + 12) 0x1000 // rva
    u32 (sec + 16) 0x200 // raw size
    u32 (sec + 20) 0x200 // raw offset
    u32 (0x200 + 12) 0x1040
    u32 (0x200 + 20 + 12) 0x1050
    Text.Encoding.ASCII.GetBytes("VERSION.dll").CopyTo(b, 0x240)
    Text.Encoding.ASCII.GetBytes("KERNEL32.dll").CopyTo(b, 0x250)
    b

[<Fact>]
let ``imports are read from the executable`` () =
    let dir = Path.Combine(Path.GetTempPath(), "enabler-pe-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore
    try
        let exe = Path.Combine(dir, "game.exe")
        File.WriteAllBytes(exe, tinyPe ())
        let imports = gatherImports exe
        Assert.Contains("version.dll", imports)
        Assert.Contains("kernel32.dll", imports)
        Assert.Equal(Ok("version.dll", "the game loads it"), chooseProxy None None imports (slots []))
    finally
        Directory.Delete(dir, true)

let private template =
    [| "; DLSS Enabler Settings"; ""; "[Meta]"; "configVersion=4.8.11.0"; ""; "[UI]"; "Monitoring=false"; "ToggleKey=Tilde"
       ""; "[Performance]"; "OverdriveMode=0"; "ForceLoadDLSSG=false"; "NewKey=7" |]

[<Fact>]
let ``an upgrade keeps the user's values and the template's new keys`` () =
    let previous = [| "[Meta]"; "configVersion=4.7.13.0"; "[UI]"; "Monitoring=true"; "ToggleKey=VK_F12"; "[Performance]"; "OverdriveMode=3"; "[Custom]"; "Mine=1" |]
    let merged = Ini.merge previous template
    Assert.Equal(Some "true", Ini.tryGet merged "UI" "Monitoring")
    Assert.Equal(Some "VK_F12", Ini.tryGet merged "UI" "ToggleKey")
    Assert.Equal(Some "3", Ini.tryGet merged "Performance" "OverdriveMode")
    Assert.Equal(Some "7", Ini.tryGet merged "Performance" "NewKey")
    Assert.Equal(Some "1", Ini.tryGet merged "Custom" "Mine")
    // The new version's own stamp, not the old one's.
    Assert.Equal(Some "4.8.11.0", Ini.tryGet merged "Meta" "configVersion")
    Assert.Equal("; DLSS Enabler Settings", merged.[0])

[<Fact>]
let ``setting a value keeps comments and adds a missing key inside its section`` () =
    let lines = [| "[UI]"; "Monitoring=false ; shown top left"; ""; "[Debug]"; "Flags=0" |]
    let a = Ini.set lines "ui" "monitoring" "true"
    Assert.Equal("monitoring=true ; shown top left", a.[1])
    let b = Ini.set lines "UI" "SideBar" "true"
    Assert.Equal<string[]>([| "[UI]"; "Monitoring=false ; shown top left"; "SideBar=true"; ""; "[Debug]"; "Flags=0" |], b)
    let c = Ini.set lines "GhostBuster" "Enabled" "true"
    Assert.Equal(Some "true", Ini.tryGet c "GhostBuster" "Enabled")

[<Fact>]
let ``overdrive latency reads back as latency`` () =
    Assert.Equal(3, Options.indexOf Options.overdriveModes (Some "3") "0")
    Assert.Equal(0, Options.indexOf Options.toggleKeys None "Tilde")

let private f target backup existed : ModInstaller.InstalledFile = { TargetPath = target; BackupPath = backup; WasExisting = existed }

[<Fact>]
let ``SUITE's record is undone newest first, and keeping settings keeps the ini`` () =
    let files = [| f @"G\winmm.dll" "" false; f @"G\dlss-enabler.ini" "" false; f @"G\sl.common.dll" @"B\sl.common.dll" true |]
    Assert.Equal<Step list>([ Restore(@"B\sl.common.dll", @"G\sl.common.dll"); Remove @"G\dlss-enabler.ini"; Remove @"G\winmm.dll" ], planManifestUndo files false)
    Assert.Equal<Step list>([ Restore(@"B\sl.common.dll", @"G\sl.common.dll"); Remove @"G\winmm.dll" ], planManifestUndo files true)

[<Fact>]
let ``the old tool's install comes off without losing anything`` () =
    let exeDir = @"G"
    let names = [ "game.exe"; "version.dll"; "version.dll.old"; "dlss-enabler.ini"; "OptiScaler.ini"; "imgui.ini"
                  "dlssg_to_fsr3_amd_is_better.dll"; "fakenvapi.log"; ".nodix_dlssenabler_version" ]
    let slNames = [ "sl.common.dll"; "sl.common.dll.nodix_bak"; "sl.common.dll.nodix_added"; "sl.pcl.dll"; "sl.pcl.dll.nodix_added" ]
    let isOurs (p: string) = p.EndsWith("version.dll")
    let plan = planLegacyUndo exeDir [ exeDir, names; @"G\sl", slNames ] isOurs true
    // The game's own version.dll goes back where it was.
    Assert.Contains(PutBack(@"G\version.dll.old", @"G\version.dll"), plan)
    // Streamline: added files go, backups come back after.
    Assert.Contains(Remove @"G\sl\sl.pcl.dll", plan)
    let removeCommon = plan |> List.findIndex ((=) (Remove @"G\sl\sl.common.dll"))
    let putBackCommon = plan |> List.findIndex ((=) (PutBack(@"G\sl\sl.common.dll.nodix_bak", @"G\sl\sl.common.dll")))
    Assert.True(removeCommon < putBackCommon)
    Assert.Contains(Remove @"G\.nodix_dlssenabler_version", plan)
    // Kept settings, and nobody else's files touched.
    let touched = plan |> List.map (function Remove t -> t | PutBack(_, t) -> t | Restore(_, t) -> t)
    for other in [ "dlss-enabler.ini"; "OptiScaler.ini"; "imgui.ini"; "dlssg_to_fsr3_amd_is_better.dll"; "fakenvapi.log" ] do
        Assert.DoesNotContain(Path.Combine(exeDir, other), touched)
    // Without keep, the settings file goes - still never OptiScaler's.
    let full = planLegacyUndo exeDir [ exeDir, names ] isOurs false
    Assert.Contains(Remove @"G\dlss-enabler.ini", full)
    Assert.DoesNotContain(Remove @"G\OptiScaler.ini", full)

[<Fact>]
let ``a proxy someone else took after the old tool is left alone`` () =
    let plan = planLegacyUndo "G" [ "G", [ "winmm.dll"; "winmm.dll.old" ] ] (fun _ -> false) true
    Assert.Empty(plan)

[<Fact>]
let ``auto Streamline never goes backwards; a chosen one only skips a match`` () =
    let v a b c = ({ Major = a; Minor = b; Build = c; Revision = 0 }: GameAnalyzer.FileVer)
    Assert.True(shouldSkip false (v 2 11 1) (v 2 12 0) false)
    Assert.False(shouldSkip false (v 2 11 1) (v 2 4 0) false)
    Assert.False(shouldSkip true (v 2 11 1) (v 2 12 0) false)
    Assert.True(shouldSkip true (v 2 11 1) (v 2 11 1) false)
