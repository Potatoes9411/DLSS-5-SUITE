namespace DLSS_5_MANAGER.Services

open System
open System.Diagnostics
open System.Globalization
open System.IO
open System.Net.Http
open System.Runtime.InteropServices
open System.Security.Cryptography.X509Certificates
open System.Text
open System.Threading
open System.Threading.Tasks
open DLSS_5_MANAGER.Models

// X509Certificate.CreateFromSignedFile is marked obsolete in favour of loading by content type; it is still
// the one call that reads an Authenticode signer from a PE file.
#nowarn "44"

/// Motion Forge's engine (MotionEngine/, MFG SUPER MOTION's engine by NODIX
/// TECH, built as engine\motion\MotionForgeEngine.exe), ported from his
/// Infrastructure/MotionEngine.fs.
///
/// The engine is its own process with no pipe or shared memory: it is given a
/// start file of "key=value" lines, then one line on stdin for every control
/// that moves, and answers with "@" lines on stdout (MotionEngine/sm_control.inl).

/// A graphics card as the engine lists it ("--cards").
type MotionCard =
    { Index: int      // "gpu=<number>"
      Dlss: bool      // DLSS MFG can run on it
      Xess: bool      // Intel's library takes it (only known once XeSS's files are picked)
      Name: string }  // a second card of the same name ends in " (2)"

type MotionPhase =
    | Idle
    | Starting
    | Picking
    | Running

/// The engine's status lines, folded into one snapshot.
type MotionStatus =
    { Phase: MotionPhase
      Countdown: int        // seconds left to click the game
      BaseFps: int          // the game's frames a second, -1 none yet
      OutputFps: int        // all frames shown a second, -1 none yet
      Width: int
      Height: int
      Api: string           // "dxgi" / "wgc"
      Placement: string     // "borderless", "scaled", "window", "screen"
      Multiplier: int       // the multiplier running, 0 off
      MfgUnavailable: bool
      MfgWhy: string        // the engine's reason when it is unavailable
      VrrFixed: bool
      Learning: int         // -1 not said, 0..100 learning, 101 learnt
      Error: string }       // the engine's error code, "" none

module MotionStatus =
    let idle =
        { Phase = Idle; Countdown = 0; BaseFps = -1; OutputFps = -1; Width = 0; Height = 0; Api = ""; Placement = ""
          Multiplier = 0; MfgUnavailable = false; MfgWhy = ""; VrrFixed = false; Learning = -1; Error = "" }

    let private number (text: string) =
        match Int32.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture) with
        | true, v -> v
        | _ -> -1

    /// One line of the engine's folded into the status before it. Lines that
    /// are not status lines (or not understood) leave it as it was.
    let apply (s: MotionStatus) (line: string) : MotionStatus =
        if String.IsNullOrEmpty line || line[0] <> '@' then s
        else
            let parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            match parts[0] with
            | "@state" when parts.Length > 1 ->
                match parts[1] with
                | "picking" -> { s with Phase = Picking }
                | "starting" -> { s with Phase = Starting }
                | "running" -> { s with Phase = Running; Countdown = 0 }
                | "stopped" -> { s with Phase = Idle }
                | _ -> s
            | "@pick" when parts.Length > 1 -> { s with Phase = Picking; Countdown = max 0 (number parts[1]) }
            | "@source" when parts.Length > 5 ->
                { s with Width = number parts[2]; Height = number parts[3]; Api = parts[4]; Placement = parts[5] }
            | "@fps" when parts.Length > 2 -> { s with BaseFps = number parts[1]; OutputFps = number parts[2] }
            | "@mfg" when parts.Length > 1 ->
                match parts[1] with
                | "off" -> { s with Multiplier = 0; MfgUnavailable = false; MfgWhy = "" }
                | "unavailable" ->
                    let why = if parts.Length > 2 then String.Join(" ", parts |> Array.skip 2) else ""
                    { s with Multiplier = 0; MfgUnavailable = true; MfgWhy = why }
                | value -> { s with Multiplier = max 0 (number value); MfgUnavailable = false; MfgWhy = "" }
            | "@vrr" when parts.Length > 1 -> { s with VrrFixed = (parts[1] = "fixed") }
            | "@aml" when parts.Length > 1 ->
                let learning =
                    match parts[1] with
                    | "learned" -> 101
                    | "learning" when parts.Length > 2 -> Math.Clamp(number parts[2], 0, 100)
                    | _ -> -1
                { s with Learning = learning }
            | "@error" when parts.Length > 1 -> if s.Error = "" then { s with Error = parts[1] } else s
            | _ -> s

    /// "@card <n> <dlss 0/1> <MB> <name>" and "@xess <n> <0/1>" lines into cards.
    /// `xessKnown`: XeSS's files are in place, so a "@xess n 0" is Intel's own
    /// answer; without them the engine cannot ask, and every card is offered.
    let parseCards (xessKnown: bool) (lines: string seq) : MotionCard array =
        let lines = lines |> Seq.map (fun l -> l.Trim()) |> Seq.toArray
        let refused =
            lines |> Array.choose (fun line ->
                let p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                if p.Length = 3 && p[0] = "@xess" && p[2] = "0" then Some(number p[1]) else None)
        let seen = Collections.Generic.Dictionary<string, int>()
        lines |> Array.choose (fun line ->
            let p = line.Split(' ', 5, StringSplitOptions.RemoveEmptyEntries)
            if p.Length = 5 && p[0] = "@card" && number p[1] >= 0 then
                let name = p[4].Trim()
                let count = (match seen.TryGetValue name with | true, n -> n | _ -> 0) + 1
                seen[name] <- count
                Some { Index = number p[1]; Dlss = (p[2] = "1")
                       Xess = (not xessKnown) || not (Array.contains (number p[1]) refused)
                       Name = (if count > 1 then sprintf "%s (%d)" name count else name) }
            else None)

module MotionCommand =
    let private flag value = if value then "1" else "0"

    /// Can this generator run on that card? (2 SMFG, 3 SMAI, 4 SUITE FG, 5 FSR FG and 6..11 SUITE's more run on any.)
    let runsOn (generator: int) (card: MotionCard) =
        match generator with
        | 0 -> card.Dlss
        | 1 -> card.Xess
        | _ -> true

    /// The engine's number for the page's generator: 0 DLSS MFG, 1 XeSS MFG, 2 SMFG (and SMAI, by msm_ai), 3 SUITE FG,
    /// 4 FSR FG, 5..10 SUITE's more (Repeat, Crossfade, MEMC, DIS, Motion-blur blend, Spacewarp - sm_options.h).
    let engineGenerator (generator: int) =
        match generator with
        | 3 -> 2
        | g when g >= 4 -> g - 1
        | g -> g

    /// A profile in the engine's own words - his optionLines, line for line,
    /// less xess_tensor (lf_dlssg_card.inl says why it is gone).
    let optionLines (cards: MotionCard array) (o: MotionOptions) : (string * string) list =
        let card =
            cards |> Array.tryFind (fun c -> c.Name = o.Gpu && runsOn o.Generator c)
            |> Option.map (fun c -> c.Index) |> Option.defaultValue -1
        let tenths = MotionPrefs.multiplierTenths o
        // Spacewarp goes by whole multipliers (its frames come after each real frame, a step apart): Adaptive and
        // x2.5-like ones are run as the whole multiplier under them.
        let ahead = MotionPrefs.extrapolates o.Generator
        let tri list which kept = string (MotionPrefs.triValue list which kept)
        [ // SMAI is the engine's SMFG path (generator 2) with the network making the frames (msm_ai 1); SUITE FG and
          // FSR FG are the engine's generators 3 and 4.
          "generator", string (engineGenerator o.Generator)
          "gpu", string card
          "mfg", flag o.FrameGeneration
          // Adaptive and x2.5-like multipliers go by the beat: the generator is made for x6's most.
          "multiplier", string (if ahead then (if o.Adaptive then 6 else max 2 (tenths / 10)) elif o.Adaptive || tenths % 10 <> 0 then 6 else tenths / 10)
          "ratio", (if ahead || o.Adaptive || tenths % 10 = 0 then "0" else string tenths)
          "adaptive", (if ahead || not o.Adaptive then "0" elif o.AdaptiveFps >= 30 then string o.AdaptiveFps else "1")
          "flow", string (max 25 o.Flow)
          "game", string o.GameType
          "queue", string o.QueueTarget
          "still", flag o.StillPixels
          "fixed_beat", flag (not o.PairPacing)
          "smooth", flag o.UltraSmooth
          // SMT: with SMFG its own switch, with DLSS / XeSS MFG another; never with SMAI.
          "msm_tri", flag (match o.Generator with | 2 -> o.TriangleMesh | 0 | 1 -> o.SmtAssist | _ -> false)
          "tri_pitch", tri MotionPrefs.triPitches 0 o.TriPitch
          "tri_rounds", tri MotionPrefs.triRounds 1 o.TriRounds
          "tri_fine", tri MotionPrefs.triFines 2 o.TriFine
          "tri_full", tri MotionPrefs.triFulls 3 o.TriFull
          "tri_home", tri MotionPrefs.triHomes 4 o.TriHome
          "tri_top", tri MotionPrefs.triRanges 5 o.TriRange
          "tri_exact", tri MotionPrefs.triExacts 6 o.TriExact
          "tri_relax", tri MotionPrefs.triRelaxes 7 o.TriRelax
          "guard", "0"
          "msm_search", string o.MsmSearch
          "aml", flag o.AdaptiveLearning
          "aml_flow", flag o.AdaptiveFlow
          "aml_guide", flag (not o.AmlGuideOff)
          // The app's order (driver, C, B, A) into the engine's (driver, A, B, C).
          "dlssg_preset", string ([| 0; 3; 2; 1 |][Math.Clamp(o.DlssPreset, 0, 3)])
          "frame_limit", string o.FrameLimit
          "game_fps", string o.GameFps
          "multi_display", flag o.MultiDisplay
          "crop_l", string o.CropLeft
          "crop_t", string o.CropTop
          "crop_r", string o.CropRight
          "crop_b", string o.CropBottom
          "cursor_clip", flag o.CursorClip
          "cursor_speed", flag o.CursorSpeed
          "cursor_hide", flag o.CursorHide
          "cursor_scale", flag o.CursorScale
          "screen_limit", flag o.LimitToScreen
          "vrr", flag o.Vrr
          "lock", flag o.LockRefresh
          "sync", string o.SyncMode
          "low_latency", flag o.LowLatency
          "msm_ai", flag (o.Generator = 3)
          "hdr", flag o.Hdr
          "gpu_class", (if o.GpuPriority = 0 then "1" else "0")
          "counter", (if not o.FpsCounter then "0" elif o.FpsCounterAdvanced then "2" else "1")
          "counter_size", string o.FpsCounterSize
          "spout", flag o.Spout
          "upscaler", string o.Upscaler
          "smsr_strength", string (100 + o.SmsrBoost)
          "sharpness", string o.Sharpness
          "bloom", string o.Bloom
          "bloom_wide", string o.BloomWide
          "over_sharpness", string o.OverSharpness
          "stretch", flag o.Stretch
          "capture_api", string o.CaptureApi
          // The engine's numbers: 2 the generator's own, 0 NVIDIA Optical Flow, 1 the GPU estimate.
          "motion",
          (match o.Generator with
           | 1 -> (match o.XessMotionSource with | 1 -> "1" | 2 -> "0" | _ -> "2")
           | 2 | 3 -> "2"
           // SUITE's more find their own (suite_xfg.inl).
           | g when MotionPrefs.isMore g -> "2"
           // SUITE FG: the GPU estimate (the Screen Engine's builtin) or NVIDIA Optical Flow (its nvof).
           | 4 -> (match o.SuiteMotionSource with | 1 -> "0" | _ -> "1")
           // FSR FG: the GPU estimate, FSR's own optical flow alone (a field of noughts), or NVIDIA Optical Flow.
           | 5 -> (match o.FsrMotionSource with | 1 -> "2" | 2 -> "0" | _ -> "1")
           | _ -> (match o.MotionSource with | 1 -> "0" | 2 -> "1" | _ -> "2"))
          "window_mode", string o.WindowMode
          "delay", string MotionPrefs.countdowns[Math.Clamp(o.Countdown, 0, MotionPrefs.countdowns.Length - 1)] ]

    /// What Adaptive Motion Learning's study writes on the game's frame.
    let studyTexts =
        [ "aml_say0", "Adaptive Motion Learning · {0}% · about {1} s"
          "aml_say1", "Stand still. Move the camera left and right."
          "aml_say2", "Now move the camera up and down."
          "aml_say3", "Now walk forward."
          "aml_say4", "Adaptive Motion Learning · done"
          "aml_say5", "Now drive a car. No car? Just stand still."
          "aml_say6", "Drive straight ahead."
          "aml_say7", "Now take some turns."
          "aml_say8", "Move left and right."
          "aml_say9", "Learning costs a little performance. It gets better when done."
          "aml_say10", "Now walk backward."
          "aml_say11", "Keep moving. Switch weapons or items a few times."
          "aml_say12", "Now run: hold your run button while you move."
          "aml_say13", "Now aim: hold your aim button and move the camera."
          "aml_say14", "Now play as you like for 30 seconds." ]

    /// One value made safe for a line of its own: the engine reads up to the
    /// line's end, and takes what follows a '#' for a remark.
    let private clean (value: string) =
        if isNull value then "" else value.Replace('\n', ' ').Replace('\r', ' ').Replace('#', ' ')

    /// The start file: the options, the study's texts, the log switch and the
    /// app's process id (the engine ends with its owner).
    let startFile (options: (string * string) list) (texts: (string * string) list) (keepLog: bool) (owner: int) (pickNow: bool) =
        let b = StringBuilder()
        for key, value in options do
            let value = if pickNow && key = "delay" then "0" else value
            b.Append(key).Append('=').Append(clean value).Append('\n') |> ignore
        for key, value in texts do
            b.Append(key).Append('=').Append(clean value).Append('\n') |> ignore
        b.Append("log=").Append(if keepLog then "1" else "0").Append('\n') |> ignore
        b.Append("owner=").Append(owner).Append('\n') |> ignore
        b.ToString()

    /// DLSS MFG runs through NVIDIA Streamline from SUITE's engine folder: the
    /// engine is told where it is (it checks NVIDIA's signature on each file
    /// before it loads them). Only for DLSS MFG - no other generator loads it.
    let withStreamline (streamlineDir: string) (options: (string * string) list) =
        match options |> List.tryFind (fun (k, _) -> k = "generator") with
        | Some(_, "0") -> options @ [ "sl_dir", streamlineDir ]
        | _ -> options

    /// FSR FG loads AMD's FidelityFX runtime from SUITE's engine folder: the engine is told where it is (it checks
    /// AMD's signature before it loads it). Only for FSR FG.
    let withFidelityFx (fidelityFxDir: string) (options: (string * string) list) =
        match options |> List.tryFind (fun (k, _) -> k = "generator") with
        | Some(_, "4") -> options @ [ "ffx_dir", fidelityFxDir ]
        | _ -> options

    /// The lines whose value moved since `sent` - only those go to a running engine.
    let changes (sent: (string * string) list) (now: (string * string) list) =
        let before = dict sent
        now |> List.filter (fun (k, v) -> not (before.ContainsKey k) || before[k] <> v)

/// Where Motion Forge keeps its files, and the files the user supplies.
module MotionFiles =
    let dataDir () = MotionPrefs.dataDir ()
    let engineDataDir () = Path.Combine(dataDir (), "Engine")
    let learnedDir () = Path.Combine(dataDir (), "Learned")
    let userDir () = Path.Combine(dataDir (), "user")
    let aiDir () = Path.Combine(dataDir (), "ai")

    [<Literal>]
    let EngineFileName = "MotionForgeEngine.exe"

    /// Shipped in its own folder under engine\, apart from the Screen Engine.
    let enginePath () = Path.Combine(AppContext.BaseDirectory, "engine", "motion", EngineFileName)

    let isEngineAvailable () = File.Exists(enginePath ())

    let xessDir () = Path.Combine(userDir (), "xess")
    let xessFiles = [ "libxess_fg.dll"; "libxell.dll" ]

    let hasXess () = xessFiles |> List.forall (fun f -> File.Exists(Path.Combine(xessDir (), f)))

    /// DLSS MFG: NVIDIA Streamline 2.14.16 and its DLSS Frame Generation runtime,
    /// shipped in SUITE's engine\ folder beside the Screen Engine (tools/build-setup.sh
    /// copies them from NVIDIA's release). Nothing for the user to pick.
    let streamlineFiles =
        [ "sl.interposer.dll"; "sl.common.dll"; "sl.dlss_g.dll"; "sl.reflex.dll"; "sl.pcl.dll"; "nvngx_dlssg.dll" ]

    let streamlineDir () = Path.Combine(AppContext.BaseDirectory, "engine")

    /// The Streamline files missing from a folder, in the engine's order.
    let missingStreamline (exists: string -> bool) (folder: string) =
        streamlineFiles |> List.filter (fun f -> not (exists (Path.Combine(folder, f))))

    let hasStreamline () = (missingStreamline File.Exists (streamlineDir ())).IsEmpty

    /// FSR FG: AMD's FidelityFX runtime (SDK 1.1.4, MIT), shipped in SUITE's engine\ folder beside the Screen
    /// Engine (tools/get-fidelityfx.ps1 fetches it, tools/build-setup.sh copies it). Nothing for the user to pick.
    [<Literal>]
    let FidelityFxFile = "amd_fidelityfx_dx12.dll"

    let fidelityFxDir () = streamlineDir ()

    let hasFidelityFx () = File.Exists(Path.Combine(fidelityFxDir (), FidelityFxFile))

    /// Who signed a file, by Authenticode; "" when it is not signed. (The
    /// engine checks the whole chain again before it maps the file - this is
    /// only so a wrong pick is told at once.)
    let signer (path: string) =
        try
            let cert = X509Certificate.CreateFromSignedFile(path)
            use cert2 = new X509Certificate2(cert)
            let subject = cert2.GetNameInfo(X509NameType.SimpleName, false)
            if isNull subject then "" else subject
        with _ -> ""

    let private version (path: string) =
        try FileVersionInfo.GetVersionInfo(path).FileVersion with _ -> ""

    /// Copies Intel's libxess_fg.dll and libxell.dll (XeSS SDK 2.1 and later)
    /// from the files the user picked into SUITE's data folder.
    let pickXess (sources: string list) : Result<string, string> =
        try
            let find name = sources |> List.tryFind (fun s -> Path.GetFileName(s).Equals(name, StringComparison.OrdinalIgnoreCase))
            match xessFiles |> List.filter (fun n -> (find n).IsNone) with
            | missing :: _ -> Error(sprintf "Pick both libxess_fg.dll and libxell.dll (from Intel's XeSS SDK) - %s is missing." missing)
            | [] ->
                let picked = xessFiles |> List.map (fun n -> n, (find n).Value)
                match picked |> List.tryFind (fun (_, s) -> signer s <> "Intel Corporation") with
                | Some(n, _) -> Error(sprintf "%s is not signed by Intel, so it cannot be used." n)
                | None ->
                    Directory.CreateDirectory(xessDir ()) |> ignore
                    for n, s in picked do File.Copy(s, Path.Combine(xessDir (), n), true)
                    Ok(sprintf "XeSS Frame Generation %s is in place for XeSS MFG." (version (snd picked.Head)))
        with ex -> Error ex.Message

    let removeXess () = for n in xessFiles do (try File.Delete(Path.Combine(xessDir (), n)) with _ -> ())

/// SMAI's runtime and network, downloaded on request into the data folder
/// (about 45 MB; too large to ship with every copy of SUITE for one generator).
///
/// Every file comes from NODIX TECH's own MFG SUPER MOTION repository at the
/// 1.1.0 commit, pinned by commit and by SHA-256:
///   https://github.com/NODIX-TECH/MFG-SUPER-MOTION/tree/7b3a71f11683f04c33edb535752bf4ad69547cb3/Engine/runtime/ai
/// - onnxruntime.dll: Microsoft ONNX Runtime 1.20.1 with DirectML (MIT)
/// - DirectML.dll: Microsoft DirectML 1.15.4 (Microsoft's DirectML licence,
///   which allows shipping it with an application)
/// - smfg-net.onnx, smfg-ai-head.onnx, smfg-ai-block0..4.onnx: SMFG Net and
///   the RIFE-derived network split into stages (RIFE: MIT, hzwer)
/// with each licence file beside them.
module MotionAi =
    let private commit = "7b3a71f11683f04c33edb535752bf4ad69547cb3"
    let private baseUrl = sprintf "https://github.com/NODIX-TECH/MFG-SUPER-MOTION/raw/%s/Engine/runtime/ai/" commit

    /// name, SHA-256, size
    let files =
        [ "onnxruntime.dll", "903C92C54ACC57CAA77D44D4C856C829EB82F5CF6755B47367A8153BD3FABEB6", 14682160L
          "DirectML.dll", "9C9E6D822561C6C41B90E6994B3E8857CF1D66DBFB1E0C4C799C7C89B4E92DA1", 18527776L
          "smfg-net.onnx", "392C6913B6FF57A203D518F0C908B4898D1DA859E5A1DA6AF5E021EFF79B7E9B", 1111052L
          "smfg-ai-head.onnx", "5C34073CC9ED3D64DB1B60D044970361C79407EDC956FCC2CD354D18F9DC8C0D", 13543L
          "smfg-ai-block0.onnx", "D216B7E1F5F55B561F8E21917FBAF06C4ECCF5500B0B7527B0E2FD6A1346E25C", 6000705L
          "smfg-ai-block1.onnx", "35A2024B3D8B7CBF065272962C4C926372CB476EE48257B5EF614458533F9D16", 2764865L
          "smfg-ai-block2.onnx", "2F64FF323264B7C2152D72E1158994BDDEB43E8DDF0EC1D78B9BD144400BE1D1", 1605693L
          "smfg-ai-block3.onnx", "102563A79CA15C1DF059B9477B50FB44494047CB2FDBABD64EB0AAD92ACC5F6B", 759901L
          "smfg-ai-block4.onnx", "E9AE49C9CF5E1F3B8C38E4EA3F403C32AD7E600030561521FA28FA223831813F", 143249L
          "LICENSE-onnxruntime.txt", "2F07C72751AED99790B8A4869CF2311DF85A860B22DED05FA22803587A48922C", 1073L
          "LICENSE-DirectML.txt", "5EBF0876AB82EBE7CBDD34C4BD89F9FD2ADF65EC8BA3236066F85B281AE8EA2C", 10392L
          "LICENSE-RIFE.txt", "7932FB49341512B959B1744A6D9CBB39E5A1EC89DA438A34D0454D5D8DF9FECD", 1062L
          "ThirdPartyNotices-onnxruntime.txt", "CF7342F7BA482EF715AE58F5F497A8D3564FA255164175AEA324CD293C5701A0", 338538L ]

    let totalBytes = files |> List.sumBy (fun (_, _, size) -> size)

    /// Every file there at its size. (The hash was checked when it came in.)
    let isInstalled () =
        files |> List.forall (fun (name, _, size) ->
            let p = Path.Combine(MotionFiles.aiDir (), name)
            File.Exists p && FileInfo(p).Length = size)

    /// Downloads what is missing; `progress` gets text and 0..1.
    let install (progress: string -> float -> unit) : Task<Result<unit, string>> =
        task {
            try
                use client = new HttpClient(Timeout = TimeSpan.FromMinutes 10.0)
                client.DefaultRequestHeaders.UserAgent.ParseAdd("DLSS5-SUITE")
                let total = float totalBytes
                let mutable before = 0L
                for name, digest, size in files do
                    let start = float before / total
                    let share = float size / total
                    do! VerifiedDownload.download client name (baseUrl + name) digest size
                            (Path.Combine(MotionFiles.aiDir (), name)) progress start share
                    before <- before + size
                progress "SMAI is ready." 1.0
                return Ok()
            with ex -> return Error ex.Message
        }

    let remove () =
        for name, _, _ in files do
            try File.Delete(Path.Combine(MotionFiles.aiDir (), name)) with _ -> ()

/// What Adaptive Motion Learning keeps for one game (a few lines of text).
type LearnedGame =
    { File: string
      Game: string
      Percent: int
      Learned: bool
      Updated: string }

module LearnedGames =
    /// Every game something is kept for, the last played first.
    let list () =
        try
            let folder = MotionFiles.learnedDir ()
            if not (Directory.Exists folder) then [||]
            else
                Directory.GetFiles(folder, "*.aml")
                |> Array.sortByDescending File.GetLastWriteTimeUtc
                |> Array.truncate 200
                |> Array.choose (fun file ->
                    try
                        let lines =
                            File.ReadLines file |> Seq.truncate 12
                            |> Seq.choose (fun line ->
                                match line.IndexOf '=' with
                                | -1 -> None
                                | at -> Some(line.Substring(0, at), line.Substring(at + 1).Trim()))
                            |> Seq.distinctBy fst
                            |> dict
                        let value key = match lines.TryGetValue key with | true, v -> v | _ -> ""
                        let number key = match Int32.TryParse(value key) with | true, v -> v | _ -> 0
                        Some { File = file
                               Game = (if value "game" = "" then Path.GetFileNameWithoutExtension file else value "game")
                               Percent = Math.Clamp(number "percent", 0, 100)
                               Learned = (value "learned" = "1")
                               Updated = value "updated" }
                    with _ -> None)
        with _ -> [||]

    /// The game's file and its HUD mask beside it.
    let delete (game: LearnedGame) =
        try
            if File.Exists game.File then File.Delete game.File
            let hud = Path.ChangeExtension(game.File, ".hud")
            if File.Exists hud then File.Delete hud
        with _ -> ()

    /// MFG SUPER MOTION's own learnt games, copied over once (never over ours).
    let importFromSuperMotion () =
        try
            let theirs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MFG SUPER MOTION", "Learned")
            if not (Directory.Exists theirs) then 0
            else
                Directory.CreateDirectory(MotionFiles.learnedDir ()) |> ignore
                let mutable n = 0
                for f in Directory.GetFiles(theirs) do
                    if f.EndsWith(".aml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".hud", StringComparison.OrdinalIgnoreCase) then
                        let target = Path.Combine(MotionFiles.learnedDir (), Path.GetFileName f)
                        if not (File.Exists target) then
                            File.Copy(f, target)
                            if f.EndsWith(".aml", StringComparison.OrdinalIgnoreCase) then n <- n + 1
                n
        with _ -> 0

/// The pointer as it was: the engine's Hide / Scale cursor swap Windows'
/// pointers and Adjust cursor speed sets its speed, and put both back when
/// they end - or, ended from outside, leave a note (cursor.state) read here.
module MotionCursor =
    [<DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")>]
    extern bool private SystemParametersInfo(uint32 action, uint32 param, nativeint value, uint32 winIni)

    let restoreFromNote () =
        let note = Path.Combine(MotionFiles.engineDataDir (), "cursor.state")
        try
            if File.Exists note then
                let values =
                    File.ReadAllLines note
                    |> Array.choose (fun line -> match line.Split('=') with | [| k; v |] -> Some(k.Trim(), v.Trim()) | _ -> None)
                    |> dict
                if values.ContainsKey "cursors" && values["cursors"] = "1" then SystemParametersInfo(0x0057u, 0u, 0n, 0u) |> ignore // SPI_SETCURSORS
                match values.TryGetValue "speed" with
                | true, v ->
                    match Int32.TryParse v with
                    | true, speed when speed >= 1 && speed <= 20 -> SystemParametersInfo(0x0071u, 0u, nativeint speed, 0u) |> ignore // SPI_SETMOUSESPEED
                    | _ -> ()
                | _ -> ()
                File.Delete note
        with _ -> ()

/// The main screen's refresh rate: what Adaptive aims at when no target is set.
module MotionScreen =
    [<DllImport("user32.dll")>]
    extern nativeint private GetDC(nativeint window)
    [<DllImport("user32.dll")>]
    extern int private ReleaseDC(nativeint window, nativeint dc)
    [<DllImport("gdi32.dll")>]
    extern int private GetDeviceCaps(nativeint dc, int index)

    let refreshRate () =
        try
            let dc = GetDC 0n
            let rate = GetDeviceCaps(dc, 116) // VREFRESH
            ReleaseDC(0n, dc) |> ignore
            if rate >= 30 && rate <= 1000 then rate else 60
        with _ -> 60

/// START / STOP while a game is in front: a system-wide shortcut, registered
/// on a thread of its own (RegisterHotKey with no window posts WM_HOTKEY to
/// the registering thread's queue), so no window of the app is needed.
type MotionHotkey() =
    [<DllImport("user32.dll", SetLastError = true)>]
    static extern bool RegisterHotKey(nativeint window, int id, uint32 modifiers, uint32 key)
    [<DllImport("user32.dll")>]
    static extern bool UnregisterHotKey(nativeint window, int id)
    [<DllImport("user32.dll")>]
    static extern int GetMessageW(MSG& msg, nativeint window, uint32 first, uint32 last)
    [<DllImport("user32.dll")>]
    static extern bool PostThreadMessageW(uint32 thread, uint32 msg, nativeint w, nativeint l)
    [<DllImport("kernel32.dll")>]
    static extern uint32 GetCurrentThreadId()

    let pressed = Event<unit>()
    let mutable threadId = 0u
    let mutable wanted = 0
    let mutable registered = false
    let ready = new ManualResetEventSlim(false)
    static let WM_HOTKEY = 0x0312u
    static let WM_APP_SET = 0x8001u
    static let WM_APP_QUIT = 0x8002u
    static let MOD_NOREPEAT = 0x4000u

    let apply () =
        UnregisterHotKey(0n, 1) |> ignore
        registered <-
            wanted > 0
            && RegisterHotKey(0n, 1, (uint32 (wanted >>> 16) &&& 0xFu) ||| MOD_NOREPEAT, uint32 (wanted &&& 0xFFFF))

    let loop () =
        threadId <- GetCurrentThreadId()
        let mutable msg = MSG()
        // (The queue exists once the thread has asked for a message; the set
        // below is posted only after this.)
        ready.Set()
        let mutable running = true
        while running && GetMessageW(&msg, 0n, 0u, 0u) > 0 do
            if msg.message = WM_HOTKEY then (try pressed.Trigger() with _ -> ())
            elif msg.message = WM_APP_SET then apply ()
            elif msg.message = WM_APP_QUIT then running <- false
        UnregisterHotKey(0n, 1) |> ignore

    let thread = Thread(ThreadStart(loop), IsBackground = true, Name = "Motion Forge shortcut")
    do
        thread.Start()
        ready.Wait(2000) |> ignore

    /// Raised on the shortcut's own thread.
    member _.Pressed = pressed.Publish
    /// modifiers * 65536 + virtual key; 0 or less turns it off.
    member _.Set(binding: int) =
        wanted <- binding
        PostThreadMessageW(threadId, WM_APP_SET, 0n, 0n) |> ignore
    member _.IsRegistered = registered

    interface IDisposable with
        member _.Dispose() = PostThreadMessageW(threadId, WM_APP_QUIT, 0n, 0n) |> ignore

and [<Struct; StructLayout(LayoutKind.Sequential)>] MSG =
    val mutable hwnd: nativeint
    val mutable message: uint32
    val mutable wParam: nativeint
    val mutable lParam: nativeint
    val mutable time: uint32
    val mutable ptX: int
    val mutable ptY: int

/// Starts, steers and stops the engine process.
type MotionEngineHost() =
    let gate = obj ()
    let changed = Event<MotionStatus>()
    let mutable proc: Process = null
    let mutable status = MotionStatus.idle
    let mutable sent: (string * string) list = []
    let mutable stopping = false
    let mutable cards: MotionCard array = [||]

    do MotionCursor.restoreFromNote ()

    let publish next =
        let fire = lock gate (fun () -> if status = next then false else (status <- next; true))
        if fire then changed.Trigger next

    let onLine (text: string) =
        if not (isNull text) then
            let next = lock gate (fun () -> MotionStatus.apply status text)
            publish next

    let onExit (ended: Process) =
        let mine = lock gate (fun () ->
            if obj.ReferenceEquals(ended, proc) then
                proc <- null
                sent <- []
                true
            else false)
        if mine then
            MotionCursor.restoreFromNote ()
            let code = try ended.ExitCode with _ -> 0
            ended.Dispose()
            let last = lock gate (fun () -> status)
            let error = if last.Error <> "" then last.Error elif code <> 0 && not stopping then "failed" else ""
            publish { MotionStatus.idle with Error = error }

    /// One question of the engine's ("--cards", "--vrr", "--warm-shaders"),
    /// its output, within `ms`. An engine that does not know the question
    /// would start for real, so it is ended when the time is up.
    let ask (argument: string) (ms: int) : string[] =
        try
            let info =
                ProcessStartInfo(
                    FileName = MotionFiles.enginePath (),
                    Arguments = argument,
                    WorkingDirectory = Path.GetDirectoryName(MotionFiles.enginePath ()),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.UTF8)
            use asking = Process.Start info
            let reading = asking.StandardOutput.ReadToEndAsync()
            if reading.Wait ms then
                asking.WaitForExit 1000 |> ignore
                reading.Result.Split('\n')
            else
                (try asking.Kill() with _ -> ())
                [||]
        with _ -> [||]

    member _.Changed = changed.Publish
    member _.Status = lock gate (fun () -> status)
    member _.IsActive = lock gate (fun () -> not (isNull proc))
    member _.Cards = cards
    member _.LogPath = Path.Combine(MotionFiles.engineDataDir (), "engine.log")

    /// The cards as the engine sees them (asked once; slow - call off the UI thread).
    member _.LoadCards() =
        if cards.Length = 0 && MotionFiles.isEngineAvailable () then
            cards <- MotionStatus.parseCards (MotionFiles.hasXess ()) (ask "--cards" 6000)
        cards

    /// Asked again after XeSS's files come or go (Intel's library answers for each card).
    member this.ReloadCards() =
        cards <- [||]
        this.LoadCards()

    /// Is VRR on for a window on this PC: Some true / Some false, None when it cannot be told.
    member _.SystemVrr() =
        if not (MotionFiles.isEngineAvailable ()) then None
        else
            ask "--vrr" 6000
            |> Array.tryPick (fun line ->
                let p = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)
                if p.Length >= 2 && p[0] = "@vrr-system" then
                    Some(match p[1] with "1" -> Some true | "0" -> Some false | _ -> None)
                else None)
            |> Option.flatten

    member private _.ShaderMark() =
        let info = FileInfo(MotionFiles.enginePath ())
        Path.Combine(MotionFiles.engineDataDir (), sprintf "shaders-%d-%d.ready" info.Length info.LastWriteTimeUtc.Ticks)

    /// The shaders compiled once per engine build, so the first SMFG start
    /// does not stand some 13 s compiling them.
    member this.ShadersNeedWarm() = MotionFiles.isEngineAvailable () && not (File.Exists(this.ShaderMark()))

    member this.WarmShaders() =
        let said = ask "--warm-shaders" 120000
        let text = String.Join("\n", said)
        if text.StartsWith "warm " then
            Directory.CreateDirectory(MotionFiles.engineDataDir ()) |> ignore
            File.WriteAllText(this.ShaderMark(), text)

    /// Starts the engine for a profile. `pickNow`: take the window in front at
    /// once (the shortcut pressed in a game), no countdown.
    member this.Start(options: MotionOptions, keepLog: bool, pickNow: bool) =
        if this.IsActive then true
        elif not (MotionFiles.isEngineAvailable ()) then
            publish { MotionStatus.idle with Error = "missing" }
            false
        else
            try
                let folder = MotionFiles.engineDataDir ()
                Directory.CreateDirectory folder |> ignore
                let lines = MotionCommand.optionLines cards options
                // DLSS MFG runs the DLSS-G version picked for Motion Forge (DlssgVersions): the
                // bundled one from engine\, any other from a folder of its own in the data folder.
                let streamlineDir =
                    if lines |> List.contains ("generator", "0") then fst (DlssgVersions.streamlineDirFor DlssgVersions.MotionForge)
                    else MotionFiles.streamlineDir ()
                let startLines =
                    MotionCommand.withStreamline streamlineDir lines
                    |> MotionCommand.withFidelityFx (MotionFiles.fidelityFxDir ())
                let startFile = Path.Combine(folder, "start.cfg")
                File.WriteAllText(startFile, MotionCommand.startFile startLines MotionCommand.studyTexts keepLog Environment.ProcessId pickNow, UTF8Encoding(false))
                let info =
                    ProcessStartInfo(
                        FileName = MotionFiles.enginePath (),
                        Arguments = sprintf "--config \"%s\"" startFile,
                        WorkingDirectory = Path.GetDirectoryName(MotionFiles.enginePath ()),
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        StandardOutputEncoding = Encoding.UTF8)
                let started = new Process(StartInfo = info, EnableRaisingEvents = true)
                started.OutputDataReceived.Add(fun a -> onLine a.Data)
                started.Exited.Add(fun _ -> onExit started)
                stopping <- false
                publish { MotionStatus.idle with Phase = Starting }
                lock gate (fun () -> proc <- started; sent <- lines)
                if not (started.Start()) then failwith "not started"
                started.BeginOutputReadLine()
                true
            with _ ->
                lock gate (fun () -> proc <- null; sent <- [])
                publish { MotionStatus.idle with Error = "failed" }
                false

    /// Sends the running engine the controls that moved since the last time.
    member _.Send(options: MotionOptions) =
        let running, moved =
            lock gate (fun () ->
                if isNull proc then null, []
                else
                    let lines = MotionCommand.optionLines cards options
                    let moved = MotionCommand.changes sent lines
                    sent <- lines
                    proc, moved)
        if not (isNull running) && not moved.IsEmpty then
            try
                for key, value in moved do running.StandardInput.WriteLine(key + "=" + value)
                running.StandardInput.Flush()
            with :? IOException | :? InvalidOperationException | :? ObjectDisposedException -> ()

    /// A game's learning deleted: an engine playing it starts it from nothing.
    member _.Forget(game: string) =
        let running = lock gate (fun () -> proc)
        if not (isNull running) && not (String.IsNullOrWhiteSpace game) then
            try
                running.StandardInput.WriteLine("aml_forget=" + game.Replace('\n', ' ').Replace('\r', ' '))
                running.StandardInput.Flush()
            with :? IOException | :? InvalidOperationException | :? ObjectDisposedException -> ()

    /// Asks the engine to stop (it puts the game's window back as it was); one
    /// that does not answer is told through its event, then ended.
    member _.Stop() =
        let running = lock gate (fun () -> proc)
        if not (isNull running) then
            stopping <- true
            try
                running.StandardInput.WriteLine "stop"
                running.StandardInput.Flush()
            with :? IOException | :? InvalidOperationException | :? ObjectDisposedException -> ()
            ThreadPool.QueueUserWorkItem(fun _ ->
                try
                    if not (running.WaitForExit 4000) then
                        try
                            use stop = EventWaitHandle.OpenExisting "Local\\DLSS5SuiteMotionStop"
                            stop.Set() |> ignore
                        with _ -> ()
                        if not (running.WaitForExit 3000) then running.Kill()
                with _ -> ()) |> ignore

    /// For the app's own exit: waits (briefly) so the game's window is put back.
    member _.StopNow() =
        let running = lock gate (fun () -> proc)
        if not (isNull running) then
            stopping <- true
            try
                running.StandardInput.WriteLine "stop"
                running.StandardInput.Flush()
                if not (running.WaitForExit 3000) then running.Kill()
            with _ -> ()

    interface IDisposable with
        member this.Dispose() = this.StopNow()
