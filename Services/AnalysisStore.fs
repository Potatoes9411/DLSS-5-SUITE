namespace DLSS_5_MANAGER.Services

open System
open System.IO
open System.Text.Json
open System.Collections.Generic
open DLSS_5_MANAGER.Models

/// Persistent per-game deep-scan cache.
///
/// Everything the Manage sheet and the installer need - the real executable,
/// where the DLSS and Streamline runtimes live, their versions, whether ReShade
/// is present - is computed once (during the library scan, or the moment a game
/// is added by hand) and written to disk. Opening a game afterwards is instant
/// and the installer never has to walk the folder tree again.
module AnalysisStore =

    [<CLIMutable>]
    type GameAnalysis =
        { GameId: string
          ExecutablePath: string
          ExecutableFolder: string
          ReShadeInstalled: bool
          DlssText: string
          DlssDirs: string[]
          StreamlineText: string
          StreamlineDirs: string[]
          /// nvngx_dlssnr.dll found -> DLSS 5 is on this game, whoever put it there.
          Dlss5Present: bool
          Dlss5Complete: bool
          Dlss5Missing: string[]
          /// True when this app performed the install and holds the backups.
          ModInstalled: bool
          /// "dx12" / "vulkan" / "dx11" / "dx10" / "dx9" / "opengl", or "".
          ///
          /// Cached because the card grid shows it on every tile and working
          /// it out means reading the executable's import tables and scanning
          /// its string data - a few hundred milliseconds a game, which is
          /// fine once during the scan and unacceptable per repaint.
          GraphicsApi: string
          /// When this game was actually put on the machine, taken from the
          /// install folder's creation time.
          ///
          /// Not the Steam manifest's LastUpdated, which is what the library
          /// sorted on first and which moves every time a game patches - so a
          /// title installed two years ago and updated last night sorted as
          /// the newest thing you own. The folder's creation time only
          /// changes when the game is actually installed.
          InstalledAtUtc: string
          /// The game's release date, from the Steam store. Empty when it is
          /// not a Steam title or the lookup failed; sorted last in that case
          /// rather than treated as ancient.
          ReleaseDateUtc: string
          AnalyzedAtUtc: string }

    let private storeLock = obj ()
    let private store = Dictionary<string, GameAnalysis>(StringComparer.OrdinalIgnoreCase)
    let mutable private isLoaded = false

    let private storePath () =
        let dir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DLSS5Suite")

        Directory.CreateDirectory(dir) |> ignore
        // v4: the record gained InstalledAtUtc and ReleaseDateUtc.
        // v3: the record gained GraphicsApi. An entry written by an older
        // build has no value for it, and a card cannot show an API that was
        // never worked out - so the file name carries the shape, and a new
        // build re-analyses instead of reading a record it cannot trust.
        Path.Combine(dir, "analysis_cache_v4.json")

    let key (game: GameItem) =
        if String.IsNullOrWhiteSpace(game.AppId) then game.Title else game.AppId

    // =====================================================================
    // PERSISTENCE
    // =====================================================================
    let private loadUnsafe () =
        if not isLoaded then
            isLoaded <- true

            try
                let path = storePath ()

                if File.Exists(path) then
                    let options = JsonSerializerOptions()
                    options.PropertyNameCaseInsensitive <- true
                    let items = JsonSerializer.Deserialize<GameAnalysis[]>(File.ReadAllText(path), options)

                    if not (isNull (box items)) then
                        for item in items do
                            if not (String.IsNullOrWhiteSpace(item.GameId)) then
                                store.[item.GameId] <- item
            with _ ->
                ()

    let private saveUnsafe () =
        try
            let options = JsonSerializerOptions()
            options.WriteIndented <- true
            let payload = store.Values |> Seq.toArray
            File.WriteAllText(storePath (), JsonSerializer.Serialize(payload, options))
        with _ ->
            ()

    let load () = lock storeLock (fun () -> loadUnsafe ())

    let save () =
        lock storeLock (fun () ->
            loadUnsafe ()
            saveUnsafe ())

    let tryGet (game: GameItem) : GameAnalysis option =
        lock storeLock (fun () ->
            loadUnsafe ()

            match store.TryGetValue(key game) with
            | true, v -> Some v
            | _ -> None)

    let put (analysis: GameAnalysis) =
        lock storeLock (fun () ->
            loadUnsafe ()
            store.[analysis.GameId] <- analysis)

    let remove (game: GameItem) =
        lock storeLock (fun () ->
            loadUnsafe ()
            store.Remove(key game) |> ignore)

    let clear () =
        lock storeLock (fun () ->
            isLoaded <- true
            store.Clear()
            saveUnsafe ())

    // =====================================================================
    // ANALYSIS
    // =====================================================================
    let private describe (folders: GameAnalyzer.ModFolder list) (label: string) =
        match folders with
        | [] -> sprintf "No %s runtime found" label
        | _ ->
            let newest =
                folders
                |> List.map GameAnalyzer.folderVersion
                |> List.sortWith (fun a b -> GameAnalyzer.compareVer b a)
                |> List.head

            sprintf "v%s in %d folder(s)" (GameAnalyzer.verText newest) folders.Length

    /// Full deep scan of a single game. This is the expensive part, so it only
    /// runs on first discovery, on an explicit re-scan, or after an install.
     // =====================================================================
    // WHEN IT ARRIVED, AND WHEN IT CAME OUT
    // =====================================================================
    /// When the game was installed, from the folder's creation time.
    let private installedAt (game: GameItem) : string =
        try
            if String.IsNullOrWhiteSpace(game.InstallDirectory)
               || not (Directory.Exists(game.InstallDirectory)) then
                ""
            else
                Directory.GetCreationTimeUtc(game.InstallDirectory).ToString("o")
        with _ ->
            ""

    /// Release date, from the Steam store, for titles that have an AppId.
    ///
    /// The date is nowhere on disk - the manifest carries when the game was
    /// installed and patched, and nothing about when it came out - so this is
    /// the one thing here that needs the network. It runs once per game and is
    /// then held in this cache, so a rescan never asks again.
    let private releaseDate (client: Net.Http.HttpClient) (game: GameItem) : string =
        // GameItem.AppId is namespaced by launcher - "steam_211670", "epic_...",
        // "gog_...", "repack_..." - because the library holds titles from all of
        // them under one id. The store only knows the number, and only for its
        // own games, so anything else has no release date to look up.
        let steamId =
            if not (isNull (box game.AppId))
               && game.AppId.StartsWith("steam_", StringComparison.OrdinalIgnoreCase) then
                game.AppId.Substring(6)
            else
                ""

        if String.IsNullOrWhiteSpace(steamId) then
            ""
        else
            try
                let url =
                    sprintf
                        // filters=release_date, not "basic". The basic filter
                        // returns the name and little else - release_date is not
                        // in it, so the lookup came back empty for every game.
                        "https://store.steampowered.com/api/appdetails?appids=%s&filters=release_date&l=english"
                        steamId

                let json = client.GetStringAsync(url).GetAwaiter().GetResult()

                use doc = Text.Json.JsonDocument.Parse(json)

                match doc.RootElement.TryGetProperty(steamId) with
                | true, entry ->
                    match entry.TryGetProperty("data") with
                    | true, data ->
                        match data.TryGetProperty("release_date") with
                        | true, rd ->
                            match rd.TryGetProperty("date") with
                            | true, d when d.ValueKind = Text.Json.JsonValueKind.String ->
                                // Steam writes "8 Sep, 2026", "Sep 2026", or
                                // "Coming soon". Only a full date is useful, and
                                // anything else is left empty so it sorts last
                                // rather than landing on the first of a month it
                                // was never released in.
                                match DateTime.TryParse(
                                        d.GetString(),
                                        Globalization.CultureInfo.InvariantCulture,
                                        Globalization.DateTimeStyles.AssumeUniversal
                                        ||| Globalization.DateTimeStyles.AdjustToUniversal) with
                                | true, parsed -> parsed.ToString("o")
                                | _ -> ""
                            | _ -> ""
                        | _ -> ""
                    | _ -> ""
                | _ -> ""
            with _ ->
                ""

    /// One client for the whole run, with a short leash: a release date is a
    /// nicety and must never hold up a scan.
    let private storeClient =
        lazy
            (let c = new Net.Http.HttpClient()
             c.Timeout <- TimeSpan.FromSeconds(4.0)
             c.DefaultRequestHeaders.UserAgent.ParseAdd("DLSS5Suite/1.2 (+https://potatoes-dev.com)")
             c)

    let analyze (game: GameItem) : GameAnalysis =
        let exePath =
            if not (String.IsNullOrWhiteSpace(game.TargetExecutablePath))
               && File.Exists(game.TargetExecutablePath) then
                game.TargetExecutablePath
            else
                let (resolved, _) =
                    GameAnalyzer.resolveGameExecutable game.InstallDirectory game.Title

                resolved

        let folder =
            if String.IsNullOrWhiteSpace(exePath) then
                game.InstallDirectory
            else
                try Path.GetDirectoryName(exePath)
                with _ -> game.InstallDirectory

        let dlssFolders = GameAnalyzer.findDlssFolders game.InstallDirectory exePath
        let streamlineFolders = GameAnalyzer.findStreamlineFolders game.InstallDirectory exePath

        let dlssDirs = dlssFolders |> List.map (fun f -> f.Directory) |> List.toArray
        let streamlineDirs = streamlineFolders |> List.map (fun f -> f.Directory) |> List.toArray
        let dlss5 = ModInstaller.inspect game exePath dlssDirs streamlineDirs

        { GameId = key game
          ExecutablePath = exePath
          ExecutableFolder = folder
          ReShadeInstalled =
            not (String.IsNullOrWhiteSpace(exePath)) && GameAnalyzer.isReShadeInstalled exePath
          DlssText =
            if String.IsNullOrWhiteSpace(exePath) then "No executable detected"
            else describe dlssFolders "DLSS"
          DlssDirs = dlssDirs
          StreamlineText =
            if String.IsNullOrWhiteSpace(exePath) then "No executable detected"
            else describe streamlineFolders "Streamline"
          StreamlineDirs = streamlineDirs
          Dlss5Present = dlss5.Present
          Dlss5Complete = dlss5.Complete
          Dlss5Missing = dlss5.Missing
          ModInstalled = dlss5.ManagedByApp
          GraphicsApi = GameAnalyzer.detectGraphicsApi exePath
          InstalledAtUtc = installedAt game
          ReleaseDateUtc = releaseDate (storeClient.Force()) game
          AnalyzedAtUtc = DateTime.UtcNow.ToString("o") }

    /// Manual add: the user already pointed at the executable, so there is
    /// nothing to resolve and no library to walk. We look at that file's own
    /// folder (two levels deep at most), which keeps the UI responsive on
    /// multi-hundred-gigabyte installs instead of freezing on a full tree walk.
    let analyzeExecutableOnly (game: GameItem) : GameAnalysis =
        let exePath = game.TargetExecutablePath

        let exeDir =
            try
                if String.IsNullOrWhiteSpace(exePath) then game.InstallDirectory
                else Path.GetDirectoryName(exePath)
            with _ ->
                game.InstallDirectory

        let dlssFolders = GameAnalyzer.findModFoldersDepth exeDir GameAnalyzer.dlssFileNames 2
        let streamlineFolders = GameAnalyzer.findModFoldersDepth exeDir GameAnalyzer.streamlineFileNames 2

        let dlssDirs = dlssFolders |> List.map (fun f -> f.Directory) |> List.toArray
        let streamlineDirs = streamlineFolders |> List.map (fun f -> f.Directory) |> List.toArray
        let dlss5 = ModInstaller.inspect game exePath dlssDirs streamlineDirs

        { GameId = key game
          ExecutablePath = exePath
          ExecutableFolder = exeDir
          ReShadeInstalled =
            not (String.IsNullOrWhiteSpace(exePath)) && GameAnalyzer.isReShadeInstalled exePath
          DlssText = describe dlssFolders "DLSS"
          DlssDirs = dlssDirs
          StreamlineText = describe streamlineFolders "Streamline"
          StreamlineDirs = streamlineDirs
          Dlss5Present = dlss5.Present
          Dlss5Complete = dlss5.Complete
          Dlss5Missing = dlss5.Missing
          ModInstalled = dlss5.ManagedByApp
          GraphicsApi = GameAnalyzer.detectGraphicsApi exePath
          InstalledAtUtc = installedAt game
          ReleaseDateUtc = releaseDate (storeClient.Force()) game
          AnalyzedAtUtc = DateTime.UtcNow.ToString("o") }

    /// Analyze and persist a single game.
    let refresh (game: GameItem) : GameAnalysis =
        let analysis = analyze game
        put analysis
        save ()
        analysis

    /// Exe-only analysis for a hand-picked executable, persisted immediately.
    let refreshExecutableOnly (game: GameItem) : GameAnalysis =
        let analysis = analyzeExecutableOnly game
        put analysis
        save ()
        analysis

    /// Analyze a whole batch (library scan / folder import) and persist once.
    /// Analyzes games until done or until asked to stop. What was analyzed
    /// before the stop is kept and saved - a stopped scan is not thrown away.
    let refreshManyUntil (stopRequested: unit -> bool) (games: GameItem list) (onProgress: int -> int -> string -> unit) : bool =
        let total = games.Length
        let mutable index = 0
        let mutable stopped = false

        for game in games do
            if not stopped then
                if stopRequested () then
                    stopped <- true
                else
                    index <- index + 1
                    onProgress index total game.Title

                    try
                        put (analyze game)
                    with ex ->
                        AppLog.error ("Analyzing " + game.Title + " failed") ex

        save ()
        not stopped

    let refreshMany (games: GameItem list) (onProgress: int -> int -> string -> unit) : unit =
        refreshManyUntil (fun () -> false) games onProgress |> ignore

    /// Only analyze the games we have never seen before - used after adding
    /// folders so existing entries are not rescanned.
    let refreshMissing (games: GameItem list) (onProgress: int -> int -> string -> unit) : unit =
        let pending = games |> List.filter (fun g -> (tryGet g).IsNone)
        if not pending.IsEmpty then refreshMany pending onProgress
