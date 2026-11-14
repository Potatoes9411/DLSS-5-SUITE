namespace DLSS_5_MANAGER.ViewModels

open System
open System.Collections.ObjectModel
open System.IO
open System.Threading
open System.Threading.Tasks
open Avalonia.Media
open Avalonia.Media.Imaging
open Avalonia.Threading
open DLSS_5_MANAGER.Models
open DLSS_5_MANAGER.Services
open DLSS_5_MANAGER.Services.UpscalerVault

/// What a choice in a file's version box does.
type VaultChoice =
    | KeepCurrent
    | PutOriginalBack
    | UseOffer of Offer

/// One upscaler DLL in the selected game, with the versions it can take.
/// Updated in place after a change rather than rebuilt: re-creating the
/// rows (a combo box each) on every swap cost the window dropped frames.
type VaultFileRow(initialFile: FoundFile, initialOffers: Offer list) =
    inherit ViewModelBase()

    let brush (hex: string) = SolidColorBrush(Color.Parse(hex)) :> IBrush

    let choicesOf (file: FoundFile) (offers: Offer list) =
        let newest = match offers with o :: _ -> o.Version | [] -> ""
        [ yield
              (sprintf "%s  ·  in the game now" (if file.Version = "" then "unknown version" else "v" + file.Version)), KeepCurrent
          match planRestore file.Facts with
          | FromBackup | FromLegacyOriginal ->
              yield (sprintf "%s  ·  the game's own" (if file.OriginalVersion = "" then "original" else "v" + file.OriginalVersion)), PutOriginalBack
          | RemoveAdded -> yield "Remove it  ·  UPSCALE IT added it", PutOriginalBack
          | _ -> ()
          for o in offers do
              let tags =
                  [ sourceName o.Source
                    if o.Version = newest then "latest"
                    if o.Source <> Library then "download" ]
              yield (sprintf "v%s  ·  %s" o.Version (String.Join(" · ", tags))), UseOffer o ]

    let mutable file = initialFile
    let mutable offers = initialOffers
    let mutable choices = choicesOf file offers
    let mutable options = choices |> List.map fst |> Array.ofList
    let mutable selectedIndex = 0

    /// Shows the file as it is now, with the versions on offer now.
    member this.Update(f: FoundFile, o: Offer list) =
        file <- f
        offers <- o
        choices <- choicesOf f o
        options <- choices |> List.map fst |> Array.ofList
        selectedIndex <- 0
        for name in [ "File"; "Label"; "FileName"; "Folder"; "VersionText"; "Options"; "IsLocked"; "SelectedIndex"; "HasChoice"
                      "HasUpdate"; "StateText"; "StateBackground"; "StateForeground" ] do
            this.RaisePropertyChanged(name)

    member _.File = file
    member _.Label = file.Family.Label
    member _.FileName = file.FileName
    member _.Folder = try Path.GetDirectoryName(file.Path) with _ -> ""
    member _.VersionText = if file.Version = "" then "version unknown" else "v" + file.Version
    member _.Options = options
    member _.IsLocked = file.State = ByInstall || file.State = Missing

    member this.SelectedIndex
        with get () = selectedIndex
        and set (i: int) =
            if i >= 0 && i < choices.Length && i <> selectedIndex then
                selectedIndex <- i
                this.RaisePropertyChanged("SelectedIndex")
                this.RaisePropertyChanged("HasChoice")

    member _.Choice = snd choices.[selectedIndex]
    member _.HasChoice = selectedIndex > 0

    member _.HasUpdate =
        match updateFor offers file with
        | Some _ -> true
        | None -> false

    member _.StateText =
        match file.State with
        | Untouched -> if offers.IsEmpty then "GAME'S OWN · NO OTHER VERSIONS" else "GAME'S OWN"
        | Swapped -> "SWAPPED"
        | SwappedByUpscaleIt -> "SWAPPED BY UPSCALE IT"
        | AddedByUpscaleIt -> "ADDED BY UPSCALE IT"
        | ByInstall -> "DLSS 5 INSTALL"
        | Missing -> "FILE MISSING"

    member _.StateBackground =
        match file.State with
        | Untouched -> brush "#1E293B"
        | Swapped | SwappedByUpscaleIt | AddedByUpscaleIt -> brush "#16324A"
        | ByInstall -> brush "#1B3629"
        | Missing -> brush "#382A2E"

    member _.StateForeground =
        match file.State with
        | Untouched -> brush "#94A3B8"
        | Swapped | SwappedByUpscaleIt | AddedByUpscaleIt -> brush "#7DD3FC"
        | ByInstall -> brush "#34D399"
        | Missing -> brush "#F87171"

/// One game in the Vault's list.
type VaultGameRow(game: GameItem, card: GameCardViewModel option) =
    inherit ViewModelBase()

    let mutable files: FoundFile list = []
    let mutable isSelected = false
    let mutable thumbnail: Bitmap option = None
    let mutable thumbnailPending = false

    member _.Game = game
    member _.Card = card
    member _.Title = match card with Some c -> c.Title | None -> game.Title
    member _.Launcher = if String.IsNullOrWhiteSpace game.LauncherTypeName then "Folder" else game.LauncherTypeName
    member _.Folder = game.InstallDirectory

    member this.Files
        with get () = files
        and set value =
            files <- value
            for name in [ "Files"; "ChipsText"; "CountText"; "IsChanged" ] do
                this.RaisePropertyChanged(name)

    /// "DLSS 310.2.1 · FSR · XeSS 2.0.1" - what the game ships, at a glance.
    member _.ChipsText =
        files
        |> List.distinctBy (fun f -> f.Family.Key)
        |> List.map (fun f -> if f.Version = "" || not f.Family.VersionFromFile && f.State = Untouched then f.Family.Chip else sprintf "%s %s" f.Family.Chip f.Version)
        |> fun parts -> String.Join("  ·  ", parts)

    member _.CountText = match files.Length with 1 -> "1 file" | n -> sprintf "%d files" n

    member _.IsChanged =
        files |> List.exists (fun f -> f.State = Swapped || f.State = SwappedByUpscaleIt || f.State = AddedByUpscaleIt)

    member this.IsSelected
        with get () = isSelected
        and set value =
            isSelected <- value
            this.RaisePropertyChanged("IsSelected")

    /// A small copy of the cover, decoded once off the UI thread, as on the
    /// Space Press page.
    member this.Cover: Bitmap =
        match thumbnail with
        | Some t -> t
        | None ->
            if not thumbnailPending then
                thumbnailPending <- true
                let path = game.LocalBannerPath
                Task.Run(fun () ->
                    let small =
                        try
                            if String.IsNullOrWhiteSpace(path) || not (File.Exists(path)) then None
                            else
                                use stream = File.OpenRead(path)
                                Some(Bitmap.DecodeToWidth(stream, 80, BitmapInterpolationMode.HighQuality))
                        with _ -> None
                    Dispatcher.UIThread.Post(fun () ->
                        match small with
                        | Some s ->
                            thumbnail <- Some s
                            this.RaisePropertyChanged("Cover")
                            this.RaisePropertyChanged("HasCover")
                        | None -> ()))
                |> ignore
            null

    member _.HasCover = thumbnail.IsSome

/// The Upscaler Vault edition: every game's upscaler DLLs, on the version
/// you pick, with the game's own one always a choice away.
///
/// Looking through the library touches thousands of folders, so it runs on
/// one lowest-priority thread, a game at a time, and only while the page is
/// open; downloads, checks and copies run off the UI thread too.
type UpscalerVaultViewModel(library: ObservableCollection<GameCardViewModel>) =
    inherit ViewModelBase()

    let rows = ObservableCollection<VaultGameRow>()
    let fileRows = ObservableCollection<VaultFileRow>()
    let looked = Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
    let mutable selected: VaultGameRow option = None
    let mutable offers: Offer list = []
    // What fits each file name, worked out once per list: the totals and
    // every row ask, and matching ~100 files against ~90 offers each time
    // cost the window frames.
    let offersCache = Collections.Generic.Dictionary<string, Offer list>()
    let offersOf (f: FoundFile) =
        let key = f.Family.Key + "|" + f.FileName.ToLowerInvariant()
        match offersCache.TryGetValue key with
        | true, o -> o
        | _ ->
            let o = offersFor f.FileName f.Family.Key offers
            offersCache.[key] <- o
            o
    let setOffers (o: Offer list) =
        offers <- o
        offersCache.Clear()
    let mutable catalogStatus = "Loading the version lists..."
    let mutable catalogLoading = false
    let mutable isActive = false
    let mutable scanStatus = ""
    let mutable scanning = false
    let mutable scannedGames = 0
    let mutable busy = false
    let mutable jobTitle = ""
    let mutable jobText = ""
    let mutable jobProgress = 0.0
    let mutable message = ""
    let mutable refusalText = ""
    let mutable cancel = new CancellationTokenSource()

    let ui (f: unit -> unit) = Dispatcher.UIThread.Post(fun () -> f ())
    let folderKey (path: string) = (try Path.GetFullPath(path) with _ -> path).TrimEnd('\\').ToLowerInvariant()

    member _.Rows = rows
    member _.FileRows = fileRows

    member _.HasSelected = selected.IsSome
    member _.SelectedRow = match selected with Some r -> box r | None -> null
    member _.SelectedTitle = match selected with Some r -> r.Title | None -> ""
    member _.SelectedFolder = match selected with Some r -> r.Folder | None -> ""

    member _.CatalogStatus = catalogStatus
    member _.ScanStatus = scanStatus
    member _.IsScanning = scanning
    member _.IsBusy = busy
    member _.IsIdle = not busy
    member _.JobTitle = jobTitle
    member _.JobText = jobText
    member _.JobProgress = jobProgress

    /// The last thing done, or what went wrong.
    member _.Message = message
    member _.HasMessage = not (String.IsNullOrWhiteSpace message)

    /// Why the selected game's files cannot be changed right now.
    member _.RefusalText = refusalText
    member _.HasRefusal = not (String.IsNullOrWhiteSpace refusalText)
    member this.CanChange = not busy && not this.HasRefusal

    // ---- totals ----
    member _.GamesText = string rows.Count
    member _.FilesText = rows |> Seq.sumBy (fun r -> r.Files.Length) |> string
    member _.ChangedText = rows |> Seq.filter (fun r -> r.IsChanged) |> Seq.length |> string

    member _.UpdatesText =
        rows
        |> Seq.sumBy (fun r -> r.Files |> List.filter (fun f -> (updateFor (offersOf f) f).IsSome) |> List.length)
        |> string

    member _.CanUpdateAll = fileRows |> Seq.exists (fun f -> f.HasUpdate)
    member _.CanRestoreAll =
        fileRows |> Seq.exists (fun f -> match planRestore f.File.Facts with NothingToDo -> false | _ -> true)

    member private this.Raise(names: string list) =
        for n in names do
            this.RaisePropertyChanged(n)

    member private this.RaiseTotals() =
        this.Raise [ "GamesText"; "FilesText"; "ChangedText"; "UpdatesText" ]

    member private this.RaiseJob() =
        this.Raise [ "IsBusy"; "IsIdle"; "JobTitle"; "JobText"; "JobProgress"; "CanChange"; "CanUpdateAll"; "CanRestoreAll" ]

    member private this.SetMessage(text: string) =
        message <- text
        this.Raise [ "Message"; "HasMessage" ]

    member private this.SetScanStatus(text: string) =
        scanStatus <- text
        scanning <- text <> ""
        this.Raise [ "ScanStatus"; "IsScanning" ]

    /// The selected game's file rows, from what is on disk now. The same
    /// files as before are updated in place; another game's are rebuilt.
    member private this.ShowFiles() =
        let files = match selected with Some r -> r.Files | None -> []
        let same =
            files.Length = fileRows.Count
            && List.forall2 (fun (f: FoundFile) (r: VaultFileRow) -> f.Path = r.File.Path) files (List.ofSeq fileRows)
        if same then
            files |> List.iteri (fun i f -> fileRows.[i].Update(f, offersOf f))
        else
            fileRows.Clear()
            for f in files do
                fileRows.Add(VaultFileRow(f, offersOf f))
        this.Raise [ "CanUpdateAll"; "CanRestoreAll" ]

    member this.Select(row: VaultGameRow) =
        selected |> Option.iter (fun r -> r.IsSelected <- false)
        selected <- Some row
        row.IsSelected <- true
        refusalText <- ""
        this.SetMessage ""
        this.ShowFiles()
        this.Raise [ "HasSelected"; "SelectedRow"; "SelectedTitle"; "SelectedFolder"; "RefusalText"; "HasRefusal"; "CanChange" ]
        // The anti-cheat walk and the write probe touch the disk.
        let game = row.Game
        Task.Run(fun () ->
            let why = try refusal game with ex -> Some ex.Message
            ui (fun () ->
                match selected with
                | Some current when obj.ReferenceEquals(current, row) ->
                    refusalText <- defaultArg why ""
                    this.Raise [ "RefusalText"; "HasRefusal"; "CanChange" ]
                | _ -> ()))
        |> ignore

    /// For the test bridge and keyboard-free use: select by list position.
    member this.SelectAt(index: int) =
        if index >= 0 && index < rows.Count then this.Select(rows.[index])

    member private this.AddRow(row: VaultGameRow) =
        // Sorted by title as they arrive, so the list does not jump about.
        let at =
            rows
            |> Seq.tryFindIndex (fun r -> String.Compare(r.Title, row.Title, StringComparison.CurrentCultureIgnoreCase) > 0)
            |> Option.defaultValue rows.Count
        rows.Insert(at, row)

    /// Looks through games not looked at yet, one at a time, at the lowest
    /// priority and only while the page is open.
    member private this.Scan(targets: (GameItem * GameCardViewModel option) list) =
        if not scanning && not targets.IsEmpty then
            let total = targets.Length
            this.SetScanStatus(sprintf "Looking through 0 of %d games..." total)
            let worker =
                Thread(
                    (fun () ->
                        let mutable n = 0
                        for (game, card) in targets do
                            if isActive then
                                n <- n + 1
                                let shown = n
                                let found = try scan game with _ -> []
                                looked.Add(folderKey game.InstallDirectory) |> ignore
                                ui (fun () ->
                                    scannedGames <- scannedGames + 1
                                    this.SetScanStatus(sprintf "Looking through %d of %d games..." shown total)
                                    if not found.IsEmpty then
                                        let row = VaultGameRow(game, card)
                                        row.Files <- found
                                        this.AddRow(row)
                                        this.RaiseTotals())
                        ui (fun () ->
                            this.SetScanStatus ""
                            this.RaiseTotals())),
                    IsBackground = true,
                    Priority = ThreadPriority.Lowest,
                    Name = "Upscaler Vault scan")
            worker.Start()

    member private this.Unlooked() =
        [ for card in library do
              let g = card.Game
              if not (String.IsNullOrWhiteSpace g.InstallDirectory) && not (looked.Contains(folderKey g.InstallDirectory))
                 && Directory.Exists g.InstallDirectory then
                  yield g, Some card ]
        |> List.distinctBy (fun (g, _) -> folderKey g.InstallDirectory)

    member this.LoadCatalog(force: bool) =
        if not catalogLoading then
            catalogLoading <- true
            if force then
                catalogStatus <- "Fetching the version lists..."
                this.RaisePropertyChanged("CatalogStatus")
            Task.Run(fun () ->
                let result =
                    try Async.RunSynchronously(catalog force)
                    with ex -> { Offers = libraryOffers (); Status = "The version lists could not be read: " + ex.Message }
                ui (fun () ->
                    catalogLoading <- false
                    setOffers result.Offers
                    catalogStatus <- result.Status
                    this.RaisePropertyChanged("CatalogStatus")
                    this.ShowFiles()
                    this.RaiseTotals()))
            |> ignore

    /// Opening the edition: the version lists, then any games not looked at.
    member this.Activate() =
        isActive <- true
        if offers.IsEmpty then this.LoadCatalog(false)
        this.Scan(this.Unlooked())

    member this.Deactivate() = isActive <- false

    /// Every game looked at again from the start.
    member this.Rescan() =
        if not scanning && not busy then
            let extra = rows |> Seq.filter (fun r -> r.Card.IsNone) |> Seq.map (fun r -> r.Game, None) |> List.ofSeq
            looked.Clear()
            selected <- None
            rows.Clear()
            fileRows.Clear()
            this.Raise [ "HasSelected"; "SelectedRow"; "SelectedTitle"; "SelectedFolder" ]
            this.RaiseTotals()
            this.Scan(this.Unlooked() @ extra)

    /// A folder outside the library - a game SUITE did not find, or a copy
    /// to try a version on first. Kept for this session only.
    member this.AddFolder(path: string) =
        if not (String.IsNullOrWhiteSpace path) && Directory.Exists path then
            let key = folderKey path
            match rows |> Seq.tryFind (fun r -> folderKey r.Folder = key) with
            | Some existing -> this.Select(existing)
            | None ->
                let full = Path.GetFullPath(path).TrimEnd('\\')
                let game =
                    { AppId = "folder:" + key; Title = Path.GetFileName(full); LauncherTypeName = "Folder"
                      InstallDirectory = full; TargetExecutablePath = ""; TargetExecutableSize = 0L; LocalBannerPath = ""
                      LastManifestTimestamp = 0L; UpscaleStatus = ""; DlssVersion = ""; FsrVersion = ""; XessVersion = "" }
                Task.Run(fun () ->
                    let found = try scan game with _ -> []
                    ui (fun () ->
                        looked.Add(key) |> ignore
                        if found.IsEmpty then this.SetMessage(sprintf "No DLSS, FSR, XeSS or XeLL DLL in %s." full)
                        else
                            let row = VaultGameRow(game, None)
                            row.Files <- found
                            this.AddRow(row)
                            this.RaiseTotals()
                            this.Select(row)))
                |> ignore

    /// Reads the selected game's files again after a change.
    member private this.Reread(row: VaultGameRow) =
        let found = try scan row.Game with _ -> row.Files
        ui (fun () ->
            row.Files <- found
            match selected with
            | Some s when obj.ReferenceEquals(s, row) -> this.ShowFiles()
            | _ -> ()
            this.RaiseTotals())

    /// Runs one change, or a list of them, off the UI thread with the strip
    /// at the top showing what is happening.
    member private this.Run(title: string, work: (string -> float -> unit) -> CancellationToken -> Async<string list>) =
        match selected with
        | Some row when not busy ->
            busy <- true
            cancel.Dispose()
            cancel <- new CancellationTokenSource()
            let token = cancel.Token
            jobTitle <- title
            jobText <- "Starting..."
            jobProgress <- 0.0
            this.SetMessage ""
            this.RaiseJob()
            let report (text: string) (fraction: float) =
                ui (fun () ->
                    jobText <- text
                    jobProgress <- fraction * 100.0
                    this.Raise [ "JobText"; "JobProgress" ])
            Task.Run(fun () ->
                let lines =
                    try Async.RunSynchronously(work report token)
                    with ex -> [ ex.Message ]
                // Something new may be in the library now.
                let library' = libraryOffers ()
                this.Reread(row)
                ui (fun () ->
                    setOffers (library' @ (offers |> List.filter (fun o -> o.Source <> Library)))
                    busy <- false
                    this.ShowFiles()
                    this.SetMessage(String.Join("  ", lines))
                    this.RaiseJob()
                    this.RaiseTotals()))
            |> ignore
        | _ -> ()

    member private this.ApplyChoice (game: GameItem) (file: FoundFile) (choice: VaultChoice) report token =
        async {
            match choice with
            | KeepCurrent -> return "Nothing to change."
            | PutOriginalBack ->
                report "Putting the game's own file back..." 0.5
                return (match restore game file with Ok l -> l | Error e -> e)
            | UseOffer o ->
                let! r = apply game file o report token
                return (match r with Ok l -> l | Error e -> e)
        }

    /// Puts the version chosen in a file's box into the game.
    member this.Apply(fileRow: VaultFileRow) =
        match selected with
        | Some row when fileRow.HasChoice ->
            let game, file, choice = row.Game, fileRow.File, fileRow.Choice
            this.Run(sprintf "%s · %s" (row.Title.ToUpperInvariant()) (file.Family.Label.ToUpperInvariant()), fun report token ->
                async {
                    let! line = this.ApplyChoice game file choice report token
                    return [ line ]
                })
        | _ -> ()

    /// For the test bridge: pick option `option` for file `index`, and apply it.
    member this.ApplyAt(index: int, option: int) =
        if index >= 0 && index < fileRows.Count then
            let r = fileRows.[index]
            r.SelectedIndex <- option
            this.Apply(r)

    /// Every file with a newer version on offer, to the newest.
    member this.UpdateAll() =
        match selected with
        | Some row ->
            let game = row.Game
            let targets = fileRows |> Seq.choose (fun f -> updateFor (offersOf f.File) f.File |> Option.map (fun o -> f.File, o)) |> List.ofSeq
            if not targets.IsEmpty then
                this.Run(sprintf "%s · UPDATING %d FILES" (row.Title.ToUpperInvariant()) targets.Length, fun report token ->
                    async {
                        let lines = ResizeArray<string>()
                        for (file, o) in targets do
                            if not token.IsCancellationRequested then
                                let! line = this.ApplyChoice game file (UseOffer o) report token
                                lines.Add line
                        return List.ofSeq lines
                    })
        | None -> ()

    /// The game's own file back in every place the Vault (or UPSCALE IT) changed.
    member this.RestoreAll() =
        match selected with
        | Some row ->
            let game = row.Game
            let targets =
                fileRows
                |> Seq.map (fun f -> f.File)
                |> Seq.filter (fun f -> match planRestore f.Facts with NothingToDo -> false | _ -> true)
                |> List.ofSeq
            if not targets.IsEmpty then
                this.Run(sprintf "%s · RESTORING" (row.Title.ToUpperInvariant()), fun report _ ->
                    async {
                        report "Putting the game's own files back..." 0.5
                        return targets |> List.map (fun f -> match restore game f with Ok l -> l | Error e -> e)
                    })
        | None -> ()

    /// Stops a download in progress; a file already being copied finishes.
    member this.Cancel() = if busy then cancel.Cancel()

    member this.OpenSelectedFolder() =
        match selected with
        | Some r ->
            match r.Card with
            | Some c -> c.OpenGameFolder()
            | None ->
                try Diagnostics.Process.Start("explorer.exe", "\"" + r.Folder + "\"") |> ignore with _ -> ()
        | None -> ()
