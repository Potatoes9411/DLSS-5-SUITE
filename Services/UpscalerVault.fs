namespace DLSS_5_MANAGER.Services

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open DLSS_5_MANAGER.Models

/// Authenticode checks for the upscaler DLLs the Vault puts into games.
///
/// WinVerifyTrust answers "is this file's signature intact, and does it
/// chain to a root Windows trusts"; the signer's certificate then says who
/// signed it. Both are needed: a valid signature from anyone at all would
/// otherwise pass for NVIDIA's.
module UpscalerSignature =

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type private FileInfoNative =
        { cbStruct: uint32
          pcwszFilePath: nativeint
          hFile: nativeint
          pgKnownSubject: nativeint }

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type private TrustData =
        { cbStruct: uint32
          pPolicyCallbackData: nativeint
          pSIPClientData: nativeint
          dwUIChoice: uint32
          fdwRevocationChecks: uint32
          dwUnionChoice: uint32
          pFile: nativeint
          mutable dwStateAction: uint32
          mutable hWVTStateData: nativeint
          pwszURLReference: nativeint
          dwProvFlags: uint32
          dwUIContext: uint32
          pSignatureSettings: nativeint }

    [<DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)>]
    extern int private WinVerifyTrust(nativeint hwnd, Guid& action, TrustData& data)

    /// WINTRUST_ACTION_GENERIC_VERIFY_V2: the Authenticode policy.
    let private genericVerifyV2 = Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE")

    /// Whether the file carries an intact Authenticode signature that chains
    /// to a trusted root. Revocation is not fetched over the network: the
    /// Vault runs offline as often as not, and a revoked vendor certificate
    /// is news SUITE would hear about long before a DLL list changes.
    let isTrusted (path: string) : bool =
        let pathPtr = Marshal.StringToHGlobalUni(path)
        let fileInfo =
            { cbStruct = uint32 (Marshal.SizeOf<FileInfoNative>())
              pcwszFilePath = pathPtr
              hFile = 0n
              pgKnownSubject = 0n }
        let filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfoNative>())
        try
            Marshal.StructureToPtr(fileInfo, filePtr, false)
            let mutable data =
                { cbStruct = uint32 (Marshal.SizeOf<TrustData>())
                  pPolicyCallbackData = 0n
                  pSIPClientData = 0n
                  dwUIChoice = 2u // WTD_UI_NONE
                  fdwRevocationChecks = 0u // WTD_REVOKE_NONE
                  dwUnionChoice = 1u // WTD_CHOICE_FILE
                  pFile = filePtr
                  dwStateAction = 1u // WTD_STATEACTION_VERIFY
                  hWVTStateData = 0n
                  pwszURLReference = 0n
                  // WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL
                  dwProvFlags = 0x10u ||| 0x1000u
                  dwUIContext = 0u
                  pSignatureSettings = 0n }
            let mutable action = genericVerifyV2
            let result = WinVerifyTrust(-1n, &action, &data)
            // The verify call keeps state that only a close releases.
            data.dwStateAction <- 2u // WTD_STATEACTION_CLOSE
            WinVerifyTrust(-1n, &action, &data) |> ignore
            result = 0
        finally
            Marshal.FreeHGlobal(filePtr)
            Marshal.FreeHGlobal(pathPtr)

    /// The organisation (O=) of the certificate that signed the file, or "".
    let signerOrganisation (path: string) : string =
        try
            use cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path))
            cert.SubjectName.EnumerateRelativeDistinguishedNames()
            |> Seq.tryPick (fun rdn ->
                if rdn.GetSingleElementType().Value = "2.5.4.10" then Some(rdn.GetSingleElementValue()) else None)
            |> Option.defaultValue ""
        with _ -> ""

/// The Upscaler Vault: every upscaler DLL a game ships - NVIDIA DLSS, AMD FSR,
/// Intel XeSS and XeLL - on the version you pick, and the game's own one back
/// whenever you want it. Ported from NODIX TECH's UPSCALE IT, with his leave.
///
/// What it keeps from UPSCALE IT: the file-name table, the version list from
/// its Cloudflare Worker, and its "<dll>.original" / "<dll>.version" markers,
/// read so a game it touched restores cleanly here. What it adds: every DLL is
/// checked to be signed by its vendor before it goes into a game, an
/// anti-cheat game is left alone, and a download is only kept once checked.
/// It never writes to the registry (UPSCALE IT's "disable signature checks"
/// fix is left out on purpose).
///
/// The DLSS files share DlssUpdater's backup name and record, so the two
/// features see one history: an update made from the game's Manage sheet can
/// be undone here, and the other way round.
module UpscalerVault =

    type Vendor =
        | Nvidia
        | Amd
        | Intel

    type Family =
        { Key: string
          Label: string
          /// The short name on a row's chip.
          Chip: string
          Vendor: Vendor
          /// Every name the family is shipped under. The names are not
          /// interchangeable (FSR 3.0's ffx_fsr3_* has a different API from
          /// FSR 3.1's amd_fidelityfx_*), so a file is only ever replaced by
          /// one of exactly the same name.
          FileNames: string list
          /// Whether the file's own version number is the one people know it
          /// by. AMD stamps FSR 3.1.4 as 1.0.1.x, so for FSR the version comes
          /// from what was installed, not from the file.
          VersionFromFile: bool
          /// FSR 4's INT8 builds are community rebuilds of AMD's DLL and carry
          /// no signature at all; they are let through on HTTPS and a size and
          /// PE check, and said to be unsigned. Every other family must be
          /// signed by its vendor.
          AllowsUnsigned: bool }

    let families =
        [ { Key = "dlss"; Label = "DLSS Super Resolution"; Chip = "DLSS"; Vendor = Nvidia
            FileNames = [ "nvngx_dlss.dll" ]; VersionFromFile = true; AllowsUnsigned = false }
          { Key = "dlssd"; Label = "DLSS Ray Reconstruction"; Chip = "DLSS RR"; Vendor = Nvidia
            FileNames = [ "nvngx_dlssd.dll" ]; VersionFromFile = true; AllowsUnsigned = false }
          { Key = "dlssg"; Label = "DLSS Frame Generation"; Chip = "DLSS FG"; Vendor = Nvidia
            FileNames = [ "nvngx_dlssg.dll" ]; VersionFromFile = true; AllowsUnsigned = false }
          { Key = "fsr_dx12"; Label = "FSR 3 (DirectX 12)"; Chip = "FSR"; Vendor = Amd
            FileNames = [ "amd_fidelityfx_dx12.dll"; "ffx_fsr3_dx12_x64.dll" ]; VersionFromFile = false; AllowsUnsigned = false }
          { Key = "fsr_vulkan"; Label = "FSR 3 (Vulkan)"; Chip = "FSR VK"; Vendor = Amd
            FileNames = [ "amd_fidelityfx_vk.dll"; "ffx_fsr3_vk_x64.dll" ]; VersionFromFile = false; AllowsUnsigned = false }
          { Key = "fsr4"; Label = "FSR 4"; Chip = "FSR 4"; Vendor = Amd
            FileNames = [ "amd_fidelityfx_upscaler_dx12.dll" ]; VersionFromFile = false; AllowsUnsigned = true }
          { Key = "xess"; Label = "XeSS"; Chip = "XeSS"; Vendor = Intel
            FileNames = [ "libxess.dll" ]; VersionFromFile = true; AllowsUnsigned = false }
          { Key = "xess_dx11"; Label = "XeSS (DirectX 11)"; Chip = "XeSS 11"; Vendor = Intel
            FileNames = [ "libxess_dx11.dll"; "libxess_d3d11.dll"; "xess_dx11.dll" ]; VersionFromFile = true; AllowsUnsigned = false }
          { Key = "xess_fg"; Label = "XeSS Frame Generation"; Chip = "XeSS FG"; Vendor = Intel
            FileNames = [ "libxess_fg.dll"; "xess_fg.dll" ]; VersionFromFile = true; AllowsUnsigned = false }
          { Key = "xell"; Label = "XeLL (low latency)"; Chip = "XeLL"; Vendor = Intel
            FileNames = [ "libxell.dll"; "xell.dll" ]; VersionFromFile = true; AllowsUnsigned = false } ]

    let familyByKey (key: string) = families |> List.tryFind (fun f -> f.Key = key)

    let familyOfFile (fileName: string) =
        families
        |> List.tryFind (fun f -> f.FileNames |> List.exists (fun n -> n.Equals(fileName, StringComparison.OrdinalIgnoreCase)))

    /// The organisation names each vendor signs its DLLs with.
    let vendorSigners =
        function
        | Nvidia -> [ "NVIDIA Corporation" ]
        | Amd -> [ "Advanced Micro Devices"; "Advanced Micro Devices, Inc."; "Advanced Micro Devices Inc." ]
        | Intel -> [ "Intel Corporation" ]

    let vendorName =
        function
        | Nvidia -> "NVIDIA"
        | Amd -> "AMD"
        | Intel -> "Intel"

    // ------------------------------------------------------------- versions

    /// Numbers only, trailing zeros dropped, so "310.10.0" > "310.9.1" and
    /// "v3.1.4" = "3.1.4.0".
    let versionKey (v: string) =
        let nums =
            Regex.Matches(defaultArg (Option.ofObj v) "", @"\d+")
            |> Seq.map (fun m -> try int64 m.Value with _ -> Int64.MaxValue)
            |> ResizeArray
        while nums.Count > 1 && nums.[nums.Count - 1] = 0L do
            nums.RemoveAt(nums.Count - 1)
        List.ofSeq nums

    let compareVersions (a: string) (b: string) = compare (versionKey a) (versionKey b)
    let isNewer (candidate: string) (current: string) = compareVersions candidate current > 0
    let sameVersion (a: string) (b: string) = versionKey a = versionKey b

    /// "v310.6.0 " and "310.6.0" read the same; anything that is not a short
    /// version-looking string is refused, since it ends up in a file name.
    let cleanVersion (raw: string) =
        let v = (defaultArg (Option.ofObj raw) "").Trim().TrimStart('-').Trim().TrimStart('v', 'V').Trim()
        if v.Length > 0 && v.Length <= 40 && Regex.IsMatch(v, @"^[0-9A-Za-z][0-9A-Za-z.\-_+]*$") && Regex.IsMatch(v, @"\d") then Some v
        else None

    // -------------------------------------------------------------- catalog

    type Source =
        /// Already downloaded and checked, in SUITE's own folder.
        | Library
        /// NVIDIA's DLSS SDK repository, at a release tag.
        | NvidiaSdk
        /// UPSCALE IT's list (the Cloudflare Worker).
        | Catalog

    let sourceName =
        function
        | Library -> "downloaded"
        | NvidiaSdk -> "NVIDIA SDK"
        | Catalog -> "UPSCALE IT list"

    let private sourceRank =
        function
        | Library -> 0
        | NvidiaSdk -> 1
        | Catalog -> 2

    type Offer =
        { FamilyKey: string
          Version: string
          /// A DLL (NVIDIA SDK) or an archive holding one (the Worker list).
          Url: string
          Source: Source
          /// The exact DLL name this offer carries, or "" when only opening
          /// the archive will tell.
          FileName: string
          /// The checked copy in the library, for Source = Library.
          LocalPath: string }

    /// A section header of the Worker's list, as a family key. The list is
    /// hand-written, so spacing, case and a leading underscore all vary.
    let headerKey (line: string) : string option =
        let h = line.Trim().TrimEnd(':').Trim().TrimStart('_').Trim().ToLowerInvariant()
        let h = Regex.Replace(h, @"\s+", "_")
        match h with
        | "dlss_upscaler" | "dlss" | "dlss_sr" -> Some "dlss"
        | "dlss_rr" | "dlssd" -> Some "dlssd"
        | "dlss_fg" | "dlssg" -> Some "dlssg"
        | "xess_upscaler" | "xess" -> Some "xess"
        | "fsr4_int8" | "fsr4" -> Some "fsr4"
        | "fsr_vk" -> Some "fsr_vulkan"
        | other when (familyByKey other).IsSome -> Some other
        | _ -> None

    /// UPSCALE IT's version list. Not JSON: a header line names a family, and
    /// each item under it reads  - <version> : "<url>". "xxx sec :" lines only
    /// group the sections. Items under an unknown header, items that are not
    /// HTTPS, and anything that does not parse are dropped.
    let parseCatalog (text: string) : Offer list =
        let mutable section: string option = None
        [ for raw in (defaultArg (Option.ofObj text) "").Split('\n') do
              let line = raw.Trim()
              if line.Length = 0 then ()
              elif line.IndexOf("http", StringComparison.OrdinalIgnoreCase) < 0 then
                  section <-
                      if Regex.IsMatch(line, @"\bsec\s*:?\s*$", RegexOptions.IgnoreCase) then None
                      else headerKey line
              else
                  match section with
                  | None -> ()
                  | Some key ->
                      let colon = line.IndexOf(':')
                      let quote = line.IndexOf('"')
                      // The version is before the first colon, and that colon
                      // must come before the URL starts.
                      if colon > 0 && (quote < 0 || colon < quote) then
                          let url = line.Substring(colon + 1).Trim().Trim('"').Trim()
                          match cleanVersion (line.Substring(0, colon)), Uri.TryCreate(url, UriKind.Absolute) with
                          | Some v, (true, uri) when uri.Scheme = Uri.UriSchemeHttps ->
                              yield { FamilyKey = key; Version = v; Url = uri.AbsoluteUri; Source = Catalog; FileName = ""; LocalPath = "" }
                          | _ -> () ]

    let private stems =
        lazy
            (families
             |> List.collect (fun f -> f.FileNames)
             |> List.map (fun n -> Path.GetFileNameWithoutExtension(n).ToLowerInvariant()))

    /// Whether an archive's address says it holds this DLL. The Worker names
    /// archives after what is inside ("nvngx_dlssg_310.6.0.zip"), and the
    /// longest DLL name the address starts with is the one it carries. This
    /// is what keeps the list's own slip - its DLSS Super Resolution entry
    /// points at the frame-generation archive - from being offered for
    /// nvngx_dlss.dll. An address that names no known DLL is offered, and
    /// opening the archive decides.
    let urlFits (fileName: string) (url: string) =
        let leaf =
            try Uri.UnescapeDataString(Uri(url).Segments |> Array.last).ToLowerInvariant()
            with _ -> ""
        let named = stems.Value |> List.filter (fun s -> leaf.StartsWith(s)) |> List.sortByDescending String.length
        match named with
        | [] -> true
        | longest :: _ -> longest = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant()

    /// What can go into one file: its family's versions that carry exactly
    /// this file name, one per version (the library's checked copy first,
    /// then NVIDIA's own, then the Worker's), newest first.
    let offersFor (fileName: string) (familyKey: string) (all: Offer list) : Offer list =
        all
        |> List.filter (fun o ->
            o.FamilyKey = familyKey
            && (if o.FileName <> "" then o.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase) else urlFits fileName o.Url))
        // Each version's key is worked out once: comparing by regex inside
        // the sort ran it thousands of times and stalled the page.
        |> List.groupBy (fun o -> versionKey o.Version)
        |> List.map (fun (key, same) -> key, (same |> List.minBy (fun o -> sourceRank o.Source)))
        |> List.sortByDescending fst
        |> List.map snd

    // ----------------------------------------------------------- file state

    /// What is known about one DLL in a game, from the disk alone.
    type Facts =
        { Exists: bool
          /// Placed by SUITE's DLSS 5 install, which owns it.
          ByInstall: bool
          /// SUITE's backup of the game's own file is beside it.
          HasBackup: bool
          /// ...and is the same build as the file now in place.
          BackupSame: bool
          /// UPSCALE IT's "<dll>.original" backup is beside it.
          HasLegacyOriginal: bool
          /// UPSCALE IT's "<dll>.version" label is beside it.
          HasLegacyVersion: bool
          /// Some true when SUITE's record says it wrote exactly this file,
          /// Some false when it recorded a different one, None for no record.
          RecordMatches: bool option }

    type FileState =
        /// The game's own file.
        | Untouched
        /// Swapped by SUITE; its backup holds the game's own.
        | Swapped
        /// Swapped by UPSCALE IT; its ".original" holds the game's own.
        | SwappedByUpscaleIt
        /// Put there by UPSCALE IT where the game had none.
        | AddedByUpscaleIt
        /// SUITE's DLSS 5 install owns it - changed only through that.
        | ByInstall
        /// Backed up, but the file itself is gone.
        | Missing

    let classify (f: Facts) : FileState =
        if f.ByInstall then ByInstall
        elif not f.Exists then (if f.HasBackup || f.HasLegacyOriginal then Missing else Untouched)
        elif f.RecordMatches = Some true then Swapped
        // A backup that differs with no record: swapped before the record
        // existed, or by the DLSS updater. Still ours to undo.
        elif f.HasBackup && not f.BackupSame && f.RecordMatches.IsNone then Swapped
        elif f.HasLegacyOriginal then SwappedByUpscaleIt
        elif f.HasLegacyVersion then AddedByUpscaleIt
        // A record that no longer matches means the launcher put the game's
        // own file back (a repair or an update); the old backup is stale.
        else Untouched

    type RestoreStep =
        /// Copy SUITE's backup back over the file.
        | FromBackup
        /// Copy UPSCALE IT's ".original" back over the file.
        | FromLegacyOriginal
        /// UPSCALE IT added the file; take it out again.
        | RemoveAdded
        /// The game's own file is in place; only a stale backup to drop.
        | DropStaleBackup
        | NothingToDo

    let planRestore (f: Facts) : RestoreStep =
        if f.ByInstall then NothingToDo
        elif f.HasBackup && f.RecordMatches = Some false && f.Exists then DropStaleBackup
        elif f.HasBackup then FromBackup
        elif f.HasLegacyOriginal then FromLegacyOriginal
        elif f.HasLegacyVersion && f.Exists then RemoveAdded
        else NothingToDo

    type BackupStep =
        /// The game's own file is already saved.
        | KeepBackup
        /// Save the file in place as the game's own.
        | SaveCurrent
        /// The saved one is stale (the launcher replaced ours): save the
        /// file in place over it.
        | ReplaceStale
        /// Take over UPSCALE IT's ".original" as SUITE's backup.
        | AdoptLegacy
        /// UPSCALE IT added the file; there is no original to keep, and its
        /// ".version" marker stays so a restore still removes it.
        | NoOriginal

    let planBackup (f: Facts) : BackupStep =
        if f.HasBackup && f.RecordMatches = Some false then ReplaceStale
        elif f.HasBackup then KeepBackup
        elif f.HasLegacyOriginal then AdoptLegacy
        elif f.HasLegacyVersion then NoOriginal
        else SaveCurrent

    // ----------------------------------------------------------- the disk

    [<Literal>]
    let UpscalerSuffix = ".dlss5-upscaler-original"

    [<Literal>]
    let private PartSuffix = ".dlss5-upscaler-part"

    [<Literal>]
    let LegacyOriginalSuffix = ".original"

    [<Literal>]
    let LegacyVersionSuffix = ".version"

    /// DLSS keeps DlssUpdater's name and record, so both features read the
    /// same history; everything else has its own.
    let backupSuffix (family: Family) =
        if family.Vendor = Nvidia then DlssUpdater.OriginalSuffix else UpscalerSuffix

    let private recordName (family: Family) =
        if family.Vendor = Nvidia then "dlss5-dlss-update.json" else "dlss5-upscaler-update.json"

    let private dataRoot () = Path.Combine(AppLog.dataFolder (), "cache", "upscalers")
    let libraryRoot () = Path.Combine(dataRoot (), "library")

    let private sha256 (path: string) =
        use fs = File.OpenRead(path)
        Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant()

    let fileVersion (path: string) = DlssUpdater.fileVersion path

    /// 64-bit PE, and a size no real upscaler DLL is outside of.
    let private isWin64Dll (path: string) =
        try
            use fs = File.OpenRead(path)
            if fs.Length < 32768L || fs.Length > 400L * 1048576L then false
            else
                use br = new BinaryReader(fs)
                if br.ReadUInt16() <> 0x5A4Dus then false
                else
                    fs.Seek(0x3CL, SeekOrigin.Begin) |> ignore
                    let peAt = br.ReadInt32()
                    if peAt <= 0 || int64 peAt > fs.Length - 6L then false
                    else
                        fs.Seek(int64 peAt, SeekOrigin.Begin) |> ignore
                        br.ReadUInt32() = 0x00004550u && br.ReadUInt16() = 0x8664us
        with _ -> false

    /// Checks a DLL before it may go into a game. Ok carries a note for the
    /// user ("signed by NVIDIA").
    let verify (family: Family) (path: string) : Result<string, string> =
        if not (isWin64Dll path) then Error "it is not a 64-bit Windows DLL"
        elif UpscalerSignature.isTrusted path then
            let org = UpscalerSignature.signerOrganisation path
            if vendorSigners family.Vendor |> List.exists (fun s -> s.Equals(org, StringComparison.OrdinalIgnoreCase)) then
                Ok(sprintf "signed by %s" (vendorName family.Vendor))
            else
                Error(sprintf "it is signed by \"%s\", not by %s" (if org = "" then "someone unknown" else org) (vendorName family.Vendor))
        else
            let org = UpscalerSignature.signerOrganisation path
            if org <> "" then Error "its signature is broken - the file was changed after it was signed"
            elif family.AllowsUnsigned then Ok "unsigned community build"
            else Error(sprintf "it is not signed by %s" (vendorName family.Vendor))

    // ------------------------------------------------------------- records

    let private inside (root: string) (path: string) =
        try
            let r = Path.GetFullPath(root).TrimEnd('\\') + "\\"
            Path.GetFullPath(path).StartsWith(r, StringComparison.OrdinalIgnoreCase)
        with _ -> false

    let private recordPath (game: GameItem) (family: Family) = Path.Combine(game.InstallDirectory, recordName family)

    /// Every entry of one record file, paths made absolute. Read as untrusted:
    /// an entry only counts when it names a known DLL inside this game.
    let private loadRecordFile (game: GameItem) (name: string) : DlssUpdater.RecordEntry list =
        try
            let p = Path.Combine(game.InstallDirectory, name)
            if not (File.Exists p) then []
            else
                let r = JsonSerializer.Deserialize<DlssUpdater.Record>(File.ReadAllText p)
                if isNull (box r) || isNull r.files then []
                else
                    r.files
                    |> Array.choose (fun e ->
                        if isNull (box e) || String.IsNullOrWhiteSpace e.path then None
                        else
                            let full = Path.GetFullPath(Path.Combine(game.InstallDirectory, e.path))
                            match familyByKey e.family, familyOfFile (Path.GetFileName full) with
                            | Some f, Some g when f.Key = g.Key && inside game.InstallDirectory full -> Some { e with path = full }
                            | _ -> None)
                    |> Array.distinctBy (fun e -> e.path.ToLowerInvariant())
                    |> List.ofArray
        with _ -> []

    let private loadRecord (game: GameItem) (family: Family) = loadRecordFile game (recordName family)

    let private saveRecord (game: GameItem) (family: Family) (entries: DlssUpdater.RecordEntry list) =
        let p = recordPath game family
        if entries.IsEmpty then
            try File.Delete p with _ -> ()
        else
            let rel =
                entries
                |> List.map (fun e -> { e with path = Path.GetRelativePath(game.InstallDirectory, e.path).Replace('\\', '/') })
                |> Array.ofList
            let kind = if family.Vendor = Nvidia then "dlss-update" else "upscaler-update"
            let json =
                JsonSerializer.Serialize(
                    ({ tool = "dlss5-suite"; kind = kind; version = 1; files = rel }: DlssUpdater.Record),
                    JsonSerializerOptions(WriteIndented = true))
            let tmp = p + ".tmp"
            File.WriteAllText(tmp, json)
            File.Move(tmp, p, true)

    let private updateRecord (game: GameItem) (family: Family) (path: string) (entry: DlssUpdater.RecordEntry option) =
        let others = loadRecord game family |> List.filter (fun e -> not (e.path.Equals(path, StringComparison.OrdinalIgnoreCase)))
        saveRecord game family (match entry with Some e -> e :: others | None -> others)

    // ---------------------------------------------------------------- scan

    /// One upscaler DLL found in a game.
    type FoundFile =
        { Path: string
          Family: Family
          FileName: string
          /// The file's own version number, "" when it has none.
          FileVersion: string
          /// What the version is known as: the one picked, UPSCALE IT's
          /// label, or the file's own number.
          Version: string
          State: FileState
          /// The version a restore puts back, "" when there is nothing to.
          OriginalVersion: string
          Facts: Facts }

    let private readLegacyLabel (path: string) =
        try
            let text = File.ReadAllText(path)
            cleanVersion (if text.Length > 64 then text.Substring(0, 64) else text) |> Option.defaultValue ""
        with _ -> ""

    let private sameBuild (a: string) (b: string) =
        try
            File.Exists a && File.Exists b
            && FileInfo(a).Length = FileInfo(b).Length
            && fileVersion a = fileVersion b
        with _ -> false

    let private recordMatches (path: string) (e: DlssUpdater.RecordEntry) =
        try
            if not (File.Exists path) then false
            else
                let size = FileInfo(path).Length
                if e.size > 0L && e.size <> size then false
                elif not (String.IsNullOrWhiteSpace e.sha256) then e.sha256.Equals(sha256 path, StringComparison.OrdinalIgnoreCase)
                else sameVersion (fileVersion path) e.written
        with _ -> false

    let private skipDirs =
        set [ "content"; "paks"; "saved"; "logs"; "movies"; "sounds"; "music"; "videos"; "localization"
              "shadercache"; "derivedcache"; "cache"; "textures"; "maps"; "levels"; "audio"; "data"
              "assets"; "mods"; "screenshots"; "steamapps"; "redist"; "_commonredist"; "host64"; "reshade-shaders" ]

    let private passThrough =
        set [ "streamline"; "dlss"; "nvidia"; "ngx"; "nvngx"; "binaries"; "plugins"; "win64"; "x64"; "bin"
              "fidelityfx"; "ffx"; "amd"; "xess"; "intel" ]

    let private allNames =
        lazy (families |> List.collect (fun f -> f.FileNames) |> List.map (fun n -> n.ToLowerInvariant()) |> set)

    /// The same bounded walk DlssUpdater makes, for every upscaler name:
    /// depth 9, 6000 folders, 6 seconds, at most 24 hits. Bulky asset folders
    /// are only passed through towards a folder where upscalers usually sit.
    let private walk (root: string) =
        let names = allNames.Value
        let hits = ResizeArray<string>()
        let clock = Stopwatch.StartNew()
        let mutable dirs = 0
        let rec go (dir: string) (depth: int) (onlyPass: bool) =
            if depth <= 9 && dirs < 6000 && hits.Count < 24 && clock.Elapsed.TotalSeconds < 6.0 then
                dirs <- dirs + 1
                try
                    if not onlyPass then
                        for f in Directory.EnumerateFiles(dir, "*.dll") do
                            if names.Contains(Path.GetFileName(f).ToLowerInvariant()) && hits.Count < 24 then hits.Add f
                    for d in Directory.EnumerateDirectories(dir) do
                        let n = Path.GetFileName(d).ToLowerInvariant()
                        if not (n.StartsWith(".")) then
                            if onlyPass then
                                if passThrough.Contains n then go d (depth + 1) false
                            elif skipDirs.Contains n then go d (depth + 1) true
                            else go d (depth + 1) false
                with _ -> ()
        if Directory.Exists root then go root 0 false
        List.ofSeq hits

    let private factsOf (path: string) (family: Family) (byInstall: Set<string>) (record: DlssUpdater.RecordEntry list) =
        let backup = path + backupSuffix family
        let exists = File.Exists path
        let hasBackup = File.Exists backup
        { Exists = exists
          ByInstall = byInstall.Contains(path.ToLowerInvariant())
          HasBackup = hasBackup
          BackupSame = hasBackup && exists && sameBuild path backup
          HasLegacyOriginal = File.Exists(path + LegacyOriginalSuffix)
          HasLegacyVersion = File.Exists(path + LegacyVersionSuffix)
          RecordMatches =
            record
            |> List.tryFind (fun e -> e.path.Equals(path, StringComparison.OrdinalIgnoreCase))
            |> Option.map (recordMatches path) }

    let private describe (game: GameItem) (byInstall: Set<string>) (records: Map<string, DlssUpdater.RecordEntry list>) (path: string) =
        match familyOfFile (Path.GetFileName path) with
        | None -> None
        | Some fam ->
            let record = records |> Map.tryFind (recordName fam) |> Option.defaultValue []
            let facts = factsOf path fam byInstall record
            let state = classify facts
            if not facts.Exists && state <> Missing then None
            else
                let fv = if facts.Exists then fileVersion path else ""
                let entry = record |> List.tryFind (fun e -> e.path.Equals(path, StringComparison.OrdinalIgnoreCase))
                let picked =
                    match state, entry with
                    | Swapped, Some e when not (String.IsNullOrWhiteSpace e.label) ->
                        cleanVersion (e.label.Split(' ').[0]) |> Option.defaultValue fv
                    | (SwappedByUpscaleIt | AddedByUpscaleIt), _ ->
                        match readLegacyLabel (path + LegacyVersionSuffix) with
                        | "" -> fv
                        | label -> label
                    | _ -> fv
                let original =
                    match planRestore facts with
                    | FromBackup -> fileVersion (path + backupSuffix fam)
                    | FromLegacyOriginal -> fileVersion (path + LegacyOriginalSuffix)
                    | _ -> ""
                Some
                    { Path = path
                      Family = fam
                      FileName = Path.GetFileName path
                      FileVersion = fv
                      Version = picked
                      State = state
                      OriginalVersion = original
                      Facts = facts }

    /// Every upscaler DLL the game has and what state each is in. Read only.
    let scan (game: GameItem) : FoundFile list =
        if String.IsNullOrWhiteSpace game.InstallDirectory || not (Directory.Exists game.InstallDirectory) then []
        else
            let byInstall = ModInstaller.installedTargets game |> List.map (fun p -> p.ToLowerInvariant()) |> set
            let records =
                [ "dlss5-dlss-update.json"; "dlss5-upscaler-update.json" ]
                |> List.map (fun n -> n, loadRecordFile game n)
                |> Map.ofList
            let exeDir = try Path.GetDirectoryName(game.TargetExecutablePath) with _ -> ""
            [ yield! walk game.InstallDirectory
              for d in [ exeDir; game.InstallDirectory ] do
                  if not (String.IsNullOrEmpty d) then
                      for n in allNames.Value do
                          let p = Path.Combine(d, n)
                          if File.Exists p then yield p
              for KeyValue(_, entries) in records do
                  for e in entries -> e.path ]
            |> List.map Path.GetFullPath
            |> List.distinctBy (fun p -> p.ToLowerInvariant())
            // NVIDIA's Streamline host copies are the driver's, not the game's.
            |> List.filter (fun p -> not (p.ToLowerInvariant().Split('\\') |> Array.contains "host64"))
            |> List.choose (describe game byInstall records)
            |> List.sortBy (fun f -> (families |> List.findIndex (fun x -> x.Key = f.Family.Key)), f.Path.ToLowerInvariant())

    /// The version to compare against the list, "" when it is not known
    /// (an FSR file nobody here installed carries AMD's internal number).
    let knownVersion (f: FoundFile) =
        match f.State with
        | Swapped | SwappedByUpscaleIt | AddedByUpscaleIt -> f.Version
        | _ -> if f.Family.VersionFromFile then f.FileVersion else ""

    /// The newest offer, when it is newer than what the file is known to be.
    let updateFor (offers: Offer list) (f: FoundFile) : Offer option =
        match f.State, knownVersion f with
        | (ByInstall | Missing), _ -> None
        | _, "" -> None
        | _, known ->
            match offersFor f.FileName f.Family.Key offers with
            | newest :: _ when isNewer newest.Version known -> Some newest
            | _ -> None

    // ----------------------------------------------------------- the guards

    let private canWrite (dir: string) =
        try
            let probe = Path.Combine(dir, ".dlss5-upscaler-write-test-" + Guid.NewGuid().ToString("N"))
            File.WriteAllText(probe, "")
            File.Delete probe
            true
        with _ -> false

    /// Why the game's upscalers may not be changed, or None. The same checks
    /// as DlssUpdater.refusal - running, anti-cheat, a folder Windows will
    /// not let SUITE write to - worded for every upscaler, not just DLSS.
    /// AntiCheat.detect already lets a game through whose BattlEye is off.
    let refusal (game: GameItem) : string option =
        if (try GameLauncher.isRunning game with _ -> false) then
            Some "Close the game first - its upscaler files are in use while it runs."
        else
            match AntiCheat.detect game.Title game.InstallDirectory game.TargetExecutablePath with
            | Some ac ->
                Some(sprintf "%s is installed with this game. An anti-cheat can treat a changed upscaler DLL as tampering, so the Vault leaves this game alone." ac)
            | None ->
                if not (canWrite game.InstallDirectory) then
                    let p = game.InstallDirectory.ToLowerInvariant()
                    if p.Contains("windowsapps") || p.Contains("xboxgames") then
                        Some "Xbox / Game Pass protects this folder, so its upscaler files cannot be changed."
                    else
                        Some "Windows will not let SUITE write to this game's folder. Run SUITE as administrator, or move the game out of Program Files."
                else None

    // ------------------------------------------------------------ fetching

    let private http =
        lazy
            (let h = new HttpClient(Timeout = TimeSpan.FromMinutes(10.0))
             h.DefaultRequestHeaders.UserAgent.ParseAdd("DLSS5-SUITE (+upscaler vault)")
             h)

    [<Literal>]
    let WorkerUrl = "https://upscaler-update.gtagatgta9.workers.dev/"

    [<Literal>]
    let private MaxDownload = 419430400L // 400 MB

    /// Downloads over HTTPS to a .part file, then moves it in place. Archives
    /// from the Worker carry no hash, so what makes them safe is the check of
    /// the DLL inside, not this.
    let private fetchFile (url: string) (target: string) (progress: float -> unit) (cancel: CancellationToken) : Async<unit> =
        async {
            let uri = Uri(url)
            if uri.Scheme <> Uri.UriSchemeHttps then failwith "the address is not HTTPS"
            Directory.CreateDirectory(Path.GetDirectoryName target) |> ignore
            let part = target + ".part"
            let mutable attempt = 0
            let mutable finished = false
            while not finished do
                attempt <- attempt + 1
                try
                    use! resp = http.Value.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancel) |> Async.AwaitTask
                    resp.EnsureSuccessStatusCode() |> ignore
                    let total = resp.Content.Headers.ContentLength |> Option.ofNullable |> Option.defaultValue 0L
                    if total > MaxDownload then failwith "the download is far larger than any upscaler"
                    do!
                        async {
                            use! src = resp.Content.ReadAsStreamAsync(cancel) |> Async.AwaitTask
                            use dst = File.Create(part)
                            let buffer = Array.zeroCreate<byte> (256 * 1024)
                            let mutable got = 0L
                            let mutable reading = true
                            while reading do
                                use stall = CancellationTokenSource.CreateLinkedTokenSource(cancel)
                                stall.CancelAfter(TimeSpan.FromSeconds(45.0))
                                let! n = src.ReadAsync(buffer.AsMemory(), stall.Token).AsTask() |> Async.AwaitTask
                                if n = 0 then reading <- false
                                else
                                    do! dst.WriteAsync(buffer.AsMemory(0, n), cancel).AsTask() |> Async.AwaitTask
                                    got <- got + int64 n
                                    if got > MaxDownload then failwith "the download is far larger than any upscaler"
                                    if total > 0L then progress (float got / float total)
                        }
                    File.Move(part, target, true)
                    finished <- true
                with ex ->
                    try File.Delete part with _ -> ()
                    if attempt >= 3 || cancel.IsCancellationRequested then raise ex
                    do! Async.Sleep(1500 * attempt)
        }

    let private safeName (s: string) = Regex.Replace(s, @"[^0-9A-Za-z.\-_+]", "_")

    let private libraryPath (familyKey: string) (version: string) (fileName: string) =
        Path.Combine(libraryRoot (), familyKey, safeName version, fileName.ToLowerInvariant())

    /// Every checked DLL already downloaded, as offers.
    let libraryOffers () : Offer list =
        try
            let root = libraryRoot ()
            if not (Directory.Exists root) then []
            else
                [ for famDir in Directory.EnumerateDirectories(root) do
                      match familyByKey (Path.GetFileName famDir) with
                      | None -> ()
                      | Some fam ->
                          for verDir in Directory.EnumerateDirectories(famDir) do
                              match cleanVersion (Path.GetFileName verDir) with
                              | None -> ()
                              | Some v ->
                                  for dll in Directory.EnumerateFiles(verDir, "*.dll") do
                                      if familyOfFile (Path.GetFileName dll) |> Option.exists (fun f -> f.Key = fam.Key) then
                                          yield { FamilyKey = fam.Key; Version = v; Url = ""; Source = Library
                                                  FileName = Path.GetFileName dll; LocalPath = dll } ]
        with _ -> []

    /// Puts the offer's DLL for this file name in the library, checked, and
    /// returns its path and the check's note. A file that fails the check is
    /// deleted, never kept.
    let prepare (offer: Offer) (fileName: string) (progress: string -> float -> unit) (cancel: CancellationToken) : Async<string * string> =
        async {
            let fam =
                match familyByKey offer.FamilyKey with
                | Some f -> f
                | None -> failwith "unknown upscaler family"
            let target = libraryPath fam.Key offer.Version fileName
            let check (path: string) =
                match verify fam path with
                | Ok note -> note
                | Error why ->
                    try File.Delete path with _ -> ()
                    failwithf "%s %s was refused: %s. Nothing was changed." fileName offer.Version why
            if File.Exists target then
                progress "Checking the signature..." 0.9
                return target, check target
            else
                Directory.CreateDirectory(Path.GetDirectoryName target) |> ignore
                match offer.Source with
                | Library ->
                    progress "Checking the signature..." 0.9
                    return offer.LocalPath, check offer.LocalPath
                | NvidiaSdk ->
                    let host = Uri(offer.Url).Host
                    if not (host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase)) then
                        failwith "NVIDIA SDK files only come from NVIDIA's GitHub repository"
                    let incoming = target + ".incoming"
                    do! fetchFile offer.Url incoming (fun p -> progress (sprintf "Downloading %s %s from NVIDIA..." fileName offer.Version) (p * 0.85)) cancel
                    progress "Checking NVIDIA's signature..." 0.9
                    let note = check incoming
                    File.Move(incoming, target, true)
                    return target, note
                | Catalog ->
                    let ext =
                        match Path.GetExtension(Uri(offer.Url).AbsolutePath).ToLowerInvariant() with
                        | ".zip" | ".rar" | ".7z" as e -> e
                        | ".dll" -> ".dll"
                        | _ -> ".zip"
                    let downloads = Path.Combine(dataRoot (), "downloads")
                    let archive = Path.Combine(downloads, sprintf "%s_%s%s" fam.Key (safeName offer.Version) ext)
                    do! fetchFile offer.Url archive (fun p -> progress (sprintf "Downloading %s %s..." fam.Label offer.Version) (p * 0.8)) cancel
                    try
                        let incoming = target + ".incoming"
                        if ext = ".dll" then File.Move(archive, incoming, true)
                        else
                            progress "Unpacking..." 0.82
                            use a = SharpCompress.Archives.ArchiveFactory.OpenArchive(archive, SharpCompress.Readers.ReaderOptions())
                            let dlls =
                                a.Entries
                                |> Seq.filter (fun e -> not e.IsDirectory && not (isNull e.Key))
                                |> Seq.filter (fun e -> e.Key.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                                |> List.ofSeq
                            // Only a DLL of exactly the game's file name: UPSCALE
                            // IT took the first DLL it found, which is how its
                            // list's frame-generation archive could land in
                            // nvngx_dlss.dll.
                            match dlls |> List.tryFind (fun e -> Path.GetFileName(e.Key.Replace('\\', '/')).Equals(fileName, StringComparison.OrdinalIgnoreCase)) with
                            | None ->
                                let names = dlls |> List.map (fun e -> Path.GetFileName(e.Key.Replace('\\', '/'))) |> String.concat ", "
                                failwithf "the %s %s download holds %s, not %s. Nothing was changed."
                                    fam.Label offer.Version (if names = "" then "no DLL" else names) fileName
                            | Some entry ->
                                if entry.Size > MaxDownload then failwith "the DLL in the archive is far larger than any upscaler"
                                use src = entry.OpenEntryStream()
                                use dst = File.Create(incoming)
                                src.CopyTo(dst)
                        progress (sprintf "Checking %s's signature..." (vendorName fam.Vendor)) 0.9
                        let note = check incoming
                        File.Move(incoming, target, true)
                        return target, note
                    finally
                        try File.Delete archive with _ -> ()
        }

    // ------------------------------------------------------------- catalog

    type CatalogResult =
        { Offers: Offer list
          /// What reached SUITE and what did not, for the page.
          Status: string }

    let private workerCache () = Path.Combine(dataRoot (), "upscale-it-list.txt")
    let private sdkCache () = Path.Combine(dataRoot (), "nvidia-sdk.json")

    [<CLIMutable>]
    type SdkTag = { tag: string; files: string[] }

    [<CLIMutable>]
    type SdkCache = { at: int64; tags: SdkTag[] }

    /// UPSCALE IT's list, fetched at most every 30 minutes unless forced. The
    /// copy on disk is used when the Worker cannot be reached.
    let private workerList (force: bool) : Async<Offer list * string> =
        async {
            let cache = workerCache ()
            let fresh = File.Exists cache && (DateTime.UtcNow - File.GetLastWriteTimeUtc cache).TotalMinutes < 30.0
            if fresh && not force then return parseCatalog (File.ReadAllText cache), ""
            else
                try
                    use cts = new CancellationTokenSource(TimeSpan.FromSeconds(15.0))
                    let! text = http.Value.GetStringAsync(WorkerUrl, cts.Token) |> Async.AwaitTask
                    let offers = parseCatalog text
                    if offers.IsEmpty then failwith "the list came back empty"
                    Directory.CreateDirectory(dataRoot ()) |> ignore
                    File.WriteAllText(cache, text)
                    return offers, ""
                with ex ->
                    AppLog.warn ("Upscaler Vault: UPSCALE IT's list could not be fetched: " + ex.Message)
                    if File.Exists cache then return parseCatalog (File.ReadAllText cache), "UPSCALE IT's list is offline - using the saved copy"
                    else return [], "UPSCALE IT's list could not be reached"
        }

    let private sdkFileUrl (tag: string) (fileName: string) =
        sprintf "https://raw.githubusercontent.com/NVIDIA/DLSS/%s/lib/Windows_x86_64/rel/%s" (Uri.EscapeDataString tag) fileName

    /// NVIDIA's own DLSS builds, one per SDK release tag. Tags never change,
    /// so which files a tag has is looked up once and kept; only the tag list
    /// is asked again, at most every six hours.
    let private nvidiaSdk (force: bool) : Async<Offer list * string> =
        async {
            let saved =
                try
                    if File.Exists(sdkCache ()) then
                        let c = JsonSerializer.Deserialize<SdkCache>(File.ReadAllText(sdkCache ()))
                        if isNull (box c) || isNull c.tags then None else Some c
                    else None
                with _ -> None
            let toOffers (tags: SdkTag[]) =
                [ for t in tags do
                      match cleanVersion t.tag with
                      | None -> ()
                      | Some v ->
                          for f in (if isNull t.files then [||] else t.files) do
                              match familyOfFile f with
                              | Some fam when fam.Vendor = Nvidia ->
                                  yield { FamilyKey = fam.Key; Version = v; Url = sdkFileUrl t.tag fam.FileNames.Head; Source = NvidiaSdk
                                          FileName = fam.FileNames.Head; LocalPath = "" }
                              | _ -> () ]
            let age = saved |> Option.map (fun c -> DateTimeOffset.UtcNow.ToUnixTimeSeconds() - c.at) |> Option.defaultValue Int64.MaxValue
            match saved with
            | Some c when not force && age < 6L * 3600L -> return toOffers c.tags, ""
            | _ ->
                try
                    use req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/NVIDIA/DLSS/tags?per_page=100")
                    req.Headers.Accept.ParseAdd("application/vnd.github+json")
                    use cts = new CancellationTokenSource(TimeSpan.FromSeconds(15.0))
                    use! resp = http.Value.SendAsync(req, cts.Token) |> Async.AwaitTask
                    resp.EnsureSuccessStatusCode() |> ignore
                    let! body = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
                    use doc = JsonDocument.Parse(body)
                    let names =
                        [ for t in doc.RootElement.EnumerateArray() do
                              match t.TryGetProperty("name") with
                              | true, n when n.ValueKind = JsonValueKind.String ->
                                  let s = n.GetString()
                                  if Regex.IsMatch(s, @"^v?\d+(\.\d+)+[a-z]?$") then yield s
                              | _ -> () ]
                    let known = saved |> Option.map (fun c -> c.tags |> Array.map (fun t -> t.tag, t) |> Map.ofArray) |> Option.defaultValue Map.empty
                    // A HEAD per file of each new tag: which of the three
                    // DLLs that release has (frame generation arrived in 3.1,
                    // ray reconstruction in 3.5, and some 310 tags skip one).
                    use gate = new SemaphoreSlim(8)
                    let probe (tag: string) (fileName: string) =
                        async {
                            do! gate.WaitAsync() |> Async.AwaitTask
                            try
                                try
                                    use head = new HttpRequestMessage(HttpMethod.Head, sdkFileUrl tag fileName)
                                    use! r = http.Value.SendAsync(head) |> Async.AwaitTask
                                    return (if r.IsSuccessStatusCode then Some fileName else None)
                                with _ -> return None
                            finally
                                gate.Release() |> ignore
                        }
                    let! tags =
                        names
                        |> List.map (fun tag ->
                            async {
                                match known.TryFind tag with
                                | Some t -> return t
                                | None ->
                                    let! found =
                                        families
                                        |> List.filter (fun f -> f.Vendor = Nvidia)
                                        |> List.map (fun f -> probe tag f.FileNames.Head)
                                        |> Async.Parallel
                                    return { tag = tag; files = found |> Array.choose id }
                            })
                        |> Async.Parallel
                    let cache = { at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(); tags = tags }
                    Directory.CreateDirectory(dataRoot ()) |> ignore
                    File.WriteAllText(sdkCache (), JsonSerializer.Serialize(cache))
                    return toOffers tags, ""
                with ex ->
                    AppLog.warn ("Upscaler Vault: NVIDIA's DLSS tags could not be fetched: " + ex.Message)
                    match saved with
                    | Some c -> return toOffers c.tags, ""
                    | None ->
                        // The tag list is rate-limited; the latest tag is not.
                        let! newest = DlssUpdater.newest false
                        let tags =
                            newest
                            |> Map.toList
                            |> List.map (fun (_, n) -> n.Tag)
                            |> List.distinct
                            |> List.map (fun t -> { tag = t; files = [| "nvngx_dlss.dll"; "nvngx_dlssg.dll"; "nvngx_dlssd.dll" |] })
                            |> Array.ofList
                        return toOffers tags, (if tags.Length = 0 then "NVIDIA's DLSS releases could not be reached" else "")
        }

    /// Every version on offer: the library, NVIDIA's SDK tags and UPSCALE
    /// IT's list. Each part fails on its own; what did arrive is still used.
    let catalog (force: bool) : Async<CatalogResult> =
        async {
            let! parts = [ workerList force; nvidiaSdk force ] |> Async.Parallel
            let worker, workerNote = parts.[0]
            let sdk, sdkNote = parts.[1]
            let library = libraryOffers ()
            let notes = [ workerNote; sdkNote ] |> List.filter (fun s -> s <> "")
            let status =
                let counts =
                    sprintf "%d from UPSCALE IT's list · %d NVIDIA SDK builds · %d downloaded" worker.Length
                        (sdk |> List.map (fun o -> o.Version) |> List.distinct |> List.length) library.Length
                if notes.IsEmpty then counts else counts + " · " + String.Join(" · ", notes)
            return { Offers = library @ sdk @ worker; Status = status }
        }

    // ------------------------------------------------------------ changing

    let private copyWhole (src: string) (dst: string) =
        let part = dst + PartSuffix
        File.Copy(src, part, true)
        if FileInfo(part).Length <> FileInfo(src).Length then
            File.Delete part
            failwith "the copied file came out the wrong size"
        File.Move(part, dst, true)

    /// Re-reads one file's facts right before it is changed.
    let private freshFacts (game: GameItem) (f: FoundFile) =
        let byInstall = ModInstaller.installedTargets game |> List.map (fun p -> p.ToLowerInvariant()) |> set
        factsOf f.Path f.Family byInstall (loadRecord game f.Family)

    /// Puts the offer's version into the file, keeping the game's own beside
    /// it. Returns a line for the user.
    let apply (game: GameItem) (f: FoundFile) (offer: Offer) (progress: string -> float -> unit) (cancel: CancellationToken) : Async<Result<string, string>> =
        async {
            match refusal game with
            | Some why -> return Error why
            | None ->
                if f.State = ByInstall then return Error "SUITE's DLSS 5 install owns this file - change it through that install."
                elif offer.FamilyKey <> f.Family.Key then return Error "That version is for a different upscaler."
                else
                    try
                        let! dll, note = prepare offer f.FileName progress cancel
                        progress "Putting it in place..." 0.95
                        let facts = freshFacts game f
                        if not facts.Exists then failwith "the game's file is gone - verify the game in its launcher"
                        if fileVersion f.Path <> f.FileVersion then failwith "the game's file changed since it was read - look again and retry"
                        let backup = f.Path + backupSuffix f.Family
                        let legacyOriginal = f.Path + LegacyOriginalSuffix
                        let legacyVersion = f.Path + LegacyVersionSuffix
                        let mutable madeBackup = false
                        match planBackup facts with
                        | KeepBackup | NoOriginal -> ()
                        | SaveCurrent ->
                            copyWhole f.Path backup
                            madeBackup <- true
                        | ReplaceStale ->
                            copyWhole f.Path backup
                        | AdoptLegacy ->
                            // UPSCALE IT's backup becomes SUITE's; its label
                            // would be wrong from now on, so it goes too.
                            File.Move(legacyOriginal, backup, true)
                            try File.Delete legacyVersion with _ -> ()
                        let placed =
                            try
                                copyWhole dll f.Path
                                None
                            with ex -> Some ex
                        match placed with
                        | Some ex ->
                            // The game keeps its own file; drop a backup made
                            // only for this attempt.
                            if madeBackup then
                                try File.Delete backup with _ -> ()
                            raise ex
                        | None -> ()
                        let written = fileVersion f.Path
                        let hash = sha256 f.Path
                        if not (hash.Equals(sha256 dll, StringComparison.OrdinalIgnoreCase)) then
                            failwith "the file in the game does not match the checked one"
                        if planBackup facts = NoOriginal then
                            // Keep UPSCALE IT's "added" marker true to what is there.
                            File.WriteAllText(legacyVersion, "v" + offer.Version)
                        let entry: DlssUpdater.RecordEntry =
                            { path = f.Path
                              family = f.Family.Key
                              original = (if File.Exists backup then fileVersion backup else "")
                              written = (if written = "" then offer.Version else written)
                              size = FileInfo(f.Path).Length
                              label = sprintf "%s (%s)" offer.Version (sourceName offer.Source)
                              at = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                              sha256 = hash }
                        updateRecord game f.Family f.Path (Some entry)
                        AppLog.info (sprintf "Upscaler Vault in %s: %s %s -> %s (%s)" game.Title f.FileName f.Version offer.Version note)
                        return Ok(sprintf "%s: %s → %s (%s)." f.Family.Label (if f.Version = "" then "?" else f.Version) offer.Version note)
                    with ex ->
                        AppLog.error ("Upscaler Vault apply in " + game.Title) ex
                        return Error ex.Message
        }

    /// Puts the game's own file back - from SUITE's backup, or UPSCALE IT's.
    let restore (game: GameItem) (f: FoundFile) : Result<string, string> =
        if (try GameLauncher.isRunning game with _ -> false) then
            Error "Close the game first - its upscaler files are in use while it runs."
        elif f.State = ByInstall then Error "SUITE's DLSS 5 install owns this file - remove that install instead."
        else
            try
                let facts = freshFacts game f
                let backup = f.Path + backupSuffix f.Family
                let legacyOriginal = f.Path + LegacyOriginalSuffix
                let legacyVersion = f.Path + LegacyVersionSuffix
                let line =
                    match planRestore facts with
                    | FromBackup ->
                        copyWhole backup f.Path
                        File.Delete backup
                        // A leftover UPSCALE IT marker would mislabel the original.
                        try File.Delete legacyVersion with _ -> ()
                        if File.Exists legacyOriginal && sameBuild legacyOriginal f.Path then (try File.Delete legacyOriginal with _ -> ())
                        sprintf "%s: the game's own %s is back." f.Family.Label (fileVersion f.Path)
                    | FromLegacyOriginal ->
                        copyWhole legacyOriginal f.Path
                        File.Delete legacyOriginal
                        try File.Delete legacyVersion with _ -> ()
                        sprintf "%s: the game's own %s is back (from UPSCALE IT's backup)." f.Family.Label (fileVersion f.Path)
                    | RemoveAdded ->
                        File.Delete f.Path
                        File.Delete legacyVersion
                        sprintf "%s: removed - UPSCALE IT had added it, the game never had one." f.Family.Label
                    | DropStaleBackup ->
                        File.Delete backup
                        sprintf "%s: the game's own file is already in place." f.Family.Label
                    | NothingToDo -> sprintf "%s: already the game's own." f.Family.Label
                updateRecord game f.Family f.Path None
                AppLog.info (sprintf "Upscaler Vault restore in %s: %s" game.Title line)
                Ok line
            with ex ->
                AppLog.error ("Upscaler Vault restore in " + game.Title) ex
                Error ex.Message
