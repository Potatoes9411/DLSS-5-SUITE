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
    /// `proxyName` is "dxgi.dll" beside a game executable or in FiveM's own
    /// game-cache folder, and "d3d11.dll" in FiveM's Plugins folder - FiveM.exe
    /// already owns dxgi.dll there, so the community packages hook d3d11
    /// instead, and every one of them checked out byte-identical to our own.
    let private payload (proxyName: string) =
        let root = ModInstaller.modFilesRoot ()
        let streamline sub = Path.Combine(root, "streamline_dlss", sub)
        [ proxyName, Path.Combine(root, "if 32 bit", "host64", "dxgi.dll")
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
    let payloadReady () = payload "dxgi.dll" |> List.forall (fun (_, source) -> File.Exists(source))

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

    /// The classic (non-Enhanced) FiveM client's own Plugins folder, used by
    /// the community's earlier "Plugins_FiveM.app.zip" style packages. Created
    /// empty by a fresh FiveM install, so its absence just means the folder
    /// has never been populated yet - not that FiveM itself is missing.
    let findFiveMPluginsTarget () =
        try
            let appRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FiveM", "FiveM.app")
            if not (Directory.Exists(appRoot)) then None
            else
                let plugins = Path.Combine(appRoot, "plugins")
                Directory.CreateDirectory(plugins) |> ignore
                Some plugins
        with _ -> None

    /// FiveM Enhanced's game-cache folder when it exists, the classic
    /// client's Plugins folder otherwise - whichever the user actually has
    /// installed, with the right proxy name for each.
    let findFiveMTarget () =
        match findFiveMEnhancedTarget () with
        | Some dir -> Some(dir, "dxgi.dll")
        | None -> findFiveMPluginsTarget () |> Option.map (fun dir -> dir, "d3d11.dll")

    let install (targetFolder: string) (proxyName: string) (report: string -> float -> unit) =
        if String.IsNullOrWhiteSpace(targetFolder) || not (Directory.Exists(targetFolder)) then
            failwith "That folder was not found."
        if not (payloadReady ()) then
            failwith "SUITE's own DLSS 5 payload is incomplete; reinstall SUITE to restore it."

        let existing = readManifest targetFolder |> Array.map (fun f -> f.Target, f) |> dict
        let backupDir = Path.Combine(dataRoot (), "GtaBackups", Path.GetFileName(manifestPath targetFolder).Replace(".json", ""))
        Directory.CreateDirectory(backupDir) |> ignore

        let files = payload proxyName
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

        // Copy standard reshade shaders
        let shadersSrc = Path.Combine(ModInstaller.modFilesRoot (), "reshade-shaders")
        let shadersDst = Path.Combine(targetFolder, "reshade-shaders")
        try
            if Directory.Exists(shadersSrc) then
                Directory.CreateDirectory(shadersDst) |> ignore
                for f in Directory.GetFiles(shadersSrc, "*", SearchOption.AllDirectories) do
                    let relative = f.Substring(shadersSrc.Length).TrimStart('\\', '/')
                    let target = Path.Combine(shadersDst, relative)
                    Directory.CreateDirectory(Path.GetDirectoryName(target)) |> ignore
                    File.Copy(f, target, true)
        with _ -> ()

        // Configure ReShade.ini to silence the tutorial, set effects path, and change overlay key
        let ini = Path.Combine(targetFolder, "ReShade.ini")
        let wanted =
            [ "EffectSearchPaths", ".\\reshade-shaders\\Shaders\\**"
              "TextureSearchPaths", ".\\reshade-shaders\\Textures\\**"
              "KeyOverlay", "35,0,0,0"
              "TutorialProgress", "4"
              "PreprocessorDefinitions", "DLSS5_MV_PROVIDER=3" ]
        try
            let startsWithKey (key: string) (line: string) = line.TrimStart().StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)
            let existing = if File.Exists(ini) then File.ReadAllLines(ini) |> List.ofArray else []
            let updated =
                wanted
                |> List.fold (fun lines (key, value) ->
                    if lines |> List.exists (startsWithKey key) then
                        lines |> List.map (fun l -> if startsWithKey key l then key + "=" + value else l)
                    else
                        let section = if key = "KeyOverlay" then "[INPUT]" else "[GENERAL]"
                        let index = lines |> List.tryFindIndex (fun l -> l.Trim().Equals(section, StringComparison.OrdinalIgnoreCase))
                        match index with
                        | Some i -> List.truncate (i + 1) lines @ [ key + "=" + value ] @ List.skip (i + 1) lines
                        | None -> [ section; key + "=" + value ] @ lines
                ) existing
            File.WriteAllLines(ini, updated)
        with _ -> ()

        report "DLSS 5 Neural Rendering installed. Enable DLSS in the game's own graphics settings." 100.0

    /// Puts back whatever this route replaced, and removes what it added.
    let uninstall (targetFolder: string) =
        for entry in readManifest targetFolder do
            try
                if entry.WasExisting && File.Exists(entry.BackupPath) then File.Copy(entry.BackupPath, entry.Target, true)
                elif not entry.WasExisting && File.Exists(entry.Target) then File.Delete(entry.Target)
            with ex -> AppLog.error ("Restoring " + entry.Target + " failed") ex
        try File.Delete(manifestPath targetFolder) with _ -> ()
