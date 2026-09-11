#load "../Models/GameItem.fs"
#load "../Services/NeedForSpeedProfiles.fs"
#load "../Services/NonGameAppClassifier.fs"

open System
open DLSS_5_MANAGER.Models
open DLSS_5_MANAGER.Services

let mutable passed = 0
let check name condition =
    if not condition then failwith ("FAILED: " + name)
    passed <- passed + 1
    printfn "PASS: %s" name

// =====================================================================
// NFS ROUTE RECOMMENDATION
// =====================================================================
printfn ""
printfn "--- NeedForSpeedProfiles.tryRecommendedRoute ---"

// DX11 titles
for title in [ "Need for Speed Heat"; "Need for Speed™ Heat"; "NEED FOR SPEED HEAT"
               "Need for Speed Payback"; "Need for Speed Rivals"
               "Need for Speed 2016"; "Need for Speed Most Wanted 2012"
               "Need for Speed The Run"; "Need for Speed™ The Run" ] do
    check (sprintf "route(%s) = dx11" title)
        (NeedForSpeedProfiles.tryRecommendedRoute title = Some "dx11")

// DX12 titles
for title in [ "Need for Speed Unbound"; "Need for Speed™ Unbound"; "NFS Unbound" ] do
    check (sprintf "route(%s) = dx12" title)
        (NeedForSpeedProfiles.tryRecommendedRoute title = Some "dx12")

// Classic titles — should return None (needs DXVK bridge, not an in-app route)
for title in [ "Need for Speed Underground"; "Need for Speed Underground 2"
               "Need for Speed Carbon"; "Need for Speed Undercover"
               "Need for Speed World"; "Need for Speed Hot Pursuit"
               "Need for Speed ProStreet"; "Need for Speed Shift" ] do
    check (sprintf "route(%s) = None (classic)" title)
        (NeedForSpeedProfiles.tryRecommendedRoute title = None)

// Non-NFS titles — should return None
for title in [ "Grand Theft Auto V"; "Cyberpunk 2077"; "Elden Ring" ] do
    check (sprintf "route(%s) = None (non-NFS)" title)
        (NeedForSpeedProfiles.tryRecommendedRoute title = None)

// Most Wanted disambiguation: bare "Most Wanted" (could be 2005 or 2012)
// Line 23 requires "most wanted 2012" in title to match dx11
// Line 26 has bare "most wanted" which matches classics -> None
check "route(Need for Speed Most Wanted) = None (ambiguous, defaults to classic)"
    (NeedForSpeedProfiles.tryRecommendedRoute "Need for Speed Most Wanted" = None)

// =====================================================================
// NFS DESCRIPTION
// =====================================================================
printfn ""
printfn "--- NeedForSpeedProfiles.tryDescribe ---"

check "Heat description mentions OptiScaler incompatible"
    (match NeedForSpeedProfiles.tryDescribe "Need for Speed Heat" "dx11" "64" with
     | Some s -> s.Contains("OptiScaler is not compatible")
     | None -> false)

check "Unbound description mentions DX12"
    (match NeedForSpeedProfiles.tryDescribe "Need for Speed Unbound" "dx12" "64" with
     | Some s -> s.Contains("DX12") || s.Contains("DirectX 12")
     | None -> false)

check "Underground 2 description mentions DXVK"
    (match NeedForSpeedProfiles.tryDescribe "Need for Speed Underground 2" "dx9" "32" with
     | Some s -> s.Contains("DXVK")
     | None -> false)

check "The Run description mentions 32-bit"
    (match NeedForSpeedProfiles.tryDescribe "Need for Speed The Run" "dx11" "32" with
     | Some s -> s.Contains("32-bit")
     | None -> false)

check "Non-NFS title returns None"
    (NeedForSpeedProfiles.tryDescribe "Cyberpunk 2077" "dx12" "64" = None)

// =====================================================================
// NON-GAME APP CLASSIFIER
// =====================================================================
printfn ""
printfn "--- NonGameAppClassifier.isUtility ---"

let makeGame title exe =
    { AppId = "0"; Title = title; LauncherTypeName = "Steam"
      InstallDirectory = ""; TargetExecutablePath = exe
      TargetExecutableSize = 0L; LocalBannerPath = ""
      LastManifestTimestamp = 0L; UpscaleStatus = ""
      DlssVersion = ""; FsrVersion = ""; XessVersion = "" }

// Known utilities
for (title, exe) in
    [ ("Blender", @"C:\Program Files\blender\blender.exe")
      ("OBS Studio", @"C:\Program Files\obs-studio\bin\64bit\obs64.exe")
      ("3DMark", @"C:\Program Files\3dmark\3DMark.exe")
      ("Clipchamp", @"C:\Program Files\Clipchamp\clipchamp.exe")
      ("Lossless Scaling", @"C:\Steam\steamapps\common\Lossless Scaling\LosslessScaling.exe")
      ("Steamworks Common Redistributables", @"C:\Steam\steamapps\common\_CommonRedist\vcredist.exe") ] do
    check (sprintf "isUtility(%s) = true" title)
        (NonGameAppClassifier.isUtility (makeGame title exe))

// Should also match via exe path alone
check "isUtility via exe path (obs64)"
    (NonGameAppClassifier.isUtility (makeGame "My Streaming App" @"C:\obs64\obs64.exe"))

// Games should NOT be classified as utilities
for title in [ "Need for Speed Heat"; "Grand Theft Auto V"; "Cyberpunk 2077"; "Elden Ring" ] do
    check (sprintf "isUtility(%s) = false" title)
        (not (NonGameAppClassifier.isUtility (makeGame title @"C:\Games\game.exe")))

// Edge case: "Lossless" alone should not match (marker is "lossless scaling")
check "isUtility(Lossless) = false (partial match)"
    (not (NonGameAppClassifier.isUtility (makeGame "Lossless" @"C:\Games\lossless.exe")))

// =====================================================================
// SUMMARY
// =====================================================================
printfn ""
printfn "%d checks passed." passed
