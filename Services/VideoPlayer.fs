namespace DLSS_5_MANAGER.Services

open System
open System.Diagnostics
open System.IO
open System.IO.Compression
open System.Net.Http
open System.Runtime.InteropServices
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open DLSS_5_MANAGER.Models

/// DLSS 5 on video: a portable MPC-HC is set up as if it were a game, and
/// the normal DX11 + Feeder install goes into it. Anything it plays - a file,
/// a YouTube link (through yt-dlp beside it), a webcam or a part of the
/// screen (both streamed in by ffmpeg over a local UDP port) - then gets the
/// neural pass. F6 toggles the pass, as in a game.
///
/// MPC-HC must use its D3D11 "MPC Video Renderer": the default EVR is D3D9,
/// which the feeder cannot reach. Home is unbound in the player because it is
/// ReShade's overlay key.
module VideoPlayer =

    [<Literal>]
    let PlayerExe = "mpc-hc64.exe"

    [<Literal>]
    let private WebcamPort = 47321

    let private webcamUrl = sprintf "udp://@127.0.0.1:%d" WebcamPort
    let private feedTarget = sprintf "udp://127.0.0.1:%d?pkt_size=1316" WebcamPort

    let defaultFolder () =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "DLSS5 Player")

    let private prefPath () =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DLSS5Suite", "video_player_dir.txt")

    let folder () =
        try
            let p = prefPath ()
            if File.Exists p then
                let d = File.ReadAllText(p).Trim()
                if d <> "" then d else defaultFolder ()
            else defaultFolder ()
        with _ -> defaultFolder ()

    let exePath () = Path.Combine(folder (), PlayerExe)
    let isSetUp () = File.Exists(exePath ())
    let private tools () = Path.Combine(folder (), "tools")

    /// The player as a library entry, so the ordinary install, Did it work?
    /// and uninstall all apply to it.
    let asGame () : GameItem =
        { AppId = "video_mpchc"
          Title = "Video player (MPC-HC)"
          LauncherTypeName = "VIDEO"
          InstallDirectory = folder ()
          TargetExecutablePath = exePath ()
          TargetExecutableSize = (try FileInfo(exePath ()).Length with _ -> 0L)
          LocalBannerPath = ""
          LastManifestTimestamp = DateTime.UtcNow.Ticks
          UpscaleStatus = "Direct3D 11"
          DlssVersion = ""
          FsrVersion = ""
          XessVersion = "" }

    // ------------------------------------------------------------- download

    let private http =
        lazy
            (let h = new HttpClient(Timeout = TimeSpan.FromMinutes(15.0))
             h.DefaultRequestHeaders.UserAgent.ParseAdd("DLSS5-SUITE")
             h)

    /// The browser_download_url of the first asset of a GitHub release whose
    /// name passes the test.
    let internal releaseAsset (api: string) (pick: string -> bool) =
        let json = http.Value.GetStringAsync(api).Result
        use doc = JsonDocument.Parse(json)
        let tag = match doc.RootElement.TryGetProperty("tag_name") with | true, v -> v.GetString() | _ -> "latest"
        doc.RootElement.GetProperty("assets").EnumerateArray()
        |> Seq.tryPick (fun a ->
            let name = a.GetProperty("name").GetString()
            if pick name then Some(tag, name, a.GetProperty("browser_download_url").GetString()) else None)

    let internal downloadTo (url: string) (target: string) (progress: string -> unit) (label: string) =
        use resp = http.Value.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).Result
        resp.EnsureSuccessStatusCode() |> ignore
        let total = resp.Content.Headers.ContentLength |> Option.ofNullable |> Option.defaultValue 0L
        use src = resp.Content.ReadAsStreamAsync().Result
        let part = target + ".part"
        do
            use dst = File.Create(part)
            let buf = Array.zeroCreate<byte> (256 * 1024)
            let mutable read = src.Read(buf, 0, buf.Length)
            let mutable got = 0L
            let mutable lastPct = -1
            while read > 0 do
                dst.Write(buf, 0, read)
                got <- got + int64 read
                if total > 0L then
                    let pct = int (got * 100L / total)
                    if pct <> lastPct && pct % 5 = 0 then
                        lastPct <- pct
                        progress (sprintf "Downloading %s... %d%%" label pct)
                read <- src.Read(buf, 0, buf.Length)
        File.Move(part, target, true)

    /// Extracts a zip, dropping a single shared top folder and refusing any
    /// entry that would land outside the target.
    let internal extractFlat (zip: string) (target: string) (only: (string -> bool) option) =
        use archive = ZipFile.OpenRead(zip)
        let entries = archive.Entries |> Seq.filter (fun e -> e.Name <> "") |> List.ofSeq
        let prefix =
            let firsts = entries |> List.map (fun e -> e.FullName.Replace('\\', '/').Split('/').[0]) |> List.distinct
            if firsts.Length = 1 && entries |> List.forall (fun e -> e.FullName.Contains "/") then firsts.Head + "/" else ""
        let root = Path.GetFullPath(target).TrimEnd('\\') + "\\"
        for e in entries do
            let rel = e.FullName.Replace('\\', '/')
            let rel = if prefix <> "" && rel.StartsWith prefix then rel.Substring(prefix.Length) else rel
            if only |> Option.forall (fun f -> f rel) then
                let dest = Path.GetFullPath(Path.Combine(target, rel))
                if not (dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) then
                    failwithf "refused an archive entry outside the folder: %s" e.FullName
                Directory.CreateDirectory(Path.GetDirectoryName dest) |> ignore
                e.ExtractToFile(dest, true)

    // ------------------------------------------------------------ the player ini

    /// Writes the settings the neural pass needs into mpc-hc64.ini, byte-safe:
    /// keeps a BOM, drops blank lines (rewriting them had MPC-HC doubling them
    /// until it crashed), CRLF throughout. One player for everything: DLSS 5
    /// is brought up once in it (see warmUp) and every video after that is
    /// handed to the same window, where it starts at once.
    let writeIni (dir: string) =
        let path = Path.Combine(dir, "mpc-hc64.ini")
        let forced = [ "DSVidRen", "14"; "UpdaterAutoCheck", "0"; "AutoZoom", "0"; "AllowMultipleInstances", "0" ]
        let defaults = [ "RememberWindowPos", "1"; "YDLMaxHeight", "1440" ]
        let bom = Encoding.UTF8.GetPreamble()
        let raw = if File.Exists path then File.ReadAllBytes path else [||]
        let hadBom = raw.Length >= 3 && raw.[0] = 0xEFuy && raw.[1] = 0xBBuy && raw.[2] = 0xBFuy
        let text = Encoding.UTF8.GetString(if hadBom then raw.[3..] else raw)
        let lines = Regex.Split(text, "\r\n|\r|\n") |> Array.filter (fun l -> l.Trim() <> "") |> List.ofArray
        let out = ResizeArray<string>()
        let mutable inSettings = false
        let mutable sawSettings = false
        let mutable seen = Set.empty
        let mutable inCommands = false
        let flushSettings () =
            for (k, v) in forced @ defaults do
                if not (seen.Contains k) then out.Add(k + "=" + v)
        for l in lines do
            let trimmed = l.Trim()
            if trimmed.StartsWith "[" then
                if inSettings then flushSettings ()
                inSettings <- trimmed.Equals("[Settings]", StringComparison.OrdinalIgnoreCase)
                inCommands <- trimmed.Equals("[Commands2]", StringComparison.OrdinalIgnoreCase)
                if inSettings then sawSettings <- true
                out.Add l
            elif inSettings then
                let k = trimmed.Split('=').[0]
                if k.Equals("YDLExePath", StringComparison.OrdinalIgnoreCase) then ()
                else
                    match forced |> List.tryFind (fun (fk, _) -> fk.Equals(k, StringComparison.OrdinalIgnoreCase)) with
                    | Some(fk, fv) ->
                        seen <- seen.Add fk
                        out.Add(fk + "=" + fv)
                    | None ->
                        defaults |> List.tryFind (fun (dk, _) -> dk.Equals(k, StringComparison.OrdinalIgnoreCase)) |> Option.iter (fun (dk, _) -> seen <- seen.Add dk)
                        out.Add l
            elif inCommands && Regex.IsMatch(trimmed, @"^CommandMod\d+=996 ") then
                // 996 is "jump to start", bound to Home - ReShade's overlay key.
                let parts = trimmed.Split(' ')
                if parts.Length > 2 && parts.[2] <> "0" then
                    parts.[2] <- "0"
                    out.Add(String.Join(" ", parts))
                else out.Add l
            else out.Add l
        if inSettings then flushSettings ()
        if not sawSettings then
            out.Add "[Settings]"
            for (k, v) in forced @ defaults do out.Add(k + "=" + v)
        let body = Encoding.UTF8.GetBytes(String.Join("\r\n", out) + "\r\n")
        File.WriteAllBytes(path, (if hadBom || raw.Length = 0 then Array.append bom body else body))

    /// One "key=value" set inside an ini section (the section is added when
    /// missing); every other line is kept as it was.
    let withIniKey (text: string) (section: string) (key: string) (value: string) =
        let lines = Regex.Split(text, "\r\n|\r|\n") |> List.ofArray
        let lines = if not lines.IsEmpty && List.last lines = "" then List.take (lines.Length - 1) lines else lines
        let isSection (l: string) = l.Trim().StartsWith "["
        let isKey (l: string) = l.Trim().StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)
        match lines |> List.tryFindIndex (fun l -> l.Trim().Equals(section, StringComparison.OrdinalIgnoreCase)) with
        | None -> String.Join("\r\n", lines @ [ section; key + "=" + value ]) + "\r\n"
        | Some start ->
            let bodyEnd =
                lines |> List.indexed |> List.tryFind (fun (i, l) -> i > start && isSection l) |> Option.map fst |> Option.defaultValue lines.Length
            let body = lines.[start + 1 .. bodyEnd - 1]
            let body =
                if body |> List.exists isKey then body |> List.map (fun l -> if isKey l then key + "=" + value else l)
                else
                    // after the section's last real line, ahead of any blank gap
                    let keep = body |> List.rev |> List.skipWhile (fun l -> l.Trim() = "") |> List.rev
                    keep @ [ key + "=" + value ] @ List.replicate (body.Length - keep.Length) ""
            String.Join("\r\n", lines.[.. start] @ body @ lines.[bodyEnd ..]) + "\r\n"

    /// The ReShade settings the player wants on top of the DX11 install:
    /// only the effects that are switched on (the DLSS 5 feed and its motion
    /// vectors) are compiled. With everything compiled, a first start built
    /// fourteen effects before the feed could begin.
    let tuneReShade (dir: string) =
        let ini = Path.Combine(dir, "ReShade.ini")
        if File.Exists ini then
            let text = File.ReadAllText ini
            let tuned = withIniKey text "[GENERAL]" "SkipLoadingDisabledEffects" "1"
            if tuned <> text then File.WriteAllText(ini, tuned)

    // ---------------------------------------------------------------- setup

    /// Downloads MPC-HC, yt-dlp and deno (yt-dlp needs a JavaScript runtime
    /// for YouTube) into the player folder. Safe to run again: what is there
    /// is kept.
    let prepare (dir: string) (progress: string -> unit) : Result<GameItem, string> =
        try
            Directory.CreateDirectory(dir) |> ignore
            Directory.CreateDirectory(Path.Combine(dir, "tools")) |> ignore
            File.WriteAllText(prefPath (), dir)
            if not (File.Exists(Path.Combine(dir, PlayerExe))) then
                progress "Finding the newest MPC-HC..."
                match releaseAsset "https://api.github.com/repos/clsid2/mpc-hc/releases/latest" (fun n -> n.EndsWith("x64.zip", StringComparison.OrdinalIgnoreCase)) with
                | None -> failwith "MPC-HC's newest release has no x64 zip"
                | Some(tag, _, url) ->
                    let zip = Path.Combine(dir, "tools", sprintf "mpc-hc-%s-x64.zip" tag)
                    downloadTo url zip progress "MPC-HC"
                    progress "Unpacking MPC-HC..."
                    extractFlat zip dir None
                    File.Delete zip
                if not (File.Exists(Path.Combine(dir, PlayerExe))) then failwith "mpc-hc64.exe was not in the download"
            writeIni dir

            // The player's own D3DCompiler_47.dll predates Shader Model 5.1 and
            // rejects the cs_5_1 target the neural pass compiles for; without
            // it the system's copy is used.
            let oldCompiler = Path.Combine(dir, "D3DCompiler_47.dll")
            if File.Exists oldCompiler then
                File.Move(oldCompiler, oldCompiler + ".dlss5-off", true)

            // yt-dlp goes beside the player: that is the only place MPC-HC looks.
            try
                let ytdlp = Path.Combine(dir, "yt-dlp.exe")
                if not (File.Exists ytdlp) || File.GetLastWriteTimeUtc ytdlp < DateTime.UtcNow.AddDays(-30.0) then
                    match releaseAsset "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest" (fun n -> n = "yt-dlp.exe") with
                    | Some(_, _, url) -> downloadTo url ytdlp progress "yt-dlp"
                    | None -> ()
                // deno in tools\: anything started from the player folder would
                // load ReShade's dxgi.dll from it.
                let deno = Path.Combine(dir, "tools", "deno.exe")
                if not (File.Exists deno) then
                    match releaseAsset "https://api.github.com/repos/denoland/deno/releases/latest" (fun n -> n = "deno-x86_64-pc-windows-msvc.zip") with
                    | Some(_, _, url) ->
                        let zip = Path.Combine(dir, "tools", "deno.zip")
                        downloadTo url zip progress "deno"
                        extractFlat zip (Path.Combine(dir, "tools")) (Some(fun r -> r.Equals("deno.exe", StringComparison.OrdinalIgnoreCase)))
                        File.Delete zip
                    | None -> ()
                let conf = Path.Combine(dir, "yt-dlp.conf")
                let keep =
                    if File.Exists conf then File.ReadAllLines conf |> Array.filter (fun l -> not (l.TrimStart().StartsWith "--js-runtimes")) else [||]
                File.WriteAllLines(conf, Array.append keep [| sprintf "--js-runtimes \"deno:%s\"" (deno.Replace('\\', '/')) |])
            with ex ->
                AppLog.warn ("YouTube support could not be set up: " + ex.Message)
                progress "The player is ready; YouTube links could not be set up right now."
            progress "The player is ready."
            Ok(asGame ())
        with ex ->
            AppLog.error "Setting up the video player" ex
            Error ex.Message

    // ---------------------------------------------------------------- ffmpeg

    let private ffmpeg () = Path.Combine(tools (), "ffmpeg.exe")

    /// ffmpeg is about 170 MB, so it only comes down the first time a webcam
    /// or the screen is used.
    let ensureFfmpeg (progress: string -> unit) =
        if not (File.Exists(ffmpeg ())) then
            let direct = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip"
            let zip = Path.Combine(tools (), "ffmpeg.zip")
            downloadTo direct zip progress "ffmpeg"
            progress "Unpacking ffmpeg..."
            extractFlat zip (tools ()) (Some(fun r -> r.EndsWith("bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase) || r.EndsWith("bin/ffprobe.exe", StringComparison.OrdinalIgnoreCase)))
            for n in [ "ffmpeg.exe"; "ffprobe.exe" ] do
                let nested = Path.Combine(tools (), "bin", n)
                if File.Exists nested then File.Move(nested, Path.Combine(tools (), n), true)
            File.Delete zip

    // ------------------------------------------------------------------ play

    let private startPlayer (arg: string) =
        let psi = ProcessStartInfo(exePath (), arg, WorkingDirectory = folder (), UseShellExecute = false)
        Process.Start(psi) |> ignore

    let looksLikeUrl (text: string) =
        not (String.IsNullOrWhiteSpace text)
        && Regex.IsMatch(text.Trim(), @"^https?://\S+$", RegexOptions.IgnoreCase)
        && text.Length < 2048

    // --------------------------------------------------------------- warm-up

    /// How far DLSS 5 has come up in the player, read from the feed's log.
    type WarmState =
        /// Still loading: effects compiling, NGX starting, the feature building.
        | Pending
        /// The neural pass has run on a frame: anything played now starts at once.
        | Ready
        /// The feed will not run in this player (no NVIDIA GPU, switched off,
        /// NGX refused): there is nothing to wait for.
        | Off

    /// The feed opens its D3D12 session and NGX on the player's render thread
    /// at the first frame: on an RTX laptop that held the picture still for
    /// 12-15 s (NVSDK_NGX_D3D12_Init and its feature queries), and the first
    /// time after a driver install a further ~40 s while the DLSS feature was
    /// built. Ready is the first "feature ready" or delivered frame.
    let warmStateOf (log: string) =
        if isNull log then Pending
        elif log.Contains "feature ready:" || Regex.IsMatch(log, @"\bframe \d+ delivered") then Ready
        elif log.Contains "stopped:" || log.Contains "NGX would not initialise" || log.Contains "enabled=0:" then Off
        else Pending

    let private feedLog () = Path.Combine(folder (), "dlss5-feed.log")
    let private hasFeed () = File.Exists(Path.Combine(folder (), "dlss5-feed.addon64"))

    let private readShared (path: string) =
        try
            use fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ||| FileShare.Delete)
            use r = new StreamReader(fs)
            r.ReadToEnd()
        with _ -> ""

    /// This player (not some other MPC-HC) is open.
    let private playerRunning () =
        Process.GetProcessesByName(Path.GetFileNameWithoutExtension PlayerExe)
        |> Array.exists (fun p ->
            try String.Equals(Path.GetFullPath(p.MainModule.FileName), Path.GetFullPath(exePath ()), StringComparison.OrdinalIgnoreCase)
            with _ -> false)

    /// A dark "DLSS 5 is starting..." clip for the player to show while the
    /// neural pass comes up, made once by ffmpeg.
    let private ensureCard (progress: string -> unit) : string option =
        let card = Path.Combine(tools (), "dlss5-starting.mp4")
        if File.Exists card then Some card
        else
            try
                ensureFfmpeg progress
                let make (withText: bool) =
                    let psi = ProcessStartInfo(ffmpeg (), WorkingDirectory = tools (), UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true)
                    let text = "drawtext=fontfile='C\\:/Windows/Fonts/segoeui.ttf':text='DLSS 5 is starting...':fontcolor=0xE0E0E0:fontsize=44:x=(w-text_w)/2:y=(h-text_h)/2"
                    for a in [ yield! [ "-hide_banner"; "-loglevel"; "error"; "-y"; "-f"; "lavfi"; "-i"; "color=c=0x101014:size=1280x720:rate=30" ]
                               if withText then yield! [ "-vf"; text ]
                               yield! [ "-t"; "300"; "-c:v"; "libx264"; "-preset"; "veryfast"; "-tune"; "stillimage"; "-g"; "60"; "-pix_fmt"; "yuv420p"; "-an"; card + ".part.mp4" ] ] do
                        psi.ArgumentList.Add a
                    use p = Process.Start(psi)
                    p.StandardError.ReadToEnd() |> ignore
                    p.WaitForExit()
                    p.ExitCode = 0
                if make true || make false then
                    File.Move(card + ".part.mp4", card, true)
                    Some card
                else None
            with ex ->
                AppLog.warn ("The DLSS 5 start-up clip could not be made: " + ex.Message)
                None

    /// Brings DLSS 5 up in the player before the real picture goes in. The
    /// player opens on the start-up clip, which takes the render-thread stall,
    /// and this returns once the feed's log says the neural pass has run (or
    /// that it never will). Whatever is played next is handed to that same
    /// window and its session opens in about a tenth of a second, so the
    /// video itself never freezes. A player already warm is used as it is.
    let warmUp (progress: string -> unit) =
        if hasFeed () then
            let wait (since: DateTime option) =
                let watch = Stopwatch.StartNew()
                let mutable state = Pending
                while state = Pending && watch.Elapsed < TimeSpan.FromSeconds 150.0 do
                    let log = feedLog ()
                    let fresh =
                        match since with
                        | None -> true
                        | Some t -> File.Exists log && File.GetLastWriteTimeUtc log >= t
                    if fresh then state <- warmStateOf (readShared log)
                    // closed by the user, or never came up
                    if state = Pending && watch.Elapsed > TimeSpan.FromSeconds 10.0 && not (playerRunning ()) then state <- Off
                    if state = Pending then Threading.Thread.Sleep(250)
                state
            if playerRunning () then
                if warmStateOf (readShared (feedLog ())) = Pending then
                    progress "Waiting for DLSS 5 to finish starting in the player..."
                    wait None |> ignore
            else
                tuneReShade (folder ())
                (try writeIni (folder ()) with _ -> ())
                match ensureCard progress with
                | None -> ()
                | Some card ->
                    let since = DateTime.UtcNow.AddSeconds(-1.0)
                    startPlayer (sprintf "\"%s\" /play" card)
                    progress "Starting DLSS 5 in the player (about 15 seconds; the first time can take a minute)..."
                    match wait (Some since) with
                    | Ready -> AppLog.info "Video player: DLSS 5 is up; handing the video over."
                    | Off -> AppLog.info "Video player: the DLSS 5 feed is not running in the player; playing without waiting."
                    | Pending -> AppLog.warn "Video player: DLSS 5 did not report ready within 150 s; playing anyway."

    /// A file, or a link MPC-HC opens itself; nothing is downloaded.
    let play (fileOrUrl: string) (progress: string -> unit) =
        if not (isSetUp ()) then Error "Set the player up first."
        elif looksLikeUrl fileOrUrl || File.Exists fileOrUrl then
            warmUp progress
            startPlayer (sprintf "\"%s\" /play" (fileOrUrl.Trim()))
            Ok()
        else Error "That is not a file or a web link."

    let mutable private feed: Process option = None
    /// yt-dlp writing a link's picture and sound into the feed's ffmpeg.
    let mutable private source: Process option = None

    let stopFeed () =
        for p in [ source; feed ] do
            p |> Option.iter (fun p -> try (if not p.HasExited then p.Kill(true)) with _ -> ())
        source <- None
        feed <- None

    let private startFfmpeg (args: string) =
        stopFeed ()
        let psi = ProcessStartInfo(ffmpeg (), args, WorkingDirectory = tools (), UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true)
        let p = Process.Start(psi)
        p.ErrorDataReceived.Add(fun _ -> ())
        p.BeginErrorReadLine()
        feed <- Some p
        p

    /// DirectShow cameras ffmpeg can see.
    let cameras () : string list =
        try
            if not (File.Exists(ffmpeg ())) then []
            else
                let psi = ProcessStartInfo(ffmpeg (), "-hide_banner -list_devices true -f dshow -i dummy", UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true)
                use p = Process.Start(psi)
                let err = p.StandardError.ReadToEndAsync()
                let out = p.StandardOutput.ReadToEnd()
                p.WaitForExit(20000) |> ignore
                [ for m in Regex.Matches(err.Result + out, "\"([^\"]+)\"\\s+\\(video\\)") -> m.Groups.[1].Value ] |> List.distinct
        with _ -> []

    /// The webcam, encoded with almost no delay and streamed into the player.
    let startWebcam (camera: string) (progress: string -> unit) : Result<unit, string> =
        try
            ensureFfmpeg progress
            warmUp progress
            let tryRun (size: string) =
                let p =
                    startFfmpeg (
                        sprintf "-hide_banner -loglevel error -f dshow -rtbufsize 64M %s-i \"video=%s\" -vcodec libx264 -preset ultrafast -tune zerolatency -g 30 -pix_fmt yuv420p -f mpegts \"%s\""
                            size camera feedTarget)
                Threading.Thread.Sleep(2000)
                not p.HasExited
            if tryRun "-video_size 1280x720 -framerate 30 " || tryRun "" then
                startPlayer (webcamUrl + " /play")
                Ok()
            else
                stopFeed ()
                Error "The camera could not be opened - another app may be using it."
        with ex -> Error ex.Message

    [<DllImport("user32.dll")>]
    extern int GetSystemMetrics(int index)

    /// One monitor's worth of the screen (the left 62% on a single-monitor
    /// PC, with the player parked in the rest), captured by Desktop
    /// Duplication and NVENC.
    let startScreen (progress: string -> unit) : Result<unit, string> =
        try
            ensureFfmpeg progress
            warmUp progress
            let monitors = GetSystemMetrics(80)
            let w = GetSystemMetrics(0)
            let h = GetSystemMetrics(1)
            let region =
                if monitors > 1 then ""
                else
                    let cw = (int (float w * 0.62)) / 2 * 2
                    sprintf ":video_size=%dx%d" cw (h / 2 * 2)
            let p =
                startFfmpeg (
                    sprintf "-hide_banner -loglevel error -init_hw_device d3d11va -filter_complex \"ddagrab=output_idx=0:framerate=60%s\" -c:v h264_nvenc -preset p1 -tune ll -zerolatency 1 -g 60 -f mpegts \"%s\""
                        region feedTarget)
            Threading.Thread.Sleep(2000)
            if p.HasExited then
                let p2 =
                    startFfmpeg (
                        sprintf "-hide_banner -loglevel error -f gdigrab -framerate 30 %s-i desktop -vcodec libx264 -preset ultrafast -tune zerolatency -pix_fmt yuv420p -g 30 -f mpegts \"%s\""
                            (if region = "" then "" else sprintf "-video_size %dx%d " ((int (float w * 0.62)) / 2 * 2) (h / 2 * 2)) feedTarget)
                Threading.Thread.Sleep(2000)
                if p2.HasExited then
                    stopFeed ()
                    failwith "the screen could not be captured"
            startPlayer (webcamUrl + " /play")
            Ok()
        with ex -> Error ex.Message

    /// A web link. YouTube now refuses its stream links (HTTP 403) to a player
    /// that fetches them itself, so MPC-HC buffered forever. yt-dlp fetches
    /// the picture and sound instead - H.264 and AAC up to 1080p, which the
    /// feed's ffmpeg passes on untouched - and ffmpeg streams them into the
    /// player in real time over the webcam's port. No seeking: it is a stream.
    let playLink (url: string) (progress: string -> unit) : Result<unit, string> =
        try
            if not (isSetUp ()) then failwith "Set the player up first."
            let ytdlp = Path.Combine(folder (), "yt-dlp.exe")
            if not (File.Exists ytdlp) then failwith "yt-dlp is missing from the player folder - set the player up again."
            ensureFfmpeg progress
            stopFeed ()
            // DLSS 5 comes up in the player while yt-dlp looks the video up;
            // nothing of the video is sent until it is ready, so it starts
            // from its first frame instead of freezing on it.
            let warming = Threading.Tasks.Task.Run(fun () -> warmUp progress)
            progress "Looking up the video..."
            let fetchInfo = ProcessStartInfo(ytdlp, WorkingDirectory = tools (), UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true)
            for a in [ "--no-playlist"; "-q"; "--no-warnings"
                       "--config-locations"; Path.Combine(folder (), "yt-dlp.conf")
                       "--ffmpeg-location"; tools ()
                       "-f"; "bv*[height<=1080][vcodec^=avc1]+ba[acodec^=mp4a]/b[vcodec^=avc1]/b"
                       "-o"; "-"
                       url.Trim() ] do
                fetchInfo.ArgumentList.Add a
            let fetcher = Process.Start(fetchInfo)
            let errors = StringBuilder()
            fetcher.ErrorDataReceived.Add(fun e -> if not (isNull e.Data) then lock errors (fun () -> errors.AppendLine(e.Data) |> ignore))
            fetcher.BeginErrorReadLine()
            source <- Some fetcher
            let streamInfo = ProcessStartInfo(ffmpeg (), sprintf "-hide_banner -loglevel error -re -i pipe:0 -c copy -f mpegts \"%s\"" feedTarget, WorkingDirectory = tools (), UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true)
            let streamer = Process.Start(streamInfo)
            feed <- Some streamer
            // The player opens once the first of the picture is on its way:
            // started earlier it gives up on a port that is still silent.
            let first = Array.zeroCreate<byte> 65536
            let got = fetcher.StandardOutput.BaseStream.Read(first, 0, first.Length)
            if got <= 0 then
                stopFeed ()
                let why = lock errors (fun () -> errors.ToString().Trim().Split('\n') |> Array.tryLast |> Option.defaultValue "")
                failwith ("yt-dlp could not get the video. " + why.Trim())
            // yt-dlp simply waits on its full pipe meanwhile.
            warming.Wait()
            streamer.StandardInput.BaseStream.Write(first, 0, got)
            Threading.Tasks.Task.Run(fun () ->
                try
                    try fetcher.StandardOutput.BaseStream.CopyTo(streamer.StandardInput.BaseStream)
                    with _ -> ()
                finally
                    try streamer.StandardInput.Close() with _ -> ())
            |> ignore
            startPlayer (webcamUrl + " /play")
            // Closing the player ends the stream too; otherwise yt-dlp would
            // go on fetching a long video nobody is watching.
            Threading.Tasks.Task.Run(fun () ->
                let playerOpen () = Process.GetProcessesByName(Path.GetFileNameWithoutExtension PlayerExe).Length > 0
                Threading.Thread.Sleep(10000)
                while source = Some fetcher && playerOpen () do
                    Threading.Thread.Sleep(3000)
                if source = Some fetcher then stopFeed ())
            |> ignore
            Ok()
        with ex -> Error ex.Message

    [<DllImport("user32.dll", CharSet = CharSet.Unicode)>]
    extern nativeint FindWindowW(string cls, string title)

    [<DllImport("user32.dll")>]
    extern bool SetForegroundWindow(nativeint hwnd)

    [<DllImport("user32.dll")>]
    extern bool ShowWindow(nativeint hwnd, int cmd)

    [<DllImport("user32.dll")>]
    extern bool IsIconic(nativeint hwnd)

    [<DllImport("user32.dll")>]
    extern void keybd_event(byte vk, byte scan, uint32 flags, unativeint extra)

    /// Presses F6 in the player - the add-on's neural-pass toggle. A real key
    /// press is needed: ReShade's input hook ignores posted messages.
    let toggleNeural () =
        let hwnd = FindWindowW("MediaPlayerClassicW", null)
        if hwnd = 0n then Error "The player is not open."
        else
            if IsIconic hwnd then ShowWindow(hwnd, 9) |> ignore
            SetForegroundWindow hwnd |> ignore
            Threading.Thread.Sleep(250)
            keybd_event(0x75uy, 0x40uy, 0u, 0un)
            Threading.Thread.Sleep(80)
            keybd_event(0x75uy, 0x40uy, 2u, 0un)
            Ok()
