namespace DLSS_5_MANAGER.Services

open System
open System.Diagnostics
open System.Globalization
open System.IO
open System.Text.Json

/// DLSS 5 on the whole screen, run by SUITE's own engine.
///
/// The engine is the full-screen wrapper's C++ core, built from Engine/ and
/// shipped inside SUITE. It runs as its own process with its control panel
/// switched off (--gui off); SUITE is the control panel. Keeping it out of
/// SUITE's process is deliberate: a crash in NVIDIA's NGX runtime or the GPU
/// driver takes down the engine, not the app managing it, and SUITE can start
/// it again.
///
/// Every setting maps to one of the engine's command-line options, which it
/// reads through the same parser its own panel used. That keeps this side a
/// plain translation - see toArguments - with the rules the engine enforces
/// mirrored here, so SUITE never hands it a value it would refuse.
module ScreenEngine =

    // =====================================================================
    // SETTINGS
    // =====================================================================
    type Source =
        | PrimaryMonitor
        | AllMonitors
        | Monitor of index: int

    type Style =
        | Standard
        | Natural
        | Cinematic

    type Skin =
        /// Skin follows the local structure strength. The engine's -1.
        | FollowStructure
        | SkinStrength of float

    type SuperResolution =
        | SrAuto
        | SrDlaa
        | SrOff

    type Motion =
        | BuiltIn
        | NvidiaOpticalFlow
        | NoMotion

    type Cursor =
        | CursorAuto
        | CursorOn
        | CursorOff

    type Compare =
        | CompareOff
        | CompareSplit
        | CompareOriginal

    type Settings =
        { Source: Source
          /// A window title (or 0xHANDLE). When set, one window is the source
          /// and Source is ignored.
          Window: string
          /// Show the result on another monitor, which enables upscaling.
          Target: int option
          NeuralRendering: bool
          /// 0 to 1. The engine refuses anything outside that range.
          Intensity: float
          Style: Style
          /// Uncapped: any finite value. Past the usual range is intentional.
          LocalStructure: float
          /// Uncapped: any finite value.
          LocalTone: float
          Skin: Skin
          AutoMask: bool
          /// Runs the model on its own output this many times a frame; at least 1.
          Passes: int
          SuperResolution: SuperResolution
          Motion: Motion
          Cursor: Cursor
          VSync: bool
          Compare: Compare
          HighPrecisionColour: bool }

    /// The engine's own defaults, so a fresh install behaves like the engine
    /// run with no options.
    let defaults =
        { Source = PrimaryMonitor
          Window = ""
          Target = None
          NeuralRendering = true
          Intensity = 1.0
          Style = Standard
          LocalStructure = 1.0
          LocalTone = 1.0
          Skin = FollowStructure
          AutoMask = true
          Passes = 1
          SuperResolution = SrAuto
          Motion = BuiltIn
          Cursor = CursorAuto
          VSync = false
          Compare = CompareOff
          HighPrecisionColour = false }

    let private kMaxMonitorIndex = 16

    let private finiteOr (fallback: float) (value: float) =
        if Double.IsFinite(value) then value else fallback

    /// Brings settings inside what the engine accepts. Every rule here is one
    /// the engine's parser enforces by refusing to start, so fixing it here
    /// means a bad saved value degrades to something sensible instead of the
    /// engine silently failing to launch.
    let normalize (s: Settings) =
        let source =
            match s.Source with
            | Monitor i -> Monitor(Math.Clamp(i, 0, kMaxMonitorIndex))
            | other -> other

        { s with
            Source = source
            Window = (if isNull s.Window then "" else s.Window.Trim())
            // --target cannot be combined with --monitor all.
            Target =
                match source, s.Target with
                | AllMonitors, _ -> None
                | _, Some t -> Some(Math.Clamp(t, 0, kMaxMonitorIndex))
                | _, None -> None
            Intensity = Math.Clamp(finiteOr 1.0 s.Intensity, 0.0, 1.0)
            LocalStructure = finiteOr 1.0 s.LocalStructure
            LocalTone = finiteOr 1.0 s.LocalTone
            Skin =
                match s.Skin with
                // Nothing between -1 and 0 means anything to the model.
                | SkinStrength v when Double.IsFinite(v) -> SkinStrength(max 0.0 v)
                | SkinStrength _ -> FollowStructure
                | FollowStructure -> FollowStructure
            Passes = max 1 s.Passes }

    // =====================================================================
    // ARGUMENTS
    // =====================================================================
    // Invariant culture, always. In a locale that writes decimals with a comma,
    // formatting 1.5 the default way gives "1,5", which the engine reads as an
    // invalid number and refuses to start.
    let private num (value: float) = value.ToString("0.#####", CultureInfo.InvariantCulture)
    let private onOff (value: bool) = if value then "on" else "off"

    /// The command line for these settings, one argument per element.
    ///
    /// Passed to ProcessStartInfo.ArgumentList rather than joined into a
    /// string, so a window title with spaces or quotes cannot break parsing.
    /// Every option is written explicitly rather than only the ones that differ
    /// from a default, so a future engine changing a default does not change
    /// what a saved setting means.
    let toArguments (engineDataDir: string) (neuralRuntimeDir: string) (settings: Settings) : string list =
        let s = normalize settings

        [ yield! [ "--gui"; "off"; "--console"; "off" ]

          if not (String.IsNullOrEmpty(neuralRuntimeDir)) then
              yield! [ "--ngx-path"; neuralRuntimeDir ]

          if not (String.IsNullOrEmpty(engineDataDir)) then
              yield! [ "--app-data"; engineDataDir ]
              yield! [ "--log-file"; Path.Combine(engineDataDir, "engine.log") ]

          if s.Window <> "" then
              yield! [ "--window"; s.Window ]
          else
              yield "--monitor"
              yield
                  match s.Source with
                  | PrimaryMonitor -> "primary"
                  | AllMonitors -> "all"
                  | Monitor i -> string i

          match s.Target with
          | Some t -> yield! [ "--target"; string t ]
          | None -> ()

          yield! [ "--nr"; onOff s.NeuralRendering ]
          yield! [ "--nr-intensity"; num s.Intensity ]
          yield "--nr-style"
          yield
              match s.Style with
              | Standard -> "0"
              | Natural -> "1"
              | Cinematic -> "2"
          yield! [ "--nr-local-structure"; num s.LocalStructure ]
          yield! [ "--nr-local-tone"; num s.LocalTone ]
          yield "--nr-skin"
          yield
              match s.Skin with
              | FollowStructure -> "-1"
              | SkinStrength v -> num v
          yield! [ "--nr-automask"; onOff s.AutoMask ]
          yield! [ "--nr-passes"; string s.Passes ]

          yield "--sr"
          yield
              match s.SuperResolution with
              | SrAuto -> "auto"
              | SrDlaa -> "dlaa"
              | SrOff -> "off"

          yield "--mv"
          yield
              match s.Motion with
              | BuiltIn -> "builtin"
              | NvidiaOpticalFlow -> "nvof"
              | NoMotion -> "none"

          yield "--cursor"
          yield
              match s.Cursor with
              | CursorAuto -> "auto"
              | CursorOn -> "on"
              | CursorOff -> "off"

          yield! [ "--vsync"; onOff s.VSync ]

          yield "--compare"
          yield
              match s.Compare with
              | CompareOff -> "off"
              | CompareSplit -> "split"
              | CompareOriginal -> "original"

          yield! [ "--format"; (if s.HighPrecisionColour then "rgba16f" else "rgba8") ] ]

    // =====================================================================
    // WHERE THINGS LIVE
    // =====================================================================
    let dataDir () =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DLSS5Suite",
            "ScreenEngine")

    let private settingsPath () = Path.Combine(dataDir (), "settings.json")

    /// Shipped beside the app, like the mod payload.
    let enginePath () =
        Path.Combine(AppContext.BaseDirectory, "engine", "DLSS5 SUITE Screen Engine.exe")

    /// The folder holding the nvngx_dlssnr.dll SUITE already bundles.
    let neuralRuntimeDir () =
        Path.Combine(ModInstaller.modFilesRoot (), "dlss 5")

    let isAvailable () = File.Exists(enginePath ())

    // =====================================================================
    // PERSISTENCE
    // =====================================================================
    // Stored as plain fields rather than F# unions, which serialise awkwardly
    // and would tie the file's shape to this code's type names.
    //
    // Public on purpose. As a private type its properties and constructor are
    // invisible to System.Text.Json, which then writes "{}" and cannot read
    // anything back - every setting silently reset on the next launch.
    [<CLIMutable>]
    type StoredSettings =
        { Source: string
          MonitorIndex: int
          Window: string
          Target: int
          NeuralRendering: bool
          Intensity: float
          Style: string
          LocalStructure: float
          LocalTone: float
          SkinFollowsStructure: bool
          Skin: float
          AutoMask: bool
          Passes: int
          SuperResolution: string
          Motion: string
          Cursor: string
          VSync: bool
          Compare: string
          HighPrecisionColour: bool }

    let private toStored (s: Settings) : StoredSettings =
        { Source =
            match s.Source with
            | PrimaryMonitor -> "primary"
            | AllMonitors -> "all"
            | Monitor _ -> "monitor"
          MonitorIndex = (match s.Source with Monitor i -> i | _ -> 0)
          Window = s.Window
          Target = defaultArg s.Target -1
          NeuralRendering = s.NeuralRendering
          Intensity = s.Intensity
          Style = (match s.Style with Standard -> "standard" | Natural -> "natural" | Cinematic -> "cinematic")
          LocalStructure = s.LocalStructure
          LocalTone = s.LocalTone
          SkinFollowsStructure = (s.Skin = FollowStructure)
          Skin = (match s.Skin with SkinStrength v -> v | FollowStructure -> 0.0)
          AutoMask = s.AutoMask
          Passes = s.Passes
          SuperResolution = (match s.SuperResolution with SrAuto -> "auto" | SrDlaa -> "dlaa" | SrOff -> "off")
          Motion = (match s.Motion with BuiltIn -> "builtin" | NvidiaOpticalFlow -> "nvof" | NoMotion -> "none")
          Cursor = (match s.Cursor with CursorAuto -> "auto" | CursorOn -> "on" | CursorOff -> "off")
          VSync = s.VSync
          Compare = (match s.Compare with CompareOff -> "off" | CompareSplit -> "split" | CompareOriginal -> "original")
          HighPrecisionColour = s.HighPrecisionColour }

    let private fromStored (d: StoredSettings) : Settings =
        let pick (value: string) (choices: (string * 'a) list) (fallback: 'a) =
            choices
            |> List.tryFind (fun (k, _) -> String.Equals(k, value, StringComparison.OrdinalIgnoreCase))
            |> Option.map snd
            |> Option.defaultValue fallback

        normalize
            { Source =
                pick d.Source [ "primary", PrimaryMonitor; "all", AllMonitors; "monitor", Monitor d.MonitorIndex ] PrimaryMonitor
              Window = d.Window
              Target = (if d.Target >= 0 then Some d.Target else None)
              NeuralRendering = d.NeuralRendering
              Intensity = d.Intensity
              Style = pick d.Style [ "standard", Standard; "natural", Natural; "cinematic", Cinematic ] Standard
              LocalStructure = d.LocalStructure
              LocalTone = d.LocalTone
              Skin = (if d.SkinFollowsStructure then FollowStructure else SkinStrength d.Skin)
              AutoMask = d.AutoMask
              Passes = d.Passes
              SuperResolution = pick d.SuperResolution [ "auto", SrAuto; "dlaa", SrDlaa; "off", SrOff ] SrAuto
              Motion = pick d.Motion [ "builtin", BuiltIn; "nvof", NvidiaOpticalFlow; "none", NoMotion ] BuiltIn
              Cursor = pick d.Cursor [ "auto", CursorAuto; "on", CursorOn; "off", CursorOff ] CursorAuto
              VSync = d.VSync
              Compare = pick d.Compare [ "off", CompareOff; "split", CompareSplit; "original", CompareOriginal ] CompareOff
              HighPrecisionColour = d.HighPrecisionColour }

    let serialize (s: Settings) =
        JsonSerializer.Serialize(toStored (normalize s), JsonSerializerOptions(WriteIndented = true))

    /// Anything unreadable falls back to the defaults rather than failing.
    let deserialize (json: string) =
        try
            let stored = JsonSerializer.Deserialize<StoredSettings>(json)
            if isNull (box stored) then defaults else fromStored stored
        with _ ->
            defaults

    let load () =
        try
            let p = settingsPath ()
            if File.Exists(p) then deserialize (File.ReadAllText(p)) else defaults
        with _ ->
            defaults

    let save (s: Settings) =
        try
            Directory.CreateDirectory(dataDir ()) |> ignore
            File.WriteAllText(settingsPath (), serialize s)
        with _ ->
            ()

    // =====================================================================
    // THE PROCESS
    // =====================================================================
    /// Owns the running engine. One per app.
    ///
    /// A crash is restarted automatically, but only a few times in quick
    /// succession: an engine that dies straight away every time (no RTX GPU, an
    /// outdated driver) must not be relaunched forever, so after that it stops
    /// and says why.
    type Host() =
        let mutable proc: Process = null
        let mutable current = defaults
        let mutable stopping = false
        let restarts = Collections.Generic.Queue<DateTime>()

        let stateChanged = Event<string>()

        let isAlive () =
            not (isNull proc) && (try not proc.HasExited with _ -> false)

        [<CLIEvent>]
        member _.StateChanged = stateChanged.Publish

        member _.IsRunning = isAlive ()

        member this.Start(settings: Settings) =
            if not (isAvailable ()) then
                stateChanged.Trigger "The screen engine is not included in this build."
            else
                this.Stop()
                current <- normalize settings
                stopping <- false

                let data = dataDir ()
                Directory.CreateDirectory(data) |> ignore

                let psi = ProcessStartInfo(enginePath ())
                psi.UseShellExecute <- false
                psi.CreateNoWindow <- true
                psi.WorkingDirectory <- Path.GetDirectoryName(enginePath ())
                for a in toArguments data (neuralRuntimeDir ()) current do
                    psi.ArgumentList.Add(a)

                let p = new Process(StartInfo = psi, EnableRaisingEvents = true)

                p.Exited.Add(fun _ ->
                    if not stopping then
                        let now = DateTime.UtcNow
                        restarts.Enqueue(now)
                        while restarts.Count > 0 && now - restarts.Peek() > TimeSpan.FromMinutes(1.0) do
                            restarts.Dequeue() |> ignore

                        if restarts.Count <= 3 then
                            stateChanged.Trigger "The screen engine stopped unexpectedly and was restarted."
                            this.Start(current)
                        else
                            stateChanged.Trigger
                                "The screen engine keeps stopping, so it was left off. See engine.log in the ScreenEngine folder.")

                if p.Start() then
                    proc <- p
                    stateChanged.Trigger "Running"
                else
                    stateChanged.Trigger "The screen engine could not be started."

        member _.Stop() =
            stopping <- true
            if isAlive () then
                try
                    proc.Kill(entireProcessTree = true)
                    proc.WaitForExit(3000) |> ignore
                with _ ->
                    ()
            proc <- null

        /// Applies new settings to a running engine.
        member this.Apply(settings: Settings) =
            current <- normalize settings
            if isAlive () then this.Start(current)

        member _.Current = current

        interface IDisposable with
            member this.Dispose() = this.Stop()
