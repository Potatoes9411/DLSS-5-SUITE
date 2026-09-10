#load "../Services/LosslessScalingDetector.fs"

open System
open System.IO
open DLSS_5_MANAGER.Services

let mutable passed = 0
let check name condition =
    if not condition then failwith ("FAILED: " + name)
    passed <- passed + 1
    printfn "PASS: %s" name

let manifest name = sprintf "\"AppState\" { \"appid\" \"993090\" \"installdir\" \"%s\" }" name
check "normal install directory" (LosslessScalingDetector.tryInstallDirectory @"D:\Steam" (manifest "Lossless Scaling") = Some @"D:\Steam\steamapps\common\Lossless Scaling")
for name in [ ".."; "."; ""; @"..\outside"; "../outside"; @"C:\outside"; "folder/sub"; "file:stream" ] do
    check ("reject unsafe name " + name) (LosslessScalingDetector.tryInstallDirectory @"D:\Steam" (manifest name) = None)
check "wrong app id" (LosslessScalingDetector.tryInstallDirectory @"D:\Steam" ((manifest "LS").Replace("993090", "123")) = None)
check "modern and legacy libraries, case-insensitive deduplication"
    (LosslessScalingDetector.parseLibraryPaths "\"libraryfolders\" { \"0\" { \"path\" \"D:\\\\Steam\" } \"1\" \"E:\\\\Games\" \"2\" { \"path\" \"d:\\\\steam\" } }" = [ @"D:\Steam"; @"E:\Games" ])
check "relative library ignored" (LosslessScalingDetector.parseLibraryPaths "\"path\" \"relative\"" = [])
check "known missing shader and provider diagnostics"
    ((LosslessScalingDetector.feederLogNotes "DLSS5_Feed.fx is not loaded; -> none (not installed)").Length = 2)
check "a success-looking log cannot certify runtime health"
    (LosslessScalingDetector.feederLogNotes "300/300 evaluates succeeded" = [])

// All fixture writes are confined to a new, unique temporary directory.
let fixture = Path.Combine(Path.GetTempPath(), "dlss5-discovery-tests-" + Guid.NewGuid().ToString("N"))
Directory.CreateDirectory(fixture) |> ignore
try
    let primary = Path.Combine(fixture, "primary")
    let secondary = Path.Combine(fixture, "secondary")
    Directory.CreateDirectory(Path.Combine(primary, "steamapps")) |> ignore
    let target = Path.Combine(secondary, "steamapps", "common", "Lossless Scaling")
    Directory.CreateDirectory(target) |> ignore
    File.WriteAllText(Path.Combine(primary, "steamapps", "libraryfolders.vdf"), sprintf "\"path\" \"%s\"" (secondary.Replace(@"\", @"\\")))
    File.WriteAllText(Path.Combine(secondary, "steamapps", "appmanifest_993090.acf"), manifest "Lossless Scaling")
    let missing = LosslessScalingDetector.scanRoots [primary]
    check "stale manifest is not installed" missing.Installations.IsEmpty
    check "stale manifest reports missing executable" (missing.Warnings |> List.exists (fun warning -> warning.Contains("missing")))
    let exe = Path.Combine(target, "LosslessScaling.exe")
    File.WriteAllText(exe, "not a PE executable")
    let invalid = LosslessScalingDetector.scanRoots [primary]
    check "invalid executable rejected" (invalid.Installations.IsEmpty && not invalid.Warnings.IsEmpty)
    File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"), exe, true)
    File.WriteAllText(Path.Combine(target, "dxgi.dll"), "existing-user-file")
    let discovered = LosslessScalingDetector.scanRoots [primary; primary.ToUpperInvariant()]
    check "secondary library discovered once" (discovered.Installations.Length = 1)
    check "proxy presence recorded without claiming identity" (discovered.Installations.Head.ExistingComponents = ["dxgi.dll"])
    check "existing files unchanged" (File.ReadAllText(Path.Combine(target, "dxgi.dll")) = "existing-user-file")
    check "no runtime success claim" ((LosslessScalingDetector.describe discovered).Contains("not runtime-verified"))
finally
    // The sole deletion target is the exact GUID fixture created above.
    Directory.Delete(fixture, true)
printfn "%d checks passed." passed
