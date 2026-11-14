namespace DLSS_5_MANAGER.Services

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open SharpCompress.Archives
open DLSS_5_MANAGER.Models

/// Enabler Dock: DLSS Enabler in a game, the way NODIX TECH's DLSS Enabler
/// Manager 1.5.1 does it, rebuilt on SUITE's own parts.
///
/// DLSS Enabler is one native DLL that the game loads under a borrowed name
/// (version.dll, winmm.dll, dxgi.dll or dbghelp.dll), plus its settings file,
/// plus - usually - a newer NVIDIA Streamline next to the game's own.
///
/// What is different from the original, on purpose:
///   * Every file written is recorded and every file replaced is backed up
///     under SUITE's data folder, like ModInstaller does, so a game comes back
///     exactly as it was. The original's in-folder `.nodix_bak` / `.nodix_added`
///     markers are still read, so a game the old tool touched restores too.
///   * A proxy name the game already uses for its own DLL is never taken. The
///     original pushed that DLL aside as `<proxy>.old` and never put it back.
///   * Uninstall never deletes OptiScaler's files, and keeping settings keeps
///     the settings file.
///   * Downloads only come from the original's two archive hosts, pinned by
///     SHA-256 where SUITE knows the file; nothing downloaded is ever run.
///   * No registry is written: the NVIDIA signature override, the Discord log
///     upload and the `*.trash.*` sweep are not here at all.
module DlssEnabler =

    // =====================================================================
    // NAMES
    // =====================================================================
    /// The names DLSS Enabler can load under, in the order the original tries.
    let proxyNames = [ "version.dll"; "winmm.dll"; "dxgi.dll"; "dbghelp.dll" ]

    let iniName = "dlss-enabler.ini"
    let logName = "dlss-enabler.log"

    /// The payload's own file name inside every DLSS Enabler archive.
    let payloadName = "version.dll"

    // The original tool's in-folder markers - read, never written.
    let legacyBackupSuffix = ".nodix_bak"
    let legacyAddedSuffix = ".nodix_added"
    let legacyOldSuffix = ".old"
    let legacyVersionMarker = ".nodix_dlssenabler_version"
    let legacyBetaMarker = ".nodix_beta"
    let legacyFsr4Marker = ".nodix_fsr4_version"
    let legacyFsr4Dll = "amd_fidelityfx_upscaler_dx12.dll"

    let private eq (a: string) (b: string) = String.Equals(a, b, StringComparison.OrdinalIgnoreCase)

    // =====================================================================
    // PATHS (all under SUITE's own data folder)
    // =====================================================================
    let root () =
        let p = Path.Combine(AppLog.dataFolder (), "EnablerDock")
        Directory.CreateDirectory(p) |> ignore
        p

    let private sub (name: string) =
        let p = Path.Combine(root (), name)
        Directory.CreateDirectory(p) |> ignore
        p

    let downloadsFolder () = sub "Downloads"
    let extractedFolder () = sub "Extracted"

    // =====================================================================
    // FILE IDENTITY
    // =====================================================================
    /// The original's test, kept: DLSS Enabler stamps "DLSS Enabler" as its
    /// product name, so a DLL in a proxy slot is ours exactly when it says so.
    let isEnablerFile (path: string) =
        try
            File.Exists(path)
            && (let name = FileVersionInfo.GetVersionInfo(path).ProductName
                not (isNull name) && eq (name.Trim()) "DLSS Enabler")
        with _ -> false

    let isStreamlineFile (path: string) =
        try
            let name = FileVersionInfo.GetVersionInfo(path).ProductName
            not (isNull name) && name.IndexOf("STREAMLINE", StringComparison.OrdinalIgnoreCase) >= 0
        with _ -> false

    /// "4.8.11.0" from a DLL's version resource, or "".
    let productVersion (path: string) =
        try
            let info = FileVersionInfo.GetVersionInfo(path)
            let text = if String.IsNullOrWhiteSpace(info.ProductVersion) then info.FileVersion else info.ProductVersion
            if isNull text then "" else (text.Split(' ').[0]).Replace(',', '.').Trim()
        with _ -> ""

    // =====================================================================
    // VERSION CATALOG
    // =====================================================================
    /// The original's catalog: a text file its Cloudflare Worker serves, in
    /// its own line format (not JSON, whatever the file was called):
    ///
    ///     _dlss-enabler-versions:
    ///     -Dlss Enabler 4.8.11.0 : "https://.../Dlss%20Enabler%204.8.11.0.rar"
    ///     _streamline-versions:
    ///     -streamline2.11.1 : "https://.../streamline2.11.1.rar"
    ///     _custom games config :
    ///     -game : Pragmata dll: dbghelp.dll note: You must download Reframework...
    module Catalog =

        type Entry =
            { Name: string
              Url: string
              /// The archive's name on disk: the URL's last segment, unescaped.
              FileName: string }

        /// A game that needs a particular proxy name, and what to tell the user.
        type GameFix =
            { Game: string
              Dll: string
              Note: string }

        type Catalog =
            { Enablers: Entry list
              Streamlines: Entry list
              Fixes: GameFix list }

        let workerUrl = "https://dlssenablermanager.gtagatgta9.workers.dev/"

        /// The only places an archive may come from: the original's two R2
        /// buckets. A catalog line naming any other host is dropped.
        let trustedHosts =
            [ "pub-057af5d8cd1e4e03a7a66d12aa8bec9c.r2.dev"
              "pub-96c761d8f940430f9936a9cc0df5d829.r2.dev" ]

        /// SHA-256 and size of the archives SUITE has checked, by file name.
        /// These match the copies shipped with DLSS Enabler Manager 1.5.1.
        let pins =
            dict
                [ "Dlss Enabler 4.8.11.0.rar", ("20003C87053AEDCF7AE493440108EBC2CBDB9FA359236DB8D5F7CBC5CA57C291", 4854774L)
                  "streamline2.11.1.rar", ("30F82F622EAB51A1CE9D3C9FB024727BA41C23EB9B10E1EA0B0E360FA520DB8B", 2111744L) ]

        let isPinned (entry: Entry) = pins.ContainsKey(entry.FileName)

        /// The first dotted number in a name - "4.8.11.0" in
        /// "Dlss Enabler 4.8.11.0", "2.11.1" in "streamline2.11.1".
        let versionOf (name: string) : Version =
            let input = if isNull name then "" else name
            let m = Regex.Match(input, @"\d+(\.\d+)+")
            let mutable v: Version = null
            if m.Success && Version.TryParse(m.Value, &v) then v else Version(0, 0)

        let isBeta (name: string) = not (isNull name) && name.IndexOf("BETA", StringComparison.OrdinalIgnoreCase) >= 0

        /// Newest first.
        let sorted (entries: Entry list) = entries |> List.sortByDescending (fun e -> versionOf e.Name)

        /// The newest version that is not a beta - what "Latest" means.
        let latestStable (entries: Entry list) =
            entries |> List.filter (fun e -> not (isBeta e.Name)) |> sorted |> List.tryHead

        let private quoted = Regex("\"([^\"]+)\"")

        /// An entry the catalog may name: HTTPS, a trusted host, and a plain
        /// .rar or .zip file name - a name with a path in it never reaches disk.
        let private entryOf (name: string) (url: string) : Entry option =
            match Uri.TryCreate(url, UriKind.Absolute) with
            | true, uri when uri.Scheme = Uri.UriSchemeHttps && trustedHosts |> List.exists (eq uri.Host) ->
                let file = Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath))
                let ext = Path.GetExtension(file).ToLowerInvariant()
                let clean =
                    file <> "" && (ext = ".rar" || ext = ".zip")
                    && file.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && not (file.Contains(".."))
                if clean && name <> "" then Some { Name = name; Url = uri.AbsoluteUri; FileName = file } else None
            | _ -> None

        /// Reads the line format. Anything malformed is skipped, never guessed.
        let parse (text: string) : Catalog =
            let enablers = List<Entry>()
            let streamlines = List<Entry>()
            let fixes = List<GameFix>()
            let mutable section = ""
            let lines = if isNull text then [||] else text.Split([| '\n' |])
            for raw in lines do
                let line = raw.Trim()
                if line.StartsWith("_") then
                    section <-
                        if eq line "_dlss-enabler-versions:" then "enabler"
                        elif eq line "_streamline-versions:" then "streamline"
                        elif line.StartsWith("_custom games config", StringComparison.OrdinalIgnoreCase)
                             || line.StartsWith("_custom games list", StringComparison.OrdinalIgnoreCase) then "games"
                        else ""
                elif line.StartsWith("-") then
                    match section with
                    | "enabler" | "streamline" ->
                        let colon = line.IndexOf(':')
                        if colon > 1 then
                            let name = line.Substring(1, colon - 1).Trim()
                            let m = quoted.Match(line.Substring(colon + 1))
                            if m.Success then
                                match entryOf name m.Groups.[1].Value with
                                | Some e -> (if section = "enabler" then enablers else streamlines).Add(e)
                                | None -> ()
                    | "games" when line.StartsWith("-game", StringComparison.OrdinalIgnoreCase) ->
                        let dllAt = line.IndexOf("dll:", StringComparison.OrdinalIgnoreCase)
                        let noteAt = line.IndexOf("note:", StringComparison.OrdinalIgnoreCase)
                        let nameAt = line.IndexOf(':') + 1
                        if nameAt > 0 && dllAt > nameAt && noteAt > dllAt then
                            let game = line.Substring(nameAt, dllAt - nameAt).Trim()
                            let dll = line.Substring(dllAt + 4, noteAt - dllAt - 4).Trim().ToLowerInvariant()
                            let note = line.Substring(noteAt + 5).Trim()
                            let note = if eq note "no note" then "" else note
                            // Only the four names DLSS Enabler loads under.
                            if game <> "" && List.contains dll proxyNames then
                                fixes.Add({ Game = game; Dll = dll; Note = note })
                    | _ -> ()
            { Enablers = List.ofSeq enablers |> List.distinctBy (fun e -> e.FileName.ToLowerInvariant())
              Streamlines = List.ofSeq streamlines |> List.distinctBy (fun e -> e.FileName.ToLowerInvariant())
              Fixes = List.ofSeq fixes }

        /// What the catalog said when 1.5.1 shipped, for when the Worker cannot
        /// be reached and nothing is cached. The game list is the original's own
        /// fallback.
        let builtInText =
            String.Join(
                "\n",
                [ "_dlss-enabler-versions:"
                  "-Dlss Enabler 4.5.3.0 : \"https://pub-057af5d8cd1e4e03a7a66d12aa8bec9c.r2.dev/Dlss%20Enabler%204.5.3.0.rar\""
                  "-Dlss Enabler 4.7.6.10 : \"https://pub-057af5d8cd1e4e03a7a66d12aa8bec9c.r2.dev/Dlss%20Enabler%204.7.6.10.rar\""
                  "-Dlss Enabler 4.7.12.2 : \"https://pub-057af5d8cd1e4e03a7a66d12aa8bec9c.r2.dev/Dlss%20Enabler%204.7.12.2.rar\""
                  "-Dlss Enabler 4.7.13.0 : \"https://pub-057af5d8cd1e4e03a7a66d12aa8bec9c.r2.dev/Dlss%20Enabler%204.7.13.0.rar\""
                  "-Dlss Enabler 4.8.11.0 : \"https://pub-057af5d8cd1e4e03a7a66d12aa8bec9c.r2.dev/Dlss%20Enabler%204.8.11.0.rar\""
                  "_streamline-versions:"
                  "-streamline2.11.1 : \"https://pub-96c761d8f940430f9936a9cc0df5d829.r2.dev/streamline2.11.1.rar\""
                  "_custom games config :"
                  "-game : Grand Theft Auto V Enhanced dll: winmm.dll note: You must disable BattlEye anti-cheat for the paid version of the game."
                  "-game : Pragmata dll: dbghelp.dll note: You must download Reframework for this game for the mod to work."
                  "-game : Resident Evil 9 dll: dxgi.dll note: You must download Reframework for this game for the mod to work."
                  "-game : Fort Solis dll: winmm.dll note: no note"
                  "-game : Mafia The Old Country dll: version.dll note: no note" ]
            )

        let builtIn = parse builtInText

        let private cachePath () = Path.Combine(root (), "catalog.txt")

        /// The last catalog fetched, or the built-in one. Never touches the
        /// network, so it is safe for the page to call when it opens.
        let cached () : Catalog * DateTime option =
            try
                let path = cachePath ()
                if File.Exists(path) then
                    let c = parse (File.ReadAllText(path))
                    if c.Enablers.IsEmpty then builtIn, None else c, Some(File.GetLastWriteTimeUtc(path))
                else builtIn, None
            with _ -> builtIn, None

        /// The catalog from the Worker, at most every 30 minutes like the
        /// original, unless forced. A failed fetch falls back to what is cached.
        let refresh (client: HttpClient) (force: bool) : Task<Catalog * DateTime option * string> = task {
            let current, at = cached ()
            let fresh = match at with Some t -> DateTime.UtcNow - t < TimeSpan.FromMinutes(30.0) | None -> false
            if fresh && not force then
                return current, at, ""
            else
                try
                    use cts = new CancellationTokenSource(TimeSpan.FromSeconds(15.0))
                    let! text = client.GetStringAsync(workerUrl, cts.Token)
                    let parsed = parse text
                    if parsed.Enablers.IsEmpty then
                        return current, at, "The version list came back empty - kept the one SUITE had."
                    else
                        File.WriteAllText(cachePath (), text)
                        return parsed, Some DateTime.UtcNow, ""
                with ex ->
                    AppLog.warn ("Enabler Dock: catalog fetch failed: " + ex.Message)
                    return current, at, "Could not reach the version list - using the one SUITE had."
        }

    // =====================================================================
    // GAME FIXES (per-game proxy overrides)
    // =====================================================================
    module Fixes =
        let private squash (text: string) =
            if isNull text then "" else String(text.ToLowerInvariant() |> Seq.filter Char.IsLetterOrDigit |> Array.ofSeq)

        /// The original's matching, kept so a catalog written for it reads the
        /// same here: exact title, the folder path naming the game, or one
        /// squashed name inside the other. Names shorter than three letters
        /// never match by containment - an empty title contains nothing.
        let matches (fixName: string) (title: string) (folder: string) =
            let fixKey = squash fixName
            let titleKey = squash title
            let folderKey =
                squash (if String.IsNullOrWhiteSpace(folder) then "" else Path.GetFileName(folder.TrimEnd('\\', '/')))
            let within (a: string) (b: string) = a.Length >= 3 && b.Length >= 3 && (a.Contains(b) || b.Contains(a))
            fixKey.Length >= 3
            && (eq fixName title
                || (not (String.IsNullOrWhiteSpace(folder)) && folder.IndexOf(fixName, StringComparison.OrdinalIgnoreCase) >= 0)
                || within titleKey fixKey
                || within folderKey fixKey
                || ((fixKey.Contains("gta5") || fixKey.Contains("grandtheftautov"))
                    && [ titleKey; folderKey ] |> List.exists (fun k -> k.Contains("gta5") || k.Contains("grandtheftautov"))))

        /// The user's own fixes first, then the catalog's.
        let find (userFixes: Catalog.GameFix list) (catalogFixes: Catalog.GameFix list) (title: string) (folder: string) =
            userFixes @ catalogFixes |> List.tryFind (fun f -> matches f.Game title folder)

    // =====================================================================
    // PROXY CHOICE
    // =====================================================================
    /// What sits in a proxy slot beside the game's executable.
    type Slot =
        | Free
        /// DLSS Enabler (from us or the old tool).
        | Ours
        /// The game's own DLL, or another mod's. Never replaced.
        | Foreign

    /// Picks the name DLSS Enabler loads under, first rule that answers:
    /// the user's choice, the name it is already installed as, a game fix,
    /// a name the game's executable imports, then the first name not taken.
    /// Returns the name and why, or why none can be used.
    let chooseProxy
        (forced: string option)
        (fix: string option)
        (imports: Set<string>)
        (slot: string -> Slot) : Result<string * string, string> =
        let usable name = slot name <> Foreign
        let taken name =
            Error(sprintf "The game already has its own %s, so DLSS Enabler cannot use that name. Pick another injection." name)
        match forced with
        | Some name when List.contains name proxyNames ->
            if usable name then Ok(name, "your choice") else taken name
        | _ ->
            match proxyNames |> List.tryFind (fun n -> slot n = Ours) with
            | Some name -> Ok(name, "already installed")
            | None ->
                match fix with
                | Some name when List.contains name proxyNames ->
                    if usable name then Ok(name, "game fix") else taken name
                | _ ->
                    match proxyNames |> List.tryFind (fun n -> imports.Contains(n) && usable n) with
                    | Some name -> Ok(name, "the game loads it")
                    | None ->
                        match proxyNames |> List.tryFind usable with
                        | Some name -> Ok(name, "first free name")
                        | None -> Error "The game already uses all four names DLSS Enabler can load under."

    /// The imports to judge by: the executable's, and when it names none of
    /// the four, its neighbours' too - often the engine DLL beside it is what
    /// loads them. Capped, so a folder of a thousand plugins stays quick.
    let gatherImports (exePath: string) : Set<string> =
        let own = GameAnalyzer.importedLibraries exePath
        if proxyNames |> List.exists own.Contains then own
        else
            let dir = Path.GetDirectoryName(exePath)
            let neighbours =
                try
                    Seq.append (Directory.EnumerateFiles(dir, "*.dll")) (Directory.EnumerateFiles(dir, "*.exe"))
                    |> Seq.filter (fun p -> not (eq p exePath))
                    |> Seq.truncate 200
                    |> List.ofSeq
                with _ -> []
            neighbours |> List.fold (fun acc p -> Set.union acc (GameAnalyzer.importedLibraries p)) own

    // =====================================================================
    // SETTINGS FILE (dlss-enabler.ini)
    // =====================================================================
    /// A line-preserving editor, as the original's SimpleIni: comments, blank
    /// lines and order survive, keys match without case, and a missing key is
    /// added at the end of its section.
    module Ini =

        let private header (line: string) =
            let t = line.Trim()
            if t.StartsWith("[") && t.EndsWith("]") && t.Length >= 2 then Some(t.Substring(1, t.Length - 2).Trim()) else None

        let private isComment (t: string) = t = "" || t.StartsWith(";") || t.StartsWith("#")

        let private keyValue (line: string) =
            let t = line.Trim()
            if isComment t then None
            else
                let i = t.IndexOf('=')
                if i > 0 then Some(t.Substring(0, i).Trim(), t.Substring(i + 1).Trim()) else None

        /// Every (section, key, value) in file order.
        let all (lines: string[]) : (string * string * string) list =
            let mutable section = ""
            [ for line in lines do
                match header line with
                | Some s -> section <- s
                | None ->
                    if section <> "" then
                        match keyValue line with
                        | Some(k, v) -> yield (section, k, v)
                        | None -> () ]

        let tryGet (lines: string[]) (section: string) (key: string) : string option =
            all lines |> List.tryFind (fun (s, k, _) -> eq s section && eq k key) |> Option.map (fun (_, _, v) -> v)

        /// Sets one value. A trailing `; comment` on the line is kept.
        let set (lines: string[]) (section: string) (key: string) (value: string) : string[] =
            let result = List<string>(lines)
            let mutable current = ""
            let mutable sectionFound = false
            let mutable lastInSection = -1
            let mutable replaced = false
            let mutable i = 0
            while not replaced && i < result.Count do
                let line = result.[i]
                match header line with
                | Some s ->
                    current <- s
                    if eq s section then
                        sectionFound <- true
                        lastInSection <- i
                | None ->
                    if eq current section then
                        let t = line.Trim()
                        if not (isComment t) then lastInSection <- i
                        match keyValue line with
                        | Some(k, _) when eq k key ->
                            let afterEq = line.Substring(line.IndexOf('=') + 1)
                            let comment =
                                let c = afterEq.IndexOf(';')
                                if c >= 0 then " " + afterEq.Substring(c).Trim() else ""
                            result.[i] <- key + "=" + value + comment
                            replaced <- true
                        | _ -> ()
                i <- i + 1
            if not replaced then
                if sectionFound then
                    result.Insert(lastInSection + 1, key + "=" + value)
                else
                    if result.Count > 0 && result.[result.Count - 1].Trim() <> "" then result.Add("")
                    result.Add("[" + section + "]")
                    result.Add(key + "=" + value)
            result.ToArray()

        /// The new version's template with the user's values put back, so an
        /// upgrade keeps every setting and a key new in the template keeps its
        /// default. `[Meta]` is the template's own: putting the old
        /// configVersion back would tell the new DLSS Enabler its settings
        /// are from the old one.
        let merge (previous: string[]) (template: string[]) : string[] =
            all previous
            |> List.filter (fun (s, _, _) -> not (eq s "Meta"))
            |> List.fold (fun acc (s, k, v) -> set acc s k v) template

        /// Writes through a temporary file, so a crash mid-write never leaves
        /// the game with half a settings file.
        let save (path: string) (lines: string[]) =
            let temp = path + ".suite-tmp"
            File.WriteAllLines(temp, lines)
            if File.Exists(path) then File.SetAttributes(path, File.GetAttributes(path) &&& ~~~FileAttributes.ReadOnly)
            File.Move(temp, path, true)

    /// The main options the page edits, with the values DLSS Enabler reads.
    module Options =
        type Choice = { Label: string; Value: string }

        let toggleKeys =
            [ { Label = "~ (Tilde)"; Value = "Tilde" }
              { Label = "Insert"; Value = "VK_INSERT" }
              { Label = "Home"; Value = "VK_HOME" }
              { Label = "End"; Value = "VK_END" }
              { Label = "Page Up"; Value = "VK_PRIOR" }
              { Label = "Page Down"; Value = "VK_NEXT" }
              { Label = "F12"; Value = "VK_F12" } ]

        let hudModes =
            [ { Label = "Game default"; Value = "0" }
              { Label = "Off"; Value = "1" }
              { Label = "On"; Value = "2" } ]

        /// All four modes. The original's reader stopped at 2, so a saved
        /// Latency showed as Off.
        let overdriveModes =
            [ { Label = "Off"; Value = "0" }
              { Label = "Potato mode"; Value = "1" }
              { Label = "Quality"; Value = "2" }
              { Label = "Latency"; Value = "3" } ]

        /// Index of a stored value in a list, or the default's.
        let indexOf (choices: Choice list) (value: string option) (fallback: string) =
            let wanted = defaultArg value fallback
            match choices |> List.tryFindIndex (fun c -> eq c.Value wanted) with
            | Some i -> i
            | None -> choices |> List.tryFindIndex (fun c -> eq c.Value fallback) |> Option.defaultValue 0

        let isTrue (value: string option) = match value with Some v -> eq v "true" || v = "1" | None -> false

    // =====================================================================
    // DOWNLOADS AND ARCHIVES
    // =====================================================================
    let private client =
        lazy
            (let c = new HttpClient(Timeout = TimeSpan.FromMinutes(10.0))
             c.DefaultRequestHeaders.UserAgent.ParseAdd("DLSS5-SUITE-EnablerDock")
             c)

    let httpClient () = client.Value

    let archivePath (entry: Catalog.Entry) = Path.Combine(downloadsFolder (), entry.FileName)
    let isDownloaded (entry: Catalog.Entry) = File.Exists(archivePath entry)

    /// SHA-256s of archives SUITE has no pin for, recorded the first time
    /// each was downloaded and checked; a later copy must match it.
    let private trustPath () = Path.Combine(root (), "trusted.json")

    let private loadTrust () =
        try
            if File.Exists(trustPath ()) then
                JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(trustPath ()))
            else Dictionary<string, string>()
        with _ -> Dictionary<string, string>()

    let private sha256 (path: string) =
        use input = File.OpenRead(path)
        SHA256.HashData(input) |> Convert.ToHexString

    /// Unpacks an archive with no entry allowed outside the target - the
    /// original trusted every path inside a RAR.
    let extract (archive: string) (target: string) =
        if Directory.Exists(target) then Directory.Delete(target, true)
        let staging = target + ".partial"
        if Directory.Exists(staging) then Directory.Delete(staging, true)
        Directory.CreateDirectory(staging) |> ignore
        let full = Path.GetFullPath(staging).TrimEnd('\\') + "\\"
        use opened = ArchiveFactory.OpenArchive(archive)
        for entry in opened.Entries do
            if not entry.IsDirectory && not (String.IsNullOrWhiteSpace(entry.Key)) then
                let outPath = Path.GetFullPath(Path.Combine(staging, entry.Key.Replace('/', '\\')))
                if not (outPath.StartsWith(full, StringComparison.OrdinalIgnoreCase)) then
                    failwithf "The archive has a file pointing outside its folder (%s) - not used." entry.Key
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)) |> ignore
                use input = entry.OpenEntryStream()
                use output = File.Create(outPath)
                input.CopyTo(output)
        Directory.Move(staging, target)

    let private findIn (dir: string) (name: string) =
        if Directory.Exists(dir) then
            Directory.GetFiles(dir, name, SearchOption.AllDirectories) |> Array.sort |> Array.tryHead
        else None

    /// An unpacked archive, re-unpacked when the folder is empty or missing.
    let extractedFor (entry: Catalog.Entry) =
        let target = Path.Combine(extractedFolder (), Path.GetFileNameWithoutExtension(entry.FileName))
        let ok = Directory.Exists(target) && Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories) |> Seq.isEmpty |> not
        if not ok then extract (archivePath entry) target
        target

    /// Checks an archive is what its catalog line says before anything in it
    /// goes near a game: DLSS Enabler's DLL of that version, or Streamline.
    let private checkContents (entry: Catalog.Entry) (isEnabler: bool) =
        let dir = extractedFor entry
        if isEnabler then
            match findIn dir payloadName with
            | Some dll when isEnablerFile dll ->
                let want = Catalog.versionOf entry.Name
                let have = Catalog.versionOf (productVersion dll)
                if want > Version(0, 0) && have > Version(0, 0) && want <> have then
                    failwithf "%s holds DLSS Enabler %O, not %O - not used." entry.FileName have want
            | _ -> failwithf "%s does not hold DLSS Enabler - not used." entry.FileName
        else
            let slFiles = Directory.GetFiles(dir, "sl.*.dll", SearchOption.AllDirectories)
            if slFiles.Length = 0 || not (slFiles |> Array.forall isStreamlineFile) then
                failwithf "%s does not hold NVIDIA Streamline - not used." entry.FileName

    let private checkOrDiscard (entry: Catalog.Entry) (isEnabler: bool) (archive: string) =
        try checkContents entry isEnabler
        with ex ->
            (try File.Delete(archive) with _ -> ())
            raise ex

    /// Downloads an archive into SUITE's data folder if it is not there yet.
    /// A pinned file goes through VerifiedDownload. One SUITE has no pin for
    /// is only fetched when `allowUnpinned`; its contents are checked and its
    /// SHA-256 remembered, so it cannot quietly change afterwards.
    let ensureArchive (entry: Catalog.Entry) (isEnabler: bool) (allowUnpinned: bool) (progress: string -> float -> unit) = task {
        let destination = archivePath entry
        match Catalog.pins.TryGetValue(entry.FileName) with
        | true, (digest, size) ->
            do! VerifiedDownload.downloadFrom Catalog.trustedHosts (httpClient ()) entry.Name entry.Url digest size destination progress 0.0 0.6
        | _ ->
            let trust = loadTrust ()
            let known = match trust.TryGetValue(entry.FileName) with | true, d -> Some d | _ -> None
            let present = File.Exists(destination)
            if present && known.IsSome && not (eq (sha256 destination) known.Value) then
                File.Delete(destination)
            if not (File.Exists(destination)) then
                if not allowUnpinned then
                    failwithf "SUITE has no checksum for %s. Turn on \"Allow unchecked versions\" to download it anyway." entry.Name
                match Uri.TryCreate(entry.Url, UriKind.Absolute) with
                | true, uri when uri.Scheme = Uri.UriSchemeHttps && Catalog.trustedHosts |> List.exists (eq uri.Host) -> ()
                | _ -> failwith "Untrusted download address"
                let partial = destination + ".part"
                use! response = (httpClient ()).GetAsync(entry.Url, HttpCompletionOption.ResponseHeadersRead)
                response.EnsureSuccessStatusCode() |> ignore
                let total = response.Content.Headers.ContentLength |> Option.ofNullable |> Option.defaultValue 0L
                if total > 512L * 1024L * 1024L then failwith "The archive is far larger than DLSS Enabler ever is - not downloaded."
                do!
                    task {
                        use! source = response.Content.ReadAsStreamAsync()
                        use target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None)
                        let buffer = Array.zeroCreate<byte> (256 * 1024)
                        let mutable received = 0L
                        let mutable reading = true
                        while reading do
                            use stall = new CancellationTokenSource(TimeSpan.FromSeconds(45.0))
                            let! count = source.ReadAsync(buffer.AsMemory(), stall.Token)
                            if count = 0 then reading <- false
                            else
                                do! target.WriteAsync(buffer.AsMemory(0, count))
                                received <- received + int64 count
                                if received > 512L * 1024L * 1024L then failwith "The download did not stop - abandoned."
                                let fraction = if total > 0L then float received / float total else 0.0
                                progress (sprintf "Downloading %s - %.1f MB" entry.Name (float received / 1048576.0)) (fraction * 0.6)
                    }
                File.Move(partial, destination, true)
                let digest = sha256 destination
                match known with
                | Some d when not (eq d digest) ->
                    File.Delete(destination)
                    failwithf "%s changed since SUITE first downloaded it - not used." entry.Name
                | _ -> ()
                // Checked before it is trusted: a wrong file never gets a record.
                checkOrDiscard entry isEnabler destination
                trust.[entry.FileName] <- digest
                File.WriteAllText(trustPath (), JsonSerializer.Serialize(trust))
        progress (sprintf "Unpacking %s..." entry.Name) 0.65
        checkContents entry isEnabler
        return extractedFor entry
    }

    /// Takes in an archive the user already has (the original's "add custom
    /// mod files"). It must hold DLSS Enabler or Streamline - checked here -
    /// and becomes a catalog-like entry named after its file.
    let importArchive (source: string) : Catalog.Entry * bool =
        let file = Path.GetFileName(source)
        let ext = Path.GetExtension(file).ToLowerInvariant()
        if ext <> ".rar" && ext <> ".zip" then failwith "Pick a .rar or .zip archive."
        let entry = { Catalog.Name = Path.GetFileNameWithoutExtension(file); Catalog.Url = ""; Catalog.FileName = file }
        let destination = archivePath entry
        File.Copy(source, destination, true)
        let staging = Path.Combine(extractedFolder (), Path.GetFileNameWithoutExtension(file))
        try
            extract destination staging
            let isEnabler = (findIn staging payloadName |> Option.map isEnablerFile) = Some true
            let isStreamline =
                let sl = Directory.GetFiles(staging, "sl.*.dll", SearchOption.AllDirectories)
                sl.Length > 0 && sl |> Array.forall isStreamlineFile
            if not isEnabler && not isStreamline then failwith "That archive holds neither DLSS Enabler nor NVIDIA Streamline."
            entry, isEnabler
        with ex ->
            (try File.Delete(destination) with _ -> ())
            (try if Directory.Exists(staging) then Directory.Delete(staging, true) with _ -> ())
            raise ex

    /// Archives on disk that the catalog does not list: imported ones, or
    /// versions the Worker has dropped since they were downloaded.
    let localEntries () : Catalog.Entry list * Catalog.Entry list =
        let files =
            try Directory.GetFiles(downloadsFolder ()) |> Array.filter (fun f -> f.EndsWith(".rar", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            with _ -> [||]
        let entries = files |> Array.map (fun f -> { Catalog.Name = Path.GetFileNameWithoutExtension(f); Catalog.Url = ""; Catalog.FileName = Path.GetFileName(f) }) |> List.ofArray
        let isStreamline (e: Catalog.Entry) = e.FileName.StartsWith("streamline", StringComparison.OrdinalIgnoreCase) || e.FileName.StartsWith("Custom-streamline", StringComparison.OrdinalIgnoreCase)
        entries |> List.filter (isStreamline >> not), entries |> List.filter isStreamline

    // =====================================================================
    // A GAME'S STATE
    // =====================================================================
    [<CLIMutable>]
    type Manifest =
        { GameId: string
          GameTitle: string
          ExecutablePath: string
          InstalledAtUtc: string
          /// The catalog name installed, e.g. "Dlss Enabler 4.8.11.0".
          Version: string
          Proxy: string
          /// "Auto", "Game original" or a Streamline catalog name.
          Streamline: string
          StreamlineDir: string
          /// DLSS Enabler itself and its settings file.
          Files: ModInstaller.InstalledFile[]
          /// The Streamline DLLs, kept apart so a Streamline change can be
          /// undone without touching DLSS Enabler.
          StreamlineFiles: ModInstaller.InstalledFile[] }

    let private safeId (game: GameItem) =
        let raw = if String.IsNullOrWhiteSpace(game.AppId) then game.Title else game.AppId
        let cleaned = Regex.Replace(raw, @"[^A-Za-z0-9_\-]", "_")
        if cleaned.Length > 80 then cleaned.Substring(0, 80) else cleaned

    let private manifestPath (game: GameItem) = Path.Combine(sub "Installs", safeId game + ".json")
    let private backupDir (game: GameItem) = Path.Combine(sub "Backups", safeId game)

    let readManifest (game: GameItem) : Manifest option =
        try
            let path = manifestPath game
            if File.Exists(path) then
                let options = JsonSerializerOptions(PropertyNameCaseInsensitive = true)
                let m = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), options)
                if isNull (box m) then None
                else
                    Some
                        { m with
                            Files = (if isNull m.Files then [||] else m.Files)
                            StreamlineFiles = (if isNull m.StreamlineFiles then [||] else m.StreamlineFiles) }
            else None
        with _ -> None

    let private writeManifest (game: GameItem) (m: Manifest) =
        File.WriteAllText(manifestPath game, JsonSerializer.Serialize(m, JsonSerializerOptions(WriteIndented = true)))

    let exeDirOf (game: GameItem) =
        try
            if String.IsNullOrWhiteSpace(game.TargetExecutablePath) then game.InstallDirectory
            else Path.GetDirectoryName(game.TargetExecutablePath)
        with _ -> game.InstallDirectory

    /// Where the game keeps Streamline, as the original finds it: the folder
    /// with sl.interposer.dll wins, then sl.common.dll, then frame generation's
    /// own DLLs. `known` is the library scan's list, which saves a walk.
    let streamlineDir (known: string[]) (game: GameItem) : string option =
        let candidates =
            if not (isNull known) && known.Length > 0 then known |> List.ofArray
            else
                GameAnalyzer.findStreamlineFolders game.InstallDirectory game.TargetExecutablePath
                |> List.map (fun f -> f.Directory)
        let withFile name = candidates |> List.tryFind (fun d -> File.Exists(Path.Combine(d, name)))
        withFile "sl.interposer.dll"
        |> Option.orElse (withFile "sl.common.dll")
        |> Option.orElse (withFile "sl.dlss_g.dll")
        |> Option.orElse (withFile "nvngx_dlssg.dll")

    /// What the frame generation side of a game looks like to DLSS Enabler.
    type Readiness =
        /// Streamline 2.4 or newer: what DLSS Enabler needs.
        | Ready of string
        /// Streamline older than 2.4: the original's "May not work".
        | MayNotWork of string
        /// No Streamline at all: the original's "Missing Frame Gen".
        | NoFrameGen

    let readiness (slDir: string option) =
        match slDir with
        | None -> NoFrameGen
        | Some dir ->
            let common = Path.Combine(dir, "sl.common.dll")
            let v = GameAnalyzer.readFileVersion common
            if not (File.Exists(common)) then MayNotWork "unknown"
            elif GameAnalyzer.compareVer v ModInstaller.minimumStreamlineVersion >= 0 then Ready(GameAnalyzer.verText v)
            else MayNotWork(GameAnalyzer.verText v)

    /// An install by DLSS Enabler Manager itself: its DLL in a proxy slot
    /// without a SUITE record, or its markers in the folders it writes.
    let hasLegacyInstall (game: GameItem) (slDir: string option) =
        let exeDir = exeDirOf game
        let manifest = readManifest game
        let ourTargets =
            match manifest with
            | Some m -> m.Files |> Array.map (fun f -> f.TargetPath) |> HashSet
            | None -> HashSet()
        let proxyHere =
            proxyNames |> List.exists (fun n ->
                let p = Path.Combine(exeDir, n)
                not (ourTargets.Contains(p)) && isEnablerFile p)
        let markers =
            [ Some exeDir; slDir ]
            |> List.choose id
            |> List.distinct
            |> List.exists (fun d ->
                try
                    Directory.Exists(d)
                    && (Directory.EnumerateFiles(d, "*" + legacyBackupSuffix) |> Seq.isEmpty |> not
                        || Directory.EnumerateFiles(d, "*" + legacyAddedSuffix) |> Seq.isEmpty |> not
                        || File.Exists(Path.Combine(d, legacyVersionMarker)))
                with _ -> false)
        proxyHere || markers

    let slotState (exeDir: string) (manifest: Manifest option) (name: string) : Slot =
        let path = Path.Combine(exeDir, name)
        let recorded =
            match manifest with
            | Some m -> m.Files |> Array.exists (fun f -> eq f.TargetPath path && not f.WasExisting)
            | None -> false
        if not (File.Exists(path)) then Free
        elif recorded || isEnablerFile path then Ours
        else Foreign

    // =====================================================================
    // UNINSTALL PLANNING (pure, so it is tested without a game)
    // =====================================================================
    type Step =
        /// Copy SUITE's backup over the file.
        | Restore of backup: string * target: string
        /// Delete a file this install added.
        | Remove of target: string
        /// Put an in-folder backup from the old tool back in place.
        | PutBack of backup: string * target: string

    /// Undoing SUITE's own record, newest first. With keepSettings the
    /// settings file stays where it is.
    let planManifestUndo (files: ModInstaller.InstalledFile[]) (keepSettings: bool) : Step list =
        [ for f in Array.rev files do
            if String.IsNullOrWhiteSpace(f.TargetPath) then ()
            elif keepSettings && eq (Path.GetFileName(f.TargetPath)) iniName then ()
            elif f.WasExisting && not (String.IsNullOrWhiteSpace(f.BackupPath)) then yield Restore(f.BackupPath, f.TargetPath)
            elif not f.WasExisting then yield Remove f.TargetPath ]

    /// Undoing what DLSS Enabler Manager left, from a listing of the folders
    /// it writes to (each folder with the file names in it). Fixed against it:
    /// a proxy pushed aside as `<proxy>.old` is put back; OptiScaler's files
    /// (OptiScaler.ini, imgui.ini, dlssg_to_fsr3_amd_is_better.dll,
    /// fakenvapi.log) are never touched; keepSettings keeps the settings file.
    let planLegacyUndo
        (exeDir: string)
        (folders: (string * string list) list)
        (isOurs: string -> bool)
        (keepSettings: bool) : Step list =
        let steps = List<Step>()
        let has (names: string list) (name: string) = names |> List.exists (eq name)
        for (dir, names) in folders |> List.distinctBy (fun (d, _) -> d.TrimEnd('\\').ToLowerInvariant()) do
            // Added files first: a Streamline swap marked files that also had
            // a backup, and the backup must win.
            for name in names do
                if name.EndsWith(legacyAddedSuffix, StringComparison.OrdinalIgnoreCase) then
                    let target = name.Substring(0, name.Length - legacyAddedSuffix.Length)
                    if has names target then steps.Add(Remove(Path.Combine(dir, target)))
                    steps.Add(Remove(Path.Combine(dir, name)))
            for name in names do
                if name.EndsWith(legacyBackupSuffix, StringComparison.OrdinalIgnoreCase) then
                    let target = name.Substring(0, name.Length - legacyBackupSuffix.Length)
                    steps.Add(PutBack(Path.Combine(dir, name), Path.Combine(dir, target)))
            if eq (dir.TrimEnd('\\')) (exeDir.TrimEnd('\\')) then
                for proxy in proxyNames do
                    let path = Path.Combine(dir, proxy)
                    let ours = has names proxy && isOurs path
                    let old = proxy + legacyOldSuffix
                    if has names old then
                        // Put the game's own DLL back, unless something else
                        // has taken the name since - then leave both alone.
                        if not (has names proxy) || ours then
                            steps.Add(PutBack(Path.Combine(dir, old), path))
                    elif ours then
                        steps.Add(Remove path)
                for marker in [ legacyVersionMarker; legacyBetaMarker ] do
                    if has names marker then steps.Add(Remove(Path.Combine(dir, marker)))
                if has names legacyFsr4Marker then
                    if has names legacyFsr4Dll && not (has names (legacyFsr4Dll + legacyBackupSuffix)) then
                        steps.Add(Remove(Path.Combine(dir, legacyFsr4Dll)))
                    steps.Add(Remove(Path.Combine(dir, legacyFsr4Marker)))
                if not keepSettings then
                    for name in [ iniName; logName ] do
                        if has names name then steps.Add(Remove(Path.Combine(dir, name)))
        List.ofSeq steps

    let private clearReadOnly (path: string) =
        try
            if File.Exists(path) then File.SetAttributes(path, File.GetAttributes(path) &&& ~~~FileAttributes.ReadOnly)
        with _ -> ()

    /// Carries out a plan. Each step stands alone: one locked file does not
    /// stop the rest. Returns (restored, removed, failed).
    let runSteps (steps: Step list) =
        let mutable restored = 0
        let mutable removed = 0
        let mutable failed = 0
        for step in steps do
            try
                match step with
                | Restore(backup, target) ->
                    if File.Exists(backup) then
                        clearReadOnly target
                        File.Copy(backup, target, true)
                        restored <- restored + 1
                | Remove target ->
                    if File.Exists(target) then
                        clearReadOnly target
                        File.Delete(target)
                        removed <- removed + 1
                | PutBack(backup, target) ->
                    if File.Exists(backup) then
                        clearReadOnly target
                        clearReadOnly backup
                        File.Move(backup, target, true)
                        restored <- restored + 1
            with ex ->
                failed <- failed + 1
                AppLog.warn (sprintf "Enabler Dock: %A failed: %s" step ex.Message)
        restored, removed, failed

    let private listing (dir: string) =
        try dir, (Directory.GetFiles(dir) |> Array.map Path.GetFileName |> List.ofArray)
        with _ -> dir, []

    // =====================================================================
    // INSTALL / UNINSTALL
    // =====================================================================
    type Outcome = { Success: bool; Message: string }

    let private fail message = { Success = false; Message = message }

    let private canWriteTo (dir: string) =
        try
            if String.IsNullOrWhiteSpace(dir) || not (Directory.Exists(dir)) then false
            else
                let probe = Path.Combine(dir, ".suite-enabler-write-test")
                use _ = File.Create(probe, 1, FileOptions.DeleteOnClose)
                true
        with _ -> false

    let private elevationNeeded =
        "Windows will not let SUITE write to this game's folder. Close DLSS 5 SUITE, right-click it and choose \"Run as administrator\", then try again."

    /// Copies payload files and remembers what was there, like ModInstaller's
    /// tracker: the first time a game file is replaced it is backed up under
    /// SUITE's data folder; a file that was not there is recorded as added.
    type private Tracker(backups: string, prior: ModInstaller.InstalledFile[]) =
        let entries = List<ModInstaller.InstalledFile>(prior)
        let shortHash (text: string) =
            SHA256.HashData(Encoding.UTF8.GetBytes(text.ToLowerInvariant()))
            |> Convert.ToHexString
            |> fun h -> h.Substring(0, 10).ToLowerInvariant()

        member _.Entries = entries.ToArray()

        member _.Note(target: string) =
            if not (entries.Exists(fun e -> eq e.TargetPath target)) then
                let existed = File.Exists(target)
                let backup =
                    if existed then
                        Directory.CreateDirectory(backups) |> ignore
                        let b = Path.Combine(backups, shortHash target + "_" + Path.GetFileName(target))
                        if not (File.Exists(b)) then File.Copy(target, b, false)
                        b
                    else ""
                entries.Add({ TargetPath = target; BackupPath = backup; WasExisting = existed })

        member this.Copy(source: string, target: string) =
            this.Note(target)
            Directory.CreateDirectory(Path.GetDirectoryName(target)) |> ignore
            clearReadOnly target
            File.Copy(source, target, true)

        /// Writes a file that is always ours (the settings file): recorded as
        /// added even if it was there, because nobody else writes it.
        member _.Write(target: string, lines: string[]) =
            if not (entries.Exists(fun e -> eq e.TargetPath target)) then
                entries.Add({ TargetPath = target; BackupPath = ""; WasExisting = false })
            Ini.save target lines

    /// How Streamline is chosen.
    type StreamlineChoice =
        | AutoStreamline
        | GameOriginal
        | Exact of Catalog.Entry

    let streamlineLabel (choice: StreamlineChoice) =
        match choice with
        | AutoStreamline -> "Auto"
        | GameOriginal -> "Game original"
        | Exact e -> e.Name

    /// Whether to leave the game's copy of a file as it is. Auto never goes
    /// backwards; a chosen version only skips files that already match.
    let shouldSkip (explicitChoice: bool) (ours: GameAnalyzer.FileVer) (theirs: GameAnalyzer.FileVer) (sameSize: bool) =
        if GameAnalyzer.isZeroVer ours then sameSize
        elif explicitChoice then GameAnalyzer.compareVer theirs ours = 0
        else GameAnalyzer.compareVer theirs ours >= 0

    let private deployStreamline (tracker: Tracker) (slDir: string) (sourceDir: string) (explicitChoice: bool) =
        let mutable copied = 0
        for file in Directory.GetFiles(sourceDir, "sl.*.dll", SearchOption.AllDirectories) do
            let target = Path.Combine(slDir, Path.GetFileName(file))
            let skip =
                File.Exists(target)
                && shouldSkip explicitChoice (GameAnalyzer.readFileVersion file) (GameAnalyzer.readFileVersion target)
                    (FileInfo(file).Length = FileInfo(target).Length)
            if not skip then
                tracker.Copy(file, target)
                copied <- copied + 1
        copied

    /// Undoes the old tool's install and then SUITE's own, keeping settings
    /// when asked. Used by uninstall, and before every reinstall.
    let private undo (game: GameItem) (slDir: string option) (keepSettings: bool) =
        let exeDir = exeDirOf game
        let manifest = readManifest game
        let fromRecord =
            match manifest with
            | Some m -> planManifestUndo (Array.append m.Files m.StreamlineFiles) keepSettings
            | None -> []
        let r1, d1, f1 = runSteps fromRecord
        let folders =
            [ Some exeDir; slDir; manifest |> Option.map (fun m -> m.StreamlineDir) ]
            |> List.choose id
            |> List.filter (fun d -> not (String.IsNullOrWhiteSpace(d)) && Directory.Exists(d))
            |> List.map listing
        let r2, d2, f2 = runSteps (planLegacyUndo exeDir folders isEnablerFile keepSettings)
        // Without a record, the settings file is still DLSS Enabler's alone.
        if not keepSettings then
            for name in [ iniName; logName ] do
                let p = Path.Combine(exeDir, name)
                try
                    if File.Exists(p) then
                        clearReadOnly p
                        File.Delete(p)
                with _ -> ()
        if f1 + f2 = 0 then (try File.Delete(manifestPath game) with _ -> ())
        r1 + r2, d1 + d2, f1 + f2

    type InstallRequest =
        { Game: GameItem
          /// None means Auto.
          Proxy: string option
          Enabler: Catalog.Entry
          Streamline: StreamlineChoice
          /// The catalog's newest Streamline, for Auto.
          LatestStreamline: Catalog.Entry option
          Fix: Catalog.GameFix option
          AllowUnpinned: bool
          /// The library scan's Streamline folders for this game, if known.
          KnownStreamlineDirs: string[] }

    /// Why this game may not have DLSS Enabler right now, or None. Touches the
    /// disk; called off the UI thread.
    let blocker (game: GameItem) =
        let exe = game.TargetExecutablePath
        if String.IsNullOrWhiteSpace(exe) || not (File.Exists(exe)) then
            Some "SUITE cannot find this game's executable."
        else
            match AntiCheat.detect game.Title game.InstallDirectory exe with
            | Some who ->
                let name = if who = "" then "an anti-cheat" else who
                Some(sprintf "This game runs %s. A proxy DLL is exactly what it looks for, so Enabler Dock will not install here%s."
                        name (if who = "BattlEye" then " unless BattlEye is switched off" else ""))
            | None ->
                if ModInstaller.isInstalled game then
                    Some "SUITE's own DLSS 5 install is on this game and uses the same DLL slots. Remove it in the DLSS 5 SUITE edition first."
                else None

    /// A reason not to go on, raised from deep inside an install and shown as is.
    exception private Refused of string

    let install (request: InstallRequest) (progress: string -> float -> unit) : Task<Outcome> = task {
        try
            let game = request.Game
            blocker game |> Option.iter (fun reason -> raise (Refused reason))
            let exeDir = exeDirOf game
            if not (canWriteTo exeDir) then raise (Refused elevationNeeded)

            progress (sprintf "Getting %s..." request.Enabler.Name) 0.02
            let! enablerDir = ensureArchive request.Enabler true request.AllowUnpinned progress
            let streamlineEntry =
                match request.Streamline with
                | AutoStreamline -> request.LatestStreamline
                | Exact e -> Some e
                | GameOriginal -> None
            let! streamlineSource =
                match streamlineEntry with
                | Some e ->
                    task {
                        progress (sprintf "Getting %s..." e.Name) 0.68
                        let! d = ensureArchive e false request.AllowUnpinned (fun t _ -> progress t 0.7)
                        return Some d
                    }
                | None -> Task.FromResult None

            progress "Looking at the game..." 0.75
            let slDir = streamlineDir request.KnownStreamlineDirs game
            let payload =
                match findIn enablerDir payloadName with
                | Some p -> p
                | None -> raise (Refused "The DLSS Enabler archive has no version.dll.")
            let template = findIn enablerDir iniName

            // Start from the game as it was: the old tool's install and any
            // earlier one of ours come off first, settings kept. If any of it
            // cannot be put back, stop - installing over it would lose the
            // record of what the game had.
            let legacy = hasLegacyInstall game slDir
            if legacy || (readManifest game).IsSome then
                progress "Clearing the previous install (settings kept)..." 0.78
                let _, _, failed = undo game slDir true
                if failed > 0 then
                    raise (Refused "Some files from the previous install could not be put back - is the game running?")

            let imports = gatherImports game.TargetExecutablePath
            let proxy, why =
                match chooseProxy request.Proxy (request.Fix |> Option.map (fun f -> f.Dll)) imports (slotState exeDir None) with
                | Ok choice -> choice
                | Error reason -> raise (Refused reason)

            progress (sprintf "Installing as %s (%s)..." proxy why) 0.82
            let tracker = Tracker(backupDir game, [||])
            tracker.Copy(payload, Path.Combine(exeDir, proxy))

            // The settings file: the user's values over the new template.
            let iniPath = Path.Combine(exeDir, iniName)
            let kept =
                match template with
                | Some t ->
                    let templateLines = File.ReadAllLines(t)
                    if File.Exists(iniPath) then
                        tracker.Write(iniPath, Ini.merge (File.ReadAllLines(iniPath)) templateLines)
                        true
                    else
                        tracker.Write(iniPath, templateLines)
                        false
                | None -> false

            // Anything else the archive brings goes where Streamline lives.
            let target = defaultArg slDir exeDir
            for file in Directory.GetFiles(enablerDir, "*", SearchOption.AllDirectories) do
                let name = Path.GetFileName(file)
                let ext = Path.GetExtension(name).ToLowerInvariant()
                if not (eq name payloadName) && not (eq name iniName) && (ext = ".dll" || ext = ".ini")
                   && not (name.StartsWith("sl.", StringComparison.OrdinalIgnoreCase)) then
                    tracker.Copy(file, Path.Combine(target, name))

            progress "Streamline..." 0.9
            let slTracker = Tracker(backupDir game, [||])
            let copied =
                match streamlineSource with
                | Some dir -> deployStreamline slTracker target dir (match request.Streamline with Exact _ -> true | _ -> false)
                | None -> 0

            writeManifest game
                { GameId = game.AppId
                  GameTitle = game.Title
                  ExecutablePath = game.TargetExecutablePath
                  InstalledAtUtc = DateTime.UtcNow.ToString("o")
                  Version = request.Enabler.Name
                  Proxy = proxy
                  Streamline = streamlineLabel request.Streamline
                  StreamlineDir = target
                  Files = tracker.Entries
                  StreamlineFiles = slTracker.Entries }
            progress "Done." 1.0
            let slText =
                match streamlineEntry with
                | Some e when copied > 0 -> sprintf " Streamline %O: %d file(s) updated." (Catalog.versionOf e.Name) copied
                | Some _ -> " The game's Streamline is already as new."
                | None -> " The game's own Streamline is kept."
            return
                { Success = true
                  Message =
                    sprintf "%s installed as %s (%s).%s%s%s" request.Enabler.Name proxy why slText
                        (if kept then " Your settings were kept." else "")
                        (if legacy then " The old DLSS Enabler Manager install was cleared first." else "") }
        with
        | Refused reason -> return fail reason
        | ex ->
            AppLog.error "Enabler Dock install" ex
            return fail ("Install failed: " + ex.Message)
    }

    /// Takes DLSS Enabler off a game - SUITE's install, the old tool's, or
    /// both. keepSettings leaves dlss-enabler.ini in place for next time.
    let uninstall (game: GameItem) (knownStreamlineDirs: string[]) (keepSettings: bool) : Outcome =
        try
            let exeDir = exeDirOf game
            if not (canWriteTo exeDir) then fail elevationNeeded
            else
                let slDir = streamlineDir knownStreamlineDirs game
                let restored, removed, failed = undo game slDir keepSettings
                if failed > 0 then
                    fail (sprintf "%d file(s) could not be put back - is the game running? Close it and try again." failed)
                else
                    { Success = true
                      Message =
                        sprintf "DLSS Enabler removed: %d original file(s) restored, %d added file(s) removed.%s"
                            restored removed (if keepSettings then " Settings kept." else "") }
        with ex ->
            AppLog.error "Enabler Dock uninstall" ex
            fail ("Uninstall failed: " + ex.Message)

    /// Changes only the Streamline side of an install: the previous swap is
    /// undone from its backups, then the chosen one goes in.
    let swapStreamline
        (game: GameItem)
        (choice: StreamlineChoice)
        (latest: Catalog.Entry option)
        (allowUnpinned: bool)
        (progress: string -> float -> unit) : Task<Outcome> = task {
        try
            match readManifest game with
            | None -> return fail "DLSS Enabler is not installed by SUITE on this game."
            | Some m ->
                let entry = match choice with AutoStreamline -> latest | Exact e -> Some e | GameOriginal -> None
                let! sourceDir =
                    match entry with
                    | Some e ->
                        task {
                            let! d = ensureArchive e false allowUnpinned progress
                            return Some d
                        }
                    | None -> Task.FromResult None
                runSteps (planManifestUndo m.StreamlineFiles false) |> ignore
                let tracker = Tracker(backupDir game, [||])
                let copied =
                    match sourceDir with
                    | Some dir -> deployStreamline tracker m.StreamlineDir dir (match choice with Exact _ -> true | _ -> false)
                    | None -> 0
                writeManifest game { m with Streamline = streamlineLabel choice; StreamlineFiles = tracker.Entries }
                return
                    { Success = true
                      Message =
                        match entry with
                        | None -> "The game's own Streamline is back."
                        | Some e -> sprintf "Streamline %O: %d file(s) in place." (Catalog.versionOf e.Name) copied }
        with ex ->
            AppLog.error "Enabler Dock Streamline swap" ex
            return fail ("Streamline change failed: " + ex.Message)
    }

    /// The template of an installed version, for "Reset settings".
    let templateFor (versionName: string) : string option =
        let dir = Path.Combine(extractedFolder (), versionName)
        findIn dir iniName
