module GtaDlss5Tests

// The GTA route's install record is all Remove has to put a game back. It
// was once saved as "{}" (System.Text.Json skips a private record's fields),
// so Remove restored nothing - pinned here so it cannot quietly happen again.

open System.Text.Json
open Xunit
open DLSS_5_MANAGER.Services.GtaDlss5

[<Fact>]
let ``the install record keeps every file through a save and a load`` () =
    let record =
        { Files =
            [| { Target = @"C:\Games\GTAV\ReShade.asi"; BackupPath = @"C:\backup\ReShade.asi"; WasExisting = true }
               { Target = @"C:\Games\GTAV\renodx-dlss5.addon64"; BackupPath = ""; WasExisting = false } |] }
    let json = JsonSerializer.Serialize(record)
    Assert.Contains("ReShade.asi", json)
    let back = JsonSerializer.Deserialize<Manifest>(json)
    Assert.Equal<ManifestFile[]>(record.Files, back.Files)
