namespace DLSS_5_MANAGER.Services

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Security.Cryptography
open System.Text.Json
open DLSS_5_MANAGER.Models

/// Keeps a game's own NVIDIA DLSS files current: super resolution
/// (nvngx_dlss.dll), frame generation (nvngx_dlssg.dll) and ray
/// reconstruction (nvngx_dlssd.dll), for any game that ships them - whether
/// DLSS 5 was installed into it or not.
///
/// The newest build comes straight from NVIDIA's own DLSS SDK repository at
/// its latest release tag. The game's file is kept beside the new one as
/// "<name>.dlss5-dlss-original", so "restore original" is always one copy
/// away, and a small record in the game folder says what was written.
///
/// A file SUITE's own DLSS 5 install placed is never touched here; that one
/// belongs to the install and changes with it.
module DlssUpdater =

    type Family =
        { Key: string
          FileName: string
          Label: string }

    let families =
        [ { Key = "dlss"; FileName = "nvngx_dlss.dll"; Label = "Super Resolution" }
          { Key = "dlssg"; FileName = "nvngx_dlssg.dll"; Label = "Frame Generation" }
          { Key = "dlssd"; FileName = "nvngx_dlssd.dll"; Label = "Ray Reconstruction" } ]

    [<Literal>]
    let private RecordName = "dlss5-dlss-update.json"

    [<Literal>]
    let OriginalSuffix = ".dlss5-dlss-original"

    [<Literal>]
    let private DisplacedSuffix = ".dlss5-dlss-displaced"

    [<Literal>]
    let private PartSuffix = ".dlss5-dlss-part"

    /// What one DLSS file in a game is right now.
    type FileState =
        /// The game's own file, never changed here.
        | Original
        /// Replaced by this updater; the game's own is kept beside it.
        | Updated
        /// Placed by SUITE's DLSS 5 install - changed only through that.
        | ByInstall
        /// Recorded and backed up, but the file itself is gone.
        | Missing

    type DlssFile =
        { Path: string
          Family: Family
          Version: string
          State: FileState
          /// Version of the backed-up original, when there is one.
          OriginalVersion: string }

    type Newest =
        { Tag: string
          Version: string
          Url: string }

    // ---------------------------------------------------------------- versions

    /// "310.8.0.0" reads as "310.8.0"; any trailing .0 past the third part
    /// is dropped.
    let fileVersion (path: string) =
        try
            let v = FileVersionInfo.GetVersionInfo(path)
            let parts = ResizeArray [ v.FileMajorPart; v.FileMinorPart; v.FileBuildPart; v.FilePrivatePart ]
            if parts |> Seq.forall ((=) 0) then ""
            else
                while parts.Count > 3 && parts.[parts.Count - 1] = 0 do
                    parts.RemoveAt(parts.Count - 1)
                String.Join(".", parts)
        with _ -> ""

    /// Numbers only, trailing zeros dropped, so "310.10.0" > "310.9.1".
    let private versionKey (v: string) =
        let nums =
            Text.RegularExpressions.Regex.Matches(v, @"\d+")
            |> Seq.map (fun m -> int64 m.Value)
            |> ResizeArray
        while nums.Count > 1 && nums.[nums.Count - 1] = 0L do
            nums.RemoveAt(nums.Count - 1)
        List.ofSeq nums

    let isNewer (candidate: string) (current: string) =
        compare (versionKey candidate) (versionKey current) > 0

    let private isWin64Dll (path: string) =
        try
            use fs = File.OpenRead(path)
            if fs.Length < 200000L then false
            else
                use br = new BinaryReader(fs)
                if br.ReadUInt16() <> 0x5A4Dus then false
                else
                    fs.Seek(0x3CL, SeekOrigin.Begin) |> ignore
                    let peAt = br.ReadInt32()
                    fs.Seek(int64 peAt, SeekOrigin.Begin) |> ignore
                    br.ReadUInt32() = 0x00004550u && br.ReadUInt16() = 0x8664us
        with _ -> false

    let private sha256 (path: string) =
        use fs = File.OpenRead(path)
        Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant()

    // ---------------------------------------------------------------- newest

    let private http =
        lazy
            (let h = new HttpClient(Timeout = TimeSpan.FromSeconds(120.0))
             h.DefaultRequestHeaders.UserAgent.ParseAdd("DLSS5-SUITE (+dlss updater)")
             h)

    let mutable private newestCache: Map<string, Newest> option = None

    /// NVIDIA's newest SDK build of each family. The tag comes from the
    /// redirect of releases/latest, so the GitHub API rate limit never
    /// applies; the DLLs come from the repository tree at that tag.
    let newest (refresh: bool) : Async<Map<string, Newest>> =
        async {
            match newestCache with
            | Some m when not refresh && not m.IsEmpty -> return m
            | _ ->
                try
                    use req = new HttpRequestMessage(HttpMethod.Head, "https://github.com/NVIDIA/DLSS/releases/latest")
                    let! resp = http.Value.SendAsync(req) |> Async.AwaitTask
                    let final = resp.RequestMessage.RequestUri.ToString().TrimEnd('/')
                    let i = final.LastIndexOf("/tag/")
                    if i < 0 then return Map.empty
                    else
                        let tag = Uri.UnescapeDataString(final.Substring(i + 5))
                        let version = tag.TrimStart('v', 'V')
                        let m =
                            families
                            |> List.map (fun f ->
                                f.Key,
                                { Tag = tag
                                  Version = version
                                  Url = sprintf "https://raw.githubusercontent.com/NVIDIA/DLSS/%s/lib/Windows_x86_64/rel/%s" tag f.FileName })
                            |> Map.ofList
                        newestCache <- Some m
                        return m
                with ex ->
                    AppLog.warn ("Could not reach NVIDIA's DLSS releases: " + ex.Message)
                    return Map.empty
        }

    // ---------------------------------------------------------------- record

    [<CLIMutable>]
    type RecordEntry =
        { path: string
          family: string
          original: string
          written: string
          size: int64
          label: string
          at: int64
          sha256: string }

    [<CLIMutable>]
    type Record =
        { tool: string
          kind: string
          version: int
          files: RecordEntry[] }

    let private recordPath (game: GameItem) = Path.Combine(game.InstallDirectory, RecordName)

    let private inside (root: string) (path: string) =
        try
            let r = Path.GetFullPath(root).TrimEnd('\\') + "\\"
            Path.GetFullPath(path).StartsWith(r, StringComparison.OrdinalIgnoreCase)
        with _ -> false

    /// The record is read as untrusted: an entry only counts when it names one
    /// of the three DLLs inside this game's folder.
    let private loadRecord (game: GameItem) : RecordEntry list =
        try
            let p = recordPath game
            if not (File.Exists p) then []
            else
                let r = JsonSerializer.Deserialize<Record>(File.ReadAllText p)
                if isNull (box r) || isNull r.files then []
                else
                    r.files
                    |> Array.choose (fun e ->
                        if isNull (box e) || String.IsNullOrWhiteSpace e.path then None
                        else
                            let full = Path.GetFullPath(Path.Combine(game.InstallDirectory, e.path))
                            match families |> List.tryFind (fun f -> f.Key = e.family) with
                            | Some f when inside game.InstallDirectory full
                                          && Path.GetFileName(full).Equals(f.FileName, StringComparison.OrdinalIgnoreCase) ->
                                Some { e with path = full }
                            | _ -> None)
                    |> Array.distinctBy (fun e -> e.path.ToLowerInvariant())
                    |> List.ofArray
        with _ -> []

    let private saveRecord (game: GameItem) (entries: RecordEntry list) =
        let p = recordPath game
        if entries.IsEmpty then
            try File.Delete p with _ -> ()
        else
            let rel =
                entries
                |> List.map (fun e -> { e with path = Path.GetRelativePath(game.InstallDirectory, e.path).Replace('\\', '/') })
                |> Array.ofList
            let json =
                JsonSerializer.Serialize(
                    { tool = "dlss5-suite"; kind = "dlss-update"; version = 1; files = rel },
                    JsonSerializerOptions(WriteIndented = true))
            let tmp = p + ".tmp"
            File.WriteAllText(tmp, json)
            File.Move(tmp, p, true)

    // ---------------------------------------------------------------- scan

    let private skipDirs =
        set [ "content"; "paks"; "saved"; "logs"; "movies"; "sounds"; "music"; "videos"; "localization"
              "shadercache"; "derivedcache"; "cache"; "textures"; "maps"; "levels"; "audio"; "data"
              "assets"; "mods"; "screenshots"; "steamapps"; "redist"; "_commonredist"; "host64"; "reshade-shaders" ]

    let private passThrough =
        set [ "streamline"; "dlss"; "nvidia"; "ngx"; "nvngx"; "binaries"; "plugins"; "win64"; "x64"; "bin" ]

    /// A bounded walk: depth 9, 6000 folders, 6 seconds, at most 6 hits.
    /// Bulky asset folders are skipped unless they hold a folder where DLSS
    /// usually sits.
    let private walk (root: string) =
        let names = families |> List.map (fun f -> f.FileName.ToLowerInvariant()) |> set
        let hits = ResizeArray<string>()
        let clock = Stopwatch.StartNew()
        let mutable dirs = 0
        let rec go (dir: string) (depth: int) (onlyPass: bool) =
            if depth <= 9 && dirs < 6000 && hits.Count < 6 && clock.Elapsed.TotalSeconds < 6.0 then
                dirs <- dirs + 1
                try
                    if not onlyPass then
                        for f in Directory.EnumerateFiles(dir, "nvngx_dlss*.dll") do
                            if names.Contains(Path.GetFileName(f).ToLowerInvariant()) && hits.Count < 6 then hits.Add f
                    for d in Directory.EnumerateDirectories(dir) do
                        let n = Path.GetFileName(d).ToLowerInvariant()
                        if not (n.StartsWith(".")) then
                            if onlyPass then
                                if passThrough.Contains n then go d (depth + 1) false
                            elif skipDirs.Contains n then
                                go d (depth + 1) true
                            else
                                go d (depth + 1) false
                with _ -> ()
        if Directory.Exists root then go root 0 false
        List.ofSeq hits

    let private familyOf (path: string) =
        families |> List.tryFind (fun f -> Path.GetFileName(path).Equals(f.FileName, StringComparison.OrdinalIgnoreCase))

    let private sameBuild (a: string) (b: string) =
        try
            File.Exists a && File.Exists b
            && FileInfo(a).Length = FileInfo(b).Length
            && fileVersion a = fileVersion b
        with _ -> false

    /// Every DLSS file the game has, and what state each is in. Read only.
    let scan (game: GameItem) : DlssFile list =
        if String.IsNullOrWhiteSpace game.InstallDirectory || not (Directory.Exists game.InstallDirectory) then []
        else
            let record = loadRecord game
            let byInstall =
                ModInstaller.installedTargets game |> List.map (fun p -> p.ToLowerInvariant()) |> set
            let exeDir = try Path.GetDirectoryName(game.TargetExecutablePath) with _ -> ""
            let candidates =
                [ yield! walk game.InstallDirectory
                  for d in [ exeDir; game.InstallDirectory ] do
                      if not (String.IsNullOrEmpty d) then
                          for f in families do
                              let p = Path.Combine(d, f.FileName)
                              if File.Exists p then yield p
                  for e in record -> e.path ]
                |> List.map Path.GetFullPath
                |> List.distinctBy (fun p -> p.ToLowerInvariant())
                |> List.filter (fun p -> not (p.ToLowerInvariant().Split('\\') |> Array.contains "host64"))
            [ for p in candidates do
                  match familyOf p with
                  | None -> ()
                  | Some fam ->
                      let backup = p + OriginalSuffix
                      let rec' = record |> List.tryFind (fun e -> e.path.Equals(p, StringComparison.OrdinalIgnoreCase))
                      let exists = File.Exists p
                      let origVersion = if File.Exists backup then fileVersion backup else ""
                      if not exists then
                          if File.Exists backup then
                              yield { Path = p; Family = fam; Version = ""; State = Missing; OriginalVersion = origVersion }
                      elif byInstall.Contains(p.ToLowerInvariant()) then
                          yield { Path = p; Family = fam; Version = fileVersion p; State = ByInstall; OriginalVersion = "" }
                      elif isWin64Dll p then
                          let v = fileVersion p
                          let updated =
                              match rec' with
                              | Some e -> versionKey e.written = versionKey v && (e.size = 0L || e.size = FileInfo(p).Length)
                              | None -> File.Exists backup && not (sameBuild p backup)
                          yield { Path = p; Family = fam; Version = v; State = (if updated then Updated else Original); OriginalVersion = origVersion } ]

    /// Whether a scanned file has a newer NVIDIA build waiting.
    let isOutdated (newestBuilds: Map<string, Newest>) (f: DlssFile) =
        match f.State, newestBuilds.TryFind f.Family.Key with
        | (Original | Updated), Some n when f.Version <> "" -> isNewer n.Version f.Version
        | _ -> false

    // ---------------------------------------------------------------- guards

    let private gameRunning (game: GameItem) =
        try GameLauncher.isRunning game with _ -> false

    let private canWrite (dir: string) =
        try
            let probe = Path.Combine(dir, ".dlss5-dlss-write-test-" + Guid.NewGuid().ToString("N"))
            File.WriteAllText(probe, "")
            File.Delete probe
            true
        with _ -> false

    /// Why an update may not go ahead, or None when it can.
    let refusal (game: GameItem) (allowAntiCheat: bool) : string option =
        if gameRunning game then
            Some "Close the game first - its DLSS files are in use while it runs."
        else
            match (if allowAntiCheat then None else AntiCheat.detect game.Title game.InstallDirectory game.TargetExecutablePath) with
            | Some ac ->
                Some(sprintf "%s is installed with this game. An anti-cheat can treat a changed DLSS file as tampering; for anything played online, keep the game's own." ac)
            | None ->
                if not (canWrite game.InstallDirectory) then
                    let p = game.InstallDirectory.ToLowerInvariant()
                    if p.Contains("windowsapps") || p.Contains("xboxgames") then
                        Some "Xbox / Game Pass protects this folder, so its DLSS files cannot be changed."
                    else
                        Some "Windows will not let SUITE write to this game's folder. Run SUITE as administrator, or move the game out of Program Files."
                else None

    // ---------------------------------------------------------------- update

    let private download (url: string) (name: string) : Async<string> =
        async {
            let cache = Path.Combine(AppLog.dataFolder (), "cache", "dlss")
            Directory.CreateDirectory(cache) |> ignore
            let target = Path.Combine(cache, name)
            if File.Exists target && isWin64Dll target then return target
            else
                let part = target + ".part"
                let mutable lastError = ""
                let mutable ok = false
                let mutable attempt = 0
                while not ok && attempt < 4 do
                    attempt <- attempt + 1
                    try
                        use! resp = http.Value.GetAsync(url, HttpCompletionOption.ResponseHeadersRead) |> Async.AwaitTask
                        resp.EnsureSuccessStatusCode() |> ignore
                        do!
                            async {
                                use! src = resp.Content.ReadAsStreamAsync() |> Async.AwaitTask
                                use dst = File.Create(part)
                                do! src.CopyToAsync(dst) |> Async.AwaitTask
                            }
                        if isWin64Dll part then
                            File.Move(part, target, true)
                            ok <- true
                        else
                            try File.Delete part with _ -> ()
                            lastError <- "the download was not a 64-bit DLL"
                            attempt <- 4
                    with ex ->
                        lastError <- ex.Message
                        do! Async.Sleep(1500 * attempt)
                if ok then return target
                else return failwithf "Could not download %s: %s. Nothing was changed." name lastError
        }

    let private copyWhole (src: string) (dst: string) =
        let part = dst + PartSuffix
        File.Copy(src, part, true)
        if FileInfo(part).Length <> FileInfo(src).Length then
            File.Delete part
            failwith "the copied file came out the wrong size"
        File.Move(part, dst, true)

    /// Copies the new build over the game's file; on failure the file that
    /// was set aside goes back and a backup made for this attempt is removed.
    let private putInPlace (src: string) (dst: string) (backup: string) (displaced: string option) (madeBackup: bool) =
        try
            copyWhole src dst
        with ex ->
            displaced |> Option.iter (fun d -> File.Move(d, dst, true))
            if madeBackup then
                try File.Delete backup with _ -> ()
            raise ex

    let private displacedName (path: string) =
        let stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        Seq.initInfinite id
        |> Seq.map (fun n -> if n = 0 then sprintf "%s%s-%d" path DisplacedSuffix stamp else sprintf "%s%s-%d-%d" path DisplacedSuffix stamp n)
        |> Seq.find (fun p -> not (File.Exists p))

    /// Drops record entries the launcher has undone (it put the original
    /// back over ours) so nothing stale is ever "restored" later.
    let private settle (game: GameItem) =
        let record = loadRecord game
        let kept =
            record
            |> List.filter (fun e ->
                let backup = e.path + OriginalSuffix
                if not (File.Exists e.path) then File.Exists backup
                elif versionKey (fileVersion e.path) = versionKey e.written then true
                elif File.Exists backup then
                    if sameBuild e.path backup then
                        (try File.Delete backup with _ -> ())
                        false
                    else true
                else false)
        if kept.Length <> record.Length then saveRecord game kept

    /// Puts NVIDIA's newest build of each outdated family into the game.
    /// Returns a line per file for the user.
    let update (game: GameItem) (allowAntiCheat: bool) (progress: string -> unit) : Async<Result<string list, string>> =
        async {
            match refusal game allowAntiCheat with
            | Some why -> return Error why
            | None ->
                let! builds = newest false
                if builds.IsEmpty then
                    return Error "NVIDIA's DLSS releases could not be reached (raw.githubusercontent.com). Nothing was changed."
                else
                    settle game
                    let targets = scan game |> List.filter (isOutdated builds)
                    if targets.IsEmpty then return Ok [ "Every DLSS file in this game is already NVIDIA's newest." ]
                    else
                        let lines = ResizeArray<string>()
                        let mutable record = loadRecord game
                        let mutable failure = None
                        for f in targets do
                            if failure.IsNone then
                                try
                                    let n = builds.[f.Family.Key]
                                    progress (sprintf "Downloading %s %s..." f.Family.Label n.Version)
                                    let! dl = download n.Url (sprintf "%s-%s.dll" f.Family.Key n.Version)
                                    if fileVersion f.Path <> f.Version then
                                        failwith "the game's file changed while the update was downloading"
                                    let backup = f.Path + OriginalSuffix
                                    let mutable displaced = None
                                    let mutable madeBackup = false
                                    let original =
                                        if not (File.Exists backup) then
                                            File.Copy(f.Path, backup)
                                            madeBackup <- true
                                            f.Version
                                        else
                                            if f.State <> Updated && not (sameBuild f.Path backup) then
                                                let d = displacedName f.Path
                                                File.Move(f.Path, d)
                                                displaced <- Some d
                                            fileVersion backup
                                    putInPlace dl f.Path backup displaced madeBackup
                                    let written = fileVersion f.Path
                                    let entry =
                                        { path = f.Path
                                          family = f.Family.Key
                                          original = original
                                          written = (if written = "" then n.Version else written)
                                          size = FileInfo(f.Path).Length
                                          label = n.Version + " (NVIDIA SDK)"
                                          at = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                                          sha256 = sha256 f.Path }
                                    record <- entry :: (record |> List.filter (fun e -> not (e.path.Equals(f.Path, StringComparison.OrdinalIgnoreCase))))
                                    saveRecord game record
                                    lines.Add(sprintf "%s: %s → %s" f.Family.Label (if f.Version = "" then "?" else f.Version) entry.written)
                                    AppLog.info (sprintf "DLSS update in %s: %s %s -> %s" game.Title f.Family.FileName f.Version entry.written)
                                with ex ->
                                    failure <- Some(sprintf "%s: %s" f.Family.Label ex.Message)
                                    AppLog.error ("DLSS update in " + game.Title) ex
                        match failure with
                        | Some why when lines.Count = 0 -> return Error why
                        | Some why -> return Ok(List.ofSeq lines @ [ "Stopped at " + why ])
                        | None -> return Ok(List.ofSeq lines)
        }

    /// Puts every original DLSS file back.
    let restore (game: GameItem) : Result<string list, string> =
        if gameRunning game then Error "Close the game first - its DLSS files are in use while it runs."
        else
            try
                settle game
                let record = loadRecord game
                let targets =
                    (record |> List.map (fun e -> e.path))
                    @ (scan game |> List.filter (fun f -> File.Exists(f.Path + OriginalSuffix)) |> List.map (fun f -> f.Path))
                    |> List.distinctBy (fun p -> p.ToLowerInvariant())
                let lines = ResizeArray<string>()
                let mutable remaining = record
                for p in targets do
                    let backup = p + OriginalSuffix
                    let fam = (familyOf p |> Option.map (fun f -> f.Label) |> Option.defaultValue (Path.GetFileName p))
                    if not (File.Exists backup) then
                        lines.Add(sprintf "%s: no saved original - verify the game's files in its launcher to get it back." fam)
                    else
                        let written = remaining |> List.tryFind (fun e -> e.path.Equals(p, StringComparison.OrdinalIgnoreCase))
                        if File.Exists p && not (sameBuild p backup) then
                            let ours = written |> Option.exists (fun e -> versionKey (fileVersion p) = versionKey e.written)
                            if not ours then File.Move(p, displacedName p)
                        copyWhole backup p
                        File.Delete backup
                        lines.Add(sprintf "%s: original %s restored." fam (fileVersion p))
                        remaining <- remaining |> List.filter (fun e -> not (e.path.Equals(p, StringComparison.OrdinalIgnoreCase)))
                saveRecord game remaining
                AppLog.info (sprintf "DLSS originals restored in %s" game.Title)
                Ok(if lines.Count = 0 then [ "Nothing to restore - the game has its own DLSS files." ] else List.ofSeq lines)
            with ex ->
                AppLog.error ("DLSS restore in " + game.Title) ex
                Error ex.Message
