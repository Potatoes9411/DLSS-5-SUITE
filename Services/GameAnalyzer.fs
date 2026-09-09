namespace DLSS_5_MANAGER.Services

open System
open System.IO
open System.Diagnostics
open System.Text.RegularExpressions
open System.Collections.Generic

/// Resolves the *real* game executable inside an install folder and locates
/// the DLSS / NVIDIA Streamline runtime files that ship with the game.
module GameAnalyzer =

    // =====================================================================
    // FILE VERSION HELPERS
    // =====================================================================
    type FileVer =
        { Major: int
          Minor: int
          Build: int
          Revision: int }

    let zeroVer = { Major = 0; Minor = 0; Build = 0; Revision = 0 }

    let isZeroVer (v: FileVer) =
        v.Major = 0 && v.Minor = 0 && v.Build = 0 && v.Revision = 0

    /// Human readable form. DLSS uses "310.8", Streamline uses "2.13".
    let verText (v: FileVer) =
        if isZeroVer v then "unknown"
        elif v.Build = 0 && v.Revision = 0 then sprintf "%d.%d" v.Major v.Minor
        else sprintf "%d.%d.%d" v.Major v.Minor v.Build

    let compareVer (a: FileVer) (b: FileVer) =
        compare (a.Major, a.Minor, a.Build, a.Revision) (b.Major, b.Minor, b.Build, b.Revision)

    let readFileVersion (path: string) : FileVer =
        try
            if File.Exists(path) then
                let fvi = FileVersionInfo.GetVersionInfo(path)
                { Major = fvi.FileMajorPart
                  Minor = fvi.FileMinorPart
                  Build = fvi.FileBuildPart
                  Revision = fvi.FilePrivatePart }
            else
                zeroVer
        with _ ->
            zeroVer

    /// FileDescription (falls back to ProductName) - the single strongest
    /// signal for identifying which executable really is the game.
    let readDescription (path: string) : string =
        try
            let fvi = FileVersionInfo.GetVersionInfo(path)
            let d =
                if String.IsNullOrWhiteSpace(fvi.FileDescription) then fvi.ProductName
                else fvi.FileDescription

            if isNull d then "" else d.Trim()
        with _ ->
            ""

    // =====================================================================
    // TEXT SIMILARITY
    // =====================================================================
    let private stopWords =
        HashSet<string>(
            [| "the"; "of"; "a"; "an"; "and"; "edition"; "complete"; "deluxe"; "ultimate"; "definitive"
               "remastered"; "remake"; "goty"; "game"; "year"; "enhanced"; "directors"; "cut"; "anniversary"
               "special"; "gold"; "premium"; "standard"; "repack"; "win64"; "shipping"; "x64"; "win"; "exe" |],
            StringComparer.OrdinalIgnoreCase
        )

    let private normalize (s: string) =
        if String.IsNullOrWhiteSpace(s) then ""
        else Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]", "")

    let private tokenize (s: string) =
        if String.IsNullOrWhiteSpace(s) then
            [||]
        else
            Regex
                .Replace(s.ToLowerInvariant(), @"[^a-z0-9]+", " ")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            |> Array.filter (fun t -> not (stopWords.Contains(t)))

    /// 0.0 .. 1.0 - 1.0 means the two names are the same thing.
    let private similarity (a: string) (b: string) : float =
        let na = normalize a
        let nb = normalize b

        if na = "" || nb = "" then
            0.0
        elif na = nb then
            1.0
        else
            let ta = HashSet<string>(tokenize a, StringComparer.OrdinalIgnoreCase)
            let tb = HashSet<string>(tokenize b, StringComparer.OrdinalIgnoreCase)

            if ta.Count = 0 || tb.Count = 0 then
                if na.Contains(nb) || nb.Contains(na) then 0.5 else 0.0
            else
                let intersection = ta |> Seq.filter tb.Contains |> Seq.length
                let smaller = min ta.Count tb.Count
                let coverage = float intersection / float smaller

                if coverage >= 0.999 then 0.85
                elif coverage >= 0.6 then 0.6
                elif coverage > 0.0 then 0.3
                elif na.Contains(nb) || nb.Contains(na) then 0.45
                else 0.0

    /// The same measure the executable resolver uses, for anyone else who has
    /// to decide whether two names are the same game. The poster lookup needs
    /// it: a store search will always return something, and without a check
    /// that the something is actually this game it will happily hand back the
    /// artwork for a different one.
    let titleSimilarity (a: string) (b: string) : float = similarity a b

    // =====================================================================
    // EXCLUSION RULES
    // =====================================================================
    let private skippedDirNames =
        HashSet<string>(
            [| "_commonredist"; "commonredist"; "redist"; "redists"; "redistributable"; "redistributables"
               "directx"; "direct x"; "vcredist"; "vc_redist"; "vcred"; "dotnet"; "dotnetfx"; "mono"; "openal"
               "dependencies"; "prerequisites"; "prereq"; "installer"; "installers"; "__installer"
               "easyanticheat"; "easyanticheat_eos"; "battleye"; "denuvo"; "nprotect"; "xigncode3"; "vanguard"
               "anticheat"; "crashreporter"; "crashreportclient"; "shadercache"; "d3dscache"; "savedata"
               "saves"; "savegames"; "logs"; "screenshots"; "manual"; "manuals"; "soundtrack"; "artbook"
               "extras"; "bonus"; "documentation"; "docs"; "support"; "tools"; "sdk"; "mods"; "dlc"
               "backup"; "backups"; "_backup"; "dlss5_backup" |],
            StringComparer.OrdinalIgnoreCase
        )

    /// Unreal ships engine-side tools under Engine\Binaries - never the game.
    let private isSkippedDirPath (fullPath: string) =
        let p = fullPath.Replace('\\', '/').ToLowerInvariant()
        p.Contains("/engine/binaries/")
        || p.Contains("/engine/extras/")
        || p.Contains("/engine/plugins/")

    let private isSkippedDir (dirPath: string) =
        let name = Path.GetFileName(dirPath)
        skippedDirNames.Contains(name) || isSkippedDirPath dirPath

    let private excludedExeNames =
        HashSet<string>(
            [| "steam.exe"; "steamservice.exe"; "steamwebhelper.exe"; "steamerrorreporter.exe"
               "epicgameslauncher.exe"; "epicwebhelper.exe"; "gog_galaxy.exe"; "galaxyclient.exe"
               "origin.exe"; "eadesktop.exe"; "ubisoftconnect.exe"; "upc.exe"; "battle.net.exe"; "uplay.exe"
               "rockstargameslauncher.exe"; "rockstar-games-launcher.exe"; "rockstar-games-epic.exe"
               "social-club-setup.exe"; "playgtav.exe"; "redprelauncher.exe"; "redistributableuninstaller.exe"
               "dxsetup.exe"; "dxwebsetup.exe"; "oalinst.exe"; "createdump.exe"; "7za.exe"; "7z.exe"
               "crashreporter.exe"; "crashreportclient.exe"; "crashreportclient-win64-shipping.exe"
               "unrealcecommon.exe"; "bugsplat.exe"; "sendrpt.exe"; "werfault.exe"; "feedback.exe"
               "reporttool.exe"; "anticheatinstaller.exe"; "easyanticheat_setup.exe"; "easyanticheat.exe"
               "beservice.exe"; "beservice_x64.exe"; "bedaisy.exe"; "vanguard.exe"; "vconsole2.exe"
               "launcher.exe"; "launch.exe"; "play.exe"; "start.exe"; "gamelauncher.exe"; "config.exe"
               "settings.exe"; "configuration.exe"; "autorun.exe"; "patch.exe"; "updater.exe"
               "uninstaller.exe"; "uninstall.exe"; "unins000.exe"; "unins001.exe"; "notification_helper.exe"
               "quickstart.exe"; "activation.exe"; "cleanup.exe" |],
            StringComparer.OrdinalIgnoreCase
        )

    let private excludedPrefixes =
        [| "vcredist"; "vc_redist"; "dotnet"; "ndp4"; "unins"; "setup_"; "install_"; "unitycrashhandler"
           "ue4prereqsetup"; "ueprereqsetup"; "directx"; "dxsetup"; "oalinst"; "steamsetup" |]

    let private excludedSuffixes =
        [| "_be.exe"; "-be.exe"; "-win64-debuggame.exe"; "-win64-test.exe"; "-win32-debuggame.exe"
           "editor.exe"; "-editor.exe"; "server.exe"; "-server.exe"; "dedicatedserver.exe"; "_dev.exe"
           "-cmd.exe"; "setup.exe"; "installer.exe"; "_legacy_app.exe"; "crashhandler.exe"
           "crashreporter.exe"; "helper.exe"; "_uninstall.exe" |]

    let private isExcludedExe (fileName: string) =
        let lower = fileName.ToLowerInvariant()

        if excludedExeNames.Contains(lower) then true
        elif excludedPrefixes |> Array.exists lower.StartsWith then true
        elif excludedSuffixes |> Array.exists lower.EndsWith then true
        else false

    /// Words that mark a binary as a helper rather than the game itself.
    let private launcherWords =
        [| "launcher"; "prelauncher"; "redirector"; "setup"; "installer"; "uninstall"; "updater"; "patcher"
           "crash"; "report"; "service"; "helper"; "console"; "config"; "settings"; "editor"; "server"
           "benchmark"; "anticheat"; "redistributable"; "dump"; "diagnostic"; "overlay"; "activation" |]

    let private isLauncherish (text: string) =
        if String.IsNullOrWhiteSpace(text) then
            false
        else
            let lower = text.ToLowerInvariant()
            launcherWords |> Array.exists lower.Contains

    // =====================================================================
    // DIRECTORY WALKER
    // =====================================================================
    let private walkDirectories (root: string) (maxDepth: int) : string list =
        let acc = List<string>()

        if Directory.Exists(root) then
            let queue = Queue<string * int>()
            queue.Enqueue((root, 0))

            while queue.Count > 0 do
                let (dir, depth) = queue.Dequeue()
                acc.Add(dir)

                if depth < maxDepth then
                    let subs =
                        try Directory.GetDirectories(dir)
                        with _ -> [||]

                    for sub in subs do
                        if not (isSkippedDir sub) then queue.Enqueue((sub, depth + 1))

        List.ofSeq acc

    // =====================================================================
    // EXECUTABLE RESOLUTION
    // =====================================================================
    /// Per-folder signals (Steam API next to it, engine DLLs, ...) cached so
    /// that a folder with 20 executables is only inspected once.
    let private folderBonus (cache: Dictionary<string, float>) (dir: string) : float =
        match cache.TryGetValue(dir) with
        | true, v -> v
        | _ ->
            let mutable bonus = 0.0

            try
                let files = Directory.GetFiles(dir)

                let names =
                    HashSet<string>(files |> Array.map Path.GetFileName, StringComparer.OrdinalIgnoreCase)

                let dllCount =
                    files
                    |> Array.filter (fun f -> f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    |> Array.length

                if names.Contains("steam_api64.dll") || names.Contains("steam_api.dll") then
                    bonus <- bonus + 6000.0

                if names.Contains("EOSSDK-Win64-Shipping.dll") || names.Contains("GalaxyPeer64.dll") then
                    bonus <- bonus + 3000.0

                if names.Contains("nvngx_dlss.dll")
                   || names.Contains("sl.interposer.dll")
                   || names.Contains("nvngx_dlssg.dll") then
                    bonus <- bonus + 5000.0

                if dllCount >= 8 then bonus <- bonus + 2500.0
                elif dllCount >= 3 then bonus <- bonus + 1000.0
            with _ ->
                ()

            cache.[dir] <- bonus
            bonus

    let private scoreExecutable
        (gameTitle: string)
        (root: string)
        (file: FileInfo)
        (cache: Dictionary<string, float>)
        (largestCandidate: int64)
        : float =

        let name = file.Name
        let lower = name.ToLowerInvariant()
        let nameNoExt = Path.GetFileNameWithoutExtension(name)
        let dir = file.DirectoryName

        let relative =
            let full = file.FullName
            if full.StartsWith(root, StringComparison.OrdinalIgnoreCase) then
                full.Substring(root.Length).TrimStart('\\', '/')
            else
                name

        let relLower = "/" + relative.Replace('\\', '/').ToLowerInvariant()
        let description = readDescription file.FullName

        let mutable score = 0.0

        // 1. Version-resource description vs. the library title (dominant signal)
        score <- score + (similarity description gameTitle) * 50000.0

        // 2. File name vs. the library title
        score <- score + (similarity nameNoExt gameTitle) * 20000.0

        // 3. Helper / launcher binaries are actively pushed down
        if isLauncherish description then score <- score - 25000.0
        if isLauncherish nameNoExt then score <- score - 15000.0

        // 4. Engine specific layouts
        if relLower.Contains("/binaries/win64/") then
            if lower.EndsWith("-win64-shipping.exe") then score <- score + 25000.0
            else score <- score + 12000.0
        elif relLower.Contains("/bin/x64_dx12/") then
            // RED Engine ships a DX11 and a DX12 build; DLSS lives in the DX12 one.
            score <- score + 14000.0
        elif relLower.Contains("/bin/x64/")
             || relLower.Contains("/bin/win64/")
             || relLower.Contains("/binaries/") then
            score <- score + 11500.0
        elif relLower.Contains("/x64/") || relLower.Contains("/win64/") then
            score <- score + 7000.0

        // 5. Depth: root executables are usually the game, deep ones usually are not
        let depth = relative.Replace('\\', '/').Split('/').Length - 1
        if depth = 0 then score <- score + 6000.0
        else score <- score - 800.0 * float (max 0 (depth - 1))

        // 6. Folder context
        score <- score + folderBonus cache dir

        // 7. Unity: <Name>.exe always sits next to <Name>_Data
        try
            if Directory.Exists(Path.Combine(dir, nameNoExt + "_Data")) then
                score <- score + 15000.0
        with _ ->
            ()

        // 8. Size, capped so a huge installer cannot outrank the real game
        score <- score + (min (float file.Length / 1048576.0) 400.0) * 20.0

        // 9. A launcher stub, measured against the biggest binary in the same
        //    install rather than against any absolute size.
        //
        //    This is the rule that decides the cases the ones above get wrong.
        //    A publisher that ships a launcher almost always names it after
        //    the game and puts it at the root, so it collects the full name
        //    similarity and the root-depth bonus and wins - while the real
        //    binary sits in Binaries\Win64 under a name that matches nothing.
        //    Evil West's stub is 741 KB against a 99 MB game, Gotham Knights'
        //    is 1.2 MB against 123 MB, Assassin's Creed Brotherhood's is
        //    239 KB against 46 MB. Two orders of magnitude is not a variation
        //    in build settings; it means the file contains no game.
        //
        //    Relative, because absolute thresholds break on the games that
        //    genuinely are small: a Unity title's executable is a few hundred
        //    kilobytes next to its UnityPlayer.dll and is still the right
        //    target - and it is also the largest executable it ships, so this
        //    never touches it. The guard on the largest size keeps the whole
        //    rule out of the way of installs where nothing is big.
        if largestCandidate > 8L * 1024L * 1024L then
            let ratio = float file.Length / float largestCandidate

            if ratio < 0.05 then
                score <- score - 45000.0 * (1.0 - ratio / 0.05)

        // 10. Repack crack folders hold a duplicate of the game exe
        if Regex.IsMatch(relLower, @"crack|razor1911|codex|plaza|skidrow|empress|goldberg|steamless") then
            score <- score - 9000.0

        score

    /// Returns (executablePath, sizeInBytes). Empty path when nothing sensible was found.
    let resolveGameExecutable (gameRoot: string) (gameTitle: string) : string * int64 =
        if String.IsNullOrWhiteSpace(gameRoot) || not (Directory.Exists(gameRoot)) then
            ("", 0L)
        else
            try
                let root = gameRoot.TrimEnd('\\', '/')
                let cache = Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)

                // Gathered before anything is scored, because the stub test
                // compares each candidate against the biggest one here and so
                // cannot be answered one file at a time.
                let files = List<FileInfo>()

                for dir in walkDirectories root 5 do
                    let found =
                        try DirectoryInfo(dir).GetFiles("*.exe")
                        with _ -> [||]

                    for file in found do
                        // Tiny stubs are never the game.
                        if not (isExcludedExe file.Name) && file.Length > 40960L then
                            files.Add(file)

                let largestCandidate =
                    files |> Seq.fold (fun acc (f: FileInfo) -> max acc f.Length) 0L

                let candidates = List<string * int64 * float>()

                for file in files do
                    let score = scoreExecutable gameTitle root file cache largestCandidate
                    candidates.Add((file.FullName, file.Length, score))

                if candidates.Count = 0 then
                    ("", 0L)
                else
                    let (bestPath, bestSize, _) =
                        candidates |> Seq.maxBy (fun (_, _, score) -> score)

                    (bestPath, bestSize)
            with _ ->
                ("", 0L)

    /// True when an executable is a launcher / helper stub rather than the game.
    /// Store manifests (Epic, GOG) often point at one of these, and mods must
    /// never be installed next to them.
    let isLikelyLauncherExe (exePath: string) : bool =
        try
            if String.IsNullOrWhiteSpace(exePath) || not (File.Exists(exePath)) then
                true
            else
                let name = Path.GetFileName(exePath)

                isExcludedExe name
                || isLauncherish (Path.GetFileNameWithoutExtension(name))
                || isLauncherish (readDescription exePath)
        with _ ->
            true

    // =====================================================================
    // DLSS / STREAMLINE DISCOVERY
    // =====================================================================
    let dlssFileNames =
        [| "nvngx_dlss.dll"; "nvngx_dlssg.dll"; "nvngx_dlssd.dll" |]

    let streamlineFileNames =
        [| "sl.interposer.dll"; "sl.common.dll"; "sl.dlss.dll"; "sl.dlss_g.dll"; "sl.dlss_nr.dll"
           "sl.nis.dll"; "sl.pcl.dll"; "sl.reflex.dll" |]

    let dlssnrFileName = "nvngx_dlssnr.dll"

    type ModFile =
        { Name: string
          Path: string
          Version: FileVer }

    type ModFolder =
        { Directory: string
          Files: ModFile list }

    /// Highest version among the files of a folder - represents the folder's runtime version.
    let folderVersion (folder: ModFolder) : FileVer =
        folder.Files
        |> List.fold (fun acc f -> if compareVer f.Version acc > 0 then f.Version else acc) zeroVer

    let findModFoldersDepth (root: string) (targetNames: string[]) (maxDepth: int) : ModFolder list =
        if String.IsNullOrWhiteSpace(root) || not (Directory.Exists(root)) then
            []
        else
            let wanted = HashSet<string>(targetNames, StringComparer.OrdinalIgnoreCase)

            [ for dir in walkDirectories (root.TrimEnd('\\', '/')) maxDepth do
                let files =
                    try Directory.GetFiles(dir, "*.dll")
                    with _ -> [||]

                let hits =
                    files
                    |> Array.filter (fun f -> wanted.Contains(Path.GetFileName(f)))
                    |> Array.map (fun f ->
                        { Name = Path.GetFileName(f)
                          Path = f
                          Version = readFileVersion f })
                    |> Array.toList

                if not hits.IsEmpty then
                    yield { Directory = dir; Files = hits } ]

    let findModFolders (root: string) (targetNames: string[]) : ModFolder list =
        findModFoldersDepth root targetNames 6

    /// Search roots: the install directory plus the executable's own folder
    /// (repacks sometimes put the runtime next to the exe only).
    let searchRoots (installDir: string) (exePath: string) : string list =
        let roots = List<string>()

        if not (String.IsNullOrWhiteSpace(installDir)) && Directory.Exists(installDir) then
            roots.Add(installDir.TrimEnd('\\', '/'))

        if not (String.IsNullOrWhiteSpace(exePath)) then
            let exeDir =
                try Path.GetDirectoryName(exePath)
                with _ -> ""

            if not (String.IsNullOrWhiteSpace(exeDir)) && Directory.Exists(exeDir) then
                let normalized = exeDir.TrimEnd('\\', '/')

                let alreadyCovered =
                    roots
                    |> Seq.exists (fun r -> normalized.StartsWith(r, StringComparison.OrdinalIgnoreCase))

                if not alreadyCovered then roots.Add(normalized)

        List.ofSeq roots

    let findDlssFolders (installDir: string) (exePath: string) : ModFolder list =
        searchRoots installDir exePath
        |> List.collect (fun r -> findModFolders r dlssFileNames)
        |> List.distinctBy (fun f -> f.Directory.ToLowerInvariant())

    let findStreamlineFolders (installDir: string) (exePath: string) : ModFolder list =
        searchRoots installDir exePath
        |> List.collect (fun r -> findModFolders r streamlineFileNames)
        |> List.distinctBy (fun f -> f.Directory.ToLowerInvariant())

    // =====================================================================
    // GRAPHICS API & ARCHITECTURE
    // =====================================================================
    // Nothing lying in a game folder names the Direct3D generation. d3d12.dll,
    // d3d11.dll, d3d9.dll, dxgi.dll, vulkan-1.dll and opengl32.dll all live in
    // System32 and are bound from there, so a copy sitting next to a game is a
    // wrapper somebody dropped on it - very often one of ours, since those are
    // exactly the slots ReShade and OptiScaler take over. Reading them back is
    // worse than useless: it reports the mod as the game.
    //
    // What does name the API is the executable itself. Its import directories
    // list the libraries it binds at load time, and the module names it hands
    // to LoadLibrary sit in its string data. Both are read here. Files on disk
    // are still consulted, but only the ones a game genuinely ships - the
    // Agility SDK, the old D3DX redistributables, an engine's own backends -
    // and an engine that states its default outright is believed over anything
    // else.

    /// The API keys the rest of the app speaks, newest first. The order is
    /// only a tie-break: when two generations score the same the newer one
    /// wins, because that is the path DLSS rides on and the one the mod hooks.
    let private apiPriority = [| "dx12"; "vulkan"; "dx11"; "dx10"; "dx9"; "opengl" |]

    /// Never read more than this off one file. A shipping executable is tens
    /// of megabytes; the cap only exists so a pathological file cannot stall
    /// the sheet.
    let private maxScanBytes = 256L * 1024L * 1024L

    /// A score at or above this came from an engine stating its own default
    /// rather than from anything inferred, and settles the answer outright.
    let private declaredWeight = 1000.0

    /// Below this, an API was mentioned once somewhere and nothing more,
    /// which is noise rather than an answer.
    let private noiseFloor = 35.0

    let private addEvidence (acc: Dictionary<string, float>) (api: string) (weight: float) =
        if api <> "" && weight <> 0.0 then
            match acc.TryGetValue(api) with
            | true, existing -> acc.[api] <- existing + weight
            | _ -> acc.[api] <- weight

    // ---------------------------------------------------------------------
    // PE IMPORT TABLES
    // ---------------------------------------------------------------------
    /// Just enough of the PE format to walk the two import directories and
    /// turn an RVA back into a file offset. The file is only ever read.
    module private Pe =

        type Section =
            { Rva: uint32
              VirtualSize: uint32
              RawOffset: uint32
              RawSize: uint32 }

        type Headers =
            { Machine: uint16
              ImageBase: uint64
              Sections: Section list
              ImportRva: uint32
              DelayImportRva: uint32 }

        /// Anything malformed aborts the read - a file we cannot parse
        /// contributes no evidence, rather than a wrong answer.
        let private bail () = failwith "not a readable PE image"

        let readHeaders (stream: Stream) (reader: BinaryReader) : Headers option =
            try
                if stream.Length < 0x40L then bail ()
                stream.Position <- 0L
                if reader.ReadUInt16() <> 0x5A4Dus then bail () // MZ

                stream.Position <- 0x3CL
                let peOffset = int64 (reader.ReadInt32())
                if peOffset <= 0L || peOffset + 24L > stream.Length then bail ()

                stream.Position <- peOffset
                if reader.ReadUInt32() <> 0x00004550u then bail () // PE\0\0

                let machine = reader.ReadUInt16()
                let sectionCount = int (reader.ReadUInt16())
                stream.Position <- stream.Position + 12L // timestamp, symbol table, symbol count
                let optionalSize = int (reader.ReadUInt16())
                reader.ReadUInt16() |> ignore // characteristics

                let optionalOffset = peOffset + 24L
                if optionalOffset + int64 optionalSize > stream.Length then bail ()

                stream.Position <- optionalOffset
                let magic = reader.ReadUInt16()

                // The data directory follows the fixed part of the optional
                // header: 96 bytes for PE32, 112 for PE32+. ImageBase is read
                // on the way past, for the delay table's older address form.
                let dirOffset, imageBase =
                    match magic with
                    | 0x010Bus ->
                        if optionalSize < 96 then bail ()
                        stream.Position <- optionalOffset + 28L
                        optionalOffset + 96L, uint64 (reader.ReadUInt32())
                    | 0x020Bus ->
                        if optionalSize < 112 then bail ()
                        stream.Position <- optionalOffset + 24L
                        optionalOffset + 112L, reader.ReadUInt64()
                    | _ ->
                        bail ()
                        0L, 0UL

                // Directory 1 is the import table, directory 13 the delay-load one.
                let readDirRva (index: int) =
                    let at = dirOffset + int64 index * 8L

                    if at + 4L > stream.Length then
                        0u
                    else
                        stream.Position <- at
                        reader.ReadUInt32()

                let importRva = readDirRva 1
                let delayRva = readDirRva 13
                let sectionOffset = optionalOffset + int64 optionalSize

                let sections =
                    [ for i in 0 .. (min sectionCount 96) - 1 do
                        let at = sectionOffset + int64 i * 40L

                        if at + 40L <= stream.Length then
                            stream.Position <- at + 8L // past the 8-byte name
                            let virtualSize = reader.ReadUInt32()
                            let rva = reader.ReadUInt32()
                            let rawSize = reader.ReadUInt32()
                            let rawOffset = reader.ReadUInt32()

                            yield
                                { Rva = rva
                                  VirtualSize = virtualSize
                                  RawOffset = rawOffset
                                  RawSize = rawSize } ]

                Some
                    { Machine = machine
                      ImageBase = imageBase
                      Sections = sections
                      ImportRva = importRva
                      DelayImportRva = delayRva }
            with _ ->
                None

        let offsetOfRva (headers: Headers) (rva: uint32) : int64 =
            if rva = 0u then
                -1L
            else
                headers.Sections
                |> List.tryFind (fun s ->
                    let span = max s.VirtualSize s.RawSize
                    span > 0u && rva >= s.Rva && rva - s.Rva < span)
                |> Option.map (fun s -> int64 s.RawOffset + int64 (rva - s.Rva))
                |> Option.defaultValue -1L

        /// A module name: printable ASCII up to the terminator. Anything else
        /// means the RVA did not point at a name, so nothing is returned.
        let private readModuleName (stream: Stream) (reader: BinaryReader) (offset: int64) : string =
            if offset < 0L || offset >= stream.Length then
                ""
            else
                stream.Position <- offset
                let sb = Text.StringBuilder(64)
                let mutable reading = true

                while reading && sb.Length < 128 && stream.Position < stream.Length do
                    let b = reader.ReadByte()

                    if b = 0uy then
                        reading <- false
                    elif b < 32uy || b > 126uy then
                        reading <- false
                        sb.Clear() |> ignore
                    else
                        sb.Append(char b) |> ignore

                sb.ToString()

        /// "32" / "64" straight from the COFF machine type.
        let architecture (path: string) : string =
            try
                use stream =
                    new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096)

                use reader = new BinaryReader(stream)

                match readHeaders stream reader with
                | None -> ""
                | Some headers ->
                    match headers.Machine with
                    | 0x014Cus -> "32"
                    | 0x8664us
                    | 0xAA64us -> "64"
                    | _ -> ""
            with _ ->
                ""

        /// The DLLs an image binds. Static and delay-load entries are kept
        /// apart, but not because one outranks the other - see the note on
        /// the needle weights, which is the opposite of what it looks like.
        let importedModules (path: string) : HashSet<string> * HashSet<string> =
            let bound = HashSet<string>(StringComparer.OrdinalIgnoreCase)
            let delayed = HashSet<string>(StringComparer.OrdinalIgnoreCase)

            try
                use stream =
                    new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 8192)

                use reader = new BinaryReader(stream)

                match readHeaders stream reader with
                | None -> ()
                | Some headers ->
                    let toOffset = offsetOfRva headers

                    // Static imports: 20-byte descriptors, the DLL name's RVA
                    // at +12, the table closed by an all-zero entry.
                    let importBase = toOffset headers.ImportRva

                    if importBase >= 0L then
                        let mutable i = 0
                        let mutable more = true

                        while more && i < 4096 do
                            let entry = importBase + int64 i * 20L

                            if entry + 20L > stream.Length then
                                more <- false
                            else
                                stream.Position <- entry + 12L
                                let nameRva = reader.ReadUInt32()

                                if nameRva = 0u then
                                    more <- false
                                else
                                    let name = readModuleName stream reader (toOffset nameRva)
                                    if name <> "" then bound.Add(name) |> ignore
                                    i <- i + 1

                    // Delay-load imports: 32-byte descriptors, the name at +4.
                    // Bit 0 of the attributes says those fields are RVAs; the
                    // original form stored virtual addresses, so rebase those.
                    let delayBase = toOffset headers.DelayImportRva

                    if delayBase >= 0L then
                        let mutable i = 0
                        let mutable more = true

                        while more && i < 4096 do
                            let entry = delayBase + int64 i * 32L

                            if entry + 32L > stream.Length then
                                more <- false
                            else
                                stream.Position <- entry
                                let attributes = reader.ReadUInt32()
                                let nameField = reader.ReadUInt32()

                                if nameField = 0u then
                                    more <- false
                                else
                                    let rva =
                                        if attributes &&& 1u = 1u then nameField
                                        elif uint64 nameField > headers.ImageBase then
                                            uint32 (uint64 nameField - headers.ImageBase)
                                        else
                                            0u

                                    let name = readModuleName stream reader (toOffset rva)
                                    if name <> "" then delayed.Add(name) |> ignore
                                    i <- i + 1
            with _ ->
                ()

            (bound, delayed)

    // ---------------------------------------------------------------------
    // BINARY STRING SEARCH
    // ---------------------------------------------------------------------
    /// Case-insensitive search of a file for a handful of short ASCII needles,
    /// each also matched in the UTF-16 form some engines store names in. This
    /// is what catches a game that never binds the runtime at load time and
    /// calls LoadLibrary("d3d12.dll") instead - which is what every engine
    /// with a switchable renderer does.
    ///
    /// Read in 4 MB chunks, so a 200 MB executable never lands in memory, and
    /// abandoned as soon as every needle has been seen.
    ///
    /// The vectorised span search is kept in its own function: a byref-like
    /// value may not live inside a `try` block.
    let private containsBytes (buffer: byte[]) (length: int) (pattern: byte[]) : bool =
        MemoryExtensions.IndexOf(ReadOnlySpan<byte>(buffer, 0, length), ReadOnlySpan<byte>(pattern))
        >= 0

    let private scanBinaryFor (path: string) (needles: string[]) : HashSet<string> =
        let hits = HashSet<string>(StringComparer.OrdinalIgnoreCase)

        try
            let patterns =
                needles
                |> Array.collect (fun n ->
                    let lower = n.ToLowerInvariant()

                    [| (n, Text.Encoding.ASCII.GetBytes(lower))
                       (n, Text.Encoding.Unicode.GetBytes(lower)) |])

            let longest = patterns |> Array.fold (fun acc (_, p) -> max acc p.Length) 0

            use stream =
                new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    65536,
                    FileOptions.SequentialScan
                )

            let chunk = 4 * 1024 * 1024
            let buffer = Array.zeroCreate<byte> (chunk + longest)
            let mutable carried = 0
            let mutable scanned = 0L
            let mutable reading = true

            while reading && scanned < maxScanBytes && hits.Count < needles.Length do
                let read = stream.Read(buffer, carried, chunk)

                if read <= 0 then
                    reading <- false
                else
                    let filled = carried + read
                    scanned <- scanned + int64 read

                    // Fold to lower case in place, so every needle compares as
                    // its lower-case form. ASCII only, which is all a module
                    // name or an entry point ever is.
                    for i in carried .. filled - 1 do
                        let b = buffer.[i]
                        if b >= 65uy && b <= 90uy then buffer.[i] <- b + 32uy

                    for j in 0 .. patterns.Length - 1 do
                        let (tag, pattern) = patterns.[j]

                        if not (hits.Contains(tag)) && containsBytes buffer filled pattern then
                            hits.Add(tag) |> ignore

                    // Carry the tail forward, so a needle straddling the seam
                    // between two chunks is still found.
                    carried <- min filled longest
                    if carried > 0 then Array.blit buffer (filled - carried) buffer 0 carried
        with _ ->
            ()

        hits

    // ---------------------------------------------------------------------
    // WHAT NAMES AN API
    // ---------------------------------------------------------------------
    /// Module names and entry points, and the generation each one implies.
    /// dxgi is deliberately absent: every Direct3D 10, 11 and 12 title binds
    /// it, so it separates nothing. d3d11 is present but scores like any
    /// other single signal, because a Direct3D 12 title carries it too - for
    /// D3D11On12, for video decode, for the fallback path it never takes.
    let private apiNeedles =
        [| "d3d12.dll", "dx12"
           "d3d12core.dll", "dx12"
           "d3d12createdevice", "dx12"
           "d3d11.dll", "dx11"
           "d3d11createdevice", "dx11"
           "d3d10.dll", "dx10"
           "d3d10_1.dll", "dx10"
           "d3d9.dll", "dx9"
           "direct3dcreate9", "dx9"
           "vulkan-1.dll", "vulkan"
           "vkgetinstanceprocaddr", "vulkan"
           "vkcreateswapchainkhr", "vulkan"
           "opengl32.dll", "opengl"
           "wglcreatecontext", "opengl" |]

    let private apiNeedleNames = apiNeedles |> Array.map fst

    let private apiOfNeedle (needle: string) =
        apiNeedles
        |> Array.tryFind (fun (n, _) -> String.Equals(n, needle, StringComparison.OrdinalIgnoreCase))
        |> Option.map snd
        |> Option.defaultValue ""

    /// Files a game really does ship, and what each one says. The system
    /// runtimes are not here on purpose - see the note at the top of this
    /// section.
    /// dxcompiler.dll and dxil.dll are deliberately absent. They look like
    /// Direct3D 12 markers and are not: DXC targets shader model 5.1 as well,
    /// so Direct3D 11 titles ship them too - Crysis Remastered is one.
    let private shippedFileEvidence =
        [| "d3d12core.dll", ("dx12", 90.0) // Agility SDK; DX12 titles carry it
           "d3d12sdklayers.dll", ("dx12", 45.0)
           "rendersystemdx11.dll", ("dx11", 70.0) // Source 2 names its backends
           "rendersystemvulkan.dll", ("vulkan", 45.0)
           "rendersystemdx9.dll", ("dx9", 45.0)
           "d3d12rhi.dll", ("dx12", 60.0) // a modular Unreal build does too
           "d3d11rhi.dll", ("dx11", 60.0)
           "vulkanrhi.dll", ("vulkan", 60.0) |]

    /// Folders the installer creates. Their contents describe the mod, not
    /// the game, so nothing inside them is ever read as evidence.
    let private modOwnedDirs =
        HashSet<string>(
            [ "host64"; "optiscaler"; "reshade-shaders"; "dlss5_backup"; "d3d9" ],
            StringComparer.OrdinalIgnoreCase
        )

    /// A last guard on the shipped-file evidence: if one of those names turns
    /// out to carry a mod's version resource, it describes the mod.
    let private isModWrapper (path: string) =
        try
            let fvi = FileVersionInfo.GetVersionInfo(path)
            let product = if isNull fvi.ProductName then "" else fvi.ProductName
            let original = if isNull fvi.OriginalFilename then "" else fvi.OriginalFilename

            product.Contains("ReShade")
            || product.Contains("dgVoodoo")
            || product.Contains("OptiScaler")
            || original.Equals("OptiScaler.dll", StringComparison.OrdinalIgnoreCase)
        with _ ->
            false

    /// Every DLL name sitting beside the executable and one or two levels
    /// under it, which is where the Agility SDK and an engine's own render
    /// backends live.
    ///
    /// The redistributable folders are skipped through the same rule the
    /// executable walker uses. That matters more than it looks: a "directX"
    /// or "_CommonRedist" folder is a copy of Microsoft's installer, so it
    /// holds the whole d3dx9 set no matter what the game renders with - Just
    /// Cause 2 is a Direct3D 10 title shipping one. Folders the installer
    /// owns are skipped for the same reason.
    let private nearbyDllNames (exeDir: string) : HashSet<string> =
        let names = HashSet<string>(StringComparer.OrdinalIgnoreCase)

        let rec walk (dir: string) (depth: int) =
            try
                for file in Directory.GetFiles(dir, "*.dll") do
                    names.Add(Path.GetFileName(file)) |> ignore
            with _ ->
                ()

            if depth > 0 then
                try
                    for sub in Directory.GetDirectories(dir) do
                        if not (isSkippedDir sub) && not (modOwnedDirs.Contains(Path.GetFileName(sub))) then
                            walk sub (depth - 1)
                with _ ->
                    ()

        walk exeDir 2
        names

    /// Unreal compiles every renderer it supports into one shipping binary,
    /// so nothing in the executable can say which one it boots with - both
    /// Direct3D 11 and Direct3D 12 are always in there. Two things outside it
    /// can, and this is the only engine where they are needed.
    ///
    /// DefaultEngine.ini states the default outright, for the games that
    /// shipped their config unpacked rather than cooked into a .pak.
    ///
    /// When it did not, the Agility SDK settles it. Unreal Engine 5 defaults
    /// to Direct3D 12 and ships D3D12Core.dll in a D3D12 folder beside the
    /// executable so it can rely on a current runtime; Unreal Engine 4
    /// defaults to Direct3D 11 and ships nothing. Across the Unreal titles
    /// this was tested against that split is exact - Silent Hill 2, Silent
    /// Hill f and Nobody Wants to Die carry it, Postal 4, Atomic Heart,
    /// Gotham Knights and Destroy All Humans! do not.
    ///
    /// Returns "" when this is not an Unreal layout at all.
    let private unrealDeclaredRhi (exePath: string) (exeDir: string) (nearby: HashSet<string>) : string =
        try
            let mutable dir = DirectoryInfo(exeDir)
            let mutable found = ""
            let mutable steps = 0

            let mutable isUnreal =
                Path.GetFileName(exePath).EndsWith("-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase)

            while not (isNull (box dir)) && found = "" && steps < 5 do
                // Engine\Binaries is Unreal's own tree, and every shipped
                // title has one however the game executable was renamed.
                if Directory.Exists(Path.Combine(dir.FullName, "Engine", "Binaries")) then
                    isUnreal <- true

                let ini = Path.Combine(dir.FullName, "Config", "DefaultEngine.ini")

                if File.Exists(ini) then
                    let text =
                        try File.ReadAllText(ini)
                        with _ -> ""

                    let m =
                        Regex.Match(
                            text,
                            @"DefaultGraphicsRHI\s*=\s*(?:DefaultGraphicsRHI_)?(\w+)",
                            RegexOptions.IgnoreCase
                        )

                    if m.Success then
                        found <-
                            match m.Groups.[1].Value.ToLowerInvariant() with
                            | "dx12"
                            | "d3d12" -> "dx12"
                            | "dx11"
                            | "d3d11" -> "dx11"
                            | "vulkan" -> "vulkan"
                            | _ -> ""

                dir <- dir.Parent
                steps <- steps + 1

            if found <> "" then found
            elif not isUnreal then ""
            elif nearby.Contains("D3D12Core.dll") then "dx12"
            else "dx11"
        with _ ->
            ""

    /// Names that would answer for the mod rather than the game: our own
    /// proxies and payload, plus the runtimes a game ships but does not
    /// render with.
    let private notGameCode =
        HashSet<string>(
            [ "d3d9.dll"; "d3d10.dll"; "d3d11.dll"; "d3d12.dll"; "dxgi.dll"; "opengl32.dll"
              "vulkan-1.dll"; "winmm.dll"; "version.dll"; "dbghelp.dll"; "wininet.dll"
              "winhttp.dll"; "nvngx_dlss.dll"; "nvngx_dlssg.dll"; "nvngx_dlssd.dll"
              "nvngx_dlssnr.dll"; "d3dcompiler_47.dll"; "d3dcompiler_46.dll"
              "msvcp140.dll"; "vcruntime140.dll"; "vcruntime140_1.dll"; "concrt140.dll"
              "steam_api.dll"; "steam_api64.dll"; "bink2w64.dll"; "binkw32.dll" ],
            StringComparer.OrdinalIgnoreCase)

    /// The biggest libraries sitting beside the executable, which is where the
    /// engine lives whenever the executable itself is only a loader.
    ///
    /// This is not the unusual case it sounds like. Far Cry 3's farcry3.exe is
    /// a 199 KB Ubisoft stub and the renderer is in FC3.dll and FC3_d3d11.dll;
    /// Dead Island's executable imports nothing at all and its engine is in
    /// engine_x64_rwdi.dll. Reading only the .exe reports "unknown" for every
    /// game built that way.
    let private engineLibraries (exeDir: string) : string list =
        try
            DirectoryInfo(exeDir).GetFiles("*.dll")
            |> Array.filter (fun f ->
                f.Length > 1048576L
                && not (notGameCode.Contains(f.Name))
                && not (f.Name.StartsWith("sl.", StringComparison.OrdinalIgnoreCase))
                && not (isModWrapper f.FullName))
            |> Array.sortByDescending (fun f -> f.Length)
            |> Array.truncate 6
            |> Array.map (fun f -> f.FullName)
            |> Array.toList
        with _ ->
            []

    /// Unity's executable is a stub; UnityPlayer.dll beside it is the engine.
    /// Returns that DLL, or the player's data folder when only that is there -
    /// either one is enough to know the title is a Unity build.
    let private unityEngineMarker (exePath: string) (exeDir: string) : string =
        try
            let player = Path.Combine(exeDir, "UnityPlayer.dll")
            let data = Path.Combine(exeDir, Path.GetFileNameWithoutExtension(exePath) + "_Data")

            if File.Exists(player) then player
            elif Directory.Exists(data) then data
            else ""
        with _ ->
            ""

    /// Everything the executable and its folder have to say, added up per API.
    /// Evidence is kept in tiers rather than pooled, because the three kinds
    /// are not comparable.
    ///
    /// `Declared` is the engine stating its own default, and settles it.
    ///
    /// `Binary` is what the game's own code reaches for. It outranks the
    /// folder outright: Grand Theft Auto IV binds d3d9.dll and nothing else,
    /// and pooling let the d3dx10 redistributable sitting beside it outvote
    /// that and call a Direct3D 9 game Direct3D 10.
    ///
    /// `Folder` is circumstantial, and only consulted when the executable
    /// said nothing at all - which happens when it is packed, and is exactly
    /// when circumstantial evidence is worth having.
    type private ApiEvidence =
        { Declared: string
          Binary: Dictionary<string, float>
          Folder: Dictionary<string, float> }

    let private collectApiEvidence (exePath: string) (exeDir: string) : ApiEvidence =
        let binary = Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
        let folder = Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)

        // One source of evidence contributes one weight per API, whichever of
        // its needles matched and however many of them did. Adding them up
        // instead would measure how many aliases this table happens to list
        // for each API rather than how strong the evidence is: CryEngine
        // names three Vulkan entry points and two Direct3D 12 ones, which
        // made Crysis Remastered - a Direct3D title - read as Vulkan.
        let addBest (target: Dictionary<string, float>) (weights: (string * float) list) =
            weights
            |> List.filter (fun (api, _) -> api <> "")
            |> List.groupBy fst
            |> List.iter (fun (api, group) -> addEvidence target api (group |> List.map snd |> List.max))

        let importWeights (path: string) (boundWeight: float) (delayWeight: float) =
            let (bound, delayed) = Pe.importedModules path

            [ for (needle, api) in apiNeedles do
                if needle.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) then
                    if bound.Contains(needle) then yield (api, boundWeight)
                    elif delayed.Contains(needle) then yield (api, delayWeight) ]

        let stringWeights (path: string) (weight: float) =
            [ for name in scanBinaryFor path apiNeedleNames -> (apiOfNeedle name, weight) ]

        // 1. What the executable binds. Static and delay-load entries carry
        //    the same weight on purpose. It is tempting to rank a static
        //    import higher - the process cannot start without it - but that
        //    is backwards for this question. An engine statically links the
        //    legacy and utility libraries it was built against and reaches
        //    for the renderer it actually uses through the delay table or
        //    LoadLibrary, because that is the one it has to be able to fall
        //    back from. Batman: Arkham Knight binds d3d9.dll and delay-loads
        //    d3d11.dll; it is a Direct3D 11 game. Just Cause 2 binds
        //    d3d9.dll and delay-loads d3d10.dll; it is a Direct3D 10 game.
        addBest binary (importWeights exePath 100.0 100.0)

        // 2. What the executable mentions. Covers everything loaded by name at
        //    runtime, which is how a switchable renderer always works.
        addBest binary (stringWeights exePath 35.0)

        // 3. Unity, whose executable is a stub that says nothing. The engine
        //    DLL is read in its place, and counts as the game's own code.
        let unity = unityEngineMarker exePath exeDir

        if unity <> "" && unity.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) then
            addBest binary (importWeights unity 80.0 80.0)
            addBest binary (stringWeights unity 20.0)

        // 3b. When the executable was a loader and said nothing, the game's
        //     code is in the libraries beside it. Only consulted on silence,
        //     so a game whose own executable answered is never second-guessed
        //     by a middleware DLL that happens to mention another API.
        if binary.Count = 0 && unity = "" then
            for library in engineLibraries exeDir do
                addBest binary (importWeights library 100.0 100.0)
                addBest binary (stringWeights library 35.0)

        // 4. Files the game ships. One weight for the whole folder: a DX12
        //    title carrying the Agility SDK, the debug layer, dxcompiler and
        //    dxil is one fact, not four, and a D3DX redistributable set is
        //    twenty copies of one fact.
        let nearby = nearbyDllNames exeDir

        addBest
            folder
            [ for (fileName, (api, weight)) in shippedFileEvidence do
                if nearby.Contains(fileName) then
                    let beside = Path.Combine(exeDir, fileName)
                    if not (File.Exists(beside)) || not (isModWrapper beside) then yield (api, weight)

              // The D3DX redistributables are era-specific and nothing else
              // ships them, which makes them a dependable marker of an older
              // title - as long as they came from the game rather than from
              // the copy of Microsoft's installer beside it, which is what
              // nearbyDllNames filters out.
              for name in nearby do
                  if Regex.IsMatch(name, @"^d3dx9_\d+\.dll$", RegexOptions.IgnoreCase) then yield ("dx9", 55.0)
                  elif Regex.IsMatch(name, @"^d3dx10(_\d+)?\.dll$", RegexOptions.IgnoreCase) then yield ("dx10", 45.0)
                  elif Regex.IsMatch(name, @"^d3dx11_\d+\.dll$", RegexOptions.IgnoreCase) then yield ("dx11", 45.0) ]

        // 5. A declared default: the engine stating what it boots with rather
        //    than us inferring it from what the binary can reach for.
        //
        //    Most specific first. A renderer named in the path is a build the
        //    publisher shipped separately - RED Engine puts its Direct3D 11
        //    and Direct3D 12 builds in bin\x64 and bin\x64_dx12 - so it beats
        //    the project-wide setting. Unity comes last, and only as a
        //    fallback: UnityPlayer.dll carries the names of every backend it
        //    supports, so its own strings cannot separate them, and a Unity
        //    player boots on Direct3D 11 unless the project asked otherwise.
        let pathLower = "/" + exeDir.Replace('\\', '/').ToLowerInvariant() + "/"

        let declared =
            if pathLower.Contains("_dx12") || pathLower.Contains("/dx12/") then "dx12"
            elif pathLower.Contains("_dx11") || pathLower.Contains("/dx11/") then "dx11"
            elif pathLower.Contains("_vulkan") || pathLower.Contains("/vulkan/") then "vulkan"
            else
                match unrealDeclaredRhi exePath exeDir nearby with
                | "" -> if unity <> "" then "dx11" else ""
                | rhi -> rhi

        { Declared = declared
          Binary = binary
          Folder = folder }

    /// "dx12" / "vulkan" / "dx11" / "dx10" / "dx9" / "opengl", or "" when the
    /// executable gave nothing away.
    let detectGraphicsApi (exePath: string) : string =
        try
            if String.IsNullOrWhiteSpace(exePath) || not (File.Exists(exePath)) then
                ""
            else
                let exeDir =
                    try Path.GetDirectoryName(exePath)
                    with _ -> ""

                if String.IsNullOrWhiteSpace(exeDir) || not (Directory.Exists(exeDir)) then
                    ""
                else
                    let evidence = collectApiEvidence exePath exeDir

                    // One passing mention and nothing else is noise, not an
                    // answer, so a candidate clears a full signal first. The
                    // game's own code is asked before the folder around it,
                    // and the folder only speaks when the executable was
                    // silent - a packed binary gives up nothing.
                    let clearing (tier: Dictionary<string, float>) =
                        apiPriority
                        |> Array.filter (fun api ->
                            match tier.TryGetValue(api) with
                            | true, score -> score >= noiseFloor
                            | _ -> false)

                    let fromBinary = clearing evidence.Binary
                    let candidates = if fromBinary.Length > 0 then fromBinary else clearing evidence.Folder

                    // An engine that stated its own default has settled it.
                    if evidence.Declared <> "" then
                        evidence.Declared
                    else
                        let has api = candidates |> Array.contains api
                        let hasDirect3D11Plus = has "dx12" || has "dx11"

                        let survivors =
                            candidates
                            |> Array.filter (fun api ->
                                match api with
                                // Vulkan and OpenGL are not Direct3D
                                // generations, so they cannot be ranked
                                // against one. An engine that can reach
                                // Direct3D 11 or 12 at all takes that path on
                                // Windows and offers the others as an option,
                                // so they only win when no modern Direct3D is
                                // referenced at all. Crysis Remastered binds
                                // vulkan-1.dll outright and still renders
                                // Direct3D 11; a Vulkan-only engine names no
                                // Direct3D device at all.
                                | "vulkan"
                                | "opengl" -> not hasDirect3D11Plus
                                // Direct3D 9 and 10 belong to the 32-bit era.
                                // A 64-bit binary referencing one is carrying
                                // a legacy code path, not rendering with it -
                                // which is exactly what Batman: Arkham Knight
                                // and Crysis Remastered do.
                                | "dx9"
                                | "dx10" ->
                                    Pe.architecture exePath <> "64"
                                    || not (hasDirect3D11Plus || has "vulkan")
                                | _ -> true)

                        // Newest wins, rather than highest scoring. A
                        // reference to an older generation is nearly always
                        // vestigial and a reference to a newer one nearly
                        // always live, so the ranking is the answer and the
                        // scores only decide what got this far.
                        if survivors.Length > 0 then survivors.[0]
                        elif candidates.Length > 0 then candidates.[0]
                        else ""
        with _ ->
            ""


    /// "32" / "64" straight from the COFF header's machine type - no loading
    /// and no guessing. Shares the header reader the API detection uses.
    let detectArchitecture (exePath: string) : string = Pe.architecture exePath

    /// Which proxy library ReShade takes over. The slot has to be one the game
    /// itself loads, so it follows the detected API rather than whatever is
    /// lying in the folder: the old check looked for vulkan-1.dll and
    /// opengl32.dll next to the executable, which are system libraries a game
    /// never ships, and answered with "vulkan" / "opengl" - names ReShade has
    /// no proxy for, so the install went to a DLL nothing would ever load.
    /// dxgi covers Direct3D 10, 11 and 12, which is every DLSS title.
    let detectReShadeApi (exePath: string) : string =
        try
            match detectGraphicsApi exePath with
            | "dx9" -> "d3d9"
            | "opengl" -> "opengl32"
            | _ -> "dxgi"
        with _ ->
            "dxgi"

    let isReShadeInstalled (exePath: string) : bool =
        try
            let dir = Path.GetDirectoryName(exePath)

            [ "dxgi.dll"; "d3d11.dll"; "d3d12.dll"; "opengl32.dll" ]
            |> List.exists (fun n ->
                let p = Path.Combine(dir, n)

                File.Exists(p)
                && (let fvi = FileVersionInfo.GetVersionInfo(p)
                    not (isNull fvi.ProductName) && fvi.ProductName.Contains("ReShade")))
        with _ ->
            false
