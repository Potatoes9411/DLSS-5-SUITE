namespace DLSS_5_MANAGER.Models

open System
open System.IO
open System.Text.Json

/// Motion Forge's settings: one profile of MFG SUPER MOTION's (NODIX TECH's
/// Models/Preferences.fs, `DisplayOptions`), kept field for field so the
/// engine gets exactly what his app sent it and his saved profiles can be
/// read as they are (MotionPrefs.importFromSuperMotion).
///
/// Left out on purpose:
/// - XessTensor (XeSS MFG on an RTX card's tensor cores) went through the
///   RTX 20/30 compatibility package, which SUITE's engine does not carry.
/// - HudAi, MsmTensor, MsmAi were already not offered in his 1.1.0 (SMAI
///   replaced the last two; SMHUD is put aside). A file that still has them
///   is read; they are ignored. MotionGuard came back in his 1.3.8 and is
///   offered again.
[<CLIMutable>]
type MotionOptions =
    { FrameGeneration: bool
      Generator: int        // 0 DLSS MFG, 1 XeSS MFG, 2 SMFG, 3 SMAI, 4 SUITE FG, 5 FSR 3.1 FG, 6 Repeat, 7 Crossfade, 8 MEMC, 9 DIS, 10 Motion-blur blend, 11 Spacewarp
      Mode: int             // the Fixed multiplier: 0..4 = x2..x6
      MultiplierCustom: int // "Custom": tenths, 11..60 (x1.1..x6.0); 0 = Mode's whole one
      Flow: int             // percent of the picture, used when GameType is Custom
      Upscaler: int         // the engine's numbers: 0 off, 1 sharpening only, 2 integer, 3 nearest, 4 Anime4K, 5 SMSR 1.0
      Sharpness: int
      OverSharpness: int
      Schema: int
      CaptureApi: int       // 0 DXGI, 1 WGC
      WindowMode: int       // 0 borderless, 1 scaled to screen, 2 windowed
      Countdown: int        // index into MotionPrefs.countdowns
      QueueTarget: int      // 0 least delay .. 2 smoothest
      FrameLimit: int       // 0 off, 20..360
      GameFps: int          // 0 auto, 20..500
      Adaptive: bool        // Mode "Adaptive": frames at an even beat of AdaptiveFps
      AdaptiveFps: int      // 30..1000, 0 = the screen's refresh
      MultiDisplay: bool
      CropLeft: int
      CropTop: int
      CropRight: int
      CropBottom: int
      CursorClip: bool
      CursorSpeed: bool
      CursorHide: bool
      CursorScale: bool
      LimitToScreen: bool
      Vrr: bool
      SyncMode: int         // 0 off (tearing), 1 default, 2 VSync, 3 VSync/2, 4 VSync/3
      LowLatency: bool
      Hdr: bool
      GpuPriority: int      // 0 realtime, 1 normal
      Gpu: string           // "Preferred GPU" by name; "" lets the engine pick
      MotionSource: int     // DLSS MFG: 0 its own, 1 NVIDIA Optical Flow, 2 GPU estimate
      XessMotionSource: int // XeSS MFG: 0 its own, 1 GPU estimate, 2 NVIDIA Optical Flow
      SuiteMotionSource: int // SUITE FG: 0 GPU estimate, 1 NVIDIA Optical Flow (the Screen Engine's builtin / nvof)
      FsrMotionSource: int  // FSR FG: 0 GPU estimate, 1 FSR's own optical flow alone, 2 NVIDIA Optical Flow
      MsmSearch: int        // SMFG: 0 full, 1 fast
      Bloom: int
      BloomWide: int
      AdaptiveLearning: bool
      DlssPreset: int       // 0 driver, 1 Performance (C), 2 Quality (B), 3 Balanced (A)
      AdaptiveFlow: bool
      AmlGuideOff: bool
      VrrChosen: bool       // VRR set by the person: no longer follows what the PC says at each start
      Spout: bool
      StillPixels: bool
      MotionGuard: bool     // SMFG: the engine's fast motion guard ("guard"), offered again in his 1.3.8
      TriPitch: int
      TriRounds: int
      TriFine: int
      TriFull: int
      TriHome: int
      TriRange: int
      TriExact: int
      TriRelax: int
      TriangleMesh: bool    // "SMT" with SMFG
      SmtAssist: bool       // SMT handing its motion to DLSS MFG / XeSS MFG
      UltraSmooth: bool     // "Super Smooth"
      PairPacing: bool      // off = "Even pacing"
      FpsCounter: bool
      Stretch: bool
      LockRefresh: bool
      FpsCounterAdvanced: bool
      FpsCounterSize: int
      SmsrBoost: int        // -100..100
      GameType: int }       // index into gameFlows; the last is Custom

[<CLIMutable>]
type MotionProfile =
    { Id: string
      Name: string
      Options: MotionOptions }

/// What Motion Forge keeps between runs, in %LOCALAPPDATA%\DLSS5Suite\Motion\preferences.json.
[<CLIMutable>]
type MotionPrefs =
    { Version: int
      AdvancedOpen: bool
      EngineLog: bool
      /// The START / STOP shortcut: modifiers * 65536 + virtual key, as
      /// RegisterHotKey takes them; 0 the default, -1 none.
      Hotkey: int
      SelectedProfile: string
      Profiles: MotionProfile array }

module MotionPrefs =
    /// MFG SUPER MOTION 1.1.0's schema: a file from it (or older) is migrated
    /// by the same steps his app takes.
    let schema = 24
    let syncModes = 5
    let upscalers = 6
    let triPitches = [| 8; 12; 16; 24; 32 |]
    let triRounds = [| 1; 2; 3; 4 |]
    let triFines = [| 0; 1; 2; 3 |]
    let triFulls = [| 0; 1; 2; 3 |]
    let triHomes = [| 0; 2; 4; 8 |]
    let triRanges = [| 2; 3; 4 |]
    let triExacts = [| 0; 1; 2; 3 |]
    let triRelaxes = [| 0; 1; 2; 3 |]
    /// The engine's own default place in each list (pitch 16, 2 rounds, ...).
    let triDefaults = [| 2; 1; 2; 2; 2; 1; 2; 2 |]
    /// A setting is kept as its place + 1, so 0 (a file without it) is the default.
    let triPlace (list: int[]) (which: int) (kept: int) = if kept >= 1 && kept <= list.Length then kept - 1 else triDefaults[which]
    let triValue (list: int[]) (which: int) (kept: int) = list[triPlace list which kept]
    let generators = 12
    /// The generators SUITE added after MFG SUPER MOTION's four: the Screen Engine's own frame generation and
    /// AMD FSR 3.1 Frame Generation; then SUITE's more (MotionEngine/suite_xfg.inl, shared with the Screen Engine):
    /// Repeat, Crossfade, MEMC, DIS, Motion-blur blend and Spacewarp - every one any card, x2 to x6.
    let suiteFg = 4
    let fsrFg = 5
    let repeatFg = 6
    let crossfadeFg = 7
    let memcFg = 8
    let disFg = 9
    let blurFg = 10
    let spacewarpFg = 11
    /// SUITE's more: drawn in the engine from the two real frames, no files of anyone's.
    let isMore (generator: int) = generator >= repeatFg && generator <= spacewarpFg
    /// The ones of them that look for the motion (at Flow's share of the picture).
    let looksForMotion (generator: int) = generator >= memcFg && generator <= spacewarpFg
    /// Spacewarp's frames come after each real frame (extrapolated), by whole multipliers: not by Adaptive's beat.
    let extrapolates (generator: int) = generator = spacewarpFg
    let countdowns = [| 3; 5; 10 |]
    /// Flow % for each kind of game (the engine's own table, sm_options.h kGameKinds).
    let gameFlows = [| 35; 75; 50; 35; 35; 35; 35 |]
    let gameCustom = gameFlows.Length
    let frameLimitMax = 360
    let cropMax = 4000
    let gameFpsRates = [| 0; 30; 40; 45; 48; 50; 60; 72; 75; 90; 100; 120; 144; 165; 180; 200; 240; 280; 300; 360 |]
    /// Shift + Alt + M (Windows counts Alt 1, Ctrl 2, Shift 4, Win 8).
    let defaultHotkey = (0x0005 <<< 16) ||| 0x4D
    let multiplierCustomMost = 60
    let maxProfiles = 100

    let defaultOptions =
        { FrameGeneration = true; Generator = 2; Mode = 2; MultiplierCustom = 0; Flow = 35; Upscaler = 0; Sharpness = 50; OverSharpness = 0
          Schema = schema; CaptureApi = 1; WindowMode = 0; Countdown = 1
          QueueTarget = 1; FrameLimit = 0; GameFps = 0; Adaptive = false; AdaptiveFps = 0; MultiDisplay = false
          CropLeft = 0; CropTop = 0; CropRight = 0; CropBottom = 0
          CursorClip = false; CursorSpeed = false; CursorHide = false; CursorScale = false; LimitToScreen = false
          Vrr = false; SyncMode = 1; LowLatency = true; Hdr = false; GpuPriority = 0; Gpu = ""
          MotionSource = 0; XessMotionSource = 0; SuiteMotionSource = 0; FsrMotionSource = 0; MsmSearch = 1; Bloom = 0; BloomWide = 0
          AdaptiveLearning = false; DlssPreset = 2; AdaptiveFlow = true; AmlGuideOff = false; VrrChosen = false
          Spout = false; StillPixels = true; MotionGuard = false
          TriPitch = 0; TriRounds = 0; TriFine = 0; TriFull = 0; TriHome = 0; TriRange = 0; TriExact = 0; TriRelax = 0
          TriangleMesh = false; SmtAssist = false; UltraSmooth = false; PairPacing = false
          FpsCounter = false; Stretch = false; LockRefresh = false; FpsCounterAdvanced = false; FpsCounterSize = 100
          SmsrBoost = 0; GameType = 0 }
    // (SMFG, not DLSS MFG, is where a new profile starts: it runs on any card
    // and needs no file of NVIDIA's - his app moved a profile to it too when no
    // RTX card was found.)

    let defaultProfile = { Id = "default"; Name = "Default"; Options = defaultOptions }

    let defaults =
        { Version = 1; AdvancedOpen = false; EngineLog = false; Hotkey = 0
          SelectedProfile = defaultProfile.Id; Profiles = [| defaultProfile |] }

    /// The size frames in between are made at, percent of the picture.
    let effectiveFlow (o: MotionOptions) =
        if o.GameType >= 0 && o.GameType < gameCustom then
            // (XeSS MFG is made bigger than the kind of game says: the engine's FlowShare, sm_options.h.)
            if o.Generator = 1 then max gameFlows[o.GameType] 60 else gameFlows[o.GameType]
        else o.Flow

    /// The most frames shown for each frame of the game a generator makes: FSR 3.1 makes one in between a pair.
    let mostMultiplier (generator: int) = if generator = fsrFg then 2 else 6

    /// The Fixed multiplier in tenths: Mode's whole one, or the Custom one.
    let multiplierTenths (o: MotionOptions) =
        if o.MultiplierCustom > 0 then o.MultiplierCustom else (o.Mode + 2) * 10

    let normalizeFrameLimit value = if value < 20 then 0 else min frameLimitMax value

    /// MFG SUPER MOTION's normalizeOptions, step for step (the reasons for each
    /// step are in his Models/Preferences.fs), so one of his saved profiles is
    /// read here as his app would read it.
    let normalizeOptions (options: MotionOptions) =
        if isNull (box options) then defaultOptions
        else
            let o = options
            let o = if o.Schema < 10 then { o with Upscaler = 0; OverSharpness = 0 } else o
            let o = if o.Schema < 11 then { o with SyncMode = 1 } else o
            let o = if o.Schema < 14 then { o with MsmSearch = 1 } else o
            let o = if o.Schema < 15 then { o with AdaptiveFlow = true } else o
            let o = if o.Schema < 18 then { o with DlssPreset = 2 } else o
            let o = if o.Schema < 19 then { o with CaptureApi = 1 } else o
            let o = if o.Schema < 20 then { o with LockRefresh = true } else o
            let o = if o.Schema < 21 then { o with LockRefresh = false } else o
            let o = if o.Schema < 22 then { o with UltraSmooth = true; PairPacing = false; TriangleMesh = true; SmtAssist = false } else o
            let o = if o.Schema < 23 then { o with TriangleMesh = false; SmtAssist = false } else o
            let o = if o.Schema < 24 then { o with UltraSmooth = false } else o
            let o =
                if o.Schema >= schema || o.Schema >= 8 then o
                elif o.Schema >= 6 then { o with XessMotionSource = 0 }
                elif o.Schema = 5 then { o with GameType = 0 }
                elif o.Schema = 4 then { o with LimitToScreen = false; GameType = 0 }
                elif o.Schema = 3 then { o with Flow = defaultOptions.Flow; LimitToScreen = false; GameType = 0 }
                elif o.Schema = 2 then { o with Flow = defaultOptions.Flow; LimitToScreen = false; MotionSource = 0; GameType = 0 }
                else
                    { defaultOptions with
                        FrameGeneration = o.FrameGeneration; Mode = o.Mode
                        Upscaler = o.Upscaler; Sharpness = o.Sharpness }
            // (A development build of his kept "Adaptive" as a sixth multiplier.)
            let o = if o.Mode >= 5 then { o with Mode = 4; Adaptive = true } else o
            { o with Mode = Math.Clamp(o.Mode, 0, 4)
                     Bloom = Math.Clamp(o.Bloom, 0, 100)
                     BloomWide = Math.Clamp(o.BloomWide, 0, 100)
                     MultiplierCustom = (if o.MultiplierCustom >= 11 then min o.MultiplierCustom multiplierCustomMost else 0)
                     Generator = Math.Clamp(o.Generator, 0, generators - 1)
                     Flow = Math.Clamp(o.Flow, 25, 100)
                     Upscaler = (if o.Upscaler >= 0 && o.Upscaler < upscalers then o.Upscaler else 0)
                     Sharpness = Math.Clamp(o.Sharpness, 0, 100)
                     OverSharpness = Math.Clamp(o.OverSharpness, 0, 100)
                     Schema = schema
                     CaptureApi = Math.Clamp(o.CaptureApi, 0, 1)
                     WindowMode = Math.Clamp(o.WindowMode, 0, 2)
                     Countdown = Math.Clamp(o.Countdown, 0, countdowns.Length - 1)
                     QueueTarget = Math.Clamp(o.QueueTarget, 0, 2)
                     FrameLimit = normalizeFrameLimit o.FrameLimit
                     GameFps = (if o.GameFps >= 20 && o.GameFps <= 500 then o.GameFps else 0)
                     AdaptiveFps = (if o.AdaptiveFps >= 30 && o.AdaptiveFps <= 1000 then o.AdaptiveFps else 0)
                     CropLeft = Math.Clamp(o.CropLeft, 0, cropMax)
                     CropTop = Math.Clamp(o.CropTop, 0, cropMax)
                     CropRight = Math.Clamp(o.CropRight, 0, cropMax)
                     CropBottom = Math.Clamp(o.CropBottom, 0, cropMax)
                     SyncMode = (if o.SyncMode >= 0 && o.SyncMode < syncModes then o.SyncMode else 1)
                     GpuPriority = Math.Clamp(o.GpuPriority, 0, 1)
                     Gpu = (if isNull o.Gpu then "" else o.Gpu.Trim())
                     MotionSource = Math.Clamp(o.MotionSource, 0, 2)
                     XessMotionSource = Math.Clamp(o.XessMotionSource, 0, 2)
                     SuiteMotionSource = Math.Clamp(o.SuiteMotionSource, 0, 1)
                     FsrMotionSource = Math.Clamp(o.FsrMotionSource, 0, 2)
                     MsmSearch = Math.Clamp(o.MsmSearch, 0, 1)
                     TriPitch = Math.Clamp(o.TriPitch, 0, triPitches.Length); TriRounds = Math.Clamp(o.TriRounds, 0, triRounds.Length)
                     TriFine = Math.Clamp(o.TriFine, 0, triFines.Length); TriFull = Math.Clamp(o.TriFull, 0, triFulls.Length)
                     TriHome = Math.Clamp(o.TriHome, 0, triHomes.Length); TriRange = Math.Clamp(o.TriRange, 0, triRanges.Length)
                     TriExact = Math.Clamp(o.TriExact, 0, triExacts.Length); TriRelax = Math.Clamp(o.TriRelax, 0, triRelaxes.Length)
                     DlssPreset = Math.Clamp(o.DlssPreset, 0, 3)
                     FpsCounterSize = (if o.FpsCounterSize >= 50 && o.FpsCounterSize <= 200 then o.FpsCounterSize else 100)
                     SmsrBoost = Math.Clamp(o.SmsrBoost, -100, 100)
                     GameType = Math.Clamp(o.GameType, 0, gameCustom) }

    let private cleanName (name: string) =
        let t = name.Trim()
        t.Substring(0, min 40 t.Length)

    /// A file of any age, or none, read into something whole: Default always
    /// there, at most 100 profiles, every profile's options normalized.
    let normalize (state: MotionPrefs) =
        if isNull (box state) || state.Version <> 1 then defaults
        else
            let profiles =
                (if isNull state.Profiles then [||] else state.Profiles)
                |> Array.filter (fun p -> not (isNull (box p)) && not (String.IsNullOrWhiteSpace p.Id) && not (String.IsNullOrWhiteSpace p.Name))
                |> Array.distinctBy (fun p -> p.Id)
                |> Array.truncate maxProfiles
                |> Array.map (fun p -> { p with Name = cleanName p.Name; Options = normalizeOptions p.Options })
            let profiles =
                if profiles |> Array.exists (fun p -> p.Id = "default") then profiles
                else Array.append [| defaultProfile |] (profiles |> Array.truncate (maxProfiles - 1))
            { state with
                Hotkey = (if state.Hotkey = -1 || state.Hotkey >= 0 then state.Hotkey else 0)
                Profiles = profiles
                SelectedProfile =
                    if profiles |> Array.exists (fun p -> p.Id = state.SelectedProfile) then state.SelectedProfile
                    else "default" }

    let private json = JsonSerializerOptions(WriteIndented = true)

    let serialize (prefs: MotionPrefs) = JsonSerializer.Serialize(prefs, json)

    let deserialize (text: string) =
        try normalize (JsonSerializer.Deserialize<MotionPrefs>(text, json))
        with _ -> defaults

    let dataDir () =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DLSS5Suite", "Motion")

    let private path () = Path.Combine(dataDir (), "preferences.json")

    let load () =
        try
            let p = path ()
            if File.Exists p then deserialize (File.ReadAllText p) else defaults
        with _ -> defaults

    /// Written beside itself first and then moved, so a crash mid-write never
    /// leaves half a file.
    let save (prefs: MotionPrefs) =
        try
            Directory.CreateDirectory(dataDir ()) |> ignore
            let p = path ()
            let temp = p + ".tmp"
            File.WriteAllText(temp, serialize prefs)
            File.Move(temp, p, true)
            true
        with _ -> false

    /// MFG SUPER MOTION's own preferences, if it is installed for this user.
    let superMotionFile () =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MFG SUPER MOTION", "preferences.json")

    /// His profiles (and engine-log choice) read from his file, through the
    /// same migrations his app runs. Profiles whose name is already here are
    /// left as they are here. None when there is nothing to read.
    let importFromSuperMotion (into: MotionPrefs) : (MotionPrefs * int) option =
        try
            let file = superMotionFile ()
            if not (File.Exists file) then None
            else
                use doc = JsonDocument.Parse(File.ReadAllText file)
                let root = doc.RootElement
                let mutable profilesElement = Unchecked.defaultof<JsonElement>
                if not (root.TryGetProperty("Profiles", &profilesElement)) || profilesElement.ValueKind <> JsonValueKind.Array then None
                else
                    let theirs =
                        profilesElement.EnumerateArray()
                        |> Seq.choose (fun p ->
                            try Some(JsonSerializer.Deserialize<MotionProfile>(p.GetRawText(), json)) with _ -> None)
                        |> Seq.filter (fun p -> not (isNull (box p)) && not (String.IsNullOrWhiteSpace p.Name))
                        |> Seq.toArray
                    let taken = into.Profiles |> Array.map (fun p -> p.Name.ToLowerInvariant()) |> Set.ofArray
                    let added =
                        theirs
                        |> Array.filter (fun p -> p.Id <> "default" && not (taken.Contains(p.Name.Trim().ToLowerInvariant())))
                        |> Array.map (fun p -> { p with Id = Guid.NewGuid().ToString("N"); Name = cleanName p.Name; Options = normalizeOptions p.Options })
                        |> Array.truncate (maxProfiles - into.Profiles.Length)
                    Some(normalize { into with Profiles = Array.append into.Profiles added }, added.Length)
        with _ -> None
