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
    // Public on purpose: System.Text.Json skips the fields of a private
    // record, so the install record used to be saved as "{}" and Remove had
    // nothing to put back.
    [<CLIMutable>]
    type ManifestFile = { Target: string; BackupPath: string; WasExisting: bool }

    [<CLIMutable>]
    type Manifest = { Files: ManifestFile[] }

    let private dataRoot () = AppLog.dataFolder ()

    let private manifestPath (targetFolder: string) =
        let dir = Path.Combine(dataRoot (), "GtaInstalls")
        Directory.CreateDirectory(dir) |> ignore
        use sha = SHA256.Create()
        let hash = sha.ComputeHash(Text.Encoding.UTF8.GetBytes(targetFolder.ToLowerInvariant()))
        Path.Combine(dir, Convert.ToHexString(hash).Substring(0, 20).ToLowerInvariant() + ".json")

    let private readManifest (targetFolder: string) : ManifestFile[] =
        try
            let p = manifestPath targetFolder
            if File.Exists(p) then
                match JsonSerializer.Deserialize<Manifest>(File.ReadAllText(p)) |> box with
                | :? Manifest as m when not (isNull m.Files) -> m.Files
                | _ -> [||]
            else [||]
        with _ -> [||]

    /// An empty record - all the old private-record bug ever saved - counts as
    /// not installed, so the folder offers Install again rather than a Remove
    /// that has nothing to put back.
    let isInstalled (targetFolder: string) = not (Array.isEmpty (readManifest targetFolder))

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
    let private payload (loaderName: string) =
        let root = ModInstaller.modFilesRoot ()
        let streamline sub = Path.Combine(root, "streamline_dlss", sub)
        [ loaderName, Path.Combine(root, "if 32 bit", "host64", "dxgi.dll")
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

    /// NaturalVision Enhanced brings its own ReShade, loaded as ReShade.asi by
    /// its ASI loader. That copy is 6.7.3 (add-on API 18) and refuses RenoDX's
    /// add-on, which asks for API 20; a ReShade proxy beside it would load a
    /// second ReShade. So where a ReShade.asi is already in the folder, SUITE's
    /// ReShade 6.8 (full add-on build) takes its place, the original kept for
    /// Remove - the route the GTA V Enhanced + NVE tutorial uses.
    [<Literal>]
    let AsiReShade = "ReShade.asi"

    let hasAsiReShade (folder: string) =
        not (String.IsNullOrWhiteSpace folder) && File.Exists(Path.Combine(folder, AsiReShade))

    /// DLSS 5 itself. GTA V Enhanced ships its own DLSS and Streamline, and the
    /// working setup keeps them, so on a native-DLSS game the rest of the
    /// payload only fills in what the folder lacks.
    let private dlss5Own = set [ "renodx-dlss5.addon64"; "nvngx_dlssnr.dll"; "sl.dlss_nr.dll" ]

    /// The screen-capture feed is for games without DLSS of their own. In GTA
    /// V Enhanced it only adds GPU work and stalls loading screens, so a copy
    /// left by another route is set aside (and put back by Remove).
    let private feedFiles = [ "dlss5-feed.addon64"; "dlss5-feed.cfg" ]

    /// Sets one key inside its own section, adding the key or the section when
    /// missing and keeping every other line as it was.
    let private setKey (section: string) (key: string) (value: string) (lines: string list) =
        let isKey (line: string) = line.TrimStart().StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)
        let isSection (line: string) = line.TrimStart().StartsWith("[")
        match lines |> List.tryFindIndex (fun l -> l.Trim().Equals(section, StringComparison.OrdinalIgnoreCase)) with
        | None -> lines @ [ ""; section; key + "=" + value ]
        | Some start ->
            let after = List.skip (start + 1) lines
            let length = after |> List.tryFindIndex isSection |> Option.defaultValue after.Length
            match List.take length after |> List.tryFindIndex isKey with
            | Some i -> lines |> List.mapi (fun n l -> if n = start + 1 + i then key + "=" + value else l)
            | None -> List.take (start + 1) lines @ [ key + "=" + value ] @ after

    /// The feed's motion-vector define, gone from the effect definitions.
    let private withoutFeedDefine (lines: string list) =
        lines
        |> List.map (fun l ->
            if l.TrimStart().StartsWith("PreprocessorDefinitions=", StringComparison.OrdinalIgnoreCase) then
                let kept =
                    l.Substring(l.IndexOf('=') + 1).Split(',')
                    |> Array.map (fun d -> d.Trim())
                    |> Array.filter (fun d -> d <> "" && not (d.StartsWith("DLSS5_MV_PROVIDER=", StringComparison.OrdinalIgnoreCase)))
                "PreprocessorDefinitions=" + String.Join(",", kept)
            else l)

    /// The ReShade.ini settings this route needs. Without the [INSTALL] hooks
    /// GTA V Enhanced hangs on its loading screen. The add-on's own settings
    /// keep their defaults: [RenoDX.DLSS5] NREnableUpscaling=1 turned out to
    /// be NR's own upscaling, which this RenoDX build lacks, and it left the
    /// game running behind a black screen. NVE's own shader paths are left alone.
    let private iniSettings (nativeDlss: bool) (asi: bool) =
        [ yield "[INPUT]", "KeyOverlay", "35,0,0,0"
          yield "[OVERLAY]", "TutorialProgress", "4"
          if not asi then
              yield "[GENERAL]", "EffectSearchPaths", ".\\reshade-shaders\\Shaders\\**"
              yield "[GENERAL]", "TextureSearchPaths", ".\\reshade-shaders\\Textures\\**"
          if nativeDlss then
              yield "[INSTALL]", "HookStreamline", "1"
              yield "[INSTALL]", "HookDirectX", "1"
          else
              yield "[GENERAL]", "PreprocessorDefinitions", "DLSS5_MV_PROVIDER=3" ]

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

    /// `nativeDlss` is GTA V Enhanced single-player: the game has DLSS of its
    /// own for the add-on to hook, and its own Streamline to keep.
    let install (targetFolder: string) (proxyName: string) (nativeDlss: bool) (report: string -> float -> unit) =
        if String.IsNullOrWhiteSpace(targetFolder) || not (Directory.Exists(targetFolder)) then
            failwith "That folder was not found."
        if not (payloadReady ()) then
            failwith "SUITE's own DLSS 5 payload is incomplete; reinstall SUITE to restore it."

        let earlier = readManifest targetFolder
        let existing = earlier |> Array.map (fun f -> f.Target, f) |> dict
        let backupDir = Path.Combine(dataRoot (), "GtaBackups", Path.GetFileName(manifestPath targetFolder).Replace(".json", ""))
        Directory.CreateDirectory(backupDir) |> ignore

        let asi = hasAsiReShade targetFolder
        let loaderName = if asi then AsiReShade else proxyName

        /// The record for a file about to change: the earlier one when this
        /// route already owns it, else a backup of what is there now.
        let claim (name: string) =
            let target = Path.Combine(targetFolder, name)
            match existing.TryGetValue(target) with
            | true, entry -> entry
            | false, _ ->
                let wasExisting = File.Exists(target)
                let backupPath = if wasExisting then Path.Combine(backupDir, name) else ""
                if wasExisting && not (File.Exists(backupPath)) then File.Copy(target, backupPath, false)
                { Target = target; BackupPath = backupPath; WasExisting = wasExisting }

        let keepsGameOwn (name: string) =
            let target = Path.Combine(targetFolder, name)
            nativeDlss && name <> loaderName && not (dlss5Own.Contains name)
            && File.Exists(target) && not (existing.ContainsKey target)

        let files = payload loaderName |> List.filter (fun (name, _) -> not (keepsGameOwn name))
        let mutable done_ = 0
        let total = float files.Length
        let copied =
            [ for (name, source) in files do
                  done_ <- done_ + 1
                  report (sprintf "Copying %s..." name) (float done_ / total * 90.0)
                  let entry = claim name
                  File.Copy(source, entry.Target, true)
                  yield entry ]

        let setAside =
            if not nativeDlss then []
            else
                [ for name in feedFiles do
                      let entry = claim name
                      if File.Exists(entry.Target) then
                          File.Delete(entry.Target)
                          yield entry ]

        let ini = Path.Combine(targetFolder, "ReShade.ini")
        let iniEntry = claim "ReShade.ini"
        // An earlier install's records stay, so Remove still restores them.
        let fresh = copied @ setAside @ [ iniEntry ]
        let kept = earlier |> Array.filter (fun e -> not (fresh |> List.exists (fun f -> f.Target = e.Target))) |> List.ofArray
        writeManifest targetFolder (List.toArray (fresh @ kept))

        // The standard shaders - not beside NVE, whose own would be overwritten.
        if not asi then
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

        try
            let lines = if File.Exists(ini) then File.ReadAllLines(ini) |> List.ofArray else []
            let configured =
                iniSettings nativeDlss asi
                |> List.fold (fun ls (section, key, value) -> setKey section key value ls) lines
            File.WriteAllLines(ini, (if nativeDlss then withoutFeedDefine configured else configured))
        with ex -> AppLog.error "Writing ReShade.ini for GTA DLSS 5 failed" ex

        report "DLSS 5 Neural Rendering installed. Enable DLSS in the game's own graphics settings." 100.0

    /// Puts back whatever this route replaced, and removes what it added.
    let uninstall (targetFolder: string) =
        for entry in readManifest targetFolder do
            try
                if entry.WasExisting && File.Exists(entry.BackupPath) then File.Copy(entry.BackupPath, entry.Target, true)
                elif not entry.WasExisting && File.Exists(entry.Target) then File.Delete(entry.Target)
            with ex -> AppLog.error ("Restoring " + entry.Target + " failed") ex
        try File.Delete(manifestPath targetFolder) with _ -> ()
