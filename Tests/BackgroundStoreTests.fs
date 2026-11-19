module BackgroundStoreTests

// The custom background, pinned in temp folders: the picture is copied and
// never referenced in place, Reset deletes only SUITE's copy, and the
// brightness math keeps a bright photo from glaring.

open System
open System.IO
open Xunit
open DLSS_5_MANAGER.Services

let private tempDir () =
    let d = Path.Combine(Path.GetTempPath(), "suite-background-tests", Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(d) |> ignore
    d

[<Fact>]
let ``a picked picture is copied, and a second pick replaces the first copy`` () =
    let dir = tempDir ()
    try
        let store = Path.Combine(dir, "Backgrounds")
        let original = Path.Combine(dir, "Holiday.JPG")
        File.WriteAllBytes(original, [| 1uy; 2uy; 3uy |])

        let first = BackgroundStore.importCustomIn store original
        Assert.True(first.IsSome)
        Assert.StartsWith(store, first.Value)
        Assert.EndsWith(".jpg", first.Value)
        Assert.True(File.Exists(original))

        let second = BackgroundStore.importCustomIn store original
        Assert.True(second.IsSome)
        Assert.Single(Directory.GetFiles(store, "custom_*")) |> ignore
    finally
        try Directory.Delete(dir, true) with _ -> ()

[<Fact>]
let ``reset deletes only SUITE's own copies`` () =
    let dir = tempDir ()
    try
        let store = Path.Combine(dir, "Backgrounds")
        Directory.CreateDirectory(store) |> ignore
        File.WriteAllText(Path.Combine(store, "custom_1.png"), "x")
        File.WriteAllText(Path.Combine(store, "keep.png"), "y")
        BackgroundStore.removeCustomIn store
        Assert.False(File.Exists(Path.Combine(store, "custom_1.png")))
        Assert.True(File.Exists(Path.Combine(store, "keep.png")))
    finally
        try Directory.Delete(dir, true) with _ -> ()

[<Fact>]
let ``only image files are taken`` () =
    let dir = tempDir ()
    try
        let notImage = Path.Combine(dir, "readme.txt")
        File.WriteAllText(notImage, "x")
        Assert.True((BackgroundStore.importCustomIn (Path.Combine(dir, "b")) notImage).IsNone)
        Assert.True((BackgroundStore.importCustomIn (Path.Combine(dir, "b")) (Path.Combine(dir, "missing.png"))).IsNone)
    finally
        try Directory.Delete(dir, true) with _ -> ()

[<Fact>]
let ``settings survive a save and a missing copy falls back to the theme`` () =
    let dir = tempDir ()
    try
        let path = Path.Combine(dir, "background.json")
        let pic = Path.Combine(dir, "custom_1.png")
        File.WriteAllText(pic, "x")
        let s = { BackgroundStore.defaults with UseCustom = true; CustomFile = pic; Brightness = 40 }
        BackgroundStore.saveTo path s
        Assert.Equal(s, BackgroundStore.loadFrom path)

        File.Delete(pic)
        Assert.False((BackgroundStore.loadFrom path).UseCustom)
        Assert.Equal(BackgroundStore.defaults, BackgroundStore.loadFrom (Path.Combine(dir, "none.json")))
    finally
        try Directory.Delete(dir, true) with _ -> ()

[<Fact>]
let ``brightness is clamped and a bright picture is held back more`` () =
    Assert.Equal(25, BackgroundStore.clampBrightness 0)
    Assert.Equal(15, BackgroundStore.clampBrightness 5)
    Assert.Equal(100, BackgroundStore.clampBrightness 400)
    let dark = BackgroundStore.exposure 50 40.0
    let bright = BackgroundStore.exposure 50 220.0
    Assert.Equal(0.5, dark, 3)
    Assert.True(bright < dark)
    Assert.True(BackgroundStore.exposure 100 55.0 <= 1.0)
