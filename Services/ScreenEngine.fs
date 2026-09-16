namespace DLSS_5_MANAGER.Services

open System
open System.Diagnostics
open System.Globalization
open System.IO
open System.Runtime.InteropServices
open System.Text
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

    module private SuiteControl =
        [<Literal>]
        let private OutputWindowClass = "FullScreenWrapperForDLSS5OutputWindow"

        [<Literal>]
        let ScreenshotMessage = 0x8451u

        [<Literal>]
        let RecordMessage = 0x8452u

        [<Literal>]
        let ComparisonMessage = 0x8453u

        /// One model setting, changed while the session runs. Without this the
        /// engine only reads its settings when it starts, so every slider move
        /// meant restarting it and the picture went away for a second.
        [<Literal>]
        let TuneMessage = 0x8454u

        // The fields the engine takes live, as it numbers them.
        [<Literal>]
        let TuneIntensity = 1
        [<Literal>]
        let TuneLocalStructure = 2
        [<Literal>]
        let TuneLocalTone = 3
        [<Literal>]
        let TuneSkin = 4
        [<Literal>]
        let TuneStyle = 5
        [<Literal>]
        let TuneAutoMask = 6
        [<Literal>]
        let TunePasses = 7
        [<Literal>]
        let TuneNeuralRendering = 8

        [<DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)>]
        extern nativeint FindWindowW(string className, string windowName)

        [<DllImport("user32.dll", SetLastError = true)>]
        extern bool PostMessageW(nativeint window, uint32 message, unativeint wParam, nativeint lParam)

        let post message payload =
            let window = FindWindowW(OutputWindowClass, null)
            window <> 0n && PostMessageW(window, message, 0un, nativeint payload)

        /// The field in the top byte, the value in the low 32 bits; a number
        /// with a fraction travels as ten-thousandths, the way the engine reads it.
        let postTuneRaw (field: int) (raw: int) =
            post TuneMessage ((int64 field <<< 56) ||| int64 (uint32 raw))

        let postTune (field: int) (value: float) =
            postTuneRaw field (int (Math.Round(Math.Clamp(value, -200000.0, 200000.0) * 10000.0)))

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

    /// Everything past the everyday controls, one field per engine option.
    ///
    /// Plain values only (no unions or options), so the record is stored as it
    /// is. Where an option can be left out, a sentinel says so: -1 for the
    /// adapter, MvScaleAuto for the motion scales, "" for the NGX ids.
    [<CLIMutable>]
    type AdvancedSettings =
        { /// Runs the engine's own control panel (--gui on) alongside SUITE.
          /// SUITE also exposes capture actions and visible-window selection directly.
          ShowPanel: bool
          NrPreset: int
          UiCorrection: bool
          /// 0 to 15; 0 is DLSS's own choice.
          SrPreset: int
          /// Finest block-matching level, 0 to 7.
          MvLevel: int
          /// 1, 2 or 4.
          NvofGrid: int
          /// "slow", "medium" or "fast".
          NvofPerf: string
          /// 0 to 1.
          DepthValue: float
          DepthInverted: bool
          /// Left out, the engine works the scale out from the two sizes.
          MvScaleAuto: bool
          MvScaleX: float
          MvScaleY: float
          /// 0 to 1.
          ResetThreshold: float
          CaptureBorder: bool
          CaptureFolder: string
          /// Folder holding the nvngx_dlssnr.dll to load; "" uses the one SUITE bundles.
          ModelFolder: string
          /// 0 off, 1 on, 2 verbose.
          NgxLog: int
          /// Hex, non-zero; "" uses the project id instead.
          NgxAppId: string
          /// A GUID; "" keeps the engine's own.
          NgxProjectId: string
          Indicator: bool
          CubinCache: bool
          /// Keeps the output window out of capture. Off feeds the output back
          /// into its own capture.
          Affinity: bool
          Topmost: bool
          ClickThrough: bool
          RedirectionBitmap: bool
          /// -1 is the first NVIDIA adapter.
          Adapter: int
          DebugLayer: bool
          /// 0 debug, 1 info, 2 warn, 3 error.
          LogLevel: int
          ExcludeOwnWindows: bool
          ExclusionLog: bool
          ShowInert: bool }

    /// The engine's own defaults (interior::DefaultOptions).
    let advancedDefaults =
        { ShowPanel = false
          NrPreset = 1
          UiCorrection = true
          SrPreset = 0
          MvLevel = 1
          NvofGrid = 1
          NvofPerf = "medium"
          DepthValue = 0.5
          DepthInverted = false
          MvScaleAuto = true
          MvScaleX = 1.0
          MvScaleY = 1.0
          ResetThreshold = 0.5
          CaptureBorder = false
          CaptureFolder = ""
          ModelFolder = ""
          NgxLog = 0
          NgxAppId = ""
          NgxProjectId = ""
          Indicator = false
          CubinCache = true
          Affinity = true
          Topmost = false
          ClickThrough = true
          RedirectionBitmap = false
          Adapter = -1
          DebugLayer = false
          LogLevel = 1
          ExcludeOwnWindows = true
          ExclusionLog = false
          ShowInert = false }

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
          HighPrecisionColour: bool
          Advanced: AdvancedSettings }

    /// The model settings the running engine takes over a window message. A
    /// change confined to these never restarts the session.
    let liveFieldsOnly (before: Settings) (after: Settings) =
        { before with
            Intensity = after.Intensity
            Style = after.Style
            LocalStructure = after.LocalStructure
            LocalTone = after.LocalTone
            Skin = after.Skin
            AutoMask = after.AutoMask
            Passes = after.Passes
            NeuralRendering = after.NeuralRendering } = after

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
          HighPrecisionColour = false
          Advanced = advancedDefaults }

    let private kMaxMonitorIndex = 16

    /// The engine's --ngx-app-id: hex, optional 0x, not zero, fits 64 bits.
    let isValidAppId (text: string) =
        let digits =
            if text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) then text.Substring(2) else text
        let ok, value =
            UInt64.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)
        digits.Length > 0 && ok && value <> 0UL

    /// The engine's --ngx-project-id: a GUID written out with dashes.
    let isValidProjectId (text: string) =
        let ok, _ = Guid.TryParseExact(text, "D")
        ok

    let private finiteOr (fallback: float) (value: float) =
        if Double.IsFinite(value) then value else fallback

    /// Brings settings inside what the engine accepts. Every rule here is one
    /// the engine's parser enforces by refusing to start, so fixing it here
    /// means a bad saved value degrades to something sensible instead of the
    /// engine silently failing to launch.
    let normalizeAdvanced (a: AdvancedSettings) =
        let d = advancedDefaults
        // A stored file from an older build can hold null strings.
        let text (value: string) = if isNull value then "" else value.Trim()
        let appId = text a.NgxAppId
        let projectId = text a.NgxProjectId
        let captureFolder = text a.CaptureFolder
        let modelFolder = text a.ModelFolder

        { a with
            NrPreset = max 0 a.NrPreset
            SrPreset = Math.Clamp(a.SrPreset, 0, 15)
            MvLevel = Math.Clamp(a.MvLevel, 0, 7)
            NvofGrid = (if a.NvofGrid = 1 || a.NvofGrid = 2 || a.NvofGrid = 4 then a.NvofGrid else d.NvofGrid)
            NvofPerf =
                (match text a.NvofPerf with
                 | "slow" | "medium" | "fast" as p -> p
                 | _ -> d.NvofPerf)
            DepthValue = Math.Clamp(finiteOr d.DepthValue a.DepthValue, 0.0, 1.0)
            MvScaleX = finiteOr d.MvScaleX a.MvScaleX
            MvScaleY = finiteOr d.MvScaleY a.MvScaleY
            ResetThreshold = Math.Clamp(finiteOr d.ResetThreshold a.ResetThreshold, 0.0, 1.0)
            NgxLog = Math.Clamp(a.NgxLog, 0, 2)
            NgxAppId = (if isValidAppId appId then appId else "")
            NgxProjectId = (if isValidProjectId projectId then projectId else "")
            CaptureFolder = captureFolder
            ModelFolder = modelFolder
            Adapter = max -1 a.Adapter
            LogLevel = Math.Clamp(a.LogLevel, 0, 3) }

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
            Passes = max 1 s.Passes
            Advanced = normalizeAdvanced s.Advanced }

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
        let a = s.Advanced
        let opt (name: string) (value: string) = "--" + name + "=" + value

        // SUITE is the only control panel; the engine's own would be a third window.
        [ yield opt "gui" "off"
          yield opt "console" "off"

          // Paths and titles stay two arguments: they may contain "=".
          let modelDir = if a.ModelFolder <> "" then a.ModelFolder else neuralRuntimeDir
          if not (String.IsNullOrEmpty(modelDir)) then
              yield! [ "--ngx-path"; modelDir ]

          if not (String.IsNullOrEmpty(engineDataDir)) then
              yield! [ "--app-data"; engineDataDir ]
              yield! [ "--log-file"; Path.Combine(engineDataDir, "engine.log") ]

          if s.Window <> "" then
              yield! [ "--window"; s.Window ]
          else
              yield
                  opt "monitor" (
                      match s.Source with
                      | PrimaryMonitor -> "primary"
                      | AllMonitors -> "all"
                      | Monitor i -> string i
                  )

          match s.Target with
          | Some t -> yield opt "target" (string t)
          | None -> ()

          yield opt "nr" (onOff s.NeuralRendering)
          yield opt "nr-intensity" (num s.Intensity)
          yield
              opt "nr-style" (
                  match s.Style with
                  | Standard -> "0"
                  | Natural -> "1"
                  | Cinematic -> "2"
              )
          yield opt "nr-local-structure" (num s.LocalStructure)
          yield opt "nr-local-tone" (num s.LocalTone)
          yield
              opt "nr-skin" (
                  match s.Skin with
                  | FollowStructure -> "-1"
                  | SkinStrength v -> num v
              )
          yield opt "nr-automask" (onOff s.AutoMask)
          yield opt "nr-passes" (string s.Passes)
          yield opt "nr-preset" (string a.NrPreset)
          yield opt "nr-ui-correction" (onOff a.UiCorrection)

          yield
              opt "sr" (
                  match s.SuperResolution with
                  | SrAuto -> "auto"
                  | SrDlaa -> "dlaa"
                  | SrOff -> "off"
              )
          yield opt "sr-preset" (string a.SrPreset)

          yield
              opt "mv" (
                  match s.Motion with
                  | BuiltIn -> "builtin"
                  | NvidiaOpticalFlow -> "nvof"
                  | NoMotion -> "none"
              )
          yield opt "mv-level" (string a.MvLevel)
          yield opt "nvof-grid" (string a.NvofGrid)
          yield opt "nvof-perf" a.NvofPerf
          yield opt "depth-value" (num a.DepthValue)
          yield opt "depth-inverted" (onOff a.DepthInverted)
          if not a.MvScaleAuto then
              yield opt "mv-scale-x" (num a.MvScaleX)
              yield opt "mv-scale-y" (num a.MvScaleY)
          yield opt "reset-threshold" (num a.ResetThreshold)

          yield
              opt "cursor" (
                  match s.Cursor with
                  | CursorAuto -> "auto"
                  | CursorOn -> "on"
                  | CursorOff -> "off"
              )
          yield opt "capture-border" (onOff a.CaptureBorder)
          if a.CaptureFolder <> "" then
              yield! [ "--capture-folder"; a.CaptureFolder ]
          yield opt "vsync" (onOff s.VSync)
          yield
              opt "compare" (
                  match s.Compare with
                  | CompareOff -> "off"
                  | CompareSplit -> "split"
                  | CompareOriginal -> "original"
              )
          yield opt "format" (if s.HighPrecisionColour then "rgba16f" else "rgba8")

          yield opt "ngx-log" (string a.NgxLog)
          if a.NgxAppId <> "" then
              yield opt "ngx-app-id" a.NgxAppId
          if a.NgxProjectId <> "" then
              yield opt "ngx-project-id" a.NgxProjectId
          yield opt "indicator" (onOff a.Indicator)
          yield opt "cubin-cache" (onOff a.CubinCache)

          yield opt "affinity" (onOff a.Affinity)
          yield opt "topmost" (onOff a.Topmost)
          yield opt "click-through" (onOff a.ClickThrough)
          yield opt "redirection-bitmap" (onOff a.RedirectionBitmap)
          if a.Adapter >= 0 then
              yield opt "adapter" (string a.Adapter)
          yield opt "debug-layer" (onOff a.DebugLayer)
          yield opt "log-level" (string a.LogLevel)
          yield opt "exclude-own-windows" (onOff a.ExcludeOwnWindows)
          yield opt "exclusion-log" (onOff a.ExclusionLog)
          yield opt "show-inert" (onOff a.ShowInert) ]

    // =====================================================================
    // WHERE THINGS LIVE
    // =====================================================================
    let dataDir () =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DLSS5Suite",
            "ScreenEngine")

    let private settingsPath () = Path.Combine(dataDir (), "settings.json")

    /// Shipped beside the app, like the mod payload. The executable keeps the
    /// name and copyright notice its build gives it.
    [<Literal>]
    let EngineFileName = "FullScreenWrapperForDLSS5.exe"

    let enginePath () =
        Path.Combine(AppContext.BaseDirectory, "engine", EngineFileName)

    /// The folder holding the nvngx_dlssnr.dll SUITE already bundles.
    /// The NVIDIA-signed copy ships in "dlss 5 signed" and is preferred: the
    /// driver's NGX loader refuses the modified build in "dlss 5", which stays
    /// for the game mods.
    let neuralRuntimeDir () =
        let signed = Path.Combine(ModInstaller.modFilesRoot (), "dlss 5 signed")
        if File.Exists(Path.Combine(signed, "nvngx_dlssnr.dll")) then signed
        else Path.Combine(ModInstaller.modFilesRoot (), "dlss 5")

    let isAvailable () = File.Exists(enginePath ())

    // =====================================================================
    // MONITORS
    // =====================================================================
    type MonitorEntry =
        { Left: int
          Top: int
          Width: int
          Height: int
          IsPrimary: bool }

    type WindowEntry =
        { Handle: nativeint
          Title: string }

    /// The order the engine numbers monitors in (interior::Ordered): primary
    /// first, then left edge, then top edge. --monitor N and --target N are
    /// indexes into this order, so SUITE must use the same one.
    let engineOrder (monitors: MonitorEntry list) =
        monitors |> List.sortBy (fun m -> (not m.IsPrimary), m.Left, m.Top)

    module private Native =
        open System.Runtime.InteropServices

        [<Struct; StructLayout(LayoutKind.Sequential)>]
        type RECT =
            val mutable left: int
            val mutable top: int
            val mutable right: int
            val mutable bottom: int

        [<Struct; StructLayout(LayoutKind.Sequential)>]
        type MONITORINFO =
            val mutable cbSize: int
            val mutable rcMonitor: RECT
            val mutable rcWork: RECT
            val mutable dwFlags: uint32

        type MonitorEnumProc = delegate of nativeint * nativeint * nativeint * nativeint -> bool
        type WindowEnumProc = delegate of nativeint * nativeint -> bool

        [<DllImport("user32.dll")>]
        extern bool EnumDisplayMonitors(nativeint hdc, nativeint clip, MonitorEnumProc callback, nativeint data)

        [<DllImport("user32.dll")>]
        extern bool GetMonitorInfoW(nativeint monitor, MONITORINFO& info)

        [<DllImport("user32.dll")>]
        extern bool EnumWindows(WindowEnumProc callback, nativeint data)

        [<DllImport("user32.dll")>]
        extern bool IsWindowVisible(nativeint window)

        [<DllImport("user32.dll", CharSet = CharSet.Unicode)>]
        extern int GetWindowTextLengthW(nativeint window)

        [<DllImport("user32.dll", CharSet = CharSet.Unicode)>]
        extern int GetWindowTextW(nativeint window, StringBuilder text, int maxCount)

        [<DllImport("user32.dll")>]
        extern uint32 GetWindowThreadProcessId(nativeint window, uint32& processId)

        [<Struct; StructLayout(LayoutKind.Sequential)>]
        type POINT =
            val mutable x: int
            val mutable y: int

        [<DllImport("user32.dll")>]
        extern bool GetCursorPos(POINT& point)

        [<DllImport("user32.dll")>]
        extern nativeint WindowFromPoint(POINT point)

        [<DllImport("user32.dll")>]
        extern nativeint GetAncestor(nativeint window, uint32 flags)

        [<DllImport("user32.dll")>]
        extern int16 GetAsyncKeyState(int virtualKey)

    /// Every connected monitor, in the engine's order. Empty if Windows will
    /// not say.
    let listMonitors () =
        let found = Collections.Generic.List<MonitorEntry>()

        let callback =
            Native.MonitorEnumProc(fun handle _ _ _ ->
                let mutable info = Native.MONITORINFO()
                info.cbSize <- Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>()
                if Native.GetMonitorInfoW(handle, &info) then
                    let r = info.rcMonitor
                    found.Add
                        { Left = r.left
                          Top = r.top
                          Width = r.right - r.left
                          Height = r.bottom - r.top
                          IsPrimary = (info.dwFlags &&& 1u) <> 0u }
                true)

        try
            Native.EnumDisplayMonitors(0n, 0n, callback, 0n) |> ignore
            GC.KeepAlive(callback)
        with _ ->
            ()

        engineOrder (List.ofSeq found)

    /// Visible top-level windows that can be captured. The handle is passed to
    /// the engine instead of the title so duplicate titles remain unambiguous.
    let listVisibleWindows () =
        let found = Collections.Generic.List<WindowEntry>()
        let ownProcessId = uint32 Environment.ProcessId

        let callback =
            Native.WindowEnumProc(fun window _ ->
                try
                    let length = Native.GetWindowTextLengthW(window)
                    let mutable processId = 0u
                    Native.GetWindowThreadProcessId(window, &processId) |> ignore
                    if Native.IsWindowVisible(window) && length > 0 && processId <> ownProcessId then
                        let title = StringBuilder(length + 1)
                        if Native.GetWindowTextW(window, title, title.Capacity) > 0 then
                            let value = title.ToString().Trim()
                            if value <> "" && not (value.StartsWith("Full-Screen Wrapper for DLSS5", StringComparison.OrdinalIgnoreCase)) then
                                found.Add { Handle = window; Title = value }
                with _ ->
                    ()
                true)

        try
            Native.EnumWindows(callback, 0n) |> ignore
            GC.KeepAlive(callback)
        with _ ->
            ()

        found
        |> Seq.distinctBy (fun entry -> entry.Handle)
        |> Seq.sortBy (fun entry -> entry.Title.ToUpperInvariant())
        |> List.ofSeq

    /// The top-level window under the mouse pointer, for the crosshair picker.
    /// None over SUITE itself, over the engine's windows or over nothing titled.
    let windowAtCursor () =
        try
            let mutable point = Native.POINT()
            if not (Native.GetCursorPos(&point)) then None
            else
                let window = Native.GetAncestor(Native.WindowFromPoint(point), 2u) // GA_ROOT
                let mutable processId = 0u
                Native.GetWindowThreadProcessId(window, &processId) |> ignore
                let length = if window = 0n then 0 else Native.GetWindowTextLengthW(window)
                if window = 0n || processId = uint32 Environment.ProcessId || length = 0 then None
                else
                    let title = StringBuilder(length + 1)
                    Native.GetWindowTextW(window, title, title.Capacity) |> ignore
                    let value = title.ToString().Trim()
                    if value = "" || value.StartsWith("Full-Screen Wrapper for DLSS5", StringComparison.OrdinalIgnoreCase) then None
                    else Some { Handle = window; Title = value }
        with _ ->
            None

    /// True while the physical left mouse button is down, or when a click
    /// happened since the previous check. Including Win32's low transition
    /// bit prevents a fast click between timer ticks from being missed.
    let isLeftMouseButtonDown () =
        try (uint16 (Native.GetAsyncKeyState(0x01)) &&& 0x8001us) <> 0us
        with _ -> false

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
          HighPrecisionColour: bool
          Advanced: AdvancedSettings }

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
          HighPrecisionColour = s.HighPrecisionColour
          Advanced = s.Advanced }

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
              HighPrecisionColour = d.HighPrecisionColour
              Advanced = (if isNull (box d.Advanced) then advancedDefaults else d.Advanced) }

    let serialize (s: Settings) =
        JsonSerializer.Serialize(toStored (normalize s), JsonSerializerOptions(WriteIndented = true))

    /// Copies every key the file has over the defaults, one level into objects.
    ///
    /// A file written before a field existed has no value for it, and the
    /// serializer would fill the gap with false or 0 rather than the default -
    /// silently turning click-through or capture exclusion off for anyone who
    /// upgrades.
    let rec private overlay (onto: Nodes.JsonObject) (from: Nodes.JsonObject) =
        for KeyValue(key, value) in List.ofSeq from do
            match onto.[key], value with
            | (:? Nodes.JsonObject as target), (:? Nodes.JsonObject as source) -> overlay target source
            | _ -> onto.[key] <- (if isNull value then null else value.DeepClone())

    /// Anything unreadable falls back to the defaults rather than failing.
    let deserialize (json: string) =
        try
            let merged = JsonSerializer.SerializeToNode(toStored defaults).AsObject()
            match Nodes.JsonNode.Parse(json) with
            | :? Nodes.JsonObject as loaded ->
                overlay merged loaded
                let stored = merged.Deserialize<StoredSettings>()
                if isNull (box stored) then defaults else fromStored stored
            | _ -> defaults
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

        member _.RequestScreenshot() =
            isAlive () && SuiteControl.post SuiteControl.ScreenshotMessage 0L

        member _.ToggleRecording() =
            isAlive () && SuiteControl.post SuiteControl.RecordMessage 0L

        /// Hands the running engine one changed setting per message. The engine
        /// applies them on its next frame, rebuilding the model only when the
        /// change actually needs it.
        member _.Tune(before: Settings, after: Settings) =
            if not (isAlive ()) then false
            else
                let styleCode (style: Style) =
                    match style with
                    | Standard -> 0
                    | Natural -> 1
                    | Cinematic -> 2
                let skinValue (skin: Skin) =
                    match skin with
                    | FollowStructure -> -1.0
                    | SkinStrength v -> v
                let sent =
                    [ if before.Intensity <> after.Intensity then yield SuiteControl.postTune SuiteControl.TuneIntensity after.Intensity
                      if before.LocalStructure <> after.LocalStructure then yield SuiteControl.postTune SuiteControl.TuneLocalStructure after.LocalStructure
                      if before.LocalTone <> after.LocalTone then yield SuiteControl.postTune SuiteControl.TuneLocalTone after.LocalTone
                      if before.Skin <> after.Skin then yield SuiteControl.postTune SuiteControl.TuneSkin (skinValue after.Skin)
                      if before.Style <> after.Style then yield SuiteControl.postTuneRaw SuiteControl.TuneStyle (styleCode after.Style)
                      if before.AutoMask <> after.AutoMask then yield SuiteControl.postTuneRaw SuiteControl.TuneAutoMask (if after.AutoMask then 1 else 0)
                      if before.Passes <> after.Passes then yield SuiteControl.postTuneRaw SuiteControl.TunePasses after.Passes
                      if before.NeuralRendering <> after.NeuralRendering then yield SuiteControl.postTuneRaw SuiteControl.TuneNeuralRendering (if after.NeuralRendering then 1 else 0) ]
                not (List.contains false sent)

        member _.StartComparison(parameter: int, values: int) =
            let safeParameter = Math.Clamp(parameter, 0, 6)
            let safeValues = Math.Clamp(values, 2, 50)
            let payload = int64 safeValues <<< (safeParameter * 6)
            isAlive () && SuiteControl.post SuiteControl.ComparisonMessage payload

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
                // The engine writes its trace file into the working directory,
                // and the install folder under Program Files is not writable.
                psi.WorkingDirectory <- data
                for a in toArguments data (neuralRuntimeDir ()) current do
                    psi.ArgumentList.Add(a)

                let p = new Process(StartInfo = psi, EnableRaisingEvents = true)

                p.Exited.Add(fun _ ->
                    // Exit code 0 is the engine's own quit (Ctrl+Alt+Shift+Q),
                    // which is a request to stop, not a crash.
                    let quitByUser = (try p.ExitCode = 0 with _ -> false)
                    if not stopping && quitByUser then
                        stateChanged.Trigger "Stopped"
                    elif not stopping then
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
