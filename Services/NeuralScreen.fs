namespace DLSS_5_MANAGER.Services

open System
open System.Diagnostics
open System.Globalization
open System.IO
open System.IO.Compression
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Microsoft.Win32

/// DLSS 5 on the whole screen for RTX 30/40 cards, and optionally RTX 50,
/// through NeuralScreen. RTX 20 is detected but deliberately rejected because
/// the model starts without processing the picture on that generation.
///
/// NeuralScreen ships inside SUITE in "mod files/neuralscreen" with its own
/// Python. SUITE is its control panel: it writes NeuralScreen's config.json
/// before a start, and while it runs it sends the same requests NeuralScreen's
/// own menu would, through the SUITE bridge (tools/neuralscreen-bridge), so a
/// slider moves the picture at once and NeuralScreen's menu never has to open.
module NeuralScreen =

    // =====================================================================
    // WHICH CARD, WHICH METHOD
    // =====================================================================
    type GpuTier =
        | Rtx50
        /// 30 or 40 series: DLSS 5 runs through NeuralScreen.
        | RtxOlder of series: int
        /// 20 series: below the minimum the model can run on.
        | Rtx20
        | NoRtx

    /// Reads the display adapters Windows has drivers for, best card first.
    let private adapterNames () =
        try
            use root =
                Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}")
            if isNull root then []
            else
                root.GetSubKeyNames()
                |> Array.filter (fun n -> n.Length = 4 && n |> Seq.forall Char.IsDigit)
                |> Array.choose (fun n ->
                    try
                        use sub = root.OpenSubKey(n)
                        match (if isNull sub then null else sub.GetValue("DriverDesc")) with
                        | :? string as s when s.Trim() <> "" -> Some s
                        | _ -> None
                    with _ -> None)
                |> List.ofArray
        with _ -> []

    /// The tier of one adapter name, by its RTX model number.
    let tierOf (name: string) =
        let n = if isNull name then "" else name.ToUpperInvariant()
        let geforce = Regex.Match(n, @"RTX\s*(\d{2})\d{2}")
        if geforce.Success then
            match int geforce.Groups.[1].Value with
            | s when s >= 50 -> Rtx50
            | s when s >= 30 -> RtxOlder s
            | 20 -> Rtx20
            | _ -> NoRtx
        elif Regex.IsMatch(n, @"RTX\s*PRO\s*\d{4}\s*BLACKWELL") then Rtx50
        elif Regex.IsMatch(n, @"RTX\s*\d{4}\s*ADA") then RtxOlder 40
        elif Regex.IsMatch(n, @"RTX\s*A\d{3,4}") then RtxOlder 30
        else NoRtx

    let private rank tier =
        match tier with
        | Rtx50 -> 3
        | RtxOlder _ -> 2
        | Rtx20 -> 1
        | NoRtx -> 0

    /// The best card in the machine and its name.
    let detect () =
        match adapterNames () |> List.map (fun n -> n, tierOf n) |> List.sortByDescending (snd >> rank) with
        | (name, tier) :: _ -> name, tier
        | [] -> "", NoRtx

    // =====================================================================
    // WHERE THINGS LIVE
    // =====================================================================
    let dataDir () =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DLSS5Suite", "NeuralScreen")

    /// Where a copy shipped beside the app would sit. An install under
    /// Program Files is read-only for a standard user, so nothing is ever
    /// written here: it is only used when something already put it there.
    let private bundledFolder () = Path.Combine(ModInstaller.modFilesRoot (), "neuralscreen")

    /// Where SUITE unpacks the add-on itself. Per-user and always writable,
    /// which an installed "mod files" folder is not.
    let installRoot () = Path.Combine(dataDir (), "app")

    let private holdsNeuralScreen (dir: string) =
        File.Exists(Path.Combine(dir, "main.py")) && File.Exists(Path.Combine(dir, "runtime", "pythonw.exe"))

    /// The copy that is actually used: one shipped with the app wins, then the
    /// one the user installed, and when neither is there the install location
    /// is what the panel points at.
    let folder () =
        let bundled = bundledFolder ()
        let installed = Path.Combine(installRoot (), "neuralscreen")
        if holdsNeuralScreen bundled then bundled
        elif holdsNeuralScreen installed then installed
        elif Directory.Exists bundled then bundled
        else installed

    let private pythonPath () = Path.Combine(folder (), "runtime", "pythonw.exe")
    let isAvailable () = holdsNeuralScreen (folder ())

    let private configPath () = Path.Combine(dataDir (), "config.json")
    let private inboxDir () = Path.Combine(dataDir (), "inbox")
    let private outboxDir () = Path.Combine(dataDir (), "outbox")

    // =====================================================================
    // WHAT NEURALSCREEN IS TOLD
    // =====================================================================
    /// NeuralScreen's own ranges (settings_io.PARAM_RANGE); past them it pulls
    /// the value back itself, so they are kept here too.
    let clampParam (key: string) (value: float) =
        let lo, hi =
            match key with
            | "intensity" -> 0.0, 1.0
            | "local_tone" | "local_structure" -> 0.0, 1.5
            | "skin_structure" -> -1.0, 2.0
            | _ -> Double.NegativeInfinity, Double.PositiveInfinity
        Math.Clamp((if Double.IsFinite value then value else 1.0), lo, hi)

    let styleOf (style: ScreenEngine.Style) =
        match style with
        | ScreenEngine.Standard -> 0
        | ScreenEngine.Natural -> 1
        | ScreenEngine.Cinematic -> 2

    let skinOf (skin: ScreenEngine.Skin) =
        match skin with
        | ScreenEngine.FollowStructure -> -1.0
        | ScreenEngine.SkinStrength v -> clampParam "skin_structure" v

    let splitOf (compare: ScreenEngine.Compare) =
        match compare with
        | ScreenEngine.CompareSplit -> 0.5
        | _ -> 0.0

    /// The controls NeuralScreen has and the Screen Engine does not. They are
    /// kept beside SUITE's own settings rather than inside ScreenEngine.Settings,
    /// which describes the engine's command line.
    type Extras =
        { /// Run the network at a reduced work resolution and composite the
          /// result back over the native picture (NeuralScreen's "Boost").
          Boost: bool
          /// 0.1 to 1. What fraction of the frame the network sees under Boost.
          WorkScale: float
          /// Skip frames that have not changed.
          SkipStatic: bool
          /// HDR-compatible capture format.
          Hdr: bool
          /// Publish the result as a Spout2 sender.
          Spout: bool
          FrameGeneration: bool
          /// 2 to 4 generated frames per real one.
          FrameMultiplier: int
          /// The red dot while recording.
          RecordingIndicator: bool
          /// "cpu" or "nvofa" (NVIDIA Optical Flow).
          MotionBackend: string
          /// Index of the graphics adapter NeuralScreen runs on.
          Gpu: int }

    let extrasDefaults =
        { Boost = true
          WorkScale = 1.0
          SkipStatic = false
          Hdr = false
          Spout = false
          FrameGeneration = false
          FrameMultiplier = 2
          RecordingIndicator = true
          MotionBackend = "cpu"
          Gpu = 0 }

    let normalizeExtras (e: Extras) =
        { e with
            WorkScale = Math.Clamp((if Double.IsFinite e.WorkScale then e.WorkScale else 1.0), 0.1, 1.0)
            FrameMultiplier = Math.Clamp(e.FrameMultiplier, 2, 4)
            MotionBackend = (if e.MotionBackend = "nvofa" then "nvofa" else "cpu")
            Gpu = max 0 e.Gpu }

    let private extrasPath () = Path.Combine(dataDir (), "suite-extras.json")

    let loadExtras () =
        try
            if File.Exists(extrasPath ()) then
                normalizeExtras (JsonSerializer.Deserialize<Extras>(File.ReadAllText(extrasPath ())))
            else extrasDefaults
        with _ -> extrasDefaults

    let saveExtras (e: Extras) =
        try
            Directory.CreateDirectory(dataDir ()) |> ignore
            File.WriteAllText(extrasPath (), JsonSerializer.Serialize(normalizeExtras e))
        with _ -> ()

    /// SUITE's settings written over NeuralScreen's config, keeping whatever
    /// else it has saved there (presets, hotkeys, its own tuning).
    let writeConfig (s: ScreenEngine.Settings) (captureFolder: string) =
        Directory.CreateDirectory(dataDir ()) |> ignore
        let baseText =
            [ configPath (); Path.Combine(folder (), "config.json") ]
            |> List.tryFind File.Exists
            |> Option.map File.ReadAllText
            |> Option.defaultValue "{}"
        let cfg =
            match (try JsonNode.Parse(baseText) with _ -> null) with
            | :? JsonObject as o -> o
            | _ -> JsonObject()
        let primary =
            ScreenEngine.listMonitors ()
            |> List.tryHead
            |> Option.defaultValue { Left = 0; Top = 0; Width = 1920; Height = 1080; IsPrimary = true }
        let monitor =
            match s.Source with
            | ScreenEngine.Monitor i -> i
            | _ -> 0
        let set (key: string) (value: JsonNode) = cfg.[key] <- value
        set "monitor" (JsonValue.Create(monitor))
        set "width" (JsonValue.Create(primary.Width))
        set "height" (JsonValue.Create(primary.Height))
        set "fullscreen" (JsonValue.Create(true))
        if isNull cfg.["warmup"] then set "warmup" (JsonValue.Create(120))
        set "profile" (JsonValue.Create("Natural"))
        set "intensity" (JsonValue.Create(clampParam "intensity" s.Intensity))
        set "local_tone" (JsonValue.Create(clampParam "local_tone" s.LocalTone))
        set "local_structure" (JsonValue.Create(clampParam "local_structure" s.LocalStructure))
        set "skin_structure" (JsonValue.Create(skinOf s.Skin))
        set "style" (JsonValue.Create(styleOf s.Style))
        set "passes" (JsonValue.Create(Math.Clamp(s.Passes, 1, 4)))
        set "split" (JsonValue.Create(splitOf s.Compare))
        set "screenshot_dir" (JsonValue.Create(captureFolder))
        // The NeuralScreen-only controls. Each of these is read once when it
        // starts; while it runs the same values go through the bridge instead.
        let extras = normalizeExtras (loadExtras ())
        set "nr_small" (JsonValue.Create(extras.Boost))
        set "work_scale" (JsonValue.Create(extras.WorkScale))
        set "skip_static" (JsonValue.Create(extras.SkipStatic))
        set "hdr" (JsonValue.Create(extras.Hdr))
        set "spout" (JsonValue.Create(extras.Spout))
        set "frame_generation" (JsonValue.Create(extras.FrameGeneration))
        set "frame_multiplier" (JsonValue.Create(extras.FrameMultiplier))
        set "rec_indicator" (JsonValue.Create(extras.RecordingIndicator))
        set "motion_backend" (JsonValue.Create(extras.MotionBackend))
        set "gpu" (JsonValue.Create(extras.Gpu))
        // SUITE's window is the menu; NeuralScreen's own stays shut.
        set "open_menu_on_start" (JsonValue.Create(false))
        set "lang" (JsonValue.Create("en"))
        let tmp = configPath () + ".tmp"
        File.WriteAllText(tmp, cfg.ToJsonString(JsonSerializerOptions(WriteIndented = true)))
        File.Move(tmp, configPath (), true)

    // =====================================================================
    // FETCHING THE ADD-ON
    // =====================================================================
    /// NeuralScreen is published beside SUITE as its own release asset. It is
    /// fetched, verified and unpacked in the app: nothing here sends the user
    /// to a browser.
    module Addon =
        let private client =
            let c = new Net.Http.HttpClient(Timeout = TimeSpan.FromMinutes(30.0))
            c.DefaultRequestHeaders.UserAgent.ParseAdd("DLSS5-SUITE-NeuralScreen")
            c

        [<Literal>]
        let private Repo = "Potatoes9411/DLSS-5-SUITE"

        let private isAddon (name: string) =
            name.Contains("NeuralScreen", StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)

        /// The newest published add-on: its address, size and the digest the
        /// download is checked against.
        let private locate () = task {
            use! response = client.GetAsync(sprintf "https://api.github.com/repos/%s/releases/latest" Repo)
            response.EnsureSuccessStatusCode() |> ignore
            use! stream = response.Content.ReadAsStreamAsync()
            use document = JsonDocument.Parse(stream)
            let asset =
                document.RootElement.GetProperty("assets").EnumerateArray()
                |> Seq.tryFind (fun item -> isAddon (item.GetProperty("name").GetString()))
                |> Option.defaultWith (fun () -> failwith "This release does not carry the NeuralScreen add-on.")
            let digest =
                match asset.TryGetProperty("digest") with
                | true, value when not (String.IsNullOrWhiteSpace(value.GetString())) -> value.GetString().Replace("sha256:", "").ToUpperInvariant()
                | _ -> failwith "GitHub published no digest for the add-on; the download was refused."
            return
                asset.GetProperty("name").GetString(),
                asset.GetProperty("browser_download_url").GetString(),
                digest,
                asset.GetProperty("size").GetInt64()
        }

        /// Unpacking replaces the folder's files, so a half-written archive
        /// from an interrupted run can never be left behind as an install:
        /// the download verifies its SHA-256 before it is opened.
        let install (progress: string -> float -> unit) = task {
            let! (name, url, digest, size) = locate ()
            let cache = Path.Combine(dataDir (), "download")
            Directory.CreateDirectory(cache) |> ignore
            let archive = Path.Combine(cache, name)
            do! VerifiedDownload.download client name url digest size archive progress 0.0 92.0
            progress "Unpacking the NeuralScreen add-on..." 94.0
            let target = installRoot ()
            Directory.CreateDirectory(target) |> ignore
            ZipFile.ExtractToDirectory(archive, target, true)
            progress "Tidying up..." 99.0
            try File.Delete archive with _ -> ()
            if not (isAvailable ()) then failwith "The add-on was unpacked but NeuralScreen is still missing."
            progress "NeuralScreen add-on installed." 100.0
        }

    // =====================================================================
    // RUNNING IT
    // =====================================================================
    type Host() =
        let mutable proc: Process = null
        let mutable stopping = false
        let stateChanged = Event<string>()
        let controlsRequested = Event<unit>()
        let recordingChanged = Event<bool>()

        /// What NeuralScreen tells SUITE: Num2 asks for the control window.
        let outboxTimer =
            new Threading.Timer(
                (fun _ ->
                    try
                        let outbox = outboxDir ()
                        if Directory.Exists outbox then
                            for file in Directory.GetFiles(outbox, "*.json") |> Array.sort do
                                let text = (try File.ReadAllText file with _ -> "")
                                (try File.Delete file with _ -> ())
                                if text.Contains("\"controls\"") then controlsRequested.Trigger()
                                elif text.Contains("\"recording:on\"") then recordingChanged.Trigger(true)
                                elif text.Contains("\"recording:off\"") then recordingChanged.Trigger(false)
                    with _ -> ()),
                null, 150, 150)

        let isAlive () = not (isNull proc) && (try not proc.HasExited with _ -> false)

        /// One request for the bridge, written whole then renamed into place.
        let send (request: JsonObject) =
            if isAlive () then
                try
                    let inbox = inboxDir ()
                    Directory.CreateDirectory(inbox) |> ignore
                    let name = sprintf "%020d-%s" DateTime.UtcNow.Ticks (Guid.NewGuid().ToString("N"))
                    let tmp = Path.Combine(inbox, name + ".tmp")
                    File.WriteAllText(tmp, request.ToJsonString())
                    File.Move(tmp, Path.Combine(inbox, name + ".json"))
                    true
                with _ -> false
            else false

        let action (parts: JsonNode list) =
            let arr = JsonArray()
            for p in parts do arr.Add(p)
            send (JsonObject([ Collections.Generic.KeyValuePair("action", arr :> JsonNode) ]))

        [<CLIEvent>]
        member _.StateChanged = stateChanged.Publish

        member _.IsRunning = isAlive ()

        [<CLIEvent>]
        member _.ControlsRequested = controlsRequested.Publish

        [<CLIEvent>]
        member _.RecordingChanged = recordingChanged.Publish

        member this.Start(settings: ScreenEngine.Settings, captureFolder: string, neuralRendering: bool) =
            if not (isAvailable ()) then
                stateChanged.Trigger "NeuralScreen is not included in this build."
            else
                this.Stop()
                stopping <- false
                try
                    writeConfig settings captureFolder
                    // Old requests belong to a run that is gone; new ones (the
                    // window, NR off) are kept by the bridge once it starts.
                    (try
                        for old in Directory.GetFiles(inboxDir ()) do File.Delete old
                        for old in Directory.GetFiles(outboxDir ()) do File.Delete old
                     with _ -> ())
                    let psi = ProcessStartInfo(pythonPath ())
                    psi.UseShellExecute <- false
                    psi.CreateNoWindow <- true
                    psi.WorkingDirectory <- folder ()
                    for a in [ "-u"; Path.Combine(folder (), "main.py"); "--config"; configPath () ] do
                        psi.ArgumentList.Add(a)
                    psi.Environment.["NS_SUITE_INBOX"] <- inboxDir ()
                    psi.Environment.["NS_SUITE_OUTBOX"] <- outboxDir ()
                    let p = new Process(StartInfo = psi, EnableRaisingEvents = true)
                    p.Exited.Add(fun _ ->
                        if not stopping then
                            let code = (try p.ExitCode with _ -> -1)
                            stateChanged.Trigger(
                                if code = 0 then "Stopped"
                                elif code = 1 then "NeuralScreen did not start. Another copy may already be running; close it and try again."
                                else sprintf "NeuralScreen stopped (exit code %d). See NeuralScreen.log in its folder." code))
                    if p.Start() then
                        proc <- p
                        stateChanged.Trigger "Running (NeuralScreen)"
                        if not neuralRendering then action [ JsonValue.Create("nr") ] |> ignore
                    else
                        stateChanged.Trigger "NeuralScreen could not be started."
                with ex ->
                    stateChanged.Trigger("NeuralScreen could not be started: " + ex.Message)

        member _.Stop() =
            stopping <- true
            if isAlive () then
                try
                    // Its own quit first, so a recording is finished properly.
                    send (JsonObject([ Collections.Generic.KeyValuePair("command", JsonValue.Create("quit") :> JsonNode) ])) |> ignore
                    if not (proc.WaitForExit(4000)) then
                        proc.Kill(entireProcessTree = true)
                        proc.WaitForExit(3000) |> ignore
                with _ -> ()
            proc <- null

        member _.SetParam(key: string, value: float) =
            action [ JsonValue.Create("param"); JsonValue.Create(key); JsonValue.Create(clampParam key value) ]

        member _.SetStyle(style: int) = action [ JsonValue.Create("style"); JsonValue.Create(Math.Clamp(style, 0, 2)) ]
        member _.SetPasses(passes: int) = action [ JsonValue.Create("passes"); JsonValue.Create(Math.Clamp(passes, 1, 4)) ]
        member _.SetSplit(split: float) = action [ JsonValue.Create("split"); JsonValue.Create(Math.Clamp(split, 0.0, 1.0)) ]
        member _.ToggleNeuralRendering() = action [ JsonValue.Create("nr") ]

        // NeuralScreen's own switches are inverting - its menu reports "the
        // user pressed this", not a value. SUITE is the only menu, so it knows
        // the state it is turning over and sends one press per real change.
        member _.ToggleBoost() = action [ JsonValue.Create("toggle"); JsonValue.Create("boost") ]
        member _.ToggleSkipStatic() = action [ JsonValue.Create("toggle"); JsonValue.Create("skip_static") ]
        member _.ToggleHdr() = action [ JsonValue.Create("toggle"); JsonValue.Create("hdr") ]
        member _.ToggleSpout() = action [ JsonValue.Create("toggle"); JsonValue.Create("spout") ]
        member _.ToggleFrameGeneration() = action [ JsonValue.Create("toggle"); JsonValue.Create("frame_generation") ]
        member _.ToggleRecordingIndicator() = action [ JsonValue.Create("toggle"); JsonValue.Create("rec_indicator") ]

        member _.SetWorkScale(scale: float) =
            action [ JsonValue.Create("nr_res"); JsonValue.Create(Math.Clamp(scale, 0.1, 1.0)) ]

        member _.SetFrameMultiplier(times: int) =
            action [ JsonValue.Create("frame_multiplier"); JsonValue.Create(Math.Clamp(times, 2, 4)) ]

        member _.SetMotionBackend(backend: string) =
            action [ JsonValue.Create("motion_backend"); JsonValue.Create(if backend = "nvofa" then "nvofa" else "cpu") ]

        /// NeuralScreen reads the index out of the label its own menu uses.
        member _.SetGpu(index: int) =
            action [ JsonValue.Create("gpu"); JsonValue.Create(sprintf "%d: adapter" (max 0 index)) ]

        /// One window, by handle; 0 goes back to the whole screen.
        member _.CaptureWindow(handle: nativeint) =
            if handle = 0n then action [ JsonValue.Create("button"); JsonValue.Create("window_mode") ]
            else action [ JsonValue.Create("window"); JsonValue.Create(sprintf "0x%X: window" (int64 handle)) ]

        member _.Screenshot(captureFolder: string) =
            let file = Path.Combine(captureFolder, sprintf "neuralscreen-%s.png" (DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)))
            send (JsonObject([ Collections.Generic.KeyValuePair("shot", JsonValue.Create(file) :> JsonNode) ]))

        member _.ToggleRecording() =
            send (JsonObject([ Collections.Generic.KeyValuePair("command", JsonValue.Create("record") :> JsonNode) ]))

        interface IDisposable with
            member this.Dispose() =
                outboxTimer.Dispose()
                this.Stop()
