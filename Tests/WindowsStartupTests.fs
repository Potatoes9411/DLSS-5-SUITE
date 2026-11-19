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
