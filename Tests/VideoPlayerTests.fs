module VideoPlayerTests

// The video player brings DLSS 5 up on a start-up clip and only then hands the
// real video over, so the 12-15 s NGX start (and the first-time feature build)
// never freezes the video itself. What decides "ready" is the feed's own log,
// and the settings that make the hand-over and the start quick are pinned here.

open System
open System.IO
open Xunit
open DLSS_5_MANAGER.Services.VideoPlayer

[<Fact>]
let ``the feed is ready once the feature is built or a frame is delivered`` () =
    let opening =
        "18:33:35.128  ################ feed: opening D3D12 session ################\n"
        + "18:33:50.159  ############# feed: session open #############\n"
        + "18:33:50.165  [feed] STALL frame 0: interval 15036.3 ms | feed 0.49 ms\n"
    Assert.Equal(Pending, warmStateOf opening)
    Assert.Equal(Ready, warmStateOf (opening + "18:34:30.268  [feed] feature ready: 540x225 DLAA, flags=74\n"))
    Assert.Equal(Ready, warmStateOf (opening + "18:34:30.282  [feed] frame 1 delivered (540x225 at 100% -> 540x225, reset=1)\n"))
    Assert.Equal(Pending, warmStateOf "")
    Assert.Equal(Pending, warmStateOf null)

[<Fact>]
let ``a feed that will never run is not waited for`` () =
    Assert.Equal(Off, warmStateOf "18:28:27.759  stopped: the D3D12/NGX session failed to start. The game renders normally.")
    Assert.Equal(Off, warmStateOf "18:28:27.758  [feed] NGX would not initialise on this device/driver")
    Assert.Equal(Off, warmStateOf "[feed] enabled=0: no frames are fed, no runtime is queried")
    // the config line names enabled=1 and is not a reason to stop waiting
    Assert.Equal(Pending, warmStateOf "[feed] config: enabled=1 mode=0 hdr=0")

[<Fact>]
let ``only the switched-on effects are compiled, the rest of ReShade.ini is kept`` () =
    let ini = "[GENERAL]\r\nEffectSearchPaths=.\\reshade-shaders\\Shaders\\**\r\nPerformanceMode=0\r\n\r\n[INPUT]\r\nKeyOverlay=36,0,0,0\r\n"
    let tuned = withIniKey ini "[GENERAL]" "SkipLoadingDisabledEffects" "1"
    Assert.Equal("[GENERAL]\r\nEffectSearchPaths=.\\reshade-shaders\\Shaders\\**\r\nPerformanceMode=0\r\nSkipLoadingDisabledEffects=1\r\n\r\n[INPUT]\r\nKeyOverlay=36,0,0,0\r\n", tuned)
    // set again it is replaced, not repeated
    let again = withIniKey (tuned.Replace("SkipLoadingDisabledEffects=1", "SkipLoadingDisabledEffects=0")) "[GENERAL]" "SkipLoadingDisabledEffects" "1"
    Assert.Equal(tuned, again)
    // a missing section is added
    Assert.Equal("[INPUT]\r\nKeyOverlay=36,0,0,0\r\n[GENERAL]\r\nSkipLoadingDisabledEffects=1\r\n", withIniKey "[INPUT]\r\nKeyOverlay=36,0,0,0\r\n" "[GENERAL]" "SkipLoadingDisabledEffects" "1")

[<Fact>]
let ``tuneReShade writes the key into the player's ReShade.ini`` () =
    let dir = Path.Combine(Path.GetTempPath(), "suite-videoplayer-test-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore
    try
        File.WriteAllText(Path.Combine(dir, "ReShade.ini"), "[GENERAL]\r\nSkipLoadingDisabledEffects=0\r\nNoEffectCache=0\r\n")
        tuneReShade dir
        let text = File.ReadAllText(Path.Combine(dir, "ReShade.ini"))
        Assert.Contains("SkipLoadingDisabledEffects=1", text)
        Assert.Contains("NoEffectCache=0", text)
        Assert.DoesNotContain("SkipLoadingDisabledEffects=0", text)
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``the player keeps one window, so a warm player takes every video after`` () =
    let dir = Path.Combine(Path.GetTempPath(), "suite-videoplayer-test-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore
    try
        let ini = Path.Combine(dir, "mpc-hc64.ini")
        File.WriteAllText(ini, "[Settings]\r\nAllowMultipleInstances=1\r\nDSVidRen=0\r\nVolume=40\r\n")
        writeIni dir
        let lines = File.ReadAllLines ini |> Array.map (fun l -> l.TrimStart('﻿'))
        Assert.Contains("AllowMultipleInstances=0", lines)
        Assert.Contains("DSVidRen=14", lines)
        Assert.Contains("Volume=40", lines)
        Assert.DoesNotContain("AllowMultipleInstances=1", lines)
    finally
        Directory.Delete(dir, true)
