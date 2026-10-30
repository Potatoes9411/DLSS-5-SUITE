namespace DLSS_5_MANAGER.ViewModels

open System
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open Avalonia.Threading
open DLSS_5_MANAGER.Services
open DLSS_5_MANAGER.Services.VideoDlss

/// The Video tab: a link or a file, DLSS 5 on every picture of it, frame
/// generation if wanted, and the result saved where and how the user says.
type VideoViewModel() as this =
    inherit ViewModelBase()

    let containers = [ Mp4, "MP4"; Mkv, "MKV"; WebM, "WebM"; Mov, "MOV" ]
    let qualities = [ High, "High (larger file)"; Balanced, "Balanced"; Small, "Small (smaller file)" ]
    let frameGens = [ 1, "Off"; 2, "2x"; 3, "3x"; 4, "4x" ]
    let sizes = [ None, "Source size"; Some 2160, "4K at most"; Some 1440, "1440p at most"; Some 1080, "1080p at most"; Some 720, "720p at most" ]
    let _, gpuTier = NeuralScreen.detect ()
    let canRun = (gpuTier = NeuralScreen.Rtx50) && ScreenEngine.isAvailable ()

    let mutable source = ""
    let mutable output = ""
    let mutable outputChosen = false
    let mutable container = Mp4
    let mutable codec = H264
    let mutable quality = Balanced
    let mutable frameGen = 2
    let mutable maxHeight: int option = Some 1080
    let mutable useNvenc = true
    let mutable keepDownload = false
    let mutable status = ""
    let mutable progress = 0.0
    let mutable hasProgress = false
    let mutable isBusy = false
    let mutable isFetchingTools = false
    let mutable lastResult: string option = None
    let mutable cancel: CancellationTokenSource option = None

    let ui (f: unit -> unit) =
        if Dispatcher.UIThread.CheckAccess() then f () else Dispatcher.UIThread.Post(f)

    let notify (names: string list) =
        for n in names do this.RaisePropertyChanged(n)

    let codecs () = codecsFor container

    let refreshOutputName () =
        if not outputChosen then
            output <- defaultOutput source container
            notify [ "OutputPath" ]
        else
            output <- Path.ChangeExtension(output, extensionOf container)
            notify [ "OutputPath" ]

    let indexIn (list: ('a * string) list) (value: 'a) =
        list |> List.tryFindIndex (fun (v, _) -> v = value) |> Option.defaultValue 0

    let valueAt (list: ('a * string) list) (index: int) =
        if index >= 0 && index < list.Length then Some(fst list.[index]) else None

    do output <- defaultOutput "" container

    member _.CanRun = canRun
    member _.CannotRun = not canRun
    member _.CannotRunText =
        "Video needs the DLSS 5 Screen Engine, which runs on an RTX 50 card. It is not available on this PC."

    member _.Source
        with get () = source
        and set (v: string) =
            if v <> source then
                source <- v
                notify [ "Source"; "IsLink"; "CanStart"; "ToolsText"; "NeedsTools" ]
                refreshOutputName ()

    member _.IsLink = looksLikeUrl source

    member _.OutputPath
        with get () = output
        and set (v: string) =
            if v <> output then
                output <- v
                outputChosen <- true
                notify [ "OutputPath"; "CanStart" ]

    /// The save dialog's answer; its extension picks the container when it names one.
    member this.ChooseOutput(path: string) =
        let ext = Path.GetExtension(path).ToLowerInvariant()
        match containers |> List.tryFind (fun (c, _) -> extensionOf c = ext) with
        | Some(c, _) when c <> container -> this.SelectedContainerIndex <- indexIn containers c
        | _ -> ()
        outputChosen <- true
        output <- Path.ChangeExtension(path, extensionOf container)
        notify [ "OutputPath"; "CanStart" ]

    member _.ContainerOptions = containers |> List.map snd

    member _.SelectedContainerIndex
        with get () = indexIn containers container
        and set (i: int) =
            match valueAt containers i with
            | Some c when c <> container ->
                container <- c
                if not (List.contains codec (codecs ())) then codec <- (codecs ()).Head
                notify [ "SelectedContainerIndex"; "CodecOptions"; "SelectedCodecIndex"; "NvencApplies" ]
                refreshOutputName ()
            | _ -> ()

    member _.CodecOptions = codecs () |> List.map codecName

    member _.SelectedCodecIndex
        with get () = codecs () |> List.tryFindIndex ((=) codec) |> Option.defaultValue 0
        and set (i: int) =
            let list = codecs ()
            if i >= 0 && i < list.Length && list.[i] <> codec then
                codec <- list.[i]
                notify [ "SelectedCodecIndex"; "NvencApplies" ]

    member _.QualityOptions = qualities |> List.map snd

    member _.SelectedQualityIndex
        with get () = indexIn qualities quality
        and set (i: int) =
            match valueAt qualities i with
            | Some q when q <> quality ->
                quality <- q
                notify [ "SelectedQualityIndex" ]
            | _ -> ()

    member _.FrameGenOptions = frameGens |> List.map snd

    member _.SelectedFrameGenIndex
        with get () = indexIn frameGens frameGen
        and set (i: int) =
            match valueAt frameGens i with
            | Some m when m <> frameGen ->
                frameGen <- m
                notify [ "SelectedFrameGenIndex" ]
            | _ -> ()

    member _.SizeOptions = sizes |> List.map snd

    member _.SelectedSizeIndex
        with get () = indexIn sizes maxHeight
        and set (i: int) =
            match valueAt sizes i with
            | Some h when h <> maxHeight ->
                maxHeight <- h
                notify [ "SelectedSizeIndex" ]
            | _ -> ()

    member _.UseNvenc
        with get () = useNvenc
        and set (v: bool) =
            if v <> useNvenc then
                useNvenc <- v
                notify [ "UseNvenc" ]

    member _.NvencApplies = hasNvenc codec

    member _.KeepDownload
        with get () = keepDownload
        and set (v: bool) =
            if v <> keepDownload then
                keepDownload <- v
                notify [ "KeepDownload" ]

    member _.Status = status
    member _.Progress = progress
    member _.HasProgress = hasProgress
    member _.IsBusy = isBusy
    member _.IsIdle = not isBusy
    member _.HasResult = lastResult.IsSome && not isBusy

    member _.NeedsTools = not (List.isEmpty (missingTools (looksLikeUrl source)))
    member _.IsFetchingTools = isFetchingTools

    member _.ToolsText =
        match missingTools (looksLikeUrl source) with
        | [] -> "Video tools: ready."
        | missing ->
            sprintf "Needs %s (about 230 MB, from each project's own GitHub releases)." (String.Join(", ", missing))

    member _.CanStart =
        canRun && not isBusy && not isFetchingTools && not (String.IsNullOrWhiteSpace source) && not (String.IsNullOrWhiteSpace output)

    member this.FetchTools() =
        if not isFetchingTools then
            isFetchingTools <- true
            notify [ "IsFetchingTools"; "CanStart" ]
            Task.Run(fun () ->
                let result = ensureTools (fun s -> ui (fun () -> status <- s; notify [ "Status" ]))
                ui (fun () ->
                    isFetchingTools <- false
                    status <- (match result with Ok() -> "Video tools are ready." | Error e -> "The video tools could not be downloaded: " + e)
                    notify [ "IsFetchingTools"; "CanStart"; "Status"; "ToolsText"; "NeedsTools" ]))
            |> ignore

    member this.Start() =
        if this.CanStart then
            let job =
                { Source = source.Trim()
                  Output = output
                  Container = container
                  Codec = codec
                  Quality = quality
                  FrameGen = frameGen
                  MaxHeight = maxHeight
                  UseNvenc = useNvenc
                  KeepDownload = keepDownload }
            let cts = new CancellationTokenSource()
            cancel <- Some cts
            isBusy <- true
            lastResult <- None
            status <- "Starting..."
            progress <- 0.0
            hasProgress <- false
            notify [ "IsBusy"; "IsIdle"; "CanStart"; "Status"; "Progress"; "HasProgress"; "HasResult" ]
            let report (u: Update) =
                ui (fun () ->
                    status <- u.Status
                    match u.Fraction with
                    | Some f ->
                        progress <- f * 100.0
                        hasProgress <- true
                    | None -> hasProgress <- false
                    notify [ "Status"; "Progress"; "HasProgress" ])
            let engineSettings = ScreenEngine.load ()
            Task.Run<unit>(fun () -> task {
                let! result =
                    try processJob job engineSettings report cts.Token
                    with ex -> Task.FromResult(Error ex.Message)
                ui (fun () ->
                    isBusy <- false
                    cancel <- None
                    match result with
                    | Ok message ->
                        status <- message + " Saved to " + job.Output
                        progress <- 100.0
                        hasProgress <- true
                        lastResult <- Some job.Output
                    | Error e ->
                        status <- e
                        hasProgress <- false
                    notify [ "IsBusy"; "IsIdle"; "CanStart"; "Status"; "Progress"; "HasProgress"; "HasResult" ])
                cts.Dispose()
            }) |> ignore

    member _.Cancel() =
        cancel |> Option.iter (fun c -> try c.Cancel() with _ -> ())

    /// Explorer, with the result selected.
    member _.ShowResult() =
        match lastResult with
        | Some path when File.Exists path ->
            try Process.Start(ProcessStartInfo("explorer.exe", sprintf "/select,\"%s\"" path, UseShellExecute = false)) |> ignore with _ -> ()
        | _ -> ()

    member _.YouTubeNotice =
        "Only use videos you made or have permission to use. Downloading from YouTube can go against YouTube's Terms of Service."
