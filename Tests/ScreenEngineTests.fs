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
    expect "--gui" "on"
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
