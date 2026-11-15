module MotionForgeTests

open System
open Xunit
open DLSS_5_MANAGER.Models
open DLSS_5_MANAGER.Services

let private lines o = MotionCommand.optionLines [||] o |> dict
let private d = MotionPrefs.defaultOptions

// ---- What the engine is told -------------------------------------------

[<Fact>]
let ``SMAI goes to the engine as SMFG with the network on`` () =
    let l = lines { d with Generator = 3 }
    Assert.Equal("2", l["generator"])
    Assert.Equal("1", l["msm_ai"])
    let s = lines { d with Generator = 2 }
    Assert.Equal("2", s["generator"])
    Assert.Equal("0", s["msm_ai"])

[<Fact>]
let ``a fractional multiplier is sent as a ratio over x6`` () =
    let l = lines { d with Adaptive = false; MultiplierCustom = 25 }
    Assert.Equal("6", l["multiplier"])
    Assert.Equal("25", l["ratio"])
    let whole = lines { d with Adaptive = false; Mode = 1; MultiplierCustom = 0 }
    Assert.Equal("3", whole["multiplier"])
    Assert.Equal("0", whole["ratio"])

[<Fact>]
let ``adaptive with no target asks for the screen's refresh`` () =
    Assert.Equal("1", (lines { d with Adaptive = true; AdaptiveFps = 0 })["adaptive"])
    Assert.Equal("144", (lines { d with Adaptive = true; AdaptiveFps = 144 })["adaptive"])
    Assert.Equal("0", (lines { d with Adaptive = false; AdaptiveFps = 144 })["adaptive"])

[<Fact>]
let ``the DLSS preset is remapped into the engine's order`` () =
    let presets = [ 0; 1; 2; 3 ] |> List.map (fun p -> (lines { d with DlssPreset = p })["dlssg_preset"])
    Assert.Equal<string list>([ "0"; "3"; "2"; "1" ], presets)

[<Fact>]
let ``motion source numbers follow each generator`` () =
    Assert.Equal("2", (lines { d with Generator = 0; MotionSource = 0 })["motion"])
    Assert.Equal("0", (lines { d with Generator = 0; MotionSource = 1 })["motion"])
    Assert.Equal("1", (lines { d with Generator = 0; MotionSource = 2 })["motion"])
    Assert.Equal("1", (lines { d with Generator = 1; XessMotionSource = 1 })["motion"])
    Assert.Equal("0", (lines { d with Generator = 1; XessMotionSource = 2 })["motion"])
    Assert.Equal("2", (lines { d with Generator = 2; MotionSource = 1 })["motion"])

[<Fact>]
let ``SMT is its own switch with SMFG and the assist with DLSS or XeSS`` () =
    Assert.Equal("1", (lines { d with Generator = 2; TriangleMesh = true; SmtAssist = false })["msm_tri"])
    Assert.Equal("0", (lines { d with Generator = 0; TriangleMesh = true; SmtAssist = false })["msm_tri"])
    Assert.Equal("1", (lines { d with Generator = 1; SmtAssist = true })["msm_tri"])
    Assert.Equal("0", (lines { d with Generator = 3; TriangleMesh = true; SmtAssist = true })["msm_tri"])

[<Fact>]
let ``the SMT gear sends the engine's own values, defaults when unset`` () =
    let l = lines d
    Assert.Equal("16", l["tri_pitch"])
    Assert.Equal("4", l["tri_home"])
    Assert.Equal("3", l["tri_top"])
    Assert.Equal("32", (lines { d with TriPitch = 5 })["tri_pitch"])

[<Fact>]
let ``the preferred GPU is sent only when the generator runs on it`` () =
    let cards = [| { Index = 0; Dlss = false; Xess = true; Name = "AMD Radeon" }
                   { Index = 1; Dlss = true; Xess = true; Name = "NVIDIA RTX" } |]
    Assert.Equal("1", (MotionCommand.optionLines cards { d with Gpu = "NVIDIA RTX"; Generator = 0 } |> dict)["gpu"])
    Assert.Equal("-1", (MotionCommand.optionLines cards { d with Gpu = "AMD Radeon"; Generator = 0 } |> dict)["gpu"])
    Assert.Equal("0", (MotionCommand.optionLines cards { d with Gpu = "AMD Radeon"; Generator = 2 } |> dict)["gpu"])

[<Fact>]
let ``no option line asks for the removed tensor-core XeSS`` () =
    Assert.False((lines d).ContainsKey "xess_tensor")

// ---- The start file ------------------------------------------------------

[<Fact>]
let ``the start file has one line per key, no remarks and the owner`` () =
    let text = MotionCommand.startFile [ "delay", "5"; "gpu", "-1" ] [ "aml_say0", "a # b\nc" ] true 4242 false
    let rows = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
    Assert.Contains("delay=5", rows)
    Assert.Contains("aml_say0=a   b c", rows)
    Assert.Contains("log=1", rows)
    Assert.Contains("owner=4242", rows)
    Assert.DoesNotContain('#', text)

[<Fact>]
let ``started by the shortcut there is no countdown`` () =
    let text = MotionCommand.startFile [ "delay", "5" ] [] false 1 true
    Assert.Contains("delay=0\n", text)
    Assert.Contains("log=0\n", text)

[<Fact>]
let ``only the lines that moved are sent again`` () =
    let before = MotionCommand.optionLines [||] d
    let after = MotionCommand.optionLines [||] { d with Sharpness = 80; Vrr = true }
    let moved = MotionCommand.changes before after |> List.map fst |> Set.ofList
    Assert.Equal<Set<string>>(set [ "sharpness"; "vrr" ], moved)
    Assert.Empty(MotionCommand.changes after after)

// ---- The engine's status lines ---------------------------------------------

let private fold (lines: string list) = lines |> List.fold MotionStatus.apply MotionStatus.idle

[<Fact>]
let ``a run from picking to running is read`` () =
    let s = fold [ "@state starting"; "@pick 4"; "@state picking" ]
    Assert.Equal(Picking, s.Phase)
    Assert.Equal(4, s.Countdown)
    let s = fold [ "@pick 3"; "@state running"; "@source 1a2b 2560 1440 wgc borderless"; "@fps 60 180"; "@mfg 3" ]
    Assert.Equal(Running, s.Phase)
    Assert.Equal(0, s.Countdown)
    Assert.Equal((2560, 1440), (s.Width, s.Height))
    Assert.Equal("wgc", s.Api)
    Assert.Equal("borderless", s.Placement)
    Assert.Equal((60, 180), (s.BaseFps, s.OutputFps))
    Assert.Equal(3, s.Multiplier)

[<Fact>]
let ``frame generation unavailable keeps the engine's reason`` () =
    let s = fold [ "@mfg unavailable no nvngx_dlssg.dll was picked" ]
    Assert.True(s.MfgUnavailable)
    Assert.Equal("no nvngx_dlssg.dll was picked", s.MfgWhy)
    let s = MotionStatus.apply s "@mfg 2"
    Assert.False(s.MfgUnavailable)

[<Fact>]
let ``learning, vrr and the first error are read; other lines are not`` () =
    Assert.Equal(40, (fold [ "@aml learning 40" ]).Learning)
    Assert.Equal(101, (fold [ "@aml learned 100 35" ]).Learning)
    Assert.Equal(-1, (fold [ "@aml learning 40"; "@aml off" ]).Learning)
    Assert.True((fold [ "@vrr fixed" ]).VrrFixed)
    Assert.Equal("nowindow", (fold [ "@error nowindow No game"; "@error closed" ]).Error)
    Assert.Equal(MotionStatus.idle, fold [ "warm 12 shaders"; ""; "@unknown 1"; "@fps" ])

[<Fact>]
let ``cards are listed with XeSS's own answer once its files are there`` () =
    let said = [ "@card 0 0 290 AMD Radeon(TM) 880M Graphics"; "@xess 0 0"; "@card 1 1 11944 NVIDIA GeForce RTX 5070 Ti Laptop GPU"; "@xess 1 1"; "@card 2 1 8000 NVIDIA GeForce RTX 5070 Ti Laptop GPU" ]
    let known = MotionStatus.parseCards true said
    Assert.Equal(3, known.Length)
    Assert.False((known[0]).Dlss)
    Assert.False((known[0]).Xess)
    Assert.True((known[1]).Dlss && (known[1]).Xess)
    Assert.Equal("NVIDIA GeForce RTX 5070 Ti Laptop GPU (2)", (known[2]).Name)
    // Without XeSS's files the engine cannot ask Intel's library: every card is offered.
    let unknown = MotionStatus.parseCards false said
    Assert.True(unknown.[0].Xess)

// ---- Saved preferences -----------------------------------------------------

[<Fact>]
let ``preferences round-trip through their file`` () =
    let p = { MotionPrefs.defaults with
                EngineLog = true
                Hotkey = MotionPrefs.defaultHotkey
                Profiles = Array.append MotionPrefs.defaults.Profiles [| { Id = "x"; Name = "Fast games"; Options = { d with Generator = 0; Mode = 4; Vrr = true; CropLeft = 12 } } |]
                SelectedProfile = "x" }
    let back = MotionPrefs.deserialize (MotionPrefs.serialize p)
    Assert.Equal(p, back)

[<Fact>]
let ``a broken or empty file reads as the defaults with Default kept`` () =
    Assert.Equal(MotionPrefs.defaults, MotionPrefs.deserialize "not json")
    let p = MotionPrefs.deserialize """{"Version":1,"Profiles":[{"Id":"a","Name":"  Mine  ","Options":null}],"SelectedProfile":"zzz"}"""
    Assert.Equal("default", p.Profiles[0].Id)
    Assert.Equal("Mine", p.Profiles[1].Name)
    Assert.Equal(MotionPrefs.defaultOptions, p.Profiles[1].Options)
    Assert.Equal("default", p.SelectedProfile)

[<Fact>]
let ``values out of range are brought back in`` () =
    let o = MotionPrefs.normalizeOptions { d with Flow = 5; Mode = 9; MultiplierCustom = 7; FrameLimit = 10; AdaptiveFps = 5000; CropTop = 99999; SmsrBoost = 300; GameType = 42 }
    Assert.Equal(25, o.Flow)
    Assert.Equal(4, o.Mode)
    Assert.True(o.Adaptive)
    Assert.Equal(0, o.MultiplierCustom)
    Assert.Equal(0, o.FrameLimit)
    Assert.Equal(0, o.AdaptiveFps)
    Assert.Equal(MotionPrefs.cropMax, o.CropTop)
    Assert.Equal(100, o.SmsrBoost)
    Assert.Equal(MotionPrefs.gameCustom, o.GameType)

[<Fact>]
let ``MFG SUPER MOTION's schema migrations are kept`` () =
    // Schema 19: WGC stays, Lock to refresh is off again (20 then 21), Super Smooth off (24).
    let o = MotionPrefs.normalizeOptions { d with Schema = 19; CaptureApi = 0; LockRefresh = true; UltraSmooth = true }
    Assert.Equal(0, o.CaptureApi)
    Assert.False(o.LockRefresh)
    Assert.False(o.UltraSmooth)
    Assert.Equal(MotionPrefs.schema, o.Schema)
    // Schema 10: SyncMode 0 is read as "default", the search starts on Fast, preset Quality.
    let o = MotionPrefs.normalizeOptions { d with Schema = 10; SyncMode = 0; MsmSearch = 0; DlssPreset = 0 }
    Assert.Equal(1, o.SyncMode)
    Assert.Equal(1, o.MsmSearch)
    Assert.Equal(2, o.DlssPreset)
    // Schema 1: only the first card's values survive.
    let o = MotionPrefs.normalizeOptions { d with Schema = 1; Mode = 3; Sharpness = 77; Vrr = true; Generator = 1 }
    Assert.Equal(3, o.Mode)
    Assert.Equal(77, o.Sharpness)
    Assert.False(o.Vrr)
    Assert.Equal(d.Generator, o.Generator)
    // Schema 23: SMT stays as set, Super Smooth goes off once.
    let o = MotionPrefs.normalizeOptions { d with Schema = 23; TriangleMesh = true; UltraSmooth = true }
    Assert.True(o.TriangleMesh)
    Assert.False(o.UltraSmooth)

[<Fact>]
let ``effective flow follows the game type and XeSS's floor`` () =
    Assert.Equal(75, MotionPrefs.effectiveFlow { d with GameType = 1; Generator = 2 })
    Assert.Equal(60, MotionPrefs.effectiveFlow { d with GameType = 0; Generator = 1 })
    Assert.Equal(88, MotionPrefs.effectiveFlow { d with GameType = MotionPrefs.gameCustom; Flow = 88 })

// ---- One overlay at a time ---------------------------------------------------

[<Fact>]
let ``claiming the overlay stops every other owner and not the claimer`` () =
    let stopped = Collections.Generic.List<string>()
    OverlayArbiter.register "test-a" (fun () -> stopped.Add "a"; true)
    OverlayArbiter.register "test-b" (fun () -> stopped.Add "b"; true)
    OverlayArbiter.register "test-idle" (fun () -> stopped.Add "idle"; false)
    let others = OverlayArbiter.claim "test-a"
    Assert.Contains("test-b", others)
    Assert.DoesNotContain("test-a", others)
    // Asked to stop, but it was not running: not reported as stopped.
    Assert.DoesNotContain("test-idle", others)
    Assert.Contains("idle", stopped)
    Assert.Contains("b", stopped)
    Assert.DoesNotContain("a", stopped)

// ---- DLSS MFG through NVIDIA Streamline ------------------------------------

[<Fact>]
let ``DLSS MFG is told where SUITE keeps Streamline; the other generators are not`` () =
    let dir = @"C:\SUITE\engine"
    let dlss = MotionCommand.withStreamline dir (MotionCommand.optionLines [||] { d with Generator = 0 }) |> dict
    Assert.Equal(dir, dlss["sl_dir"])
    for g in [ 1; 2; 3 ] do
        let other = MotionCommand.withStreamline dir (MotionCommand.optionLines [||] { d with Generator = g }) |> dict
        Assert.False(other.ContainsKey "sl_dir")

[<Fact>]
let ``the Streamline files missing from a folder are named in the engine's order`` () =
    let folder = @"C:\SUITE\engine"
    let present = set [ IO.Path.Combine(folder, "sl.interposer.dll"); IO.Path.Combine(folder, "sl.common.dll"); IO.Path.Combine(folder, "sl.pcl.dll") ]
    Assert.Equal<string list>([ "sl.dlss_g.dll"; "sl.reflex.dll"; "nvngx_dlssg.dll" ], MotionFiles.missingStreamline present.Contains folder)
    Assert.Empty(MotionFiles.missingStreamline (fun _ -> true) folder)
    Assert.Equal(6, (MotionFiles.missingStreamline (fun _ -> false) folder).Length)

[<Fact>]
let ``the engine's word that Streamline cannot start reaches the page`` () =
    let s = MotionStatus.apply MotionStatus.idle "@mfg unavailable DLSS Frame Generation is not supported on this graphics card (it needs an RTX 40 or 50) (Streamline 3)"
    Assert.True(s.MfgUnavailable)
    Assert.StartsWith("DLSS Frame Generation is not supported", s.MfgWhy)

// ---- SUITE FG and FSR 3.1 FG -------------------------------------------------

[<Fact>]
let ``SUITE FG and FSR FG go to the engine as its generators 3 and 4`` () =
    Assert.Equal("3", (lines { d with Generator = MotionPrefs.suiteFg })["generator"])
    Assert.Equal("4", (lines { d with Generator = MotionPrefs.fsrFg })["generator"])
    for g in [ MotionPrefs.suiteFg; MotionPrefs.fsrFg ] do
        let l = lines { d with Generator = g; TriangleMesh = true; SmtAssist = true }
        Assert.Equal("0", l["msm_ai"])
        Assert.Equal("0", l["msm_tri"])
    Assert.Equal<int list>([ 0; 1; 2; 2; 3; 4 ], [ 0 .. 5 ] |> List.map MotionCommand.engineGenerator)

[<Fact>]
let ``SUITE FG is always drawn along a motion field of the engine's`` () =
    Assert.Equal("1", (lines { d with Generator = MotionPrefs.suiteFg; SuiteMotionSource = 0 })["motion"])
    Assert.Equal("0", (lines { d with Generator = MotionPrefs.suiteFg; SuiteMotionSource = 1 })["motion"])
    // (Whatever DLSS MFG's own choice says.)
    Assert.Equal("1", (lines { d with Generator = MotionPrefs.suiteFg; MotionSource = 0 })["motion"])

[<Fact>]
let ``FSR FG takes the GPU estimate, its own optical flow or NVIDIA Optical Flow`` () =
    Assert.Equal("1", (lines { d with Generator = MotionPrefs.fsrFg; FsrMotionSource = 0 })["motion"])
    Assert.Equal("2", (lines { d with Generator = MotionPrefs.fsrFg; FsrMotionSource = 1 })["motion"])
    Assert.Equal("0", (lines { d with Generator = MotionPrefs.fsrFg; FsrMotionSource = 2 })["motion"])

[<Fact>]
let ``only FSR FG is told where SUITE keeps AMD's runtime`` () =
    let dir = @"C:\SUITE\engine"
    let fsr = MotionCommand.withFidelityFx dir (MotionCommand.optionLines [||] { d with Generator = MotionPrefs.fsrFg }) |> dict
    Assert.Equal(dir, fsr["ffx_dir"])
    for g in [ 0; 1; 2; 3; MotionPrefs.suiteFg ] do
        let other = MotionCommand.withFidelityFx dir (MotionCommand.optionLines [||] { d with Generator = g }) |> dict
        Assert.False(other.ContainsKey "ffx_dir")
    // (And Streamline stays DLSS MFG's alone.)
    Assert.False((MotionCommand.withStreamline dir (MotionCommand.optionLines [||] { d with Generator = MotionPrefs.fsrFg }) |> dict).ContainsKey "sl_dir")

[<Fact>]
let ``the new generators run on any card`` () =
    let amd = { Index = 0; Dlss = false; Xess = false; Name = "AMD Radeon" }
    Assert.True(MotionCommand.runsOn MotionPrefs.suiteFg amd)
    Assert.True(MotionCommand.runsOn MotionPrefs.fsrFg amd)
    Assert.Equal("0", (MotionCommand.optionLines [| amd |] { d with Gpu = "AMD Radeon"; Generator = MotionPrefs.fsrFg } |> dict)["gpu"])

[<Fact>]
let ``FSR 3.1 goes no higher than x2; the others to x6`` () =
    Assert.Equal(2, MotionPrefs.mostMultiplier MotionPrefs.fsrFg)
    for g in [ 0; 1; 2; 3; MotionPrefs.suiteFg ] do Assert.Equal(6, MotionPrefs.mostMultiplier g)

[<Fact>]
let ``a profile keeps the new generators and their motion choices, within range`` () =
    let n = MotionPrefs.normalizeOptions { d with Generator = MotionPrefs.fsrFg; SuiteMotionSource = 7; FsrMotionSource = -3 }
    Assert.Equal(MotionPrefs.fsrFg, n.Generator)
    Assert.Equal(1, n.SuiteMotionSource)
    Assert.Equal(0, n.FsrMotionSource)
    Assert.Equal(MotionPrefs.generators - 1, (MotionPrefs.normalizeOptions { d with Generator = 99 }).Generator)
    // A file written before they existed reads them as their defaults.
    let old = MotionPrefs.deserialize """{ "Version": 1, "Profiles": [ { "Id": "default", "Name": "Default", "Options": { "Generator": 2, "Schema": 24 } } ] }"""
    Assert.Equal(0, old.Profiles[0].Options.SuiteMotionSource)
    Assert.Equal(0, old.Profiles[0].Options.FsrMotionSource)
