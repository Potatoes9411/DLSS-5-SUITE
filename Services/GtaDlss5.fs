namespace DLSS_5_MANAGER.Services

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json

/// DLSS 5 Neural Rendering for GTA V (Legacy and Enhanced) and FiveM Enhanced,
/// through ReShade and RenoDX's standalone "DLSS5 Tool" addon.
///
/// Every file this deploys is already part of SUITE's own trusted payload -
/// the same ReShade module, the same pinned/signed compatibility model, and
/// the same RenoDX and Streamline files the main game-mod route uses. Nothing
/// from the Discord threads is fetched or bundled: the community's own
/// "patched" files were checked against ours and are byte-identical, so
/// there was nothing new to trust.
module GtaDlss5 =
    [<CLIMutable>]
    type private ManifestFile = { Target: string; BackupPath: string; WasExisting: bool }

    [<CLIMutable>]
    type private Manifest = { Files: ManifestFile[] }

    let private dataRoot () = AppLog.dataFolder ()

    let private manifestPath (targetFolder: string) =
        let dir = Path.Combine(dataRoot (), "GtaInstalls")
        Directory.CreateDirectory(dir) |> ignore
        use sha = SHA256.Create()
        let hash = sha.ComputeHash(Text.Encoding.UTF8.GetBytes(targetFolder.ToLowerInvariant()))
        Path.Combine(dir, Convert.ToHexString(hash).Substring(0, 20).ToLowerInvariant() + ".json")

    let isInstalled (targetFolder: string) = File.Exists(manifestPath targetFolder)

    let private readManifest (targetFolder: string) : ManifestFile[] =
        try
            let p = manifestPath targetFolder
            if File.Exists(p) then (JsonSerializer.Deserialize<Manifest>(File.ReadAllText(p))).Files else [||]
        with _ -> [||]

    let private writeManifest (targetFolder: string) (files: ManifestFile[]) =
        try File.WriteAllText(manifestPath targetFolder, JsonSerializer.Serialize({ Files = files }, JsonSerializerOptions(WriteIndented = true)))
        with ex -> AppLog.error "Writing the GTA DLSS 5 install record failed" ex

    /// The signed model matches a current driver; the pinned exception is
    /// tried only when it is missing, exactly as the Screen Engine does.
    let private modelFile () =
        let signed = Path.Combine(ModInstaller.modFilesRoot (), "dlss 5 signed", "nvngx_dlssnr.dll")
        if File.Exists(signed) then signed
        else Path.Combine(ModInstaller.modFilesRoot (), "dlss 5", "nvngx_dlssnr.dll")

    /// Every payload file, as (name-in-the-target-folder, source-in-mod-files).
    let private payload () =
        let root = ModInstaller.modFilesRoot ()
        let streamline sub = Path.Combine(root, "streamline_dlss", sub)
        [ "dxgi.dll", Path.Combine(root, "if 32 bit", "host64", "dxgi.dll")
          "renodx-dlss5.addon64", Path.Combine(root, "renodx-dlss5.addon64")
          "nvngx_dlssnr.dll", modelFile ()
          "nvngx_dlss.dll", streamline "dlss/nvngx_dlss.dll"
          "nvngx_dlssg.dll", streamline "dlss/nvngx_dlssg.dll"
          "sl.common.dll", streamline "streamline/sl.common.dll"
          "sl.dlss.dll", streamline "streamline/sl.dlss.dll"
          "sl.dlss_g.dll", streamline "streamline/sl.dlss_g.dll"
          "sl.dlss_nr.dll", streamline "streamline/sl.dlss_nr.dll"
          "sl.interposer.dll", streamline "streamline/sl.interposer.dll"
          "sl.nis.dll", streamline "streamline/sl.nis.dll"
          "sl.pcl.dll", streamline "streamline/sl.pcl.dll"
          "sl.reflex.dll", streamline "streamline/sl.reflex.dll" ]

    /// Every file this route needs is present in "mod files" before the button
    /// is even shown as usable.
    let payloadReady () = payload () |> List.forall (fun (_, source) -> File.Exists(source))

    /// FiveM Enhanced keeps its game cache under a build-specific subfolder
    /// ("gamecache_gen9\1158_13"), which changes with FiveM's own updates -
    /// so it is found by what's in it, not by a version number pinned here.
    let findFiveMEnhancedTarget () =
        try
            let root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FiveM for GTAV Enhanced", "gamecache", "gamecache_gen9")
            if not (Directory.Exists(root)) then None
            else
                Directory.GetDirectories(root)
                |> Array.filter (fun d -> Directory.GetFiles(d, "*.rpf", SearchOption.TopDirectoryOnly).Length > 0 || Directory.GetFiles(d).Length > 0)
                |> Array.sortByDescending (fun d -> Directory.GetLastWriteTimeUtc(d))
                |> Array.tryHead
        with _ -> None

    let install (targetFolder: string) (report: string -> float -> unit) =
        if String.IsNullOrWhiteSpace(targetFolder) || not (Directory.Exists(targetFolder)) then
            failwith "That folder was not found."
        if not (payloadReady ()) then
            failwith "SUITE's own DLSS 5 payload is incomplete; reinstall SUITE to restore it."

        let existing = readManifest targetFolder |> Array.map (fun f -> f.Target, f) |> dict
        let backupDir = Path.Combine(dataRoot (), "GtaBackups", Path.GetFileName(manifestPath targetFolder).Replace(".json", ""))
        Directory.CreateDirectory(backupDir) |> ignore

        let files = payload ()
        let mutable done_ = 0
        let total = float files.Length
        let recorded =
            [| for (name, source) in files do
                   let target = Path.Combine(targetFolder, name)
                   done_ <- done_ + 1
                   report (sprintf "Copying %s..." name) (float done_ / total * 100.0)
                   match existing.TryGetValue(target) with
                   | true, entry ->
                       File.Copy(source, target, true)
                       yield entry
                   | false, _ ->
                       let wasExisting = File.Exists(target)
                       let backupPath = if wasExisting then Path.Combine(backupDir, name) else ""
                       if wasExisting && not (File.Exists(backupPath)) then File.Copy(target, backupPath, false)
                       File.Copy(source, target, true)
                       yield { Target = target; BackupPath = backupPath; WasExisting = wasExisting } |]
        writeManifest targetFolder recorded
        report "DLSS 5 Neural Rendering installed. Enable DLSS in the game's own graphics settings." 100.0

    /// Puts back whatever this route replaced, and removes what it added.
    let uninstall (targetFolder: string) =
        for entry in readManifest targetFolder do
            try
                if entry.WasExisting && File.Exists(entry.BackupPath) then File.Copy(entry.BackupPath, entry.Target, true)
                elif not entry.WasExisting && File.Exists(entry.Target) then File.Delete(entry.Target)
            with ex -> AppLog.error ("Restoring " + entry.Target + " failed") ex
        try File.Delete(manifestPath targetFolder) with _ -> ()
