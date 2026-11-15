module DlssgVersionsTests

open Xunit
open DLSS_5_MANAGER.Services
open DLSS_5_MANAGER.Services.DlssgVersions

let private offer version source url fileName local : UpscalerVault.Offer =
    { FamilyKey = "dlssg"; Version = version; Url = url; Source = source; FileName = fileName; LocalPath = local }

let private sdk v = offer v UpscalerVault.NvidiaSdk (sprintf "https://raw.githubusercontent.com/NVIDIA/DLSS/v%s/lib/Windows_x86_64/rel/nvngx_dlssg.dll" v) "nvngx_dlssg.dll" ""
let private worker v = offer v UpscalerVault.Catalog (sprintf "https://example.r2.dev/nvngx_dlssg_%s.zip" v) "" ""
let private library v = offer v UpscalerVault.Library "" "nvngx_dlssg.dll" (sprintf @"C:\data\library\dlssg\%s\nvngx_dlssg.dll" v)

[<Fact>]
let ``every choice survives being written down`` () =
    for c in [ Bundled; OwnFile("310.4.0", @"C:\Users\x\AppData\Local\DLSS 5 SUITE\dlssg\own\310.4.0-ab\nvngx_dlssg.dll"); Release "310.2.1" ] do
        Assert.Equal(c, decode (encode c))

[<Fact>]
let ``anything unreadable is the bundled build`` () =
    for text in [ null; ""; "garbage"; "release|"; "release|../../evil"; "own|310.4.0|"; "own|not a version!|C:\\x.dll" ] do
        Assert.Equal(Bundled, decode text)

[<Fact>]
let ``a path with a bar in it is kept whole`` () =
    Assert.Equal(OwnFile("310.4.0", @"C:\odd|name\nvngx_dlssg.dll"), decode @"own|310.4.0|C:\odd|name\nvngx_dlssg.dll")

[<Fact>]
let ``only a DLSS-G version resource counts as frame generation`` () =
    Assert.True(looksLikeDlssg "NVIDIA DLSS-G MFGLW" "NVIDIA DLSS-G MFGLW - DVS PRODUCTION")
    Assert.True(looksLikeDlssg "" "NVIDIA DLSSG")
    Assert.False(looksLikeDlssg "NVIDIA DLSS" "NVIDIA DLSS - DVS PRODUCTION")
    Assert.False(looksLikeDlssg null null)

[<Fact>]
let ``rows: bundled first, own files next, then releases newest first, one per version`` () =
    let rows =
        entries "310.9.16" [ "310.4.0", @"C:\own\a\nvngx_dlssg.dll" ]
            [ sdk "310.2.1"; worker "310.5.3"; sdk "310.5.3"; library "310.5.3"; sdk "3.10.0"; sdk "310.9.16" ]
    Assert.Equal(Bundled, rows.[0].Choice)
    Assert.Contains("310.9.16", rows.[0].Label)
    Assert.Equal(OwnFile("310.4.0", @"C:\own\a\nvngx_dlssg.dll"), rows.[1].Choice)
    let releases = rows |> List.skip 2 |> List.map (fun r -> r.Choice)
    Assert.Equal<Choice list>([ Release "310.5.3"; Release "310.2.1"; Release "3.10.0" ], releases)
    // The downloaded copy wins over a download; the bundled version is not offered twice.
    Assert.True(rows.[2].Download.IsNone)
    Assert.Equal(Some UpscalerVault.NvidiaSdk, rows.[3].Download |> Option.map (fun o -> o.Source))

[<Fact>]
let ``a catalog archive of another DLL is not offered as frame generation`` () =
    let wrong = { worker "310.6.0" with Url = "https://example.r2.dev/nvngx_dlssd_310.6.0.zip" }
    let rows = entries "310.9.16" [] [ wrong ]
    Assert.Equal(1, rows.Length)

[<Fact>]
let ``the choice is found again in a rebuilt list, and an unknown one shows the bundled row`` () =
    let rows = entries "310.9.16" [ "310.4.0", @"C:\own\a\nvngx_dlssg.dll" ] [ sdk "310.2.1"; library "310.5.3" ]
    Assert.Equal(2, indexOf (Release "310.5.3.0") rows)
    Assert.Equal(3, indexOf (Release "310.2.1") rows)
    Assert.Equal(1, indexOf (OwnFile("310.4.0", @"c:\OWN\a\nvngx_dlssg.dll")) rows)
    Assert.Equal(0, indexOf (Release "999.0") rows)
    Assert.Equal(0, indexOf Bundled rows)

[<Fact>]
let ``the staging folder is fixed by the DLL and the Streamline build`` () =
    let a = stagingName "310.4.0" "AA11" "sl:1"
    Assert.Equal(a, stagingName "310.4.0" "aa11" "sl:1")
    Assert.NotEqual<string>(a, stagingName "310.4.0" "aa12" "sl:1")
    Assert.NotEqual<string>(a, stagingName "310.4.0" "aa11" "sl:2")
    Assert.StartsWith("310.4.0-", a)
    Assert.DoesNotContain("\\", stagingName @"..\..\x" "aa" "sl")

[<Fact>]
let ``only staging folders no engine has chosen are stale`` () =
    Assert.Equal<string list>([ "old-1" ], staleStaging [ "310.4.0-abc"; "old-1"; "310.2.1-def" ] [ "310.4.0-ABC"; "310.2.1-def" ])

[<Fact>]
let ``streamline files are the five plugins and the DLSS-G runtime`` () =
    Assert.Equal<string list>([ "sl.interposer.dll"; "sl.common.dll"; "sl.dlss_g.dll"; "sl.reflex.dll"; "sl.pcl.dll"; "nvngx_dlssg.dll" ], streamlineFiles)
    Assert.Equal<string list>(MotionFiles.streamlineFiles, streamlineFiles)

[<Fact>]
let ``the screen engine is told a streamline folder only for another DLSS-G with NVIDIA frame gen on`` () =
    let on = { ScreenEngine.defaults with Generator = ScreenEngine.NvidiaDlssg }
    let args = [ "--gui=off" ]
    Assert.Equal<string list>(args @ [ "--streamline-path"; @"C:\data\dlssg\run\310.4.0-ab" ],
                              ScreenEngine.withStreamlinePath on @"C:\app\engine" @"C:\data\dlssg\run\310.4.0-ab" args)
    Assert.Equal<string list>(args, ScreenEngine.withStreamlinePath on @"C:\app\engine" @"C:\app\engine\" args)
    Assert.Equal<string list>(args, ScreenEngine.withStreamlinePath ScreenEngine.defaults @"C:\app\engine" @"C:\data\x" args)
