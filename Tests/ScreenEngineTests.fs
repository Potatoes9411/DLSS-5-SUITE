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

/// The value following an option, or None when the option is absent.
let private valueOf (option: string) (arguments: string list) =
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
