namespace DLSS_5_MANAGER.Services

open System
open System.IO

/// SUITE's own clutter, for Space Press's CLEAN UP tab: caches an older build
/// left behind, the rotated log, download caches it can fetch again, leftover
/// extraction folders. Space Press squishes games; this only ever looks inside
/// SUITE's own data folders.
///
/// What is never offered, by design:
/// - anything outside SUITE's data folders (no game file, ever);
/// - install records and backups (Installs, Backups, GtaEnhanced\backups) -
///   they are what Remove puts a game back from;
/// - payloads, builds and tools SUITE keeps (mod files, OptiScaler builds,
///   Vault, NeuralScreen, own frame generation, video tools);
/// - any file or folder that holds an nvngx_dlssnr.dll - or anything named
///   like one - wherever it is, so a model the user modified can never go.
module SuiteCleaner =

    type CleanItem =
        { Id: string
          Label: string
          Detail: string
          /// Full paths to remove: files or folders.
          Paths: string list
          Bytes: int64
          /// Ticked when the tab opens.
          DefaultOn: bool }

    let suiteRoot () =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DLSS5Suite")

    let formatBytes (bytes: int64) =
        if bytes >= 1073741824L then sprintf "%.1f GB" (float bytes / 1073741824.0)
        elif bytes >= 1048576L then sprintf "%.0f MB" (float bytes / 1048576.0)
        elif bytes >= 1024L then sprintf "%.0f KB" (float bytes / 1024.0)
        else sprintf "%d B" bytes

    /// A model by any name SUITE or a user would give it.
    let private looksLikeModel (name: string) =
        let n = name.ToLowerInvariant()
        n.Contains("dlssnr")

    /// Whether a path is, or holds, something that could be the user's model.
    let isProtected (path: string) =
        try
            if File.Exists(path) then
                looksLikeModel (Path.GetFileName(path))
            elif Directory.Exists(path) then
                looksLikeModel (Path.GetFileName(path))
                || Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                   |> Seq.exists (fun f -> looksLikeModel (Path.GetFileName(f)))
            else
                false
        with _ ->
            // Could not look inside: treat it as untouchable.
            true

    let private isInside (root: string) (path: string) =
        try
            let r = Path.GetFullPath(root).TrimEnd('\\', '/') + "\\"
            Path.GetFullPath(path).StartsWith(r, StringComparison.OrdinalIgnoreCase)
        with _ ->
            false

    let sizeOf (path: string) : int64 =
        try
            if File.Exists(path) then FileInfo(path).Length
            elif Directory.Exists(path) then
                Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                |> Seq.sumBy (fun f -> try FileInfo(f).Length with _ -> 0L)
            else
                0L
        with _ ->
            0L

    let private existing (paths: string list) =
        paths |> List.filter (fun p -> File.Exists(p) || Directory.Exists(p))

    /// Files directly in `dir` older than `cutoff`.
    let private filesOlderThan (dir: string) (cutoff: DateTime) =
        try
            if Directory.Exists(dir) then
                Directory.GetFiles(dir)
                |> Array.filter (fun f -> File.GetLastWriteTimeUtc(f) < cutoff)
                |> Array.toList
            else
                []
        with _ ->
            []

    /// Everything that could go, measured, under SUITE's data folder
    /// (`root`) and its log folder (`logRoot`). Only items with something to
    /// remove are returned; protected paths are dropped, never offered.
    let findIn (root: string) (logRoot: string) (now: DateTime) : CleanItem list =
        let at (parts: string list) = Path.Combine(Array.ofList (root :: parts))
        let weekAgo = now.AddDays(-7.0)
        let hourAgo = now.AddHours(-1.0)

        let staleCaches =
            [ "library_cache.json"; "library_cache_v2.json"
              "analysis_cache_v1.json"; "analysis_cache_v2.json"; "analysis_cache_v3.json" ]
            |> List.map (fun n -> at [ n ])

        let oldLogs =
            [ Path.Combine(logRoot, "suite.old.log") ]
            @ (filesOlderThan logRoot weekAgo |> List.filter (fun f -> not (Path.GetFileName(f).Equals("suite.log", StringComparison.OrdinalIgnoreCase))))

        let downloads =
            [ for sub in [ "REFramework"; "remix"; "mfg" ] do
                  yield! filesOlderThan (at [ "Downloads"; sub ]) weekAgo ]

        // Unpacked archives an interrupted install left behind. Only the
        // extract-* folders - GtaEnhanced\backups is the way back and stays.
        let leftovers =
            let gta = at [ "Downloads"; "GtaEnhanced" ]
            let extracts =
                try
                    if Directory.Exists(gta) then
                        Directory.GetDirectories(gta, "extract-*")
                        |> Array.filter (fun d -> Directory.GetLastWriteTimeUtc(d) < hourAgo)
                        |> Array.toList
                    else []
                with _ -> []
            extracts @ filesOlderThan (at [ "Compatibility"; "LosslessScaling"; ".downloads" ]) weekAgo

        let community = [ at [ "Cache"; "Community" ]; at [ "Cache"; "Pulse" ] ]
        // Icons cached by builds before the sharper "_hq" extraction. The
        // current ones stay: cards point straight at them.
        let oldIcons =
            try
                let dir = at [ "Cache"; "Icons" ]
                if Directory.Exists(dir) then
                    Directory.GetFiles(dir, "*.png")
                    |> Array.filter (fun f -> not (f.EndsWith("_hq.png", StringComparison.OrdinalIgnoreCase)))
                    |> Array.toList
                else []
            with _ -> []

        let item id label detail paths defaultOn =
            let safe =
                existing paths
                |> List.filter (fun p -> (isInside root p || isInside logRoot p) && not (isProtected p))

            { Id = id
              Label = label
              Detail = detail
              Paths = safe
              Bytes = safe |> List.sumBy sizeOf
              DefaultOn = defaultOn }

        [ item "stale" "Caches from older builds" "Library and analysis caches a newer build replaced. Nothing reads them any more." staleCaches true
          item "logs" "Old logs" "The rotated suite.old.log and logs older than a week. Today's log stays." oldLogs true
          item "downloads" "Old downloads" "Installers SUITE downloaded more than a week ago (REFramework, RTX Remix, MFG unlock). Fetched again when needed." downloads true
          item "leftovers" "Install leftovers" "Unpacked archives an interrupted install left behind." leftovers true
          item "community" "Community cache" "Cached Community pictures and posts. The feature is off in this build." community true
          item "icons" "Old game icons" "Small icons an older build cached. Nothing shows them any more." oldIcons true ]
        |> List.filter (fun i -> not i.Paths.IsEmpty && i.Bytes > 0L)

    let find () = findIn (suiteRoot ()) (AppLog.logFolder ()) DateTime.UtcNow

    /// Removes the items' paths and returns the bytes freed. Every path is
    /// checked again right before it goes: still inside SUITE's folders and
    /// still holding nothing that looks like a model.
    let cleanIn (root: string) (logRoot: string) (items: CleanItem list) : int64 =
        let mutable freed = 0L

        for i in items do
            for p in i.Paths do
                if (isInside root p || isInside logRoot p) && not (isProtected p) then
                    let size = sizeOf p
                    try
                        if File.Exists(p) then
                            File.Delete(p)
                            freed <- freed + size
                        elif Directory.Exists(p) then
                            Directory.Delete(p, true)
                            freed <- freed + size
                    with ex ->
                        AppLog.error ("Clean up: " + p) ex

        freed

    let clean (items: CleanItem list) = cleanIn (suiteRoot ()) (AppLog.logFolder ()) items
