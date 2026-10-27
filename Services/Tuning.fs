namespace DLSS_5_MANAGER.Services

open System
open System.IO
open System.IO.Compression
open System.Net.Http
open System.Text.Json
open System.Text.RegularExpressions
open DLSS_5_MANAGER.Models

/// After-install tuning for one game:
/// - Quality / Balanced / Performance: how much of the picture the model
///   works on (the feeder's work_resolution, OptiScaler's DLSS-NR
///   WorkingScale). The cost grows with the square of it.
/// - Before / after: the last two ReShade screenshots taken a few minutes
///   apart, put side by side in one PNG.
/// - RTX 40 multi-frame generation: dashdogy's RTX40MFG-Unlock, loaded by
///   Ultimate ASI Loader, for 3x-4x frame generation on RTX 40 cards.
module Tuning =

    type Preset =
        | Quality
        | Balanced
        | Performance

    let presetValues p =
        match p with
        | Quality -> 100, "1.0"
        | Balanced -> 85, "0.75"
        | Performance -> 70, "0.5"

    let private exeDir (game: GameItem) = Path.GetDirectoryName(game.TargetExecutablePath)

    /// Sets key=value in a plain key=value file, keeping every other line.
    let private setPlain (path: string) (key: string) (value: string) =
        let lines = if File.Exists path then File.ReadAllLines path |> List.ofArray else []
        let isKey (l: string) = Regex.IsMatch(l, @"^\s*" + Regex.Escape key + @"\s*=", RegexOptions.IgnoreCase)
        let updated = if lines |> List.exists isKey then lines |> List.map (fun l -> if isKey l then key + "=" + value else l) else lines @ [ key + "=" + value ]
        File.WriteAllLines(path, updated)

    let private setInSection (path: string) (section: string) (key: string) (value: string) =
        let lines = if File.Exists path then File.ReadAllLines path |> List.ofArray else []
        let header = "[" + section + "]"
        match lines |> List.tryFindIndex (fun l -> l.Trim().Equals(header, StringComparison.OrdinalIgnoreCase)) with
        | None -> File.WriteAllLines(path, lines @ [ ""; header; key + "=" + value ])
        | Some start ->
            let stop =
                lines |> List.skip (start + 1) |> List.tryFindIndex (fun l -> l.TrimStart().StartsWith "[")
                |> Option.map (fun i -> start + 1 + i) |> Option.defaultValue lines.Length
            match [ start + 1 .. stop - 1 ] |> List.tryFind (fun i -> lines.[i].TrimStart().StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) with
            | Some i -> File.WriteAllLines(path, lines |> List.mapi (fun j l -> if j = i then key + "=" + value else l))
            | None -> File.WriteAllLines(path, List.truncate (start + 1) lines @ [ key + "=" + value ] @ List.skip (start + 1) lines)

    /// The preset the game's files say it is on, if any.
    let currentPreset (game: GameItem) : Preset option =
        try
            let dir = exeDir game
            let feed = Path.Combine(dir, "dlss5-feed.cfg")
            let opti = Path.Combine(dir, "OptiScaler.ini")
            let pct =
                if File.Exists feed then
                    let m = Regex.Match(File.ReadAllText feed, @"^\s*work_resolution\s*=\s*([\d.,]+)", RegexOptions.Multiline)
                    if m.Success then Some(float (m.Groups.[1].Value.Replace(',', '.'))) else None
                elif File.Exists opti then
                    let m = Regex.Match(File.ReadAllText opti, @"^\s*WorkingScale\s*=\s*([\d.,]+)", RegexOptions.Multiline)
                    if m.Success then Some(100.0 * float (m.Groups.[1].Value.Replace(',', '.'))) else None
                else None
            match pct with
            | Some v when v >= 95.0 -> Some Quality
            | Some v when v >= 78.0 -> Some Balanced
            | Some _ -> Some Performance
            | None -> None
        with _ -> None

    let applyPreset (game: GameItem) (p: Preset) : Result<string, string> =
        try
            let dir = exeDir game
            let feed = Path.Combine(dir, "dlss5-feed.cfg")
            let opti = Path.Combine(dir, "OptiScaler.ini")
            let pct, scale = presetValues p
            let mutable wrote = false
            if File.Exists feed || File.Exists(Path.Combine(dir, "dlss5-feed.addon64")) || File.Exists(Path.Combine(dir, "dlss5-feed.addon32")) then
                setPlain feed "work_resolution" (string pct)
                wrote <- true
            if File.Exists opti then
                setInSection opti "DlssNr" "WorkingScale" scale
                wrote <- true
            if wrote then Ok(sprintf "%A: the model works on %d%% of the picture. It takes effect the next time the game starts." p pct)
            else Error "This route has no setting for how much of the picture the model works on."
        with ex -> Error ex.Message

    // ------------------------------------------------------- before / after

    /// The last two screenshots taken within five minutes of each other,
    /// oldest first: press F6 to switch the pass, shoot, switch, shoot.
    let screenshotPair (game: GameItem) : (string * string) option =
        let dir = exeDir game
        let savePath =
            try
                let ini = File.ReadAllText(Path.Combine(dir, "ReShade.ini"))
                let m = Regex.Match(ini, @"\[SCREENSHOT\][^\[]*?^\s*SavePath\s*=\s*(.*)$", RegexOptions.Multiline ||| RegexOptions.Singleline ||| RegexOptions.IgnoreCase)
                if m.Success && m.Groups.[1].Value.Trim() <> "" then
                    let v = m.Groups.[1].Value.Trim()
                    if Path.IsPathRooted v then v else Path.GetFullPath(Path.Combine(dir, v))
                else dir
            with _ -> dir
        let shots =
            [ dir; savePath ]
            |> List.distinct
            |> List.collect (fun d ->
                try
                    Directory.GetFiles(d)
                    |> Array.filter (fun f ->
                        let e = Path.GetExtension(f).ToLowerInvariant()
                        (e = ".png" || e = ".jpg" || e = ".jpeg" || e = ".bmp")
                        && not (Path.GetFileName(f).StartsWith("dlss5_compare", StringComparison.OrdinalIgnoreCase)))
                    |> List.ofArray
                with _ -> [])
            |> List.distinct
            |> List.sortByDescending File.GetLastWriteTimeUtc
        match shots with
        | newest :: older :: _ when (File.GetLastWriteTimeUtc newest - File.GetLastWriteTimeUtc older).TotalSeconds <= 300.0 -> Some(older, newest)
        | _ -> None

    /// Side by side in one PNG beside the game, each half at most 1920 wide.
    let exportComparison (game: GameItem) : Result<string, string> =
        match screenshotPair game with
        | None -> Error "No pair of screenshots found. In the game: F6 to switch DLSS 5, Print Screen, F6 again, Print Screen - then press this."
        | Some(a, b) ->
            try
                use left = SkiaSharp.SKBitmap.Decode(a)
                use right = SkiaSharp.SKBitmap.Decode(b)
                let fit (bmp: SkiaSharp.SKBitmap) =
                    let s = min 1.0 (1920.0 / float bmp.Width)
                    int (float bmp.Width * s), int (float bmp.Height * s)
                let (lw, lh), (rw, rh) = fit left, fit right
                use surface = SkiaSharp.SKSurface.Create(SkiaSharp.SKImageInfo(lw + rw, max lh rh))
                let canvas = surface.Canvas
                canvas.Clear(SkiaSharp.SKColors.Black)
                canvas.DrawBitmap(left, SkiaSharp.SKRect(0.0f, 0.0f, float32 lw, float32 lh))
                canvas.DrawBitmap(right, SkiaSharp.SKRect(float32 lw, 0.0f, float32 (lw + rw), float32 rh))
                use image = surface.Snapshot()
                use data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100)
                let out = Path.Combine(exeDir game, sprintf "dlss5_compare_%s.png" (DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss")))
                use fs = File.Create(out)
                data.SaveTo(fs)
                Ok out
            with ex -> Error ex.Message

    // ------------------------------------------------------------ RTX 40 MFG

    let private rtx40 =
        lazy
            (let gpu = (try (SystemSpecs.detect ()).Gpu with _ -> "").ToUpperInvariant()
             Regex.IsMatch(gpu, @"RTX\s*40\d\d") || gpu.Contains "RTX 2000 ADA")

    /// Read once: the card does not change while SUITE runs.
    let isRtx40 () = rtx40.Value

    let hasGameFrameGen (game: GameItem) =
        try
            [ "nvngx_dlssg.dll"; "sl.dlss_g.dll" ]
            |> List.exists (fun n -> Directory.EnumerateFiles(game.InstallDirectory, n, SearchOption.AllDirectories) |> Seq.isEmpty |> not)
        with _ -> false

    let private mfgRecord (game: GameItem) = Path.Combine(exeDir game, "dlss5-suite-mfg.json")
    let isMfgInstalled (game: GameItem) = File.Exists(mfgRecord game)

    let private http =
        lazy
            (let h = new HttpClient(Timeout = TimeSpan.FromMinutes(10.0))
             h.DefaultRequestHeaders.UserAgent.ParseAdd("DLSS5-SUITE")
             h)

    let private download (url: string) (target: string) =
        use s = http.Value.GetStreamAsync(url).Result
        use f = File.Create(target)
        s.CopyTo(f)

    /// RTX40MFG-Unlock beside the exe, plus Ultimate ASI Loader as
    /// version.dll (winmm.dll when the game already has a version.dll) so the
    /// .asi is loaded. Needs the game's own DLSS Frame Generation.
    let installMfg (game: GameItem) (progress: string -> unit) : Result<string, string> =
        try
            if not (isRtx40 ()) then failwith "this unlock is for RTX 40 cards only"
            if not (hasGameFrameGen game) then failwith "the game has no DLSS Frame Generation of its own for the unlock to extend"
            let dir = exeDir game
            let cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DLSS5Suite", "Downloads", "mfg")
            Directory.CreateDirectory(cache) |> ignore
            progress "Finding the newest RTX40MFG-Unlock..."
            let json = http.Value.GetStringAsync("https://api.github.com/repos/dashdogy/RTX40MFG-Unlock/releases?per_page=20").Result
            use doc = JsonDocument.Parse(json)
            let asset =
                doc.RootElement.EnumerateArray()
                |> Seq.collect (fun r -> r.GetProperty("assets").EnumerateArray())
                |> Seq.tryFind (fun a ->
                    let n = a.GetProperty("name").GetString().ToLowerInvariant()
                    n.StartsWith "universal-rtx-40-mfg-unlock" && n.EndsWith ".zip")
            match asset with
            | None -> failwith "no universal RTX 40 MFG release was found"
            | Some a ->
                let zip = Path.Combine(cache, a.GetProperty("name").GetString())
                progress "Downloading RTX40MFG-Unlock..."
                download (a.GetProperty("browser_download_url").GetString()) zip
                let loaderZip = Path.Combine(cache, "Ultimate-ASI-Loader_x64.zip")
                progress "Downloading Ultimate ASI Loader..."
                download "https://github.com/ThirteenAG/Ultimate-ASI-Loader/releases/latest/download/Ultimate-ASI-Loader_x64.zip" loaderZip
                let placed = ResizeArray<string>()
                use archive = ZipFile.OpenRead(zip)
                for e in archive.Entries do
                    let ext = Path.GetExtension(e.Name).ToLowerInvariant()
                    if ext = ".dll" || ext = ".asi" || ext = ".addon64" || ext = ".ini" then
                        let dest = Path.Combine(dir, e.Name)
                        if File.Exists dest then failwithf "%s is already in the game folder" e.Name
                        e.ExtractToFile(dest)
                        placed.Add dest
                let loaderName = if File.Exists(Path.Combine(dir, "version.dll")) then "winmm.dll" else "version.dll"
                use loader = ZipFile.OpenRead(loaderZip)
                match loader.Entries |> Seq.tryFind (fun e -> e.Name.Equals("dinput8.dll", StringComparison.OrdinalIgnoreCase)) with
                | None -> failwith "Ultimate ASI Loader's zip had no dinput8.dll"
                | Some e ->
                    let dest = Path.Combine(dir, loaderName)
                    if File.Exists dest then failwithf "%s is already in the game folder" loaderName
                    e.ExtractToFile(dest)
                    placed.Add dest
                let iniPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(loaderName) + ".ini")
                if not (File.Exists iniPath) then
                    File.WriteAllLines(iniPath, [| "[GlobalSets]"; "LoadPlugins=1"; "LoadFromScriptsOnly=1"; "DontLoadFromDllMain=0"; "ForceEntryPointHook=0"; "LoadExtraPlugins=RTX40MFG.asi" |])
                    placed.Add iniPath
                File.WriteAllText(mfgRecord game, JsonSerializer.Serialize(placed.ToArray()))
                Ok "RTX 40 multi-frame generation is in. Turn DLSS Frame Generation on in the game, then pick the multiplier in the RTX40MFG tab of the ReShade overlay (Home)."
        with ex ->
            AppLog.error ("RTX 40 MFG in " + game.Title) ex
            Error ex.Message

    let removeMfg (game: GameItem) : Result<string, string> =
        try
            let r = mfgRecord game
            if File.Exists r then
                for f in JsonSerializer.Deserialize<string[]>(File.ReadAllText r) do
                    if File.Exists f then File.Delete f
                File.Delete r
            Ok "RTX 40 multi-frame generation is removed."
        with ex -> Error ex.Message
