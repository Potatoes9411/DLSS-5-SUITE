namespace DLSS_5_MANAGER.Services

open System
open System.IO
open System.IO.Compression
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open DLSS_5_MANAGER.Models

/// DLSS 5 inside RTX Remix. A Remix game renders through its own path-traced
/// runtime in a ".trex" folder (d3d9.dll), on Vulkan - a ReShade proxy only
/// crashes it, so this route installs no ReShade at all: the neural model
/// goes into .trex and one option in rtx.conf switches the runtime's neural
/// stage on.
///
/// Only two community runtimes have that stage: lunks' dxvk-remix-plus-dlssnr
/// ("rtx.neuralRendering") and Kim2091's fork in the GTA IV mod
/// ("rtx.neuralUplift"). A stock NVIDIA runtime has none, so SUITE can swap
/// lunks' build in - keeping the mod's own to put back.
module RtxRemix =

    type RemixMod =
        { Game: string
          Patterns: string list
          Url: string
          /// Its release zip carries .trex, so SUITE can install it.
          Installable: bool }

    /// Known Remix mods, matched against the game's name, exe and folder.
    let mods =
        [ { Game = "Portal with RTX"; Patterns = [ "portal with rtx"; "portal_rtx" ]; Url = "https://store.steampowered.com/app/2012840/"; Installable = false }
          { Game = "Portal: Prelude RTX"; Patterns = [ "prelude rtx"; "portal prelude" ]; Url = "https://store.steampowered.com/app/2410180/"; Installable = false }
          { Game = "Half-Life 2 RTX"; Patterns = [ "half-life 2 rtx"; "hl2rtx" ]; Url = "https://store.steampowered.com/app/2477290/"; Installable = false }
          { Game = "Grand Theft Auto IV"; Patterns = [ "grand theft auto iv"; "gtaiv"; "gta 4" ]; Url = "https://github.com/xoxor4d/gta4-rtx"; Installable = true }
          { Game = "Need for Speed Underground 2"; Patterns = [ "underground 2"; "nfsu2"; "speed2" ]; Url = "https://github.com/Ekozmaster/NFSU2-RTX-Remix"; Installable = true }
          { Game = "Garry's Mod"; Patterns = [ "garry"; "gmod" ]; Url = "https://github.com/Xenthio/garrys-mod-rtx-remixed"; Installable = false }
          { Game = "Deus Ex"; Patterns = [ "deus ex" ]; Url = "https://github.com/onnoj/DeusExEchelonRenderer"; Installable = false }
          { Game = "Thief Gold"; Patterns = [ "thief gold"; "thief" ]; Url = "https://github.com/Night1099/thief-gold-rtx-remix"; Installable = false }
          { Game = "Morrowind"; Patterns = [ "morrowind"; "openmw" ]; Url = "https://github.com/BrunchyChineapple/Morrowind-RTX-Remix-source"; Installable = false }
          { Game = "Vampire: The Masquerade - Bloodlines"; Patterns = [ "bloodlines"; "vampire" ]; Url = "https://github.com/CattoSalad/VTMB-RTX-Remix"; Installable = false }
          { Game = "Prince of Persia: The Sands of Time"; Patterns = [ "sands of time" ]; Url = "https://github.com/kaminoer/pop-sot-rtx"; Installable = false }
          { Game = "Saints Row 2"; Patterns = [ "saints row 2"; "saintsrow2" ]; Url = "https://github.com/BRAGme/sr2-rtx-remix-proxy"; Installable = false }
          { Game = "Saints Row: The Third"; Patterns = [ "saints row the third"; "saintsrowthethird" ]; Url = "https://github.com/PurrsianMilkman/Saints-Row-The-Third-RTX-REMIX-compatibility-mod"; Installable = false }
          { Game = "Red Faction"; Patterns = [ "red faction" ]; Url = "https://github.com/BRAGme/RedFaction-RTX"; Installable = false }
          { Game = "Total Overdose"; Patterns = [ "total overdose" ]; Url = "https://github.com/Utkar5hM/TotalOverDoseRTXRemix"; Installable = false }
          { Game = "Assassin's Creed II"; Patterns = [ "assassin's creed ii"; "assassins creed 2"; "ac2" ]; Url = "https://github.com/Kamzik123/ac2-rtx"; Installable = false }
          { Game = "Populous: The Beginning"; Patterns = [ "populous" ]; Url = "https://github.com/xmarre/Populous-3-RTX-Remix"; Installable = false }
          { Game = "Silent Storm"; Patterns = [ "silent storm" ]; Url = "https://github.com/WormSlayer/silent-storm-rtx"; Installable = false }
          { Game = "Dungeon Keeper 2"; Patterns = [ "dungeon keeper 2" ]; Url = "https://github.com/mencelot/dk2-dxwrapper-with-path-tracing-support"; Installable = false }
          { Game = "GTA Vice City"; Patterns = [ "vice city" ]; Url = "https://github.com/GmanRO/GTA-VICE-CITY-RTX-REMIX-.ASI-compiled-within-linux-"; Installable = false }
          { Game = "Cry of Fear"; Patterns = [ "cry of fear" ]; Url = "https://github.com/michaelabilliot/CryofFear_RTX-REMIX"; Installable = false } ]

    let modFor (game: GameItem) : RemixMod option =
        let hay =
            (game.Title + " " + Path.GetFileName(game.TargetExecutablePath) + " " + game.InstallDirectory)
                .ToLowerInvariant()
                .Replace('_', ' ')
        // "Vice City - The Definitive Edition" is a UE4 remaster, not the
        // original Remix can reach.
        if hay.Contains "definitive edition" then None
        else mods |> List.tryFind (fun m -> m.Patterns |> List.exists hay.Contains)

    // -------------------------------------------------------------- runtime

    let private skip =
        set [ "content"; "paks"; "saved"; "logs"; "movies"; "sounds"; "music"; "videos"; "localization"; "shadercache"
              "cache"; "textures"; "maps"; "levels"; "audio"; "data"; "assets"; "mods"; "screenshots"; "steamapps"
              "redist"; "_commonredist"; "rtx-remix"; "common"; "update" ]

    /// The ".trex" folder holding a Remix runtime, three levels deep at most.
    let findRuntime (folder: string) : string option =
        let rec go (dirs: string list) (depth: int) =
            if dirs.IsEmpty || depth > 3 then None
            else
                match dirs |> List.tryFind (fun d -> File.Exists(Path.Combine(d, ".trex", "d3d9.dll"))) with
                | Some d -> Some(Path.Combine(d, ".trex"))
                | None ->
                    let next =
                        dirs
                        |> List.collect (fun d ->
                            try
                                Directory.GetDirectories(d)
                                |> Array.filter (fun s ->
                                    let n = Path.GetFileName(s).ToLowerInvariant()
                                    not (n.StartsWith ".") && not (skip.Contains n))
                                |> List.ofArray
                            with _ -> [])
                    go next (depth + 1)
        if Directory.Exists folder then go [ folder ] 0 else None

    /// "neural", "uplift" or "" (stock - no neural stage), read from the
    /// runtime's own option names. The DLL is 150-230 MB, read in chunks
    /// that overlap so a name on a boundary is still found.
    let flavour (trex: string) =
        try
            let needleA = Encoding.ASCII.GetBytes("rtx.neuralRendering")
            let needleB = Encoding.ASCII.GetBytes("rtx.neuralUplift")
            use fs = File.OpenRead(Path.Combine(trex, "d3d9.dll"))
            let size = 4 * 1024 * 1024
            let buf = Array.zeroCreate<byte> (size + 32)
            let mutable carry = 0
            let mutable a = false
            let mutable b = false
            let mutable read = fs.Read(buf, carry, size)
            while read > 0 && not a do
                let span = ReadOnlySpan<byte>(buf, 0, carry + read)
                if span.IndexOf(ReadOnlySpan<byte>(needleA)) >= 0 then a <- true
                if span.IndexOf(ReadOnlySpan<byte>(needleB)) >= 0 then b <- true
                let keep = min 31 (carry + read)
                Array.Copy(buf, carry + read - keep, buf, 0, keep)
                carry <- keep
                read <- fs.Read(buf, carry, size)
            if a then "neural" elif b then "uplift" else ""
        with _ -> ""

    let private optionKey (fl: string) =
        if fl = "uplift" then "rtx.neuralUplift.enable" else "rtx.neuralRendering.enable"

    // --------------------------------------------------------------- record

    [<CLIMutable>]
    type RemixRecord =
        { Trex: string
          Conf: string
          Key: string
          /// Files placed, with the backup each replaced ("" when new).
          Files: string[]
          Backups: string[]
          ConfHadKey: bool
          ModFiles: string[]
          ModBackups: string[] }

    let private recordPath (game: GameItem) =
        let dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DLSS5Suite", "Remix")
        Directory.CreateDirectory(dir) |> ignore
        let id = Regex.Replace((if String.IsNullOrWhiteSpace game.AppId then game.Title else game.AppId), @"[^A-Za-z0-9_\-]", "_")
        Path.Combine(dir, id + ".json")

    let private loadRecord (game: GameItem) =
        try
            let p = recordPath game
            if File.Exists p then Some(JsonSerializer.Deserialize<RemixRecord>(File.ReadAllText p)) else None
        with _ -> None

    let private saveRecord (game: GameItem) (r: RemixRecord) =
        File.WriteAllText(recordPath game, JsonSerializer.Serialize(r, JsonSerializerOptions(WriteIndented = true)))

    let isEnabled (game: GameItem) = loadRecord game |> Option.exists (fun r -> not (String.IsNullOrEmpty r.Key))

    // ------------------------------------------------------------- download

    let private http =
        lazy
            (let h = new HttpClient(Timeout = TimeSpan.FromMinutes(15.0))
             h.DefaultRequestHeaders.UserAgent.ParseAdd("DLSS5-SUITE")
             h)

    let private fetch (url: string) (target: string) =
        use s = http.Value.GetStreamAsync(url).Result
        use f = File.Create(target + ".part")
        s.CopyTo(f)
        f.Close()
        File.Move(target + ".part", target, true)

    let private backupSuffix = ".dlss5-suite-backup"

    let private place (src: string) (dst: string) (files: ResizeArray<string>) (backups: ResizeArray<string>) =
        let backup =
            if File.Exists dst then
                let b = dst + backupSuffix
                if not (File.Exists b) then File.Copy(dst, b)
                b
            else ""
        File.Copy(src, dst, true)
        files.Add dst
        backups.Add backup

    /// Sets "key = True" in rtx.conf, in place; adds a newline first when the
    /// file's last line has none (GTA IV's has none, and appending merged two
    /// lines into one broken one).
    let private setConf (conf: string) (key: string) =
        let text = if File.Exists conf then File.ReadAllText conf else ""
        let nl = if text.Contains "\r\n" then "\r\n" else "\n"
        let pattern = Regex(@"^[ \t]*" + Regex.Escape key + @"[ \t]*=.*$", RegexOptions.Multiline)
        let had = pattern.IsMatch text
        let updated =
            if had then
                let mutable first = true
                pattern.Replace(text, (fun (_: Match) -> if first then (first <- false; key + " = True") else ""))
            else
                let body = if text = "" || text.EndsWith "\n" then text else text + nl
                body + key + " = True" + nl
        File.WriteAllText(conf, updated)
        had

    let private clearConf (conf: string) (key: string) =
        try
            if File.Exists conf then
                let text = File.ReadAllText conf
                File.WriteAllText(conf, Regex.Replace(text, @"^[ \t]*" + Regex.Escape key + @"[ \t]*=.*(\r?\n)?", "", RegexOptions.Multiline))
        with _ -> ()

    // ---------------------------------------------------------------- enable

    /// Switches DLSS 5 on inside the game's Remix runtime. swapStock: put
    /// lunks' runtime in when the game's has no neural stage.
    let enable (game: GameItem) (swapStock: bool) (progress: string -> unit) : Result<string, string> =
        try
            match findRuntime game.InstallDirectory with
            | None -> Error "This game has no RTX Remix runtime (.trex folder). Install its Remix mod first."
            | Some trex ->
                let files = ResizeArray<string>()
                let backups = ResizeArray<string>()
                let mutable fl = flavour trex
                let mutable note = ""
                if fl = "" then
                    if not swapStock then failwith "this Remix runtime has no neural stage - allow SUITE to swap in one that has"
                    progress "Downloading the Remix runtime with DLSS-NR (lunks' dxvk-remix-plus-dlssnr)..."
                    let cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DLSS5Suite", "Downloads", "remix")
                    Directory.CreateDirectory(cache) |> ignore
                    for name in [ "d3d9.dll"; "remix_nvngx.dll" ] do
                        let local = Path.Combine(cache, name)
                        fetch (sprintf "https://github.com/lunks/dxvk-remix-plus-dlssnr/releases/latest/download/%s" name) local
                        // remix_nvngx.dll keeps that name: the NR snippet checks
                        // its caller's path contains "nvngx.dll".
                        place local (Path.Combine(trex, name)) files backups
                    fl <- "neural"
                    note <- " The game's own runtime is kept and goes back on Remove."
                progress "Putting the DLSS 5 model into the Remix runtime..."
                let model = Path.Combine(ModInstaller.modFilesRoot (), ModInstaller.dlss5DirName, GameAnalyzer.dlssnrFileName)
                if not (File.Exists model) then failwith "the DLSS 5 model is missing from SUITE's mod files"
                place model (Path.Combine(trex, GameAnalyzer.dlssnrFileName)) files backups
                let parent = Path.GetDirectoryName(trex)
                let conf =
                    [ Path.Combine(parent, "rtx.conf"); Path.Combine(game.InstallDirectory, "rtx.conf") ]
                    |> List.tryFind File.Exists
                    |> Option.defaultValue (Path.Combine(parent, "rtx.conf"))
                let key = optionKey fl
                let had = setConf conf key
                let prior = loadRecord game
                saveRecord
                    game
                    { Trex = trex
                      Conf = conf
                      Key = key
                      Files = files.ToArray()
                      Backups = backups.ToArray()
                      ConfHadKey = had
                      ModFiles = (prior |> Option.map (fun r -> r.ModFiles) |> Option.defaultValue [||])
                      ModBackups = (prior |> Option.map (fun r -> r.ModBackups) |> Option.defaultValue [||]) }
                AppLog.info (sprintf "RTX Remix DLSS 5 on in %s (%s)" game.Title key)
                Ok(sprintf "DLSS 5 is on in the Remix runtime (%s).%s Turn it on and off in Alt+X -> Neural Rendering." key note)
        with ex ->
            AppLog.error ("RTX Remix enable in " + game.Title) ex
            Error ex.Message

    /// Takes back exactly what enable (and installMod) put in.
    let remove (game: GameItem) : Result<string, string> =
        match loadRecord game with
        | None -> Ok "Nothing of SUITE's is in this game's Remix runtime."
        | Some r ->
            try
                let restore (files: string[]) (backups: string[]) =
                    for (f, b) in Array.zip files backups do
                        if b <> "" && File.Exists b then File.Move(b, f, true)
                        elif File.Exists f then File.Delete f
                restore (if isNull r.Files then [||] else r.Files) (if isNull r.Backups then [||] else r.Backups)
                if not r.ConfHadKey && not (String.IsNullOrEmpty r.Key) then clearConf r.Conf r.Key
                restore (if isNull r.ModFiles then [||] else r.ModFiles) (if isNull r.ModBackups then [||] else r.ModBackups)
                File.Delete(recordPath game)
                Ok "Removed - the Remix runtime is as it was."
            with ex -> Error ex.Message

    /// Installs a known Remix mod straight from its GitHub release, when the
    /// release zip carries the .trex runtime.
    let installMod (game: GameItem) (progress: string -> unit) : Result<string, string> =
        try
            match modFor game with
            | None -> Error "No Remix mod is known for this game."
            | Some m when not m.Installable -> Error(sprintf "The %s Remix mod has to be installed by hand: %s" m.Game m.Url)
            | Some m ->
                if (findRuntime game.InstallDirectory).IsSome then failwith "this game already has a Remix runtime"
                let repo = Regex.Match(m.Url, @"github\.com/([^/]+/[^/]+)").Groups.[1].Value
                progress (sprintf "Finding the newest %s Remix mod..." m.Game)
                let json = http.Value.GetStringAsync(sprintf "https://api.github.com/repos/%s/releases?per_page=10" repo).Result
                use doc = JsonDocument.Parse(json)
                let asset =
                    doc.RootElement.EnumerateArray()
                    |> Seq.collect (fun rel -> rel.GetProperty("assets").EnumerateArray())
                    |> Seq.tryFind (fun a -> a.GetProperty("name").GetString().EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                match asset with
                | None -> failwith "its releases have no zip"
                | Some a ->
                    let cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DLSS5Suite", "Downloads", "remix")
                    Directory.CreateDirectory(cache) |> ignore
                    let zip = Path.Combine(cache, a.GetProperty("name").GetString())
                    progress "Downloading the mod..."
                    fetch (a.GetProperty("browser_download_url").GetString()) zip
                    use archive = ZipFile.OpenRead(zip)
                    let rootEntry =
                        archive.Entries
                        |> Seq.map (fun e -> e.FullName.Replace('\\', '/'))
                        |> Seq.filter (fun n -> n.EndsWith(".trex/d3d9.dll", StringComparison.OrdinalIgnoreCase))
                        |> Seq.sortBy (fun n -> n.Split('/').Length)
                        |> Seq.tryHead
                    match rootEntry with
                    | None -> failwith "the mod's zip has no .trex runtime - it ships only a proxy, install it by hand"
                    | Some e ->
                        let root = e.Substring(0, e.Length - ".trex/d3d9.dll".Length)
                        let target = Path.GetFullPath(Path.GetDirectoryName(game.TargetExecutablePath)).TrimEnd('\\') + "\\"
                        let files = ResizeArray<string>()
                        let backups = ResizeArray<string>()
                        progress "Unpacking the mod into the game..."
                        for entry in archive.Entries do
                            let n = entry.FullName.Replace('\\', '/')
                            if entry.Name <> "" && n.StartsWith(root, StringComparison.OrdinalIgnoreCase) then
                                let dest = Path.GetFullPath(Path.Combine(target, n.Substring(root.Length)))
                                if not (dest.StartsWith(target, StringComparison.OrdinalIgnoreCase)) then failwithf "refused an entry outside the game: %s" n
                                Directory.CreateDirectory(Path.GetDirectoryName dest) |> ignore
                                let backup =
                                    if File.Exists dest then
                                        let b = dest + backupSuffix
                                        if not (File.Exists b) then File.Copy(dest, b)
                                        b
                                    else ""
                                entry.ExtractToFile(dest, true)
                                files.Add dest
                                backups.Add backup
                        // There was no runtime before this, so there is no
                        // earlier record to keep.
                        saveRecord
                            game
                            { Trex = ""; Conf = ""; Key = ""; Files = [||]; Backups = [||]; ConfHadKey = false
                              ModFiles = files.ToArray()
                              ModBackups = backups.ToArray() }
                        Ok(sprintf "The %s Remix mod is installed." m.Game)
        with ex ->
            AppLog.error ("Remix mod install in " + game.Title) ex
            Error ex.Message

    /// What the Remix log says about the neural stage.
    let verdict (game: GameItem) : string =
        match findRuntime game.InstallDirectory with
        | None -> "No Remix runtime in the folder any more."
        | Some trex ->
            let log = Path.Combine(Path.GetDirectoryName(trex), "rtx-remix", "logs", "remix-dxvk.log")
            let text = try (if File.Exists log then File.ReadAllText log else "") with _ -> ""
            if text = "" then "Not run yet, or the Remix runtime never loaded."
            elif Regex.IsMatch(text, @"\[DLSS-NR\] Created the [\w ]+ feature") then "Working."
            elif Regex.Matches(text, @"NVIDIA DLSS-NR evaluated \(count=(\d+)") |> Seq.exists (fun m -> int m.Groups.[1].Value > 0) then "Working."
            elif text.Contains "does not export the Vulkan NGX entry points" then
                "Remix ran, but the DLSS 5 model has no Vulkan entry points - this build of the model cannot run inside Remix."
            elif text.Contains "nvngx_dlssnr.dll could not be loaded" || text.Contains "nvngx_dlssnr.dll was not found" then
                "Remix ran, but the DLSS 5 snippet never started - the model could not be loaded."
            elif text.Contains "NVIDIA DLSS-NR inactive:" then "Remix ran with the neural pass switched off - turn it on in Alt+X -> Neural Rendering."
            elif text.Contains "NVIDIA DLSS-NR skipped:" then "Remix ran; the neural pass skipped its frames."
            else "Remix ran; the neural pass was never even attempted."
