module UpscalerVaultTests

open System
open System.IO
open Xunit
open DLSS_5_MANAGER.Models
open DLSS_5_MANAGER.Services
open DLSS_5_MANAGER.Services.UpscalerVault

/// UPSCALE IT's list as its Worker served it, slips and spacing included.
let private workerList =
    """dlss sec :

_dlss upscaler:
-v310.6.0 : "https://pub-96c761d8f940430f9936a9cc0df5d829.r2.dev/nvngx_dlssg_310.6.0.zip"

_dlss rr :
-v310.6.0 : "https://pub-96c761d8f940430f9936a9cc0df5d829.r2.dev/nvngx_dlssd_310.6.0.zip"

_dlss_fg :
-v310.6.0 : "https://pub-96c761d8f940430f9936a9cc0df5d829.r2.dev/nvngx_dlssg_310.6.0.zip"

fsr sec :

fsr_dx12 :
v3.1.4 : "https://pub-96c761d8f940430f9936a9cc0df5d829.r2.dev/amd_fidelityfx_dx12-v3.1.4.zip"

fsr_vulkan :
v3.1.4 : "https://pub-96c761d8f940430f9936a9cc0df5d829.r2.dev/amd_fidelityfx_vkv-3.1.4.zip"

xess sec :

xess upscaler :
v2.0.2.68 : "https://pub-96c761d8f940430f9936a9cc0df5d829.r2.dev/libxess-v2.0.2.68.zip"

xess_fg :
v1.3.1.78 : "https://pub-96c761d8f940430f9936a9cc0df5d829.r2.dev/libxess_fg-v1.3.1.78.zip"

xess_dx11 :
v2.0.2.68 : "https://pub-96c761d8f940430f9936a9cc0df5d829.r2.dev/libxess_dx11-v2.0.2.68.zip"

xell :
v1.3.0.5: "https://pub-96c761d8f940430f9936a9cc0df5d829.r2.dev/libxell-v1.3.0.5.rar"
"""

[<Fact>]
let ``the Worker list parses into one offer per section`` () =
    let offers = parseCatalog workerList
    let byFamily = offers |> List.map (fun o -> o.FamilyKey, o.Version) |> set
    Assert.Equal(9, offers.Length)
    for expected in [ "dlss", "310.6.0"; "dlssd", "310.6.0"; "dlssg", "310.6.0"; "fsr_dx12", "3.1.4"; "fsr_vulkan", "3.1.4"
                      "xess", "2.0.2.68"; "xess_fg", "1.3.1.78"; "xess_dx11", "2.0.2.68"; "xell", "1.3.0.5" ] do
        Assert.Contains(expected, byFamily)
    Assert.All(offers, fun o -> Assert.StartsWith("https://", o.Url))

[<Fact>]
let ``items under an unknown header or a group line, and plain HTTP, are dropped`` () =
    let text = "mystery thing :\n- 1.0 : \"https://x.test/a.zip\"\nxess sec :\n- 2.0 : \"https://x.test/b.zip\"\nxell :\n- 3.0 : \"http://x.test/libxell.zip\"\n- nope\n"
    Assert.Empty(parseCatalog text)

[<Fact>]
let ``header spellings map to family keys`` () =
    Assert.Equal(Some "dlss", headerKey "_dlss upscaler:")
    Assert.Equal(Some "dlssd", headerKey "_dlss rr :")
    Assert.Equal(Some "dlssg", headerKey "_dlss_fg :")
    Assert.Equal(Some "xess", headerKey "xess upscaler :")
    Assert.Equal(Some "fsr4", headerKey "fsr4 int8 :")
    Assert.Equal(Some "fsr_dx12", headerKey "fsr_dx12 : ")
    Assert.Equal(None, headerKey "something else")

[<Fact>]
let ``the list's frame-generation archive is never offered for nvngx_dlss`` () =
    let offers = parseCatalog workerList
    Assert.Empty(offersFor "nvngx_dlss.dll" "dlss" offers)
    Assert.Single(offersFor "nvngx_dlssg.dll" "dlssg" offers) |> ignore
    Assert.Single(offersFor "nvngx_dlssd.dll" "dlssd" offers) |> ignore

[<Fact>]
let ``an archive is matched to the longest DLL name it starts with`` () =
    Assert.True(urlFits "libxess_fg.dll" "https://x.test/libxess_fg-v1.3.1.78.zip")
    Assert.False(urlFits "libxess.dll" "https://x.test/libxess_fg-v1.3.1.78.zip")
    Assert.True(urlFits "amd_fidelityfx_vk.dll" "https://x.test/amd_fidelityfx_vkv-3.1.4.zip")
    // FSR 3.0's names have a different API; a 3.1 archive does not fit them.
    Assert.False(urlFits "ffx_fsr3_dx12_x64.dll" "https://x.test/amd_fidelityfx_dx12-v3.1.4.zip")
    // An address that names nothing known is left to the archive's contents.
    Assert.True(urlFits "libxess.dll" "https://x.test/download?id=7")

[<Fact>]
let ``offers keep one per version, the library first, newest first`` () =
    let o src v url file = { FamilyKey = "dlss"; Version = v; Url = url; Source = src; FileName = file; LocalPath = "" }
    let all =
        [ o Catalog "310.6.0" "https://x.test/nvngx_dlss_310.6.0.zip" ""
          o NvidiaSdk "310.6.0" "https://raw.githubusercontent.com/NVIDIA/DLSS/v310.6.0/lib/Windows_x86_64/rel/nvngx_dlss.dll" "nvngx_dlss.dll"
          o NvidiaSdk "310.10.0" "https://raw.githubusercontent.com/a" "nvngx_dlss.dll"
          o Library "310.6.0.0" "" "nvngx_dlss.dll"
          o NvidiaSdk "3.7.20" "https://raw.githubusercontent.com/b" "nvngx_dlss.dll" ]
    let picked = offersFor "nvngx_dlss.dll" "dlss" all
    Assert.Equal<string list>([ "310.10.0"; "310.6.0.0"; "3.7.20" ], picked |> List.map (fun x -> x.Version))
    Assert.Equal(Library, picked.[1].Source)

[<Fact>]
let ``versions compare by number, not by text`` () =
    Assert.True(isNewer "310.10.0" "310.9.16")
    Assert.True(sameVersion "v3.1.4" "3.1.4.0")
    Assert.False(isNewer "2.0.2.68" "2.0.2.68")
    Assert.Equal(Some "310.6.0", cleanVersion "-v310.6.0 ")
    Assert.Equal(None, cleanVersion "..\\..\\evil")
    Assert.Equal(None, cleanVersion "latest")

let private facts =
    { Exists = true; ByInstall = false; HasBackup = false; BackupSame = false
      HasLegacyOriginal = false; HasLegacyVersion = false; RecordMatches = None }

[<Fact>]
let ``file states, including UPSCALE IT's markers`` () =
    Assert.Equal(Untouched, classify facts)
    Assert.Equal(ByInstall, classify { facts with ByInstall = true; HasBackup = true })
    Assert.Equal(Swapped, classify { facts with HasBackup = true; RecordMatches = Some true })
    Assert.Equal(Swapped, classify { facts with HasBackup = true })
    Assert.Equal(Untouched, classify { facts with HasBackup = true; BackupSame = true })
    Assert.Equal(SwappedByUpscaleIt, classify { facts with HasLegacyOriginal = true; HasLegacyVersion = true })
    Assert.Equal(AddedByUpscaleIt, classify { facts with HasLegacyVersion = true })
    // The launcher put its own file back over ours.
    Assert.Equal(Untouched, classify { facts with HasBackup = true; RecordMatches = Some false })
    Assert.Equal(Missing, classify { facts with Exists = false; HasBackup = true })

[<Fact>]
let ``restore plans`` () =
    Assert.Equal(NothingToDo, planRestore facts)
    Assert.Equal(FromBackup, planRestore { facts with HasBackup = true; RecordMatches = Some true })
    Assert.Equal(FromBackup, planRestore { facts with HasBackup = true; HasLegacyOriginal = true })
    Assert.Equal(FromLegacyOriginal, planRestore { facts with HasLegacyOriginal = true; HasLegacyVersion = true })
    Assert.Equal(RemoveAdded, planRestore { facts with HasLegacyVersion = true })
    Assert.Equal(DropStaleBackup, planRestore { facts with HasBackup = true; RecordMatches = Some false })
    Assert.Equal(NothingToDo, planRestore { facts with ByInstall = true; HasBackup = true })
    Assert.Equal(FromBackup, planRestore { facts with Exists = false; HasBackup = true })

[<Fact>]
let ``backup plans`` () =
    Assert.Equal(SaveCurrent, planBackup facts)
    Assert.Equal(KeepBackup, planBackup { facts with HasBackup = true; RecordMatches = Some true })
    Assert.Equal(ReplaceStale, planBackup { facts with HasBackup = true; RecordMatches = Some false })
    Assert.Equal(AdoptLegacy, planBackup { facts with HasLegacyOriginal = true; HasLegacyVersion = true })
    Assert.Equal(NoOriginal, planBackup { facts with HasLegacyVersion = true })

// ---- on disk, in a throwaway folder ----

let private tempGame () =
    let dir = Path.Combine(Path.GetTempPath(), "dlss5-vault-test-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore
    dir,
    { AppId = "test"; Title = "Vault Test"; LauncherTypeName = "Custom"; InstallDirectory = dir
      TargetExecutablePath = ""; TargetExecutableSize = 0L; LocalBannerPath = ""; LastManifestTimestamp = 0L
      UpscaleStatus = ""; DlssVersion = ""; FsrVersion = ""; XessVersion = "" }

[<Fact>]
let ``a game UPSCALE IT swapped reads its label and restores from its backup`` () =
    let dir, game = tempGame ()
    try
        let dll = Path.Combine(dir, "libxess.dll")
        File.WriteAllText(dll, "swapped")
        File.WriteAllText(dll + ".original", "the game's own")
        File.WriteAllText(dll + ".version", "v2.0.2.68")
        let found = scan game |> List.exactlyOne
        Assert.Equal(SwappedByUpscaleIt, found.State)
        Assert.Equal("2.0.2.68", found.Version)
        match restore game found with
        | Ok _ -> ()
        | Error e -> failwith e
        Assert.Equal("the game's own", File.ReadAllText dll)
        Assert.False(File.Exists(dll + ".original"))
        Assert.False(File.Exists(dll + ".version"))
        Assert.Equal(Untouched, (scan game |> List.exactlyOne).State)
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``a DLL UPSCALE IT added is taken out again`` () =
    let dir, game = tempGame ()
    try
        let dll = Path.Combine(dir, "libxell.dll")
        File.WriteAllText(dll, "added")
        File.WriteAllText(dll + ".version", "v1.3.0.5")
        let found = scan game |> List.exactlyOne
        Assert.Equal(AddedByUpscaleIt, found.State)
        match restore game found with
        | Ok _ -> ()
        | Error e -> failwith e
        Assert.False(File.Exists dll)
        Assert.False(File.Exists(dll + ".version"))
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``a file that is not a signed vendor DLL is refused`` () =
    let dir, _ = tempGame ()
    try
        let dll = Path.Combine(dir, "nvngx_dlss.dll")
        File.WriteAllBytes(dll, Array.zeroCreate 100000)
        let dlss = (familyByKey "dlss").Value
        Assert.True(Result.isError (verify dlss dll))
    finally
        Directory.Delete(dir, true)

/// Real vendor DLLs are tens of megabytes, so they are not in the repository.
/// Point DLSS5_VAULT_SAMPLES at a folder of them (as unpacked from the
/// Worker's archives) to check the signer test against the real thing.
[<Fact>]
let ``vendor-signed DLLs pass only for their own vendor`` () =
    match Environment.GetEnvironmentVariable("DLSS5_VAULT_SAMPLES") with
    | null | "" -> ()
    | folder ->
        let checkedAny = ref false
        for dll in Directory.EnumerateFiles(folder, "*.dll", SearchOption.AllDirectories) do
            match familyOfFile (Path.GetFileName dll) with
            | Some fam ->
                checkedAny.Value <- true
                Assert.True(Result.isOk (verify fam dll), sprintf "%s should pass as %s" dll fam.Key)
                let other = families |> List.find (fun f -> f.Vendor <> fam.Vendor && not f.AllowsUnsigned)
                Assert.True(Result.isError (verify other dll), sprintf "%s should not pass as %s" dll other.Key)
            | None -> ()
        Assert.True(checkedAny.Value)

[<Fact>]
let ``a Windows file signed only through a catalog is not taken for NVIDIA's`` () =
    let p = Path.Combine(Environment.SystemDirectory, "kernel32.dll")
    if File.Exists p then
        Assert.True(Result.isError (verify (familyByKey "dlss").Value p))
