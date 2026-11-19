module ScreenEngineTests

// The engine reads SUITE's settings as command-line options, and it refuses to
// start on any value its parser rejects. A refusal looks like the feature simply
// not working, so the translation and its safety rules are pinned down here.

open System.Globalization
open System.Threading
open Xunit
open DLSS_5_MANAGER.Services
open DLSS_5_MANAGER.Services.ScreenEngine

let private args s = toArguments "" "" s

/// The value an option was given, written either "--name value" or
/// "--name=value" (the engine's parser takes both), or None.
let private valueOf (option: string) (arguments: string list) =
    let joined =
        arguments
        |> List.tryPick (fun a -> if a.StartsWith(option + "=") then Some(a.Substring(option.Length + 1)) else None)
    match joined with
    | Some v -> Some v
    | None ->
        arguments
        |> List.pairwise
        |> List.tryFind (fun (name, _) -> name = option)
        |> Option.map snd

[<Fact>]
let ``the engine always starts without its own control panel`` () =
    let a = args defaults
    Assert.Equal(Some "off", valueOf "--gui" a)

[<Fact>]
let ``the bundled model folder is used unless another one is chosen`` () =
    let bundled = toArguments "" @"C:\SUITE\mod files\dlss 5" defaults
    Assert.Equal(Some @"C:\SUITE\mod files\dlss 5", valueOf "--ngx-path" bundled)
    let chosen = toArguments "" @"C:\SUITE\mod files\dlss 5" { defaults with Advanced = { advancedDefaults with ModelFolder = @"D:\Signed Model" } }
    Assert.Equal(Some @"D:\Signed Model", valueOf "--ngx-path" chosen)

[<Fact>]
let ``decimals are written with a dot whatever the system locale`` () =
    // In a comma-decimal locale "1.5" would otherwise become "1,5", which the
    // engine rejects as an invalid number.
    let previous = Thread.CurrentThread.CurrentCulture
    try
        Thread.CurrentThread.CurrentCulture <- CultureInfo("de-DE")
        let a = args { defaults with LocalStructure = 1.5; Intensity = 0.25 }
        Assert.Equal(Some "1.5", valueOf "--nr-local-structure" a)
        Assert.Equal(Some "0.25", valueOf "--nr-intensity" a)
    finally
        Thread.CurrentThread.CurrentCulture <- previous

[<Fact>]
let ``intensity is kept between 0 and 1`` () =
    Assert.Equal(Some "1", valueOf "--nr-intensity" (args { defaults with Intensity = 5.0 }))
    Assert.Equal(Some "0", valueOf "--nr-intensity" (args { defaults with Intensity = -2.0 }))

[<Fact>]
let ``local structure and tone are not capped`` () =
    // Values well past 1 are a deliberate feature of the engine.
    let a = args { defaults with LocalStructure = 20.0; LocalTone = 3.0 }
    Assert.Equal(Some "20", valueOf "--nr-local-structure" a)
    Assert.Equal(Some "3", valueOf "--nr-local-tone" a)

[<Fact>]
let ``a value that is not a number falls back instead of reaching the engine`` () =
    let a = args { defaults with LocalTone = nan; Intensity = infinity }
    Assert.Equal(Some "1", valueOf "--nr-local-tone" a)
    Assert.Equal(Some "1", valueOf "--nr-intensity" a)

[<Fact>]
let ``skin following structure is the engine's -1`` () =
    Assert.Equal(Some "-1", valueOf "--nr-skin" (args { defaults with Skin = FollowStructure }))
    Assert.Equal(Some "0.8", valueOf "--nr-skin" (args { defaults with Skin = SkinStrength 0.8 }))

[<Fact>]
let ``a negative skin strength becomes zero rather than a meaningless value`` () =
    Assert.Equal(Some "0", valueOf "--nr-skin" (args { defaults with Skin = SkinStrength -0.5 }))

[<Fact>]
let ``passes never drop below one`` () =
    Assert.Equal(Some "1", valueOf "--nr-passes" (args { defaults with Passes = 0 }))

[<Fact>]
let ``frame generation is written as the engine's multiplier and kept within one to six`` () =
    Assert.Equal(Some "1", valueOf "--frame-gen" (args defaults))
    Assert.Equal(Some "4", valueOf "--frame-gen" (args { defaults with FrameGen = 4 }))
    Assert.Equal(Some "1", valueOf "--frame-gen" (args { defaults with FrameGen = 0 }))
    Assert.Equal(Some "6", valueOf "--frame-gen" (args { defaults with FrameGen = 7 }))

[<Fact>]
let ``SUITE's blend makes the pictures between unless another generator is picked, and picking one restarts the overlay`` () =
    Assert.Equal(Some "blend", valueOf "--frame-gen-method" (args defaults))
    Assert.Equal(None, valueOf "--nvidia-frame-gen" (args defaults))
    // A generator is set up with the session, so no running session takes another.
    for g, _ in FrameGenerator.all do
        if g <> OwnBlend then Assert.False(liveFieldsOnly defaults { defaults with Generator = g })

[<Fact>]
let ``every generator is written as the engine's name for it`` () =
    let expected =
        [ OwnBlend, "blend"; NvidiaDlssg, "nvidia"; Smfg, "smfg"; Smt, "smt"; Smai, "smai"; XessMfg, "xess"; Fsr31, "fsr"
          RepeatFrames, "repeat"; Crossfade, "crossfade"; Memc, "memc"; Dis, "dis"; MotionBlur, "blur"; Spacewarp, "spacewarp" ]
    for g, name in expected do
        Assert.Equal(Some name, valueOf "--frame-gen-method" (args { defaults with Generator = g }))
        Assert.Equal(Some g, FrameGenerator.ofName name)
    Assert.Equal(None, FrameGenerator.ofName "dlss4")

[<Fact>]
let ``only Motion Forge's generators are forge ones`` () =
    Assert.Equal<FrameGenerator list>(
        [ Smfg; Smt; Smai; XessMfg; Fsr31; RepeatFrames; Crossfade; Memc; Dis; MotionBlur; Spacewarp ],
        FrameGenerator.all |> List.map fst |> List.filter FrameGenerator.isForge)

[<Fact>]
let ``SUITE's more generators look for the motion at Flow's share, all to x6, and are saved by name`` () =
    Assert.Equal<FrameGenerator list>(
        [ Smfg; Smt; Memc; Dis; MotionBlur; Spacewarp ],
        FrameGenerator.all |> List.map fst |> List.filter FrameGenerator.usesFlow)
    for g in [ RepeatFrames; Crossfade; Memc; Dis; MotionBlur; Spacewarp ] do
        Assert.Equal(6, FrameGenerator.mostMultiplier g)
        let a = args { defaults with Generator = g; FrameGen = 3; Forge = { forgeDefaults with Flow = 50 } }
        Assert.Equal(Some "3", valueOf "--frame-gen" a)
        Assert.Equal(Some "50", valueOf "--forge-flow" a)
    Assert.Equal(2, FrameGenerator.mostMultiplier Fsr31)

[<Fact>]
let ``Motion Forge's settings are written as the engine's options, flow kept within ten and a hundred`` () =
    Assert.Equal(Some "35", valueOf "--forge-flow" (args defaults))
    Assert.Equal(Some "on", valueOf "--forge-keep-still" (args defaults))
    Assert.Equal(Some "off", valueOf "--forge-light-search" (args defaults))
    Assert.Equal(Some "off", valueOf "--forge-rife" (args defaults))
    let a = args { defaults with Generator = Smai; Forge = { Flow = 70; KeepStill = false; LightSearch = true; Rife = true } }
    Assert.Equal(Some "70", valueOf "--forge-flow" a)
    Assert.Equal(Some "off", valueOf "--forge-keep-still" a)
    Assert.Equal(Some "on", valueOf "--forge-light-search" a)
    Assert.Equal(Some "on", valueOf "--forge-rife" a)
    Assert.Equal(Some "10", valueOf "--forge-flow" (args { defaults with Forge = { forgeDefaults with Flow = 3 } }))
    Assert.Equal(Some "100", valueOf "--forge-flow" (args { defaults with Forge = { forgeDefaults with Flow = 400 } }))
    Assert.False(liveFieldsOnly defaults { defaults with Forge = { forgeDefaults with Flow = 50 } })

[<Fact>]
let ``a settings file from when NVIDIA frame gen was a switch loads as NVIDIA's generator`` () =
    Assert.Equal(NvidiaDlssg, (deserialize """{ "NvidiaFrameGen": true }""").Generator)
    Assert.Equal(OwnBlend, (deserialize """{ "NvidiaFrameGen": false }""").Generator)
    Assert.Equal(Smt, (deserialize """{ "Generator": "smt" }""").Generator)
    Assert.Equal(OwnBlend, (deserialize """{ "Generator": "nonsense" }""").Generator)
    // and older builds still read NVIDIA's from it
    Assert.Contains("\"NvidiaFrameGen\": true", serialize { defaults with Generator = NvidiaDlssg })

[<Fact>]
let ``frame generation is a live setting`` () =
    Assert.True(liveFieldsOnly defaults { defaults with FrameGen = 3 })

[<Fact>]
let ``a target is dropped when every monitor is the source`` () =
    // The engine refuses --target together with --monitor all.
    let a = args { defaults with Source = AllMonitors; Target = Some 1 }
    Assert.Equal(Some "all", valueOf "--monitor" a)
    Assert.Equal(None, valueOf "--target" a)

[<Fact>]
let ``a window replaces the monitor as the source`` () =
    let a = args { defaults with Window = "My Game"; Source = Monitor 2 }
    Assert.Equal(Some "My Game", valueOf "--window" a)
    Assert.Equal(None, valueOf "--monitor" a)

[<Fact>]
let ``a window title with spaces and quotes stays one argument`` () =
    let title = "A \"quoted\" title with spaces"
    Assert.Equal(Some title, valueOf "--window" (args { defaults with Window = title }))

[<Fact>]
let ``every choice is written as the word the engine expects`` () =
    let a =
        args
            { defaults with
                Style = Cinematic
                SuperResolution = SrDlaa
                Motion = NvidiaOpticalFlow
                Cursor = CursorOff
                Compare = CompareSplit
                HighPrecisionColour = true }
    Assert.Equal(Some "2", valueOf "--nr-style" a)
    Assert.Equal(Some "dlaa", valueOf "--sr" a)
    Assert.Equal(Some "nvof", valueOf "--mv" a)
    Assert.Equal(Some "off", valueOf "--cursor" a)
    Assert.Equal(Some "split", valueOf "--compare" a)
    Assert.Equal(Some "rgba16f", valueOf "--format" a)

[<Fact>]
let ``settings survive being saved and loaded`` () =
    let original =
        { defaults with
            Source = Monitor 3
            Target = Some 1
            Style = Natural
            LocalStructure = 7.5
            Skin = SkinStrength 0.4
            Passes = 2
            FrameGen = 3
            Generator = XessMfg
            Forge = { Flow = 60; KeepStill = false; LightSearch = true; Rife = true }
            Motion = NoMotion }
    Assert.Equal(original, deserialize (serialize original))

[<Fact>]
let ``an unreadable settings file gives the defaults`` () =
    Assert.Equal(defaults, deserialize "{ not json")
    Assert.Equal(defaults, deserialize "")

[<Fact>]
let ``monitors are numbered primary first, then left to right, then top to bottom`` () =
    // interior::Ordered in the engine; --monitor 1 has to mean the same screen on both sides.
    let m left top primary = { Left = left; Top = top; Width = 1920; Height = 1080; IsPrimary = primary }
    let ordered = engineOrder [ m 1920 0 false; m -1920 0 false; m 0 0 true; m 1920 -1080 false ]
    Assert.Equal<MonitorEntry list>([ m 0 0 true; m -1920 0 false; m 1920 -1080 false; m 1920 0 false ], ordered)

[<Fact>]
let ``SUITE looks for the engine under the name its build gives it`` () =
    Assert.EndsWith("FullScreenWrapperForDLSS5.exe", enginePath ())

// ---------------------------------------------------------------------------
// Advanced options
// ---------------------------------------------------------------------------

let private everythingChanged =
    { defaults with
        Window = "Some Game = Title"
        Target = Some 1
        Skin = SkinStrength 0.5
        Advanced =
            { advancedDefaults with
                ShowPanel = true
                NrPreset = 2
                UiCorrection = false
                SrPreset = 3
                MvLevel = 2
                NvofGrid = 4
                NvofPerf = "fast"
                DepthValue = 0.25
                DepthInverted = true
                MvScaleAuto = false
                MvScaleX = 2.0
                MvScaleY = -1.5
                ResetThreshold = 0.75
                CaptureBorder = true
                CaptureFolder = @"C:\Captures With Spaces"
                NgxLog = 2
                NgxAppId = "0x1234"
                NgxProjectId = "5e9b2a44-7c31-4d0e-9f2b-8d3c1a6e7f10"
                Indicator = true
                CubinCache = false
                Affinity = false
                Topmost = true
                ClickThrough = false
                RedirectionBitmap = true
                Adapter = 1
                DebugLayer = true
                LogLevel = 0
                ExcludeOwnWindows = false
                ExclusionLog = true
                ShowInert = true } }

[<Fact>]
let ``even with every option set the command line fits the engine's 64-argument limit`` () =
    // interior::kMaxArguments. One more and the engine refuses to start at all.
    let a = toArguments @"C:\Users\x\AppData\Local\DLSS5Suite\ScreenEngine" @"C:\mods\dlss 5" everythingChanged
    Assert.True(a.Length <= 64, sprintf "%d arguments" a.Length)

[<Fact>]
let ``a window title containing an equals sign stays a separate argument`` () =
    // The engine splits "--name=value" at the first "=", so a title is never joined.
    let a = args everythingChanged
    Assert.Equal(Some "Some Game = Title", valueOf "--window" a)
    Assert.DoesNotContain(a, fun x -> x.StartsWith("--window="))

[<Fact>]
let ``every advanced option reaches the engine in its own words`` () =
    let a = args everythingChanged
    let expect name value = Assert.Equal(Some value, valueOf name a)
    expect "--gui" "off" // never on: SUITE is the control panel
    expect "--nr-preset" "2"
    expect "--nr-ui-correction" "off"
    expect "--sr-preset" "3"
    expect "--mv-level" "2"
    expect "--nvof-grid" "4"
    expect "--nvof-perf" "fast"
    expect "--depth-value" "0.25"
    expect "--depth-inverted" "on"
    expect "--mv-scale-x" "2"
    expect "--mv-scale-y" "-1.5"
    expect "--reset-threshold" "0.75"
    expect "--capture-border" "on"
    expect "--capture-folder" @"C:\Captures With Spaces"
    expect "--ngx-log" "2"
    expect "--ngx-app-id" "0x1234"
    expect "--ngx-project-id" "5e9b2a44-7c31-4d0e-9f2b-8d3c1a6e7f10"
    expect "--indicator" "on"
    expect "--cubin-cache" "off"
    expect "--affinity" "off"
    expect "--topmost" "on"
    expect "--click-through" "off"
    expect "--redirection-bitmap" "on"
    expect "--adapter" "1"
    expect "--debug-layer" "on"
    expect "--log-level" "0"
    expect "--exclude-own-windows" "off"
    expect "--exclusion-log" "on"
    expect "--show-inert" "on"

[<Fact>]
let ``options that can be left out are left out by default`` () =
    let a = args defaults
    Assert.Equal(None, valueOf "--adapter" a)
    Assert.Equal(None, valueOf "--mv-scale-x" a)
    Assert.Equal(None, valueOf "--mv-scale-y" a)
    Assert.Equal(None, valueOf "--ngx-app-id" a)
    Assert.Equal(None, valueOf "--ngx-project-id" a)

[<Fact>]
let ``values the engine would refuse are brought into range`` () =
    let bad =
        { defaults with
            Advanced =
                { advancedDefaults with
                    SrPreset = 99
                    MvLevel = 8
                    NvofGrid = 3
                    NvofPerf = "turbo"
                    DepthValue = 1.5
                    ResetThreshold = -1.0
                    NgxLog = 5
                    NgxAppId = "0"
                    NgxProjectId = "not-a-guid"
                    LogLevel = 9 } }
    let a = args bad
    Assert.Equal(Some "15", valueOf "--sr-preset" a)
    Assert.Equal(Some "7", valueOf "--mv-level" a)
    Assert.Equal(Some "1", valueOf "--nvof-grid" a)
    Assert.Equal(Some "medium", valueOf "--nvof-perf" a)
    Assert.Equal(Some "1", valueOf "--depth-value" a)
    Assert.Equal(Some "0", valueOf "--reset-threshold" a)
    Assert.Equal(Some "2", valueOf "--ngx-log" a)
    Assert.Equal(None, valueOf "--ngx-app-id" a)
    Assert.Equal(None, valueOf "--ngx-project-id" a)
    Assert.Equal(Some "3", valueOf "--log-level" a)

[<Fact>]
let ``a settings file from before the advanced options keeps their defaults`` () =
    // Without the overlay onto defaults these would load as false/0: click-through
    // and capture exclusion silently off for everyone who upgrades.
    let old = """{ "Source": "all", "Intensity": 0.5, "NeuralRendering": true }"""
    let loaded = deserialize old
    Assert.Equal(AllMonitors, loaded.Source)
    Assert.Equal(0.5, loaded.Intensity)
    Assert.Equal(advancedDefaults, loaded.Advanced)
    // Frame generation came later still: off, not a multiplier of 0 the engine refuses.
    Assert.Equal(1, loaded.FrameGen)
    Assert.Equal(OwnBlend, loaded.Generator)
    Assert.Equal(forgeDefaults, loaded.Forge)

[<Fact>]
let ``a settings file with some advanced options keeps the rest at their defaults`` () =
    let loaded = deserialize """{ "Advanced": { "Topmost": true } }"""
    Assert.True(loaded.Advanced.Topmost)
    Assert.True(loaded.Advanced.ClickThrough)
    Assert.True(loaded.Advanced.CubinCache)
    Assert.Equal(-1, loaded.Advanced.Adapter)

[<Fact>]
let ``advanced settings survive being saved and loaded`` () =
    let original = normalize everythingChanged
    Assert.Equal(original, deserialize (serialize original))

// ---------------------------------------------------------------------------
// Which DLSS 5 method a card gets
// ---------------------------------------------------------------------------

[<Fact>]
let ``cards are sorted into the method they can run`` () =
    Assert.Equal(NeuralScreen.Rtx50, NeuralScreen.tierOf "NVIDIA GeForce RTX 5070 Ti Laptop GPU")
    Assert.Equal(NeuralScreen.Rtx50, NeuralScreen.tierOf "NVIDIA GeForce RTX 5090")
    Assert.Equal(NeuralScreen.RtxOlder 40, NeuralScreen.tierOf "NVIDIA GeForce RTX 4060")
    Assert.Equal(NeuralScreen.RtxOlder 30, NeuralScreen.tierOf "NVIDIA GeForce RTX 3080 Ti")
    Assert.Equal(NeuralScreen.Rtx20, NeuralScreen.tierOf "NVIDIA GeForce RTX 2070 SUPER")
    Assert.Equal(NeuralScreen.NoRtx, NeuralScreen.tierOf "NVIDIA GeForce GTX 1660")
    Assert.Equal(NeuralScreen.NoRtx, NeuralScreen.tierOf "AMD Radeon RX 7900 XT")
    Assert.Equal(NeuralScreen.NoRtx, NeuralScreen.tierOf "Intel(R) UHD Graphics")
    Assert.Equal(NeuralScreen.RtxOlder 30, NeuralScreen.tierOf "NVIDIA RTX A4000")
    Assert.Equal(NeuralScreen.RtxOlder 40, NeuralScreen.tierOf "NVIDIA RTX 4000 Ada Generation")

[<Fact>]
let ``NeuralScreen values stay inside its own ranges`` () =
    Assert.Equal(1.5, NeuralScreen.clampParam "local_structure" 20.0)
    Assert.Equal(1.0, NeuralScreen.clampParam "intensity" 3.0)
    Assert.Equal(-1.0, NeuralScreen.skinOf FollowStructure)

// ---------------------------------------------------------------------------
// NeuralScreen: what SUITE asks of it
// ---------------------------------------------------------------------------

[<Fact>]
let ``NeuralScreen shows the original picture for Original only`` () =
    Assert.Equal(0.0, NeuralScreen.splitOf CompareOff)
    Assert.Equal(0.5, NeuralScreen.splitOf CompareSplit)
    Assert.Equal(1.0, NeuralScreen.splitOf CompareOriginal)

[<Fact>]
let ``NeuralScreen frame generation is offered only where DLSS-G runs`` () =
    Assert.Equal(0, NeuralScreen.frameGenerationCeiling (NeuralScreen.RtxOlder 30))
    Assert.Equal(2, NeuralScreen.frameGenerationCeiling (NeuralScreen.RtxOlder 40))
    Assert.Equal(4, NeuralScreen.frameGenerationCeiling NeuralScreen.Rtx50)
    Assert.Equal(0, NeuralScreen.frameGenerationCeiling NeuralScreen.Rtx20)
    let asked = { NeuralScreen.extrasDefaults with FrameGeneration = true; FrameMultiplier = 4 }
    Assert.False((NeuralScreen.forCard (NeuralScreen.RtxOlder 30) asked).FrameGeneration)
    let ada = NeuralScreen.forCard (NeuralScreen.RtxOlder 40) asked
    Assert.True(ada.FrameGeneration)
    Assert.Equal(2, ada.FrameMultiplier)
    Assert.Equal(4, (NeuralScreen.forCard NeuralScreen.Rtx50 asked).FrameMultiplier)

[<Fact>]
let ``an RTX 30 card starts NeuralScreen on the performance bundle`` () =
    Assert.Equal(NeuralScreen.Speed, NeuralScreen.performanceFor (NeuralScreen.RtxOlder 30))
    Assert.Equal(NeuralScreen.Balanced, NeuralScreen.performanceFor (NeuralScreen.RtxOlder 40))
    Assert.Equal(NeuralScreen.Quality, NeuralScreen.performanceFor NeuralScreen.Rtx50)
    let rtx30 = NeuralScreen.defaultsFor (NeuralScreen.RtxOlder 30)
    Assert.True(rtx30.Boost)
    Assert.Equal(0.5, rtx30.WorkScale)
    Assert.True(rtx30.SkipStatic)
    Assert.Equal("nvofa", rtx30.MotionBackend)
    Assert.Equal("ultrafast", rtx30.FlowPreset)
    Assert.False(rtx30.FrameGeneration)
    Assert.Equal(Some NeuralScreen.Speed, NeuralScreen.performanceOf rtx30)
    Assert.Equal(None, NeuralScreen.performanceOf { rtx30 with WorkScale = 0.42 })

[<Fact>]
let ``NeuralScreen defaults to its own local tone and a neutral exposure`` () =
    let d = NeuralScreen.extrasDefaults
    Assert.Equal(NeuralScreen.NaturalLocalTone, d.LocalTone)
    Assert.Equal(0.5, d.LocalTone)
    Assert.Equal(1.0, d.Exposure)
    Assert.False(d.LiftDarkScenes)
    Assert.Equal<(string * string) list>(
        [ "NS_PW", "1"; "NS_PW_MIN", "1.0000"; "NS_PW_MAX", "1.0000" ], NeuralScreen.exposureEnv d)
    Assert.Equal<(string * string) list>(
        [ "NS_PW", "1"; "NS_PW_MIN", "0.8000"; "NS_PW_MAX", "0.8800" ],
        NeuralScreen.exposureEnv { d with Exposure = 0.8; LiftDarkScenes = true })

[<Fact>]
let ``NeuralScreen extras are pulled back into range`` () =
    let wild =
        { NeuralScreen.extrasDefaults with
            LocalTone = 9.0; Exposure = 0.0; FlowPreset = null; WorkScale = nan; MotionBackend = "gpu" }
    let e = NeuralScreen.normalizeExtras wild
    Assert.Equal(1.5, e.LocalTone)
    Assert.Equal(0.5, e.Exposure)
    Assert.Equal("fast", e.FlowPreset)
    Assert.Equal(1.0, e.WorkScale)
    Assert.Equal("cpu", e.MotionBackend)

[<Fact>]
let ``NeuralScreen settings saved by an older SUITE are brought up to date once`` () =
    // What 1.3.0 wrote: no local tone, exposure, flow preset, sound or version.
    let old =
        """{"Boost":true,"WorkScale":1.0,"SkipStatic":false,"Hdr":false,"Spout":false,"FrameGeneration":true,"FrameMultiplier":3,"RecordingIndicator":false,"MotionBackend":"cpu","Gpu":0}"""
    let path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), sprintf "suite-ns-extras-%s.json" (System.Guid.NewGuid().ToString("N")))
    try
        System.IO.File.WriteAllText(path, old)
        let e = (NeuralScreen.loadExtrasFor (NeuralScreen.RtxOlder 30) path).Value
        Assert.Equal(NeuralScreen.ExtrasVersion, e.Version)
        Assert.Equal(0.5, e.LocalTone)
        Assert.Equal(1.0, e.Exposure)
        Assert.True(e.RecordAudio)
        Assert.Equal(0.5, e.WorkScale)
        Assert.Equal("nvofa", e.MotionBackend)
        // Choices that are not part of the bundle are kept.
        Assert.False(e.RecordingIndicator)
        // Once current, a file is read as it is.
        NeuralScreen.saveExtrasTo path { e with WorkScale = 0.8; LocalTone = 1.2 }
        let again = (NeuralScreen.loadExtrasFor (NeuralScreen.RtxOlder 30) path).Value
        Assert.Equal(0.8, again.WorkScale)
        Assert.Equal(1.2, again.LocalTone)
    finally
        try System.IO.File.Delete path with _ -> ()
