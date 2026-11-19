module SettingsRecordTests

// The settings file outlives any one build. A build whose settings record was
// missing a field dropped that field on every save - which is how the stored
// last-run version kept vanishing and the update popup never fired.

open System.Text.Json
open Xunit
open DLSS_5_MANAGER.Services

let private options =
    let o = JsonSerializerOptions()
    o.PropertyNameCaseInsensitive <- true
    o

[<Fact>]
let ``saving settings writes the fields that update detection depends on`` () =
    let json = JsonSerializer.Serialize(GameScanner.defaultSettings ())
    Assert.Contains("\"LastRunVersion\"", json)
    Assert.Contains("\"ShowNonGameApps\"", json)
    Assert.Contains("\"SortMode\"", json)
    Assert.Contains("\"NoScanOnStartup\"", json)
    Assert.Contains("\"MinimizeToTray\"", json)

[<Fact>]
let ``a settings file from an older build still loads`` () =
    // Written before LastRunVersion, ShowNonGameApps and SortMode existed.
    let old = "{ \"IsSidebarLayout\": false, \"ColorAtmosphere\": \"Neon Emerald\", \"Language\": \"en\" }"
    let settings = JsonSerializer.Deserialize<GameScanner.AppSettings>(old, options)
    Assert.Equal("Neon Emerald", settings.ColorAtmosphere)

[<Fact>]
let ``every field survives a save and load`` () =
    let original =
        { GameScanner.defaultSettings () with
            LastRunVersion = "1.2.2-suite.1"
            ShowNonGameApps = true
            NoScanOnStartup = true
            MinimizeToTray = true }

    let roundTripped =
        JsonSerializer.Deserialize<GameScanner.AppSettings>(JsonSerializer.Serialize(original), options)

    Assert.Equal(original, roundTripped)

[<Fact>]
let ``an older settings file keeps scanning at start-up`` () =
    let old = "{ \"IsSidebarLayout\": false, \"ShowNonGameApps\": true }"
    let settings = JsonSerializer.Deserialize<GameScanner.AppSettings>(old, options)
    Assert.False(settings.NoScanOnStartup)
    Assert.False((GameScanner.defaultSettings ()).NoScanOnStartup)