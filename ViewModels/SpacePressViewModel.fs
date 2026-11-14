namespace DLSS_5_MANAGER.ViewModels

open System
open System.Collections.Generic
open System.Collections.ObjectModel
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Avalonia.Media
open Avalonia.Media.Imaging
open Avalonia.Threading
open DLSS_5_MANAGER.Services
open DLSS_5_MANAGER.Services.StorageCompressor

/// What Space Press remembers about a game between launches, so a library
/// of big games is not walked file by file every time the page opens.
/// Public on purpose: System.Text.Json writes a private F# record as "{}".
type SpacePressCached =
    { OriginalBytes: int64
      OnDiskBytes: int64
      EstimatedSavingBytes: int64
      Files: int
      /// "plain", "partly" or the algorithm name.
      Status: string }

type SpacePressSettings =
    { Algorithm: string
      SkipMedia: bool
      SkipPackages: bool }

/// One game in the Space Press list.
type StorageRowViewModel(card: GameCardViewModel) =
    inherit ViewModelBase()

    let mutable info: FolderInfo option = None
    let mutable isMeasuring = false
    let mutable isWorking = false
    let mutable workVerb = ""
    let mutable progress = 0.0
    let mutable progressText = ""
    let mutable isSelected = false
    let mutable message = ""

    let brush (hex: string) = SolidColorBrush(Color.Parse(hex)) :> IBrush

    member _.Card = card
    member _.Key = card.Game.AppId
    member _.Title = card.Title
    member _.Folder = card.InstallDirectory
    member _.Launcher = card.LauncherType
    member _.Cover: Bitmap = card.BannerImage
    member _.HasCover = card.HasBannerImage

    member this.Info
        with get () = info
        and set value =
            info <- value
            for name in [ "Info"; "SizeText"; "OriginalText"; "SavedText"; "HasSaved"; "EstimateText"; "StatusText"
                          "StatusBackground"; "StatusForeground"; "IsSquished"; "IsPlain" ] do
                this.RaisePropertyChanged(name)

    member this.IsMeasuring
        with get () = isMeasuring
        and set value =
            isMeasuring <- value
            this.RaisePropertyChanged("IsMeasuring")
            this.RaisePropertyChanged("SizeText")
            this.RaisePropertyChanged("StatusText")

    member this.IsWorking
        with get () = isWorking
        and set value =
            isWorking <- value
            this.RaisePropertyChanged("IsWorking")
            this.RaisePropertyChanged("StatusText")
            this.RaisePropertyChanged("StatusBackground")
            this.RaisePropertyChanged("StatusForeground")

    member this.WorkVerb
        with get () = workVerb
        and set value = workVerb <- value

    member this.Progress
        with get () = progress
        and set value =
            progress <- value
            this.RaisePropertyChanged("Progress")
            this.RaisePropertyChanged("StatusText")

    member this.ProgressText
        with get () = progressText
        and set value =
            progressText <- value
            this.RaisePropertyChanged("ProgressText")

    member this.IsSelected
        with get () = isSelected
        and set value =
            isSelected <- value
            this.RaisePropertyChanged("IsSelected")

    /// The last thing that went wrong, or why it cannot be squished.
    member this.Message
        with get () = message
        and set value =
            message <- value
            this.RaisePropertyChanged("Message")
            this.RaisePropertyChanged("HasMessage")

    member _.HasMessage = not (String.IsNullOrWhiteSpace(message))

    member _.SizeText =
        match info with
        | Some i -> formatBytes i.OnDiskBytes
        | None -> if isMeasuring then "Measuring..." else "-"

    member _.OriginalText =
        match info with
        | Some i -> formatBytes i.OriginalBytes
        | None -> "-"

    member _.SavedBytes =
        match info with
        | Some i -> max 0L (i.OriginalBytes - i.OnDiskBytes)
        | None -> 0L

    member this.HasSaved = this.SavedBytes > 1048576L

    member this.SavedText =
        match info with
        | Some i when i.OriginalBytes > 0L && this.HasSaved ->
            let pct = float this.SavedBytes / float i.OriginalBytes * 100.0
            sprintf "%s saved (%s)" (formatBytes this.SavedBytes) (if pct < 1.0 then sprintf "%.1f%%" pct else sprintf "%.0f%%" pct)
        | _ -> ""

    member _.EstimateText =
        match info with
        | Some i when i.Status = Plain && i.EstimatedSavingBytes > 1048576L ->
            sprintf "could save about %s" (formatBytes i.EstimatedSavingBytes)
        | _ -> ""

    member _.IsSquished =
        match info with
        | Some { Status = Squished _ } -> true
        | _ -> false

    member _.IsPlain =
        match info with
        | Some { Status = Plain } -> true
        | _ -> false

    member _.StatusText =
        if isWorking then sprintf "%s %.0f%%" workVerb progress
        elif isMeasuring && info.IsNone then "Measuring"
        else
            match info with
            | Some { Status = Plain } -> "Not squished"
            | Some { Status = Partly } -> "Partly squished"
            | Some { Status = Squished alg } -> sprintf "Squished (%s)" alg
            | None -> "Waiting"

    member _.StatusBackground =
        if isWorking then brush "#1E293B"
        else
            match info with
            | Some { Status = Plain } -> brush "#382A2E"
            | Some { Status = Partly } -> brush "#3A2E1E"
            | Some { Status = Squished _ } -> brush "#1B3629"
            | None -> brush "#1E293B"

    member _.StatusForeground =
        if isWorking then brush "#60A5FA"
        else
            match info with
            | Some { Status = Plain } -> brush "#F87171"
            | Some { Status = Partly } -> brush "#FBBF24"
            | Some { Status = Squished _ } -> brush "#34D399"
            | None -> brush "#94A3B8"

/// The Space Press edition: the library, sized up, with squish and unsquish.
type SpacePressViewModel(library: ObservableCollection<GameCardViewModel>) =
    inherit ViewModelBase()

    let rows = ObservableCollection<StorageRowViewModel>()
    let mutable selected: StorageRowViewModel option = None
    let mutable job: Job option = None
    let mutable working: StorageRowViewModel option = None
    let mutable batchQueue: StorageRowViewModel list = []
    let mutable isBatch = false
    let mutable jobVerb = ""
    let mutable measureStatus = ""
    let mutable started = false
    let mutable isActive = false

    let settingsPath () = Path.Combine(AppLog.dataFolder (), "SpacePress", "settings.json")
    let cachePath () = Path.Combine(AppLog.dataFolder (), "SpacePress", "sizes.json")

    let loadSettings () =
        try
            if File.Exists(settingsPath ()) then
                let s = JsonSerializer.Deserialize<SpacePressSettings>(File.ReadAllText(settingsPath ()))
                if isNull (box s) || isNull s.Algorithm then None else Some s
            else None
        with _ -> None

    let initial = loadSettings ()
    let mutable algorithm =
        initial
        |> Option.bind (fun s -> algorithms |> List.tryFind (fun a -> algorithmName a = s.Algorithm))
        |> Option.defaultValue Xpress8K
    let mutable skipMedia = initial |> Option.map (fun s -> s.SkipMedia) |> Option.defaultValue true
    let mutable skipPackages = initial |> Option.map (fun s -> s.SkipPackages) |> Option.defaultValue true

    let saveSettings () =
        try
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath ())) |> ignore
            let s = { Algorithm = algorithmName algorithm; SkipMedia = skipMedia; SkipPackages = skipPackages }
            File.WriteAllText(settingsPath (), JsonSerializer.Serialize(s))
        with ex -> AppLog.error "Saving the Space Press settings failed" ex

    let cache = Dictionary<string, SpacePressCached>()

    let toCached (i: FolderInfo) : SpacePressCached =
        { OriginalBytes = i.OriginalBytes
          OnDiskBytes = i.OnDiskBytes
          EstimatedSavingBytes = i.EstimatedSavingBytes
          Files = i.Files
          Status =
            match i.Status with
            | Plain -> "plain"
            | Partly -> "partly"
            | Squished alg -> alg }

    let ofCached (c: SpacePressCached) : FolderInfo =
        { OriginalBytes = c.OriginalBytes
          OnDiskBytes = c.OnDiskBytes
          EstimatedSavingBytes = c.EstimatedSavingBytes
          Files = c.Files
          Status =
            match c.Status with
            | "plain" -> Plain
            | "partly" -> Partly
            | alg -> Squished alg }

    let loadCache () =
        try
            if File.Exists(cachePath ()) then
                let saved = JsonSerializer.Deserialize<Dictionary<string, SpacePressCached>>(File.ReadAllText(cachePath ()))
                if not (isNull saved) then
                    for kv in saved do cache.[kv.Key] <- kv.Value
        with _ -> ()

    let saveCache () =
        try
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath ())) |> ignore
            File.WriteAllText(cachePath (), JsonSerializer.Serialize(cache))
        with ex -> AppLog.error "Saving the Space Press sizes failed" ex

    let ui (f: unit -> unit) = Dispatcher.UIThread.Post(fun () -> f ())

    member _.Rows = rows

    member this.Selected
        with get () = selected
        and set (value: StorageRowViewModel option) =
            selected |> Option.iter (fun r -> r.IsSelected <- false)
            selected <- value
            selected |> Option.iter (fun r -> r.IsSelected <- true)
            for name in [ "Selected"; "HasSelected"; "SelectedRow"; "CanSquish"; "CanUnsquish"; "CanControl"; "SelectedReason" ] do
                this.RaisePropertyChanged(name)

    member _.HasSelected = selected.IsSome
    member _.SelectedRow = match selected with Some r -> box r | None -> null

    /// Why the selected game cannot be squished, worked out when it is picked.
    member _.SelectedReason =
        match selected with
        | Some r -> r.Message
        | None -> ""

    member _.IsBusy = job.IsSome
    member _.IsPaused = match job with Some j -> j.IsPaused | None -> false
    member this.CanControl = job.IsSome
    member this.CanSquish = job.IsNone && (match selected with Some r -> r.Info.IsSome && not r.IsSquished | None -> false)
    member this.CanUnsquish = job.IsNone && (match selected with Some r -> (match r.Info with Some i -> i.Status <> Plain | None -> false) | None -> false)

    member _.AlgorithmOptions = algorithms |> List.map algorithmBlurb |> Array.ofList

    member this.AlgorithmIndex
        with get () = algorithms |> List.findIndex ((=) algorithm)
        and set (i: int) =
            if i >= 0 && i < algorithms.Length && algorithms.[i] <> algorithm then
                algorithm <- algorithms.[i]
                saveSettings ()
                this.RaisePropertyChanged("AlgorithmIndex")

    member this.SkipMedia
        with get () = skipMedia
        and set value =
            if skipMedia <> value then
                skipMedia <- value
                saveSettings ()
                this.RaisePropertyChanged("SkipMedia")

    member this.SkipPackages
        with get () = skipPackages
        and set value =
            if skipPackages <> value then
                skipPackages <- value
                saveSettings ()
                this.RaisePropertyChanged("SkipPackages")

    member _.MeasureStatus = measureStatus
    member _.IsMeasuring = not (String.IsNullOrWhiteSpace(measureStatus))

    // ---- totals across the library ----
    member _.ReclaimedText =
        rows |> Seq.sumBy (fun r -> r.SavedBytes) |> formatBytes

    member _.FootprintText =
        rows |> Seq.sumBy (fun r -> match r.Info with Some i -> i.OnDiskBytes | None -> 0L) |> formatBytes

    member _.SquishedCountText =
        let measured = rows |> Seq.filter (fun r -> r.Info.IsSome) |> Seq.length
        let squished = rows |> Seq.filter (fun r -> r.IsSquished) |> Seq.length
        sprintf "%d of %d" squished measured

    member _.PotentialText =
        rows
        |> Seq.sumBy (fun r -> match r.Info with Some i when i.Status = Plain -> i.EstimatedSavingBytes | _ -> 0L)
        |> formatBytes

    member this.BatchText =
        if isBatch then sprintf "STOP SQUISH ALL (%d left)" (batchQueue.Length + 1)
        else
            let n = rows |> Seq.filter (fun r -> r.IsPlain) |> Seq.length
            sprintf "SQUISH ALL (%d)" n

    member private this.RaiseTotals() =
        for name in [ "ReclaimedText"; "FootprintText"; "SquishedCountText"; "PotentialText"; "BatchText"
                      "IsBusy"; "IsPaused"; "CanControl"; "CanSquish"; "CanUnsquish"; "SelectedReason" ] do
            this.RaisePropertyChanged(name)

    member private this.SetMeasureStatus(text: string) =
        measureStatus <- text
        this.RaisePropertyChanged("MeasureStatus")
        this.RaisePropertyChanged("IsMeasuring")

    /// Builds the list from the library and measures what is not known yet.
    /// Called each time the edition opens; cheap after the first time.
    member this.Activate() =
        isActive <- true
        if not started then
            started <- true
            loadCache ()

        let known = rows |> Seq.map (fun r -> r.Key) |> HashSet
        for card in library do
            if not (known.Contains(card.Game.AppId)) && Directory.Exists(card.InstallDirectory) then
                let row = StorageRowViewModel(card)
                match cache.TryGetValue(card.Game.AppId) with
                | true, c -> row.Info <- Some(ofCached c)
                | _ -> ()
                rows.Add(row)

        this.RaiseTotals()
        let unknown = rows |> Seq.filter (fun r -> r.Info.IsNone) |> List.ofSeq
        if not unknown.IsEmpty && not this.IsMeasuring then
            this.Measure(unknown)

    /// Walks each folder one game at a time, on a lowest-priority thread so
    /// the rest of the app (and the PC) keeps its speed, and only while the
    /// page is open - leaving stops it between games, and coming back picks
    /// up the ones still unmeasured.
    member this.Measure(targets: StorageRowViewModel list) =
        let total = targets.Length
        this.SetMeasureStatus(sprintf "Measuring 0 of %d games..." total)
        let worker =
            Threading.Thread(
                (fun () ->
                    let mutable i = 0
                    for row in targets do
                        if isActive then
                            i <- i + 1
                            let n = i
                            ui (fun () ->
                                row.IsMeasuring <- true
                                this.SetMeasureStatus(sprintf "Measuring %d of %d games..." n total))
                            let info = try Some(analyse row.Folder) with _ -> None
                            ui (fun () ->
                                row.IsMeasuring <- false
                                match info with
                                | Some i ->
                                    row.Info <- Some i
                                    cache.[row.Key] <- toCached i
                                    // Saved as it goes: closing halfway keeps what was measured.
                                    saveCache ()
                                | None -> ()
                                this.RaiseTotals())
                    ui (fun () -> this.SetMeasureStatus(""))),
                IsBackground = true,
                Priority = Threading.ThreadPriority.Lowest,
                Name = "Space Press measuring")
        worker.Start()

    /// Leaving the edition: stop measuring after the game in hand.
    member this.Deactivate() = isActive <- false

    member this.Select(row: StorageRowViewModel) =
        this.Selected <- Some row
        // The check looks at every running process, so not on the UI thread.
        if job.IsNone then
            Task.Run(fun () ->
                let reason = try refusal row.Folder with _ -> None
                ui (fun () ->
                    row.Message <- defaultArg reason ""
                    this.RaisePropertyChanged("SelectedReason")))
            |> ignore

    member this.Refresh() =
        match selected with
        | Some row when job.IsNone -> this.Measure([ row ])
        | _ -> ()

    member private this.Finish(row: StorageRowViewModel) (ok: bool) =
        row.IsWorking <- false
        row.ProgressText <- ""
        job <- None
        working <- None
        // Measure what actually happened on disk rather than trusting the job.
        Task.Run(fun () ->
            let info = try Some(analyse row.Folder) with _ -> None
            ui (fun () ->
                info |> Option.iter (fun i ->
                    row.Info <- Some i
                    cache.[row.Key] <- toCached i
                    saveCache ())
                if not ok then row.Message <- "Stopped. What was already squished stays squished - Unsquish undoes it."
                this.RaiseTotals()
                // A batch carries on with the next game.
                if isBatch then
                    match batchQueue with
                    | next :: rest when ok ->
                        batchQueue <- rest
                        this.Start(next, true)
                    | _ ->
                        isBatch <- false
                        batchQueue <- []
                        this.RaiseTotals()))
        |> ignore

    member private this.Start(row: StorageRowViewModel, squishIt: bool) =
        match refusal row.Folder with
        | Some reason ->
            row.Message <- reason
            this.RaisePropertyChanged("SelectedReason")
            if isBatch then
                // Skip it and carry on with the rest.
                match batchQueue with
                | next :: rest ->
                    batchQueue <- rest
                    this.Start(next, true)
                | [] ->
                    isBatch <- false
                    this.RaiseTotals()
        | None ->
            let j = Job()
            job <- Some j
            working <- Some row
            row.Message <- ""
            jobVerb <- (if squishIt then "Squishing" else "Unsquishing")
            row.WorkVerb <- jobVerb
            row.Progress <- 0.0
            row.IsWorking <- true
            this.RaiseTotals()
            let alg, media, packages = algorithm, skipMedia, skipPackages
            let report (p: Progress) =
                ui (fun () ->
                    row.Progress <- p.Percent
                    row.ProgressText <- sprintf "%s  (%d of %d files)" (Path.GetFileName(p.CurrentFile)) p.DoneFiles p.TotalFiles)
            Task.Run(fun () ->
                let ok =
                    try
                        if squishIt then squish row.Folder alg media packages j report
                        else unsquish row.Folder j report
                    with ex ->
                        AppLog.error "Space Press failed" ex
                        false
                ui (fun () -> this.Finish row ok))
            |> ignore

    member this.SquishSelected() =
        match selected with
        | Some row when job.IsNone -> this.Start(row, true)
        | _ -> ()

    member this.UnsquishSelected() =
        match selected with
        | Some row when job.IsNone -> this.Start(row, false)
        | _ -> ()

    member this.PauseOrResume() =
        match job, working with
        | Some j, Some row ->
            if j.IsPaused then
                j.Resume()
                row.WorkVerb <- jobVerb
            else
                j.Pause()
                row.WorkVerb <- "Paused"
            row.Progress <- row.Progress
            this.RaisePropertyChanged("IsPaused")
            this.RaisePropertyChanged("PauseText")
        | _ -> ()

    member this.PauseText = if this.IsPaused then "RESUME" else "PAUSE"

    member this.StopJob() =
        isBatch <- false
        batchQueue <- []
        job |> Option.iter (fun j -> j.Stop())
        this.RaiseTotals()

    /// Every game not yet squished, smallest first, one after another.
    member this.SquishAllOrStop() =
        if isBatch then this.StopJob()
        elif job.IsNone then
            let queue =
                rows
                |> Seq.filter (fun r -> r.IsPlain)
                |> Seq.sortBy (fun r -> match r.Info with Some i -> i.OnDiskBytes | None -> Int64.MaxValue)
                |> List.ofSeq
            match queue with
            | first :: rest ->
                isBatch <- true
                batchQueue <- rest
                this.Start(first, true)
            | [] -> ()

    member this.OpenSelectedFolder() =
        selected |> Option.iter (fun r -> r.Card.OpenGameFolder())
