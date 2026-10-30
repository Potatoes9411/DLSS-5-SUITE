namespace DLSS_5_MANAGER.Services

open System
open System.Diagnostics
open System.Globalization
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks

/// DLSS 5 on a video file, picture by picture: yt-dlp fetches a link, ffmpeg
/// decodes it into raw pictures, the screen engine runs DLSS 5 (and its own
/// frame generation) on every one of them in its video mode, and ffmpeg
/// encodes what comes back with the original sound into the container asked
/// for. Nothing is played or captured from the screen, so no frame is dropped
/// however slow DLSS 5 is: a minute of video simply takes longer than a minute.
module VideoDlss =

    // =====================================================================
    // TOOLS
    // =====================================================================

    /// yt-dlp, deno (yt-dlp needs a JavaScript runtime for YouTube) and ffmpeg
    /// live here, apart from the MPC-HC player's copies.
    let toolsDir () =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DLSS5Suite", "VideoTools")

    let private onPath (name: string) =
        let path = Environment.GetEnvironmentVariable("PATH")
        if String.IsNullOrEmpty path then None
        else
            path.Split(';', StringSplitOptions.RemoveEmptyEntries)
            |> Array.tryPick (fun dir ->
                let candidate = (try Path.Combine(dir.Trim().Trim('"'), name) with _ -> "")
                if candidate <> "" && File.Exists candidate then Some candidate else None)

    /// SUITE's own copy first, then one already on the PATH.
    let findTool (name: string) =
        let own = Path.Combine(toolsDir (), name)
        if File.Exists own then Some own else onPath name

    let ffmpegPath () = findTool "ffmpeg.exe"
    let ffprobePath () = findTool "ffprobe.exe"
    let ytDlpPath () = findTool "yt-dlp.exe"
    let denoPath () = findTool "deno.exe"

    /// What is still missing for a file; a link also needs yt-dlp and deno.
    let missingTools (forLink: bool) =
        [ if ffmpegPath().IsNone then yield "ffmpeg"
          if ffprobePath().IsNone then yield "ffprobe"
          if forLink && ytDlpPath().IsNone then yield "yt-dlp"
          if forLink && denoPath().IsNone then yield "deno" ]

    /// Fetches whatever is missing from each project's own GitHub releases:
    /// yt-dlp (about 18 MB), deno (about 45 MB) and the BtbN ffmpeg build
    /// (about 170 MB, of which ffmpeg.exe and ffprobe.exe are kept).
    let ensureTools (progress: string -> unit) : Result<unit, string> =
        try
            let dir = toolsDir ()
            Directory.CreateDirectory(dir) |> ignore
            if ytDlpPath().IsNone then
                match VideoPlayer.releaseAsset "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest" (fun n -> n = "yt-dlp.exe") with
                | Some(_, _, url) -> VideoPlayer.downloadTo url (Path.Combine(dir, "yt-dlp.exe")) progress "yt-dlp"
                | None -> failwith "yt-dlp's latest release has no yt-dlp.exe"
            if denoPath().IsNone then
                match VideoPlayer.releaseAsset "https://api.github.com/repos/denoland/deno/releases/latest" (fun n -> n = "deno-x86_64-pc-windows-msvc.zip") with
                | Some(_, _, url) ->
                    let zip = Path.Combine(dir, "deno.zip")
                    VideoPlayer.downloadTo url zip progress "deno"
                    VideoPlayer.extractFlat zip dir (Some(fun r -> r.Equals("deno.exe", StringComparison.OrdinalIgnoreCase)))
                    File.Delete zip
                | None -> failwith "deno's latest release has no Windows build"
            if ffmpegPath().IsNone || ffprobePath().IsNone then
                let zip = Path.Combine(dir, "ffmpeg.zip")
                VideoPlayer.downloadTo "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip" zip progress "ffmpeg"
                progress "Unpacking ffmpeg..."
                VideoPlayer.extractFlat zip dir (Some(fun r -> r.EndsWith("bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase) || r.EndsWith("bin/ffprobe.exe", StringComparison.OrdinalIgnoreCase)))
                for n in [ "ffmpeg.exe"; "ffprobe.exe" ] do
                    let nested = Path.Combine(dir, "bin", n)
                    if File.Exists nested then File.Move(nested, Path.Combine(dir, n), true)
                File.Delete zip
            progress "Video tools are ready."
            Ok()
        with ex ->
            AppLog.error "Setting up the video tools" ex
            Error ex.Message

    // =====================================================================
    // CHOICES
    // =====================================================================

    type Container =
        | Mp4
        | Mkv
        | WebM
        | Mov

    type Codec =
        | H264
        | H265
        | AV1
        | VP9
        | ProRes

    type Quality =
        | High
        | Balanced
        | Small

    let extensionOf =
        function
        | Mp4 -> ".mp4"
        | Mkv -> ".mkv"
        | WebM -> ".webm"
        | Mov -> ".mov"

    /// The codecs each container is given here: the ones it holds and players expect in it.
    let codecsFor =
        function
        | Mp4 -> [ H264; H265; AV1 ]
        | Mkv -> [ H264; H265; AV1; VP9 ]
        | WebM -> [ VP9; AV1 ]
        | Mov -> [ H264; H265; ProRes ]

    let codecName =
        function
        | H264 -> "H.264"
        | H265 -> "H.265 (HEVC)"
        | AV1 -> "AV1"
        | VP9 -> "VP9"
        | ProRes -> "ProRes 422 HQ"

    /// NVIDIA's hardware encoder has H.264, H.265 and (RTX 40 and up) AV1.
    let hasNvenc =
        function
        | H264
        | H265
        | AV1 -> true
        | VP9
        | ProRes -> false

    type Job =
        { /// A web link or a file on disk.
          Source: string
          Output: string
          Container: Container
          Codec: Codec
          Quality: Quality
          /// Pictures written for each one in the source, 1 (off) to 4.
          FrameGen: int
          /// Processed no taller than this; DLSS 5 costs grow with the picture.
          MaxHeight: int option
          UseNvenc: bool
          KeepDownload: bool }

    // =====================================================================
    // PROBING
    // =====================================================================

    type Probe =
        { Width: int
          Height: int
          RateNum: int64
          RateDen: int64
          Duration: float
          HasAudio: bool
          Title: string }

    let private run (exe: string) (args: string list) =
        let psi = ProcessStartInfo(exe, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true)
        for a in args do psi.ArgumentList.Add a
        use p = Process.Start(psi)
        let out = p.StandardOutput.ReadToEndAsync()
        let err = p.StandardError.ReadToEndAsync()
        p.WaitForExit()
        p.ExitCode, out.Result, err.Result

    let private rational (text: string) =
        match text.Split('/') with
        | [| n; d |] ->
            match Int64.TryParse n, Int64.TryParse d with
            | (true, n), (true, d) when n > 0L && d > 0L -> Some(n, d)
            | _ -> None
        | _ -> None

    let probe (path: string) : Result<Probe, string> =
        match ffprobePath () with
        | None -> Error "ffprobe is not installed."
        | Some ffprobe ->
            let code, out, err =
                run ffprobe [ "-v"; "error"; "-show_entries"; "stream=codec_type,width,height,avg_frame_rate,r_frame_rate:format=duration"; "-of"; "json"; path ]
            if code <> 0 then Error(sprintf "ffprobe could not read the video: %s" (err.Trim()))
            else
                try
                    use doc = JsonDocument.Parse(out)
                    let streams = doc.RootElement.GetProperty("streams").EnumerateArray() |> List.ofSeq
                    let kind (s: JsonElement) = match s.TryGetProperty("codec_type") with | true, v -> v.GetString() | _ -> ""
                    match streams |> List.tryFind (fun s -> kind s = "video") with
                    | None -> Error "The file has no video in it."
                    | Some v ->
                        let rate =
                            [ "avg_frame_rate"; "r_frame_rate" ]
                            |> List.tryPick (fun k -> match v.TryGetProperty(k) with | true, r -> rational (r.GetString()) | _ -> None)
                        let duration =
                            match doc.RootElement.TryGetProperty("format") with
                            | true, f ->
                                match f.TryGetProperty("duration") with
                                | true, d -> (match Double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture) with | true, x -> x | _ -> 0.0)
                                | _ -> 0.0
                            | _ -> 0.0
                        match rate with
                        | None -> Error "The video does not say its frame rate."
                        | Some(n, d) ->
                            Ok { Width = v.GetProperty("width").GetInt32()
                                 Height = v.GetProperty("height").GetInt32()
                                 RateNum = n
                                 RateDen = d
                                 Duration = duration
                                 HasAudio = streams |> List.exists (fun s -> kind s = "audio")
                                 Title = Path.GetFileNameWithoutExtension(path) }
                with ex -> Error(sprintf "ffprobe's answer could not be read: %s" ex.Message)

    // =====================================================================
    // THE PIPELINE
    // =====================================================================

    let looksLikeUrl (text: string) = VideoPlayer.looksLikeUrl text

    /// Processed size: the source's, or scaled down to the height cap, kept even for the encoders.
    let processedSize (p: Probe) (maxHeight: int option) =
        let even x = max 2 (x / 2 * 2)
        match maxHeight with
        | Some h when p.Height > h -> even (int (Math.Round(float p.Width * float h / float p.Height))), even h
        | _ -> even p.Width, even p.Height

    /// One picture's display time in microseconds, which the engine plans the frames between with.
    let periodMicroseconds (p: Probe) =
        max 1L (int64 (Math.Round(1000000.0 * float p.RateDen / float p.RateNum)))

    let private quantizer (codec: Codec) (quality: Quality) =
        match codec, quality with
        | VP9, High -> 24
        | VP9, Balanced -> 31
        | VP9, Small -> 38
        | AV1, High -> 22
        | AV1, Balanced -> 30
        | AV1, Small -> 38
        | _, High -> 18
        | _, Balanced -> 23
        | _, Small -> 28

    /// ffmpeg's video encoder arguments for the codec, quality and encoder chosen.
    let encoderArgs (job: Job) =
        let q = string (quantizer job.Codec job.Quality)
        let nvenc = job.UseNvenc && hasNvenc job.Codec
        match job.Codec with
        | H264 when nvenc -> [ "-c:v"; "h264_nvenc"; "-preset"; "p5"; "-rc"; "vbr"; "-cq"; q; "-b:v"; "0"; "-pix_fmt"; "yuv420p" ]
        | H265 when nvenc -> [ "-c:v"; "hevc_nvenc"; "-preset"; "p5"; "-rc"; "vbr"; "-cq"; q; "-b:v"; "0"; "-pix_fmt"; "yuv420p"; "-tag:v"; "hvc1" ]
        | AV1 when nvenc -> [ "-c:v"; "av1_nvenc"; "-preset"; "p5"; "-rc"; "vbr"; "-cq"; q; "-b:v"; "0"; "-pix_fmt"; "yuv420p" ]
        | H264 -> [ "-c:v"; "libx264"; "-preset"; "medium"; "-crf"; q; "-pix_fmt"; "yuv420p" ]
        | H265 -> [ "-c:v"; "libx265"; "-preset"; "medium"; "-crf"; q; "-pix_fmt"; "yuv420p"; "-tag:v"; "hvc1" ]
        | AV1 -> [ "-c:v"; "libsvtav1"; "-preset"; "8"; "-crf"; q; "-pix_fmt"; "yuv420p" ]
        | VP9 -> [ "-c:v"; "libvpx-vp9"; "-crf"; q; "-b:v"; "0"; "-row-mt"; "1"; "-pix_fmt"; "yuv420p" ]
        | ProRes -> [ "-c:v"; "prores_ks"; "-profile:v"; "3"; "-pix_fmt"; "yuv422p10le" ]

    let audioArgs (container: Container) =
        match container with
        | WebM -> [ "-c:a"; "libopus"; "-b:a"; "160k" ]
        | Mp4
        | Mkv
        | Mov -> [ "-c:a"; "aac"; "-b:a"; "192k" ]

    /// What happened, for the page.
    type Update =
        { Status: string
          /// 0 to 1, or nothing while it cannot be measured.
          Fraction: float option }

    let private downloadDir () = Path.Combine(Path.GetTempPath(), "DLSS5SuiteVideo")

    /// yt-dlp's own "[download]  42.1% of ..." lines, read as they come.
    let private downloadPercent = Regex(@"\[download\]\s+([0-9.]+)%", RegexOptions.Compiled)

    let private fetch (url: string) (report: Update -> unit) (ct: CancellationToken) : Task<Result<string, string>> = task {
        match ytDlpPath (), ffmpegPath (), denoPath () with
        | Some ytdlp, Some ffmpeg, Some deno ->
            let dir = Path.Combine(downloadDir (), Guid.NewGuid().ToString("N"))
            Directory.CreateDirectory(dir) |> ignore
            let psi = ProcessStartInfo(ytdlp, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true)
            for a in [ "--no-playlist"; "--newline"; "--no-mtime"
                       "-f"; "bv*[height<=2160]+ba/b"
                       "--merge-output-format"; "mkv"
                       "--ffmpeg-location"; Path.GetDirectoryName(ffmpeg)
                       "--js-runtimes"; "deno:" + deno.Replace('\\', '/')
                       "-o"; Path.Combine(dir, "%(title).80B [%(id)s].%(ext)s")
                       "--print"; "after_move:filepath"
                       url ] do
                psi.ArgumentList.Add a
            use p = Process.Start(psi)
            use _ = ct.Register(fun () -> try p.Kill(true) with _ -> ())
            let mutable finalPath = ""
            let errors = Text.StringBuilder()
            p.ErrorDataReceived.Add(fun e -> if not (isNull e.Data) then errors.AppendLine(e.Data) |> ignore)
            p.BeginErrorReadLine()
            let mutable line = p.StandardOutput.ReadLine()
            while not (isNull line) do
                let m = downloadPercent.Match(line)
                if m.Success then
                    match Double.TryParse(m.Groups.[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture) with
                    | true, pct -> report { Status = sprintf "Downloading the video... %.0f%%" pct; Fraction = Some(pct / 100.0) }
                    | _ -> ()
                elif line.Trim() <> "" && File.Exists(line.Trim()) then
                    finalPath <- line.Trim()
                line <- p.StandardOutput.ReadLine()
            do! p.WaitForExitAsync()
            let failed = ct.IsCancellationRequested || p.ExitCode <> 0 || finalPath = ""
            if failed then
                try Directory.Delete(dir, true) with _ -> ()
            if ct.IsCancellationRequested then return Error "Cancelled."
            elif failed then
                let last = errors.ToString().Trim().Split('\n') |> Array.tryLast |> Option.defaultValue ""
                return Error(sprintf "yt-dlp could not download it. %s" (last.Trim()))
            else return Ok finalPath
        | _ -> return Error "yt-dlp, deno or ffmpeg is missing: download the video tools first."
    }

    let private started (exe: string) (args: string list) (stdin: bool) (stdout: bool) =
        let psi = ProcessStartInfo(exe, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = stdin, RedirectStandardOutput = stdout, RedirectStandardError = true)
        for a in args do psi.ArgumentList.Add a
        let p = Process.Start(psi)
        p

    /// The last lines a process wrote to its error stream, for a message that says why.
    let private tail (lines: Collections.Concurrent.ConcurrentQueue<string>) =
        lines.ToArray() |> Array.rev |> Array.truncate 3 |> Array.rev |> String.concat " "

    let private drained (p: Process) =
        let lines = Collections.Concurrent.ConcurrentQueue<string>()
        p.ErrorDataReceived.Add(fun e ->
            if not (isNull e.Data) then
                lines.Enqueue(e.Data)
                while lines.Count > 40 do lines.TryDequeue() |> ignore)
        p.BeginErrorReadLine()
        lines

    /// Decoder → engine → encoder. Progress is pictures written against the
    /// pictures the source's length and rate promise.
    let private pipeline (ffmpeg: string) (source: string) (probe: Probe) (job: Job) (engine: ScreenEngine.Settings) (report: Update -> unit) (ct: CancellationToken) : Task<Result<string, string>> = task {
        let w, h = processedSize probe job.MaxHeight
        let multiplier = Math.Clamp(job.FrameGen, 1, 4)
        let pictureBytes = int64 w * int64 h * 4L
        let expected = max 1L (int64 (Math.Round(probe.Duration * float probe.RateNum / float probe.RateDen)) * int64 multiplier)
        let data = ScreenEngine.dataDir ()
        Directory.CreateDirectory(data) |> ignore

        let decoderArgs =
            [ yield! [ "-v"; "error"; "-nostdin"; "-i"; source; "-map"; "0:v:0" ]
              if (w, h) <> (probe.Width, probe.Height) then yield! [ "-vf"; sprintf "scale=%d:%d:flags=bicubic" w h ]
              yield! [ "-f"; "rawvideo"; "-pix_fmt"; "bgra"; "-" ] ]
        // The Screen Engine's own DLSS 5 settings, then what makes it a video
        // session: the last of any option given twice is the one that counts.
        let engineArgs =
            ScreenEngine.toArguments data (ScreenEngine.neuralRuntimeDir ()) engine
            @ [ "--video-width"; string w
                "--video-height"; string h
                "--video-frame-us"; string (periodMicroseconds probe)
                "--frame-gen"; string multiplier
                "--nvidia-frame-gen"; "off"
                "--gui"; "off"
                "--console"; "off"
                "--log-file"; Path.Combine(data, "video-engine.log") ]
        let rate = sprintf "%d/%d" (probe.RateNum * int64 multiplier) probe.RateDen
        let encoderArgsAll =
            [ yield! [ "-v"; "error"; "-y"
                       "-f"; "rawvideo"; "-pix_fmt"; "rgba"; "-s"; sprintf "%dx%d" w h; "-framerate"; rate; "-i"; "-" ]
              if probe.HasAudio then yield! [ "-i"; source; "-map"; "0:v:0"; "-map"; "1:a:0?" ] else yield! [ "-map"; "0:v:0" ]
              yield! encoderArgs job
              if probe.HasAudio then yield! audioArgs job.Container
              yield job.Output ]

        report { Status = "Starting DLSS 5 (about 15 seconds)..."; Fraction = None }
        let decoder = started ffmpeg decoderArgs false true
        let engineProc = started (ScreenEngine.enginePath ()) engineArgs true true
        let encoder = started ffmpeg encoderArgsAll true false
        let decoderErrors = drained decoder
        let engineErrors = drained engineProc
        let encoderErrors = drained encoder
        let all = [ decoder; engineProc; encoder ]
        use _ = ct.Register(fun () -> for p in all do try (if not p.HasExited then p.Kill(true)) with _ -> ())
        let watch = Stopwatch.StartNew()

        let feed = task {
            try
                try do! decoder.StandardOutput.BaseStream.CopyToAsync(engineProc.StandardInput.BaseStream, 1 <<< 20, ct)
                with _ -> ()
            finally
                try engineProc.StandardInput.Close() with _ -> ()
        }
        let collect = task {
            let buffer = Array.zeroCreate<byte> (1 <<< 20)
            let mutable total = 0L
            let mutable lastReport = 0L
            // Speed is measured from the first finished picture, not from the engine's start-up.
            let mutable firstAt = TimeSpan.Zero
            try
                try
                    let source = engineProc.StandardOutput.BaseStream
                    let target = encoder.StandardInput.BaseStream
                    let mutable read = 1
                    while read > 0 do
                        let! n = source.ReadAsync(buffer, 0, buffer.Length, ct)
                        read <- n
                        if n > 0 then
                            do! target.WriteAsync(buffer, 0, n, ct)
                            total <- total + int64 n
                            let pictures = total / pictureBytes
                            if pictures <> lastReport then
                                if lastReport = 0L then firstAt <- watch.Elapsed
                                lastReport <- pictures
                                let seconds = (watch.Elapsed - firstAt).TotalSeconds
                                let fraction = min 1.0 (float pictures / float expected)
                                let speed = if seconds > 0.0 then float pictures / seconds else 0.0
                                let left = if speed > 0.0 then TimeSpan.FromSeconds(float (max 0L (expected - pictures)) / speed) else TimeSpan.Zero
                                report { Status = sprintf "Frame %d of about %d · %.1f fps · %s left" pictures expected speed (left.ToString(@"h\:mm\:ss"))
                                         Fraction = Some fraction }
                with _ -> ()
            finally
                try encoder.StandardInput.Close() with _ -> ()
            return total / pictureBytes
        }
        do! feed
        let! written = collect
        for p in all do
            do! p.WaitForExitAsync()
        if ct.IsCancellationRequested then
            let _ = (try File.Delete job.Output; true with _ -> false)
            return Error "Cancelled."
        elif decoder.ExitCode <> 0 then return Error(sprintf "ffmpeg could not decode the video: %s" (tail decoderErrors))
        elif engineProc.ExitCode <> 0 then
            return Error(sprintf "The screen engine stopped (code %d). Its log is %s" engineProc.ExitCode (Path.Combine(data, "video-engine.log")))
        elif encoder.ExitCode <> 0 then
            let why = tail encoderErrors
            let hint = if job.UseNvenc && hasNvenc job.Codec then " Try again with the NVIDIA encoder switched off." else ""
            return Error(sprintf "ffmpeg could not encode the result: %s%s" why hint)
        elif written = 0L then return Error "No pictures came out of the engine."
        else
            ignore engineErrors
            return Ok(sprintf "Done: %d frames in %s." written (watch.Elapsed.ToString(@"h\:mm\:ss")))
    }

    let private convert (source: string) (probe: Probe) (job: Job) (engine: ScreenEngine.Settings) (report: Update -> unit) (ct: CancellationToken) : Task<Result<string, string>> =
        match ffmpegPath () with
        | None -> Task.FromResult(Error "ffmpeg is missing: download the video tools first.")
        | Some _ when not (ScreenEngine.isAvailable ()) -> Task.FromResult(Error "The screen engine is not included in this build.")
        | Some ffmpeg -> pipeline ffmpeg source probe job engine report ct

    /// The whole job: fetch a link when it is one, probe, convert, tidy up.
    let processJob (job: Job) (engine: ScreenEngine.Settings) (report: Update -> unit) (ct: CancellationToken) : Task<Result<string, string>> = task {
        let isLink = looksLikeUrl job.Source
        let! fetched =
            if isLink then fetch (job.Source.Trim()) report ct
            elif File.Exists job.Source then Task.FromResult(Ok job.Source)
            else Task.FromResult(Error "That is not a web link or a file.")
        match fetched with
        | Error e -> return Error e
        | Ok source ->
            try
                match probe source with
                | Error e -> return Error e
                | Ok p ->
                    Directory.CreateDirectory(Path.GetDirectoryName(job.Output)) |> ignore
                    return! convert source p job engine report ct
            finally
                if isLink && not job.KeepDownload then
                    try Directory.Delete(Path.GetDirectoryName(source), true) with _ -> ()
    }

    /// A name for the result beside the user's other videos.
    let defaultOutput (source: string) (container: Container) =
        let videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
        let stem =
            if looksLikeUrl source || String.IsNullOrWhiteSpace source then sprintf "DLSS 5 video %s" (DateTime.Now.ToString("yyyy-MM-dd HH-mm"))
            else Path.GetFileNameWithoutExtension(source) + " (DLSS 5)"
        let safe = String(stem.ToCharArray() |> Array.map (fun c -> if Array.contains c (Path.GetInvalidFileNameChars()) then '_' else c))
        Path.Combine(videos, safe + extensionOf container)
