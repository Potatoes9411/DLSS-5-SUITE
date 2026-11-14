namespace DLSS_5_MANAGER.Services

open System
open System.Diagnostics
open System.IO
open System.IO.Pipes
open System.Reflection
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Avalonia
open Avalonia.Controls
open Avalonia.Interactivity
open Avalonia.Media.Imaging
open Avalonia.Threading
open Avalonia.VisualTree

/// A local control channel for testing SUITE from scripts, so features can
/// be exercised and measured without anyone at the keyboard and without the
/// window having to be in front.
///
/// Off unless SUITE is started with --bridge. It is a named pipe that only
/// the same Windows user can open, it answers one line per command, and
/// everything it does runs on the UI thread exactly as a click would.
///
/// Commands (one per line):
///   get Path.To.Property          read a view-model value
///   set Path.To.Property value    write one
///   call Path.To.Method args...   call a view-model method
///   click Name                    press a button by x:Name, or by its text
///   snap file.png                 render the window to a PNG, off-screen
///   frames seconds                frame-time statistics while it runs
///   wheel x y delta               a real mouse-wheel message at a point
///   size                          the window's size
/// Windows' power throttling ("efficiency mode"): on a laptop it slows the
/// timers and CPU of programs it decides are in the background. A UI that
/// animates suffers from it - frames come late and motion drags.
module PowerThrottling =
    [<Struct; System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)>]
    type private State =
        { Version: uint32
          ControlMask: uint32
          StateMask: uint32 }

    [<System.Runtime.InteropServices.DllImport("kernel32.dll")>]
    extern nativeint private GetCurrentProcess()

    [<System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)>]
    extern bool private SetProcessInformation(nativeint proc, int infoClass, State& info, int size)

    /// Asks Windows not to throttle this process's speed or timers.
    let optOut () =
        try
            // ProcessPowerThrottling = 4. Control: execution speed (1) and
            // timer resolution (4); state 0 = not throttled.
            let mutable state = { Version = 1u; ControlMask = 5u; StateMask = 0u }
            SetProcessInformation(GetCurrentProcess(), 4, &state, 12)
        with _ -> false

module TestBridge =

    [<Literal>]
    let PipeName = "DLSS5SuiteBridge"

    let isRequested () =
        Environment.GetCommandLineArgs()
        |> Array.exists (fun a -> a.Equals("--bridge", StringComparison.OrdinalIgnoreCase))

    [<Struct; System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)>]
    type Win32Point = { mutable X: int; mutable Y: int }

    [<Struct; System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)>]
    type Win32Rect = { mutable Left: int; mutable Top: int; mutable Right: int; mutable Bottom: int }

    module private Native =
        [<System.Runtime.InteropServices.DllImport("user32.dll")>]
        extern bool GetWindowRect(nativeint window, Win32Rect& rect)

        [<System.Runtime.InteropServices.DllImport("user32.dll")>]
        extern bool PrintWindow(nativeint window, nativeint dc, uint32 flags)

        [<System.Runtime.InteropServices.DllImport("user32.dll")>]
        extern bool PostMessage(nativeint window, uint32 msg, nativeint wParam, nativeint lParam)

        [<System.Runtime.InteropServices.DllImport("user32.dll")>]
        extern bool ClientToScreen(nativeint window, Win32Point& point)

    let private split (line: string) =
        line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)

    /// Follows a dotted path of properties from an object.
    let private resolve (root: obj) (path: string) : obj =
        path.Split('.', StringSplitOptions.RemoveEmptyEntries)
        |> Array.fold
            (fun (current: obj) name ->
                if isNull current then null
                else
                    let p = current.GetType().GetProperty(name, BindingFlags.Public ||| BindingFlags.Instance)
                    if isNull p then failwithf "no property '%s' on %s" name (current.GetType().Name)
                    p.GetValue(current))
            root

    let private convert (text: string) (target: Type) : obj =
        if target = typeof<string> then box text
        elif target = typeof<bool> then box (Boolean.Parse text)
        elif target = typeof<int> then box (Int32.Parse text)
        elif target = typeof<float> then box (Double.Parse(text, Globalization.CultureInfo.InvariantCulture))
        else Convert.ChangeType(text, target)

    let private describe (value: obj) =
        match value with
        | null -> "null"
        | :? string as s -> s
        | :? Collections.IEnumerable as items when not (value :? string) ->
            let parts = [ for i in items -> if isNull i then "null" else i.ToString() ]
            sprintf "[%d] %s" parts.Length (String.Join(" | ", parts |> List.truncate 50))
        | v -> v.ToString()

    let private findButton (window: Window) (name: string) : Button option =
        let all = window.GetVisualDescendants() |> Seq.choose (function :? Button as b -> Some b | _ -> None) |> List.ofSeq
        let visible (b: Button) = b.IsEffectivelyVisible && b.IsEffectivelyEnabled
        let textOf (b: Button) =
            match b.Content with
            | :? string as s -> s
            | :? TextBlock as t -> t.Text
            | :? Control as c ->
                c.GetVisualDescendants()
                |> Seq.choose (function :? TextBlock as t -> Some t.Text | _ -> None)
                |> Seq.tryHead
                |> Option.defaultValue ""
            | _ -> ""
        all |> List.tryFind (fun b -> b.Name = name && visible b)
        |> Option.orElse (all |> List.tryFind (fun b -> visible b && String.Equals((textOf b).Trim(), name, StringComparison.OrdinalIgnoreCase)))

    /// Frame times: the compositor calls back once per presented frame.
    let private measureFrames (window: Window) (seconds: float) : Task<string> =
        let done' = TaskCompletionSource<string>()
        let clock = Stopwatch.StartNew()
        let gaps = ResizeArray<float>()
        let mutable last = 0.0
        let rec step (_: TimeSpan) =
            let now = clock.Elapsed.TotalMilliseconds
            if last > 0.0 then gaps.Add(now - last)
            last <- now
            if clock.Elapsed.TotalSeconds < seconds then window.RequestAnimationFrame(step)
            else
                let sorted = gaps |> Seq.sort |> Array.ofSeq
                let pct (p: float) = if sorted.Length = 0 then 0.0 else sorted.[min (sorted.Length - 1) (int (float sorted.Length * p))]
                let avg = if sorted.Length = 0 then 0.0 else Array.average sorted
                done'.SetResult(
                    sprintf "frames=%d fps=%.1f avg=%.1fms p50=%.1fms p95=%.1fms max=%.1fms over50ms=%d"
                        sorted.Length (if avg > 0.0 then 1000.0 / avg else 0.0) avg (pct 0.5) (pct 0.95)
                        (if sorted.Length = 0 then 0.0 else Array.max sorted)
                        (sorted |> Array.filter (fun g -> g > 50.0) |> Array.length))
        window.RequestAnimationFrame(step)
        done'.Task

    let private run (window: Window) (line: string) : Task<string> =
        let parts = split line
        if parts.Length = 0 then Task.FromResult "error: empty"
        else
            let rest = if parts.Length > 1 then parts.[1] else ""
            try
                match parts.[0].ToLowerInvariant() with
                | "ping" -> Task.FromResult "pong"
                | "size" -> Task.FromResult(sprintf "%.0fx%.0f scale=%.2f" window.Bounds.Width window.Bounds.Height window.RenderScaling)
                | "get" -> Task.FromResult(describe (resolve window.DataContext rest))
                | "set" ->
                    let path, value = let a = rest.Split(' ', 2) in a.[0], (if a.Length > 1 then a.[1] else "")
                    let dot = path.LastIndexOf('.')
                    let owner = if dot < 0 then window.DataContext else resolve window.DataContext (path.Substring(0, dot))
                    let prop = owner.GetType().GetProperty(path.Substring(dot + 1))
                    prop.SetValue(owner, convert value prop.PropertyType)
                    Task.FromResult "ok"
                | "call" ->
                    let tokens = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    let path = tokens.[0]
                    let dot = path.LastIndexOf('.')
                    let owner = if dot < 0 then window.DataContext else resolve window.DataContext (path.Substring(0, dot))
                    let name = path.Substring(dot + 1)
                    let args = tokens |> Array.skip 1
                    let methodInfo =
                        owner.GetType().GetMethods(BindingFlags.Public ||| BindingFlags.Instance)
                        |> Array.find (fun m -> m.Name = name && m.GetParameters().Length = args.Length)
                    let values = Array.map2 (fun (a: string) (p: ParameterInfo) -> convert a p.ParameterType) args (methodInfo.GetParameters())
                    Task.FromResult(describe (methodInfo.Invoke(owner, values)))
                | "click" ->
                    match findButton window rest with
                    | Some b ->
                        b.RaiseEvent(RoutedEventArgs(Button.ClickEvent))
                        Task.FromResult "ok"
                    | None -> Task.FromResult(sprintf "error: no visible button '%s'" rest)
                | "snap" ->
                    // Windows' own capture of this window, as it is on screen -
                    // even when covered. Rendering off-screen instead draws in
                    // software, which cannot see GPU-held pictures and showed
                    // the covers blank.
                    match window.TryGetPlatformHandle() with
                    | null -> Task.FromResult "error: no window handle"
                    | handle ->
                        let mutable rect = Unchecked.defaultof<Win32Rect>
                        Native.GetWindowRect(handle.Handle, &rect) |> ignore
                        use bitmap = new System.Drawing.Bitmap(max 1 (rect.Right - rect.Left), max 1 (rect.Bottom - rect.Top))
                        use g = System.Drawing.Graphics.FromImage(bitmap)
                        let dc = g.GetHdc()
                        // PW_RENDERFULLCONTENT = 2: includes what DirectComposition draws.
                        Native.PrintWindow(handle.Handle, dc, 2u) |> ignore
                        g.ReleaseHdc(dc)
                        bitmap.Save(rest, System.Drawing.Imaging.ImageFormat.Png)
                        Task.FromResult "ok"
                | "frames" ->
                    measureFrames window (Double.Parse(rest, Globalization.CultureInfo.InvariantCulture))
                | "wheel" ->
                    let a = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    let x, y, delta = int a.[0], int a.[1], int a.[2]
                    match window.TryGetPlatformHandle() with
                    | null -> Task.FromResult "error: no window handle"
                    | handle ->
                        let mutable point = { X = int (float x * window.RenderScaling); Y = int (float y * window.RenderScaling) }
                        Native.ClientToScreen(handle.Handle, &point) |> ignore
                        let lParam = nativeint ((point.Y <<< 16) ||| (point.X &&& 0xFFFF))
                        let wParam = nativeint ((delta &&& 0xFFFF) <<< 16)
                        Native.PostMessage(handle.Handle, 0x020Au, wParam, lParam) |> ignore
                        Task.FromResult "ok"
                | "prop" ->
                    // prop ControlName Property value - for switching parts of
                    // the window off and on while measuring.
                    let a = rest.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries)
                    let target =
                        window.GetVisualDescendants()
                        |> Seq.tryPick (function :? Control as c when c.Name = a.[0] -> Some c | _ -> None)
                    match target with
                    | None -> Task.FromResult(sprintf "error: no control named '%s'" a.[0])
                    | Some c ->
                        let p = c.GetType().GetProperty(a.[1])
                        if a.Length > 2 then p.SetValue(c, convert a.[2] p.PropertyType)
                        Task.FromResult(describe (p.GetValue(c)))
                | "strip" ->
                    // strip effects|shadows|hq|clips - switches one kind of
                    // drawing off across the whole window, to find what a
                    // frame is spending its time on. Restart to undo.
                    let mutable n = 0
                    for v in window.GetVisualDescendants() do
                        match rest, v with
                        | "effects", (:? Visual as c) when not (isNull c.Effect) ->
                            c.Effect <- null
                            n <- n + 1
                        | r, (:? Border as b) when r.StartsWith("shadows:") && b.BoxShadow.Count > 0 && b.Classes.Contains(r.Substring(8)) ->
                            b.BoxShadow <- Avalonia.Media.BoxShadows()
                            n <- n + 1
                        | "shadows", (:? Border as b) when b.BoxShadow.Count > 0 ->
                            b.BoxShadow <- Avalonia.Media.BoxShadows()
                            n <- n + 1
                        | "hqbg", (:? Image as i) when not (isNull i.Name) && (i.Name.EndsWith("Plate") || i.Name.StartsWith("StarPlate")) ->
                            Avalonia.Media.RenderOptions.SetBitmapInterpolationMode(i, Avalonia.Media.Imaging.BitmapInterpolationMode.LowQuality)
                            n <- n + 1
                        | "hqcards", (:? Image as i) when isNull i.Name || not (i.Name.EndsWith("Plate") || i.Name.StartsWith("StarPlate")) ->
                            Avalonia.Media.RenderOptions.SetBitmapInterpolationMode(i, Avalonia.Media.Imaging.BitmapInterpolationMode.LowQuality)
                            n <- n + 1
                        | "hq", (:? Image as i) ->
                            Avalonia.Media.RenderOptions.SetBitmapInterpolationMode(i, Avalonia.Media.Imaging.BitmapInterpolationMode.LowQuality)
                            n <- n + 1
                        | "clips", (:? Control as c) when c.ClipToBounds && not (c :? Avalonia.Controls.Presenters.ScrollContentPresenter) ->
                            c.ClipToBounds <- false
                            n <- n + 1
                        | _ -> ()
                    Task.FromResult(sprintf "changed %d" n)
                | "front" ->
                    window.Topmost <- true
                    window.Activate()
                    window.Topmost <- false
                    Task.FromResult "ok"
                | "nothrottle" -> Task.FromResult(if PowerThrottling.optOut () then "ok" else "error: refused")
                | other -> Task.FromResult(sprintf "error: unknown command '%s'" other)
            with ex ->
                Task.FromResult(sprintf "error: %s" (ex.GetBaseException().Message))

    /// Starts listening, if SUITE was started with --bridge.
    let start (window: Window) =
        if isRequested () then
            AppLog.info "Test bridge listening on \\\\.\\pipe\\DLSS5SuiteBridge"
            Task.Run(fun () ->
                task {
                    while true do
                        try
                            use pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous)
                            do! pipe.WaitForConnectionAsync()
                            use reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true)
                            use writer = new StreamWriter(pipe, UTF8Encoding(false), 4096, true)
                            writer.AutoFlush <- true
                            let mutable line = reader.ReadLine()
                            while not (isNull line) do
                                let! inner = Dispatcher.UIThread.InvokeAsync<Task<string>>(fun () -> run window line).GetTask()
                                let! answer = inner
                                do! writer.WriteLineAsync(answer.Replace("\r", " ").Replace("\n", " "))
                                line <- reader.ReadLine()
                        with ex ->
                            AppLog.error "Test bridge connection failed" ex
                }
                :> Task)
            |> ignore
