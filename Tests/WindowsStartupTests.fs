module WindowsStartupTests

// Start with Windows is a shortcut and nothing else. Every test here works in
// a temp folder of its own; the real Startup folder is never touched.

open System
open System.IO
open Xunit
open DLSS_5_MANAGER.Services

let private tempDir () =
    let d = Path.Combine(Path.GetTempPath(), "suite-startup-tests", Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(d) |> ignore
    d

[<Fact>]
let ``the shortcut turns the option on and deleting it turns it off`` () =
    let dir = tempDir ()
    try
        let folder = Path.Combine(dir, "Startup")
        let target = Path.Combine(dir, "DLSS 5 SUITE.exe")
        File.WriteAllBytes(target, [| 0uy |])

        Assert.False(WindowsStartup.isEnabledIn folder)
        Assert.True(WindowsStartup.setIn folder target true)
        Assert.True(WindowsStartup.isEnabledIn folder)
        Assert.True(File.Exists(Path.Combine(folder, WindowsStartup.shortcutName)))

        // A .lnk starts with its own 0x4C header.
        let bytes = File.ReadAllBytes(WindowsStartup.shortcutPathIn folder)
        Assert.Equal(0x4Cuy, bytes.[0])

        Assert.True(WindowsStartup.setIn folder target false)
        Assert.False(WindowsStartup.isEnabledIn folder)
    finally
        try Directory.Delete(dir, true) with _ -> ()

[<Fact>]
let ``no shortcut is made for a program that is not there`` () =
    let dir = tempDir ()
    try
        Assert.False(WindowsStartup.setIn dir (Path.Combine(dir, "missing.exe")) true)
        Assert.False(WindowsStartup.isEnabledIn dir)
    finally
        try Directory.Delete(dir, true) with _ -> ()

[<Fact>]
let ``refreshing leaves an option that is off alone`` () =
    let dir = tempDir ()
    try
        let target = Path.Combine(dir, "app.exe")
        File.WriteAllBytes(target, [| 0uy |])
        WindowsStartup.refreshIn dir target
        Assert.False(WindowsStartup.isEnabledIn dir)
    finally
        try Directory.Delete(dir, true) with _ -> ()

[<Fact>]
let ``a run from the shortcut is recognised by its argument`` () =
    Assert.True(WindowsStartup.launchedAtStartup [| "--startup" |])
    Assert.True(WindowsStartup.launchedAtStartup [| "--updated"; "1.2.0"; "--STARTUP" |])
    Assert.False(WindowsStartup.launchedAtStartup [||])
    Assert.False(WindowsStartup.launchedAtStartup null)

// ---- Window placement (same idea: a temp file, never the real one) ----

[<Fact>]
let ``a placement survives a save and a broken one is ignored`` () =
    let dir = tempDir ()
    try
        let path = Path.Combine(dir, "window.json")
        let s = { WindowPlacement.Saved.X = 100; WindowPlacement.Saved.Y = 80; WindowPlacement.Saved.Width = 1400.0; WindowPlacement.Saved.Height = 900.0; WindowPlacement.Saved.Maximized = true }
        WindowPlacement.saveTo path s
        Assert.Equal(Some s, WindowPlacement.loadFrom path)

        File.WriteAllText(path, "{ \"X\": 0, \"Y\": 0, \"Width\": 10, \"Height\": 10 }")
        Assert.Equal(None, WindowPlacement.loadFrom path)
        File.WriteAllText(path, "not json")
        Assert.Equal(None, WindowPlacement.loadFrom path)
    finally
        try Directory.Delete(dir, true) with _ -> ()

[<Fact>]
let ``a placement off every screen is not reachable`` () =
    let screens = [ (0, 0, 1920, 1040); (1920, 0, 2560, 1400) ]
    let at x y = { WindowPlacement.Saved.X = x; WindowPlacement.Saved.Y = y; WindowPlacement.Saved.Width = 1280.0; WindowPlacement.Saved.Height = 820.0; WindowPlacement.Saved.Maximized = false }
    Assert.True(WindowPlacement.isReachable screens (at 100 100))
    Assert.True(WindowPlacement.isReachable screens (at 2400 200))
    Assert.False(WindowPlacement.isReachable screens (at 5000 100))
    Assert.False(WindowPlacement.isReachable screens (at 100 1100))
    Assert.False(WindowPlacement.isReachable [ (0, 0, 1920, 1040) ] (at -1250 100))