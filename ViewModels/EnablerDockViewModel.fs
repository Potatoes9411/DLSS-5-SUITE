namespace DLSS_5_MANAGER.ViewModels

open System
open System.Collections.Generic
open System.Collections.ObjectModel
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Avalonia.Media
open Avalonia.Media.Imaging
open Avalonia.Threading
open DLSS_5_MANAGER.Models
open DLSS_5_MANAGER.Services
open DLSS_5_MANAGER.Services.DlssEnabler

/// What one game was set to on this page. Public on purpose: System.Text.Json
/// writes a private F# record as "{}".
[<CLIMutable>]
type EnablerGameChoice =
    { Key: string
      /// "" for Auto, or a proxy name.
      Proxy: string
      /// "" for the newest stable version, or a catalog name.
      Version: string
      /// "Auto", "Game original" or a Streamline catalog name.
      Streamline: string }

[<CLIMutable>]
type EnablerUserFix =
    { Game: string
      Dll: string
      Note: string }

[<CLIMutable>]
type EnablerDockSettings =
    { /// Show games without Streamline too. Off, like the original's
      /// "show only supported games", which was on by default.
      ShowAll: bool
      /// Download catalog versions SUITE has no checksum for.
      AllowUnchecked: bool
      /// Leave dlss-enabler.ini behind on uninstall.
      KeepSettings: bool
      /// Folders added on this page, outside the library.
      Folders: string[]
      Choices: EnablerGameChoice[]
      Fixes: EnablerUserFix[] }

/// One game in the Enabler Dock list.
type EnablerRowViewModel(game: GameItem, knownStreamlineDirs: string[], isCustom: bool) =
    inherit ViewModelBase()

    let mutable isSelected = false
    let mutable checkedOnce = false
    let mutable installedVersion = ""
    let mutable proxy = ""
    let mutable legacy = false
    let mutable readiness: Readiness option = None
    let mutable thumbnail: Bitmap option = None
    let mutable thumbnailPending = false

    let brush (hex: string) = SolidColorBrush(Color.Parse(hex)) :> IBrush

    member _.Game = game
    member _.Key = game.AppId
    member _.Title = game.Title
    member _.Folder = game.InstallDirectory
    member _.Launcher = if isCustom then "Added folder" else game.LauncherTypeName
    member _.IsCustom = isCustom
    member _.KnownStreamlineDirs = knownStreamlineDirs

    /// A small cover, decoded once off the UI thread at the size shown.
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

    member this.IsSelected
        with get () = isSelected
        and set value =
            isSelected <- value
            this.RaisePropertyChanged("IsSelected")

    /// Takes what a background look at the game found.
    member this.SetState(version: string, proxyName: string, isLegacy: bool, ready: Readiness) =
        checkedOnce <- true
        installedVersion <- version
        proxy <- proxyName
        legacy <- isLegacy
        readiness <- Some ready
        for name in [ "IsInstalled"; "IsLegacy"; "InstalledVersion"; "Proxy"; "Detail"; "StatusText"; "StatusBackground"; "StatusForeground"; "IsChecked"; "CanRun" ] do
            this.RaisePropertyChanged(name)

    member _.IsChecked = checkedOnce
    member _.IsInstalled = installedVersion <> ""
    member _.IsLegacy = legacy
    member _.InstalledVersion = installedVersion
    member _.Proxy = proxy
    member _.Readiness = readiness

    /// Could DLSS Enabler run here at all - what "show only" filters on.
    member _.CanRun =
        installedVersion <> "" || legacy
        || (match readiness with Some(Ready _) | Some(MayNotWork _) -> true | _ -> false)

    member _.Detail =
        let sl =
            match readiness with
            | Some(Ready v) -> sprintf "Streamline %s" v
            | Some(MayNotWork v) -> sprintf "Streamline %s (older than 2.4)" v
            | Some NoFrameGen -> "No Streamline"
            | None -> ""
        if installedVersion <> "" then sprintf "%s as %s  ·  %s" (installedVersion.Replace("Dlss Enabler ", "v")) proxy sl
        else sl

    member _.StatusText =
        if not checkedOnce then "Checking"
        elif installedVersion <> "" then "Installed"
        elif legacy then "Old manager install"
        else
            match readiness with
            | Some(Ready _) -> "Ready"
            | Some(MayNotWork _) -> "May not work"
            | Some NoFrameGen -> "Missing frame gen"
            | None -> "Checking"

    member _.StatusBackground =
        if not checkedOnce then brush "#1E293B"
        elif installedVersion <> "" then brush "#1B3629"
        elif legacy then brush "#3A2E1E"
        else
            match readiness with
            | Some(Ready _) -> brush "#1A2F3A"
            | Some(MayNotWork _) -> brush "#3A2E1E"
            | _ -> brush "#382A2E"

    member _.StatusForeground =
        if not checkedOnce then brush "#94A3B8"
        elif installedVersion <> "" then brush "#34D399"
        elif legacy then brush "#FBBF24"
        else
            match readiness with
            | Some(Ready _) -> brush "#7DD3FC"
            | Some(MayNotWork _) -> brush "#FBBF24"
            | _ -> brush "#F87171"

/// The Enabler Dock edition: DLSS Enabler on any game in the library, with
/// its version, proxy name and Streamline chosen per game, and its main
/// settings edited in place. Every disk walk, download and install runs off
/// the UI thread; the page only ever receives finished answers.
type EnablerDockViewModel(library: ObservableCollection<GameCardViewModel>) as this =
    inherit ViewModelBase()

    let allRows = List<EnablerRowViewModel>()
    let rows = ObservableCollection<EnablerRowViewModel>()
    let mutable selected: EnablerRowViewModel option = None
    let mutable isActive = false
    let mutable checking = false
    let mutable catalog, catalogAt = Catalog.cached ()
    let mutable catalogNote = ""
    let mutable refreshingCatalog = false

    // The job strip.
    let mutable jobTitle = ""
    let mutable jobText = ""
    let mutable jobProgress = 0.0
    let mutable busy = false

    // The selected game, as last looked at.
    let mutable message = ""
    let mutable messageIsError = false
    let mutable blockerText = ""
    let mutable fixNote = ""
    let mutable injectionNames: string list = []
    let mutable injectionIndex = 0
    let mutable versionIndex = 0
    let mutable streamlineIndex = 0
    let mutable loadingSelection = false

    // Settings file of the selected game.
    let mutable iniLines: string[] = [||]
    let mutable iniPath = ""
    let mutable watcher: FileSystemWatcher option = None
    let mutable ignoreWatcherUntil = DateTime.MinValue

    let mutable isFixesOpen = false
    let mutable newFixGame = ""
    let mutable newFixDllIndex = 0
    let mutable newFixNote = ""

    let settingsPath () = Path.Combine(DlssEnabler.root (), "settings.json")

    let loadSettings () =
        let empty = { ShowAll = false; AllowUnchecked = false; KeepSettings = false; Folders = [||]; Choices = [||]; Fixes = [||] }
        try
            if File.Exists(settingsPath ()) then
                let s = JsonSerializer.Deserialize<EnablerDockSettings>(File.ReadAllText(settingsPath ()))
                if isNull (box s) then empty
                else
                    { s with
                        Folders = (if isNull s.Folders then [||] else s.Folders)
                        Choices = (if isNull s.Choices then [||] else s.Choices)
                        Fixes = (if isNull s.Fixes then [||] else s.Fixes) }
            else empty
        with _ -> empty

    let mutable settings = loadSettings ()

    let saveSettings () =
        try File.WriteAllText(settingsPath (), JsonSerializer.Serialize(settings, JsonSerializerOptions(WriteIndented = true)))
        with ex -> AppLog.error "Saving the Enabler Dock settings failed" ex

    let ui (f: unit -> unit) = Dispatcher.UIThread.Post(fun () -> f ())

    let choiceFor (key: string) =
        settings.Choices
        |> Array.tryFind (fun c -> c.Key = key)
        |> Option.defaultValue { Key = key; Proxy = ""; Version = ""; Streamline = "Auto" }

    let setChoice (choice: EnablerGameChoice) =
        settings <- { settings with Choices = Array.append (settings.Choices |> Array.filter (fun c -> c.Key <> choice.Key)) [| choice |] }
        saveSettings ()

    let userFixes () =
        settings.Fixes |> Array.map (fun f -> { Catalog.Game = f.Game; Catalog.Dll = f.Dll; Catalog.Note = f.Note }) |> List.ofArray

    /// Catalog versions newest first, then archives on disk it does not list.
    let enablerEntries () =
        let local, _ = localEntries ()
        let listed = Catalog.sorted catalog.Enablers
        listed @ (local |> List.filter (fun l -> not (listed |> List.exists (fun e -> e.FileName = l.FileName))))

    let streamlineEntries () =
        let _, local = localEntries ()
        let listed = Catalog.sorted catalog.Streamlines
        listed @ (local |> List.filter (fun l -> not (listed |> List.exists (fun e -> e.FileName = l.FileName))))

    let latestEnabler () = Catalog.latestStable (enablerEntries ())
    let latestStreamline () = Catalog.sorted (streamlineEntries ()) |> List.tryHead

    let shortHash (text: string) =
        SHA256.HashData(Encoding.UTF8.GetBytes(text.ToLowerInvariant())) |> Convert.ToHexString |> fun h -> h.Substring(0, 12)

    /// A folder added on this page, as a library-like game.
    let customGame (folder: string) : GameItem =
        let name = Path.GetFileName(folder.TrimEnd('\\', '/'))
        let exe, size = try GameScanner.resolvePrimaryExecutable folder name with _ -> "", 0L
        { AppId = "enabler_" + shortHash folder
          Title = name
          LauncherTypeName = "CUSTOM"
          InstallDirectory = folder
          TargetExecutablePath = exe
          TargetExecutableSize = size
          LocalBannerPath = ""
          LastManifestTimestamp = 0L
          UpscaleStatus = ""
          DlssVersion = ""
          FsrVersion = ""
          XessVersion = "" }

    let iniWatchTimer = DispatcherTimer(Interval = TimeSpan.FromMilliseconds(200.0))

    do
        iniWatchTimer.Tick.Add(fun _ ->
            iniWatchTimer.Stop()
            this.ReloadIni())

    // =====================================================================
    // THE LIST
    // =====================================================================
    member _.Rows = rows
    member _.HasRows = rows.Count > 0
    member _.IsEmpty = rows.Count = 0 && not checking

    member _.EmptyText =
        if allRows.Count = 0 then "No games yet. Scan your library in the DLSS 5 SUITE edition, or add a game folder here."
        else "No game here has NVIDIA Streamline. Turn on \"Show every game\" to see them all."

    member this.ShowAll
        with get () = settings.ShowAll
        and set value =
            if value <> settings.ShowAll then
                settings <- { settings with ShowAll = value }
                saveSettings ()
                this.RaisePropertyChanged("ShowAll")
                this.ApplyFilter()

    member this.AllowUnchecked
        with get () = settings.AllowUnchecked
        and set value =
            if value <> settings.AllowUnchecked then
                settings <- { settings with AllowUnchecked = value }
                saveSettings ()
                this.RaisePropertyChanged("AllowUnchecked")

    member this.KeepSettings
        with get () = settings.KeepSettings
        and set value =
            if value <> settings.KeepSettings then
                settings <- { settings with KeepSettings = value }
                saveSettings ()
                this.RaisePropertyChanged("KeepSettings")

    member private this.ApplyFilter() =
        let wanted =
            allRows
            |> Seq.filter (fun r -> settings.ShowAll || not r.IsChecked || r.CanRun || r.IsCustom)
            |> Seq.sortBy (fun r -> (not r.IsInstalled, r.Title.ToLowerInvariant()))
            |> List.ofSeq
        let same = wanted.Length = rows.Count && (wanted |> List.mapi (fun i r -> obj.ReferenceEquals(rows.[i], r)) |> List.forall id)
        if not same then
            rows.Clear()
            for r in wanted do rows.Add(r)
        for name in [ "HasRows"; "IsEmpty"; "EmptyText" ] do this.RaisePropertyChanged(name)

    member private this.RaiseTotals() =
        for name in [ "InstalledCountText"; "ReadyCountText"; "LatestText"; "CatalogText"; "IsChecking" ] do
            this.RaisePropertyChanged(name)

    member _.InstalledCountText = allRows |> Seq.filter (fun r -> r.IsInstalled) |> Seq.length |> string

    member _.ReadyCountText =
        let n = allRows |> Seq.filter (fun r -> r.CanRun) |> Seq.length
        if checking then sprintf "%d so far" n else string n

    member _.LatestText =
        match latestEnabler () with
        | Some e -> "v" + string (Catalog.versionOf e.Name)
        | None -> "-"

    member _.CatalogText =
        if refreshingCatalog then "Checking..."
        else
            match catalogAt with
            | Some t ->
                let ago = DateTime.UtcNow - t
                if ago.TotalMinutes < 1.0 then "Just now"
                elif ago.TotalHours < 1.0 then sprintf "%d min ago" (int ago.TotalMinutes)
                elif ago.TotalDays < 1.0 then sprintf "%d h ago" (int ago.TotalHours)
                else t.ToLocalTime().ToString("d MMM")
            | None -> "Built-in list"

    member _.CatalogNote = catalogNote
    member _.HasCatalogNote = catalogNote <> ""
    member _.IsChecking = checking

    /// One row per game folder: the library's, then folders added here.
    member private this.BuildRows() =
        let folderKey (path: string) = (try Path.GetFullPath(path) with _ -> path).TrimEnd('\\').ToLowerInvariant()
        let known = allRows |> Seq.map (fun r -> r.Key) |> HashSet
        let folders = allRows |> Seq.map (fun r -> folderKey r.Folder) |> HashSet
        let added = List<EnablerRowViewModel>()
        for card in library do
            let g = card.Game
            if not (known.Contains(g.AppId)) && not (String.IsNullOrWhiteSpace(g.TargetExecutablePath))
               && Directory.Exists(g.InstallDirectory) && folders.Add(folderKey g.InstallDirectory) then
                let dirs = match AnalysisStore.tryGet g with Some a when not (isNull a.StreamlineDirs) -> a.StreamlineDirs | _ -> [||]
                let row = EnablerRowViewModel(g, dirs, false)
                allRows.Add(row)
                added.Add(row)
        added |> List.ofSeq

    /// Looks at each game - installed, the old tool's, Streamline - on a
    /// lowest-priority thread, one game at a time, only while the page is open.
    member private this.Check(targets: EnablerRowViewModel list) =
        if not targets.IsEmpty then
            checking <- true
            this.RaiseTotals()
            let worker =
                Thread(
                    (fun () ->
                        for row in targets do
                            if isActive then
                                try
                                    let g = row.Game
                                    let sl = streamlineDir row.KnownStreamlineDirs g
                                    let manifest = readManifest g
                                    let version, proxyName = match manifest with Some m -> m.Version, m.Proxy | None -> "", ""
                                    let legacy = manifest.IsNone && hasLegacyInstall g sl
                                    let ready = readiness sl
                                    ui (fun () -> row.SetState(version, proxyName, legacy, ready))
                                with ex -> AppLog.warn ("Enabler Dock: checking " + row.Title + " failed: " + ex.Message)
                        ui (fun () ->
                            checking <- false
                            this.ApplyFilter()
                            this.RaiseTotals())),
                    IsBackground = true,
                    Priority = ThreadPriority.Lowest,
                    Name = "Enabler Dock checking")
            worker.Start()

    /// Called each time the edition opens; cheap after the first time.
    member this.Activate() =
        isActive <- true
        let fresh = this.BuildRows()
        // Folders added here earlier: resolving their executable reads the
        // folder, so they join from the background.
        let pendingFolders =
            settings.Folders |> Array.filter (fun f -> not (allRows |> Seq.exists (fun r -> r.IsCustom && r.Folder = f)))
        this.ApplyFilter()
        let unchecked = allRows |> Seq.filter (fun r -> not r.IsChecked) |> List.ofSeq
        if not checking then this.Check(unchecked)
        if pendingFolders.Length > 0 then
            Task.Run(fun () ->
                let games = pendingFolders |> Array.filter Directory.Exists |> Array.map customGame
                ui (fun () ->
                    let added = games |> Array.map (fun g -> EnablerRowViewModel(g, [||], true)) |> List.ofArray
                    allRows.AddRange(added)
                    this.ApplyFilter()
                    if not checking then this.Check(added)))
            |> ignore
        this.RefreshCatalog(false)
        ignore fresh

    member this.Deactivate() =
        isActive <- false
        this.StopWatching()

    member this.RefreshCatalog(force: bool) =
        if not refreshingCatalog then
            refreshingCatalog <- true
            this.RaiseTotals()
            task {
                let! c, at, note = Catalog.refresh (httpClient ()) force
                ui (fun () ->
                    catalog <- c
                    catalogAt <- at
                    catalogNote <- note
                    refreshingCatalog <- false
                    this.RaisePropertyChanged("CatalogNote")
                    this.RaisePropertyChanged("HasCatalogNote")
                    this.RaiseTotals()
                    this.RaiseOptions())
            }
            |> ignore

    member this.UpdateCatalog() = this.RefreshCatalog(true)

    // =====================================================================
    // THE JOB STRIP
    // =====================================================================
    member _.IsBusy = busy
    member _.IsIdle = not busy
    member _.HasJob = busy
    member _.JobTitle = jobTitle
    member _.JobText = jobText
    member _.JobProgress = jobProgress

    member private this.RaiseJob() =
        for name in [ "IsBusy"; "IsIdle"; "HasJob"; "JobTitle"; "JobText"; "JobProgress"; "CanInstall"; "CanUninstall"; "InstallText" ] do
            this.RaisePropertyChanged(name)

    member private this.Report(text: string) (fraction: float) =
        ui (fun () ->
            jobText <- text
            jobProgress <- max 0.0 (min 100.0 (fraction * 100.0))
            this.RaisePropertyChanged("JobText")
            this.RaisePropertyChanged("JobProgress"))

    member private this.SetMessage(text: string, isError: bool) =
        message <- text
        messageIsError <- isError
        for name in [ "Message"; "HasMessage"; "MessageBrush" ] do this.RaisePropertyChanged(name)

    member _.Message = message
    member _.HasMessage = message <> ""
    member _.MessageBrush = SolidColorBrush(Color.Parse(if messageIsError then "#FBBF24" else "#86EFAC")) :> IBrush

    /// Runs one install-side job, then looks at the game again.
    member private this.RunJob(row: EnablerRowViewModel, title: string, work: (string -> float -> unit) -> Task<Outcome>) =
        if not busy then
            busy <- true
            jobTitle <- title
            jobText <- "Starting..."
            jobProgress <- 0.0
            this.RaiseJob()
            this.SetMessage("", false)
            Task.Run(fun () ->
                task {
                    let! outcome =
                        try work (fun t f -> this.Report t f)
                        with ex -> Task.FromResult { Success = false; Message = ex.Message }
                    let g = row.Game
                    let sl = streamlineDir row.KnownStreamlineDirs g
                    let manifest = readManifest g
                    let version, proxyName = match manifest with Some m -> m.Version, m.Proxy | None -> "", ""
                    let legacy = manifest.IsNone && hasLegacyInstall g sl
                    let ready = readiness sl
                    ui (fun () ->
                        busy <- false
                        row.SetState(version, proxyName, legacy, ready)
                        this.RaiseJob()
                        this.RaiseTotals()
                        this.SetMessage(outcome.Message, not outcome.Success)
                        if selected |> Option.exists (fun s -> obj.ReferenceEquals(s, row)) then this.LoadSelection(row, false))
                } :> Task)
            |> ignore

    // =====================================================================
    // THE SELECTED GAME
    // =====================================================================
    member _.HasSelected = selected.IsSome
    member _.SelectedRow = match selected with Some r -> box r | None -> null
    member _.Blocker = blockerText
    member _.HasBlocker = blockerText <> ""
    member _.FixNote = fixNote
    member _.HasFixNote = fixNote <> ""

    member this.Select(row: EnablerRowViewModel) =
        selected |> Option.iter (fun r -> r.IsSelected <- false)
        selected <- Some row
        row.IsSelected <- true
        this.SetMessage("", false)
        for name in [ "HasSelected"; "SelectedRow" ] do this.RaisePropertyChanged(name)
        this.LoadSelection(row, true)

    /// For the test bridge: select the n-th visible row.
    member this.SelectIndex(index: int) =
        if index >= 0 && index < rows.Count then this.Select(rows.[index])

    /// For the test bridge: select by a word of the title.
    member this.SelectTitle(word: string) =
        match rows |> Seq.tryFind (fun r -> r.Title.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0) with
        | Some r -> this.Select(r)
        | None -> ()

    /// Reads what the selected game needs - anti-cheat, which proxy names
    /// are free, its game fix, its settings file - off the UI thread.
    member private this.LoadSelection(row: EnablerRowViewModel, resetChoices: bool) =
        let g = row.Game
        Task.Run(fun () ->
            let block = try blocker g with _ -> None
            let exeDir = exeDirOf g
            let manifest = readManifest g
            let free =
                proxyNames |> List.filter (fun n -> try slotState exeDir manifest n <> Foreign with _ -> true)
            let fix = Fixes.find (userFixes ()) catalog.Fixes g.Title exeDir
            let path = Path.Combine(exeDir, iniName)
            let lines = try (if File.Exists(path) then File.ReadAllLines(path) else [||]) with _ -> [||]
            ui (fun () ->
                if selected |> Option.exists (fun s -> obj.ReferenceEquals(s, row)) then
                    blockerText <- defaultArg block ""
                    fixNote <-
                        match fix with
                        | Some f when f.Note <> "" -> sprintf "%s loads it as %s. %s" f.Game f.Dll f.Note
                        | Some f -> sprintf "Game fix: %s loads it as %s." f.Game f.Dll
                        | None -> ""
                    injectionNames <- free
                    loadingSelection <- true
                    let choice = choiceFor row.Key
                    injectionIndex <- (match free |> List.tryFindIndex ((=) choice.Proxy) with Some i -> i + 1 | None -> 0)
                    versionIndex <-
                        if choice.Version = "" then 0
                        else (match enablerEntries () |> List.tryFindIndex (fun e -> e.Name = choice.Version) with Some i -> i + 1 | None -> 0)
                    streamlineIndex <-
                        match choice.Streamline with
                        | "Game original" -> 1
                        | "Auto" | "" -> 0
                        | name -> (match streamlineEntries () |> List.tryFindIndex (fun e -> e.Name = name) with Some i -> i + 2 | None -> 0)
                    this.RaiseOptions()
                    loadingSelection <- false
                    iniLines <- lines
                    iniPath <- path
                    this.RaiseIni()
                    this.Watch(exeDir)
                    for name in [ "Blocker"; "HasBlocker"; "FixNote"; "HasFixNote"; "CanInstall"; "CanUninstall"; "InstallText"; "SelectedSummary" ] do
                        this.RaisePropertyChanged(name)))
        |> ignore
        ignore resetChoices

    member _.SelectedSummary =
        match selected with
        | Some r when r.IsInstalled -> sprintf "DLSS Enabler %s is in this game as %s." (r.InstalledVersion.Replace("Dlss Enabler ", "")) r.Proxy
        | Some r when r.IsLegacy -> "DLSS Enabler Manager installed DLSS Enabler here. Install replaces it cleanly; Uninstall puts the game back."
        | Some _ -> "DLSS Enabler is not in this game."
        | None -> ""

    member this.CanInstall = not busy && selected.IsSome && blockerText = ""
    member this.CanUninstall = not busy && (selected |> Option.exists (fun r -> r.IsInstalled || r.IsLegacy))
    member _.InstallText = if selected |> Option.exists (fun r -> r.IsInstalled) then "REINSTALL" else "INSTALL"

    // ---- the three choices ----
    member private this.RaiseOptions() =
        for name in [ "InjectionOptions"; "InjectionIndex"; "VersionOptions"; "VersionIndex"; "StreamlineOptions"; "StreamlineIndex" ] do
            this.RaisePropertyChanged(name)

    member _.InjectionOptions =
        ("Auto (from the game)" :: (injectionNames |> List.map id)) |> Array.ofList

    member _.VersionOptions =
        let label (e: Catalog.Entry) =
            let notes =
                [ if Catalog.isBeta e.Name then "beta"
                  if not (isDownloaded e) then (if Catalog.isPinned e || e.Url = "" then "download" else "download, unchecked") ]
            if notes.IsEmpty then e.Name else sprintf "%s  (%s)" e.Name (String.Join(", ", notes))
        let latest = match latestEnabler () with Some e -> sprintf "Latest (%s)" (string (Catalog.versionOf e.Name)) | None -> "Latest"
        latest :: (enablerEntries () |> List.map label) |> Array.ofList

    member _.StreamlineOptions =
        let label (e: Catalog.Entry) = if isDownloaded e then e.Name else e.Name + "  (download)"
        "Auto (newest, never older than the game's)" :: "Game original" :: (streamlineEntries () |> List.map label) |> Array.ofList

    member this.InjectionIndex
        with get () = injectionIndex
        and set (i: int) =
            if i >= 0 && i <> injectionIndex then
                injectionIndex <- i
                this.RaisePropertyChanged("InjectionIndex")
                if not loadingSelection then
                    match selected with
                    | Some row ->
                        let name = if i = 0 then "" else (List.tryItem (i - 1) injectionNames |> Option.defaultValue "")
                        setChoice { choiceFor row.Key with Proxy = name }
                        // The original reinstalled on a change; so does this.
                        if row.IsInstalled then this.Install()
                    | None -> ()

    member this.VersionIndex
        with get () = versionIndex
        and set (i: int) =
            if i >= 0 && i <> versionIndex then
                versionIndex <- i
                this.RaisePropertyChanged("VersionIndex")
                if not loadingSelection then
                    match selected with
                    | Some row ->
                        let name = if i = 0 then "" else (List.tryItem (i - 1) (enablerEntries ()) |> Option.map (fun e -> e.Name) |> Option.defaultValue "")
                        setChoice { choiceFor row.Key with Version = name }
                        if row.IsInstalled then this.Install()
                    | None -> ()

    member this.StreamlineIndex
        with get () = streamlineIndex
        and set (i: int) =
            if i >= 0 && i <> streamlineIndex then
                streamlineIndex <- i
                this.RaisePropertyChanged("StreamlineIndex")
                if not loadingSelection then
                    match selected with
                    | Some row ->
                        let choice = this.StreamlineChoiceAt(i)
                        setChoice { choiceFor row.Key with Streamline = streamlineLabel choice }
                        // Only Streamline changes - DLSS Enabler stays as it is.
                        if row.IsInstalled then
                            let latest = latestStreamline ()
                            let allow = settings.AllowUnchecked
                            this.RunJob(row, "STREAMLINE · " + row.Title, fun report -> swapStreamline row.Game choice latest allow report)
                    | None -> ()

    member private _.StreamlineChoiceAt(i: int) =
        match i with
        | 0 -> AutoStreamline
        | 1 -> GameOriginal
        | n ->
            match List.tryItem (n - 2) (streamlineEntries ()) with
            | Some e -> Exact e
            | None -> AutoStreamline

    member this.Install() =
        match selected with
        | Some row when this.CanInstall ->
            let choice = choiceFor row.Key
            let enabler =
                if choice.Version = "" then latestEnabler ()
                else enablerEntries () |> List.tryFind (fun e -> e.Name = choice.Version) |> Option.orElse (latestEnabler ())
            match enabler with
            | None -> this.SetMessage("No DLSS Enabler version is known. Update the version list first.", true)
            | Some entry ->
                let request =
                    { Game = row.Game
                      Proxy = (if choice.Proxy = "" then None else Some choice.Proxy)
                      Enabler = entry
                      Streamline = this.StreamlineChoiceAt(streamlineIndex)
                      LatestStreamline = latestStreamline ()
                      Fix = Fixes.find (userFixes ()) catalog.Fixes row.Game.Title (exeDirOf row.Game)
                      AllowUnpinned = settings.AllowUnchecked
                      KnownStreamlineDirs = row.KnownStreamlineDirs }
                this.RunJob(row, "INSTALLING · " + row.Title, fun report -> install request report)
        | _ -> ()

    member this.Uninstall() =
        match selected with
        | Some row when this.CanUninstall ->
            let keep = settings.KeepSettings
            this.RunJob(row, "REMOVING · " + row.Title, fun report ->
                report "Putting the game's files back..." 0.3
                Task.FromResult(uninstall row.Game row.KnownStreamlineDirs keep))
        | _ -> ()

    member this.OpenSelectedFolder() =
        match selected with
        | Some row ->
            let dir = exeDirOf row.Game
            if Directory.Exists(dir) then
                try Process.Start(ProcessStartInfo("explorer.exe", "\"" + dir + "\"", UseShellExecute = true)) |> ignore
                with _ -> ()
        | None -> ()

    // =====================================================================
    // FOLDERS AND ARCHIVES ADDED BY HAND
    // =====================================================================
    member this.AddFolder(folder: string) =
        if not (String.IsNullOrWhiteSpace(folder)) && Directory.Exists(folder)
           && not (settings.Folders |> Array.exists (fun f -> String.Equals(f, folder, StringComparison.OrdinalIgnoreCase))) then
            settings <- { settings with Folders = Array.append settings.Folders [| folder |] }
            saveSettings ()
            Task.Run(fun () ->
                let g = customGame folder
                ui (fun () ->
                    if String.IsNullOrWhiteSpace(g.TargetExecutablePath) then
                        this.SetMessage("No game executable found in " + folder + ".", true)
                    let row = EnablerRowViewModel(g, [||], true)
                    allRows.Add(row)
                    this.ApplyFilter()
                    this.Select(row)
                    this.Check([ row ])))
            |> ignore

    /// Drops a folder added here from the list. The game is not touched.
    member this.RemoveSelectedFolder() =
        match selected with
        | Some row when row.IsCustom ->
            settings <- { settings with Folders = settings.Folders |> Array.filter (fun f -> f <> row.Folder) }
            saveSettings ()
            allRows.Remove(row) |> ignore
            selected <- None
            this.StopWatching()
            this.ApplyFilter()
            for name in [ "HasSelected"; "SelectedRow" ] do this.RaisePropertyChanged(name)
        | _ -> ()

    member _.CanRemoveFolder = selected |> Option.exists (fun r -> r.IsCustom)

    member this.ImportArchive(path: string) =
        Task.Run(fun () ->
            let text, isError =
                try
                    let entry, isEnabler = importArchive path
                    sprintf "%s added as a %s version." entry.Name (if isEnabler then "DLSS Enabler" else "Streamline"), false
                with ex -> ex.Message, true
            ui (fun () ->
                this.SetMessage(text, isError)
                this.RaiseOptions()
                this.RaiseTotals()))
        |> ignore

    // =====================================================================
    // DLSS ENABLER'S SETTINGS (the selected game's dlss-enabler.ini)
    // =====================================================================
    member _.HasSettings = iniLines.Length > 0
    member _.NoSettings = iniLines.Length = 0
    member private _.Get (section: string) (key: string) = Ini.tryGet iniLines section key

    member private this.RaiseIni() =
        for name in [ "HasSettings"; "NoSettings"; "ToggleKeyIndex"; "Monitoring"; "GhostBuster"; "HudIndex"; "OverdriveIndex"
                      "ForceFrameGen"; "EarlyInit"; "DisableUi"; "SettingsVersion" ] do
            this.RaisePropertyChanged(name)

    member this.SettingsVersion =
        match this.Get "Meta" "configVersion" with
        | Some v -> "Settings for DLSS Enabler " + v
        | None -> "DLSS Enabler settings"

    /// Writes one value, off the UI thread, with the watcher told to look away.
    member private this.Put (section: string) (key: string) (value: string) =
        if iniLines.Length > 0 && iniPath <> "" then
            iniLines <- Ini.set iniLines section key value
            let lines = iniLines
            let path = iniPath
            ignoreWatcherUntil <- DateTime.UtcNow.AddMilliseconds(800.0)
            Task.Run(fun () ->
                try Ini.save path lines
                with ex -> ui (fun () -> this.SetMessage("Could not save the settings: " + ex.Message, true)))
            |> ignore
            this.RaiseIni()

    member _.ToggleKeyOptions = Options.toggleKeys |> List.map (fun c -> c.Label) |> Array.ofList
    member _.HudOptions = Options.hudModes |> List.map (fun c -> c.Label) |> Array.ofList
    member _.OverdriveOptions = Options.overdriveModes |> List.map (fun c -> c.Label) |> Array.ofList

    member this.ToggleKeyIndex
        with get () = Options.indexOf Options.toggleKeys (this.Get "UI" "ToggleKey") "Tilde"
        and set (i: int) =
            if i >= 0 && i < Options.toggleKeys.Length && i <> this.ToggleKeyIndex then
                this.Put "UI" "ToggleKey" Options.toggleKeys.[i].Value

    member this.HudIndex
        with get () = Options.indexOf Options.hudModes (this.Get "NGX" "HudDetectionMode") "0"
        and set (i: int) =
            if i >= 0 && i < Options.hudModes.Length && i <> this.HudIndex then
                this.Put "NGX" "HudDetectionMode" Options.hudModes.[i].Value

    member this.OverdriveIndex
        with get () = Options.indexOf Options.overdriveModes (this.Get "Performance" "OverdriveMode") "0"
        and set (i: int) =
            if i >= 0 && i < Options.overdriveModes.Length && i <> this.OverdriveIndex then
                this.Put "Performance" "OverdriveMode" Options.overdriveModes.[i].Value

    member private this.Flag (section: string) (key: string) = Options.isTrue (this.Get section key)
    member private this.SetFlag (section: string) (key: string) (value: bool) =
        if value <> this.Flag section key then this.Put section key (if value then "true" else "false")

    member this.Monitoring with get () = this.Flag "UI" "Monitoring" and set v = this.SetFlag "UI" "Monitoring" v
    member this.GhostBuster with get () = this.Flag "GhostBuster" "Enabled" and set v = this.SetFlag "GhostBuster" "Enabled" v
    member this.ForceFrameGen with get () = this.Flag "Performance" "ForceLoadDLSSG" and set v = this.SetFlag "Performance" "ForceLoadDLSSG" v
    member this.EarlyInit with get () = this.Flag "Debug" "EarlyInit" and set v = this.SetFlag "Debug" "EarlyInit" v
    member this.DisableUi with get () = this.Flag "Debug" "DisableUI" and set v = this.SetFlag "Debug" "DisableUI" v

    /// DLSS Enabler rewrites its own file from its in-game menu; follow it.
    member private this.Watch(exeDir: string) =
        this.StopWatching()
        try
            if Directory.Exists(exeDir) then
                let w = new FileSystemWatcher(exeDir, iniName)
                let poke = fun (_: obj) ->
                    if DateTime.UtcNow > ignoreWatcherUntil then
                        ui (fun () ->
                            iniWatchTimer.Stop()
                            iniWatchTimer.Start())
                w.Changed.Add(fun _ -> poke null)
                w.Created.Add(fun _ -> poke null)
                w.Renamed.Add(fun _ -> poke null)
                w.Deleted.Add(fun _ -> poke null)
                w.EnableRaisingEvents <- true
                watcher <- Some w
        with _ -> ()

    member private this.StopWatching() =
        watcher |> Option.iter (fun w -> w.EnableRaisingEvents <- false; w.Dispose())
        watcher <- None

    member this.ReloadIni() =
        let path = iniPath
        if path <> "" then
            Task.Run(fun () ->
                let lines = try (if File.Exists(path) then File.ReadAllLines(path) else [||]) with _ -> [||]
                ui (fun () ->
                    if iniPath = path then
                        iniLines <- lines
                        this.RaiseIni()))
            |> ignore

    /// The installed version's own defaults, the user's file kept as a backup.
    member this.ResetSettings() =
        match selected with
        | Some row when row.IsInstalled && iniPath <> "" ->
            let path = iniPath
            let version = row.InstalledVersion
            Task.Run(fun () ->
                let text, isError =
                    match templateFor version with
                    | Some template ->
                        try
                            let backup = Path.Combine(DlssEnabler.root (), "Backups", "settings-" + row.Key + ".ini")
                            Directory.CreateDirectory(Path.GetDirectoryName(backup)) |> ignore
                            if File.Exists(path) then File.Copy(path, backup, true)
                            ignoreWatcherUntil <- DateTime.UtcNow.AddMilliseconds(800.0)
                            Ini.save path (File.ReadAllLines(template))
                            "Settings are back to DLSS Enabler's defaults. The old file is kept in SUITE's backups.", false
                        with ex -> "Could not reset the settings: " + ex.Message, true
                    | None -> "This version's defaults are not on disk - reinstall to get them back.", true
                ui (fun () ->
                    this.SetMessage(text, isError)
                    this.ReloadIni()))
            |> ignore
        | _ -> ()

    /// These eight options onto every other game SUITE installed it in. Only
    /// these keys change - the original copied the whole file over each one.
    member this.CopySettingsToAll() =
        match selected with
        | Some row when iniLines.Length > 0 ->
            let keys =
                [ "UI", "ToggleKey"; "UI", "Monitoring"; "GhostBuster", "Enabled"; "NGX", "HudDetectionMode"
                  "Performance", "OverdriveMode"; "Performance", "ForceLoadDLSSG"; "Debug", "EarlyInit"; "Debug", "DisableUI" ]
                |> List.choose (fun (s, k) -> Ini.tryGet iniLines s k |> Option.map (fun v -> s, k, v))
            let others = allRows |> Seq.filter (fun r -> r.IsInstalled && not (obj.ReferenceEquals(r, row))) |> List.ofSeq
            Task.Run(fun () ->
                let mutable done' = 0
                for other in others do
                    try
                        let path = Path.Combine(exeDirOf other.Game, iniName)
                        if File.Exists(path) then
                            let updated = keys |> List.fold (fun acc (s, k, v) -> Ini.set acc s k v) (File.ReadAllLines(path))
                            Ini.save path updated
                            done' <- done' + 1
                    with _ -> ()
                let n = done'
                ui (fun () -> this.SetMessage(sprintf "Copied these settings to %d other game(s)." n, false)))
            |> ignore
        | _ -> ()

    // =====================================================================
    // GAME FIXES (which name a game needs)
    // =====================================================================
    member _.IsFixesOpen = isFixesOpen
    member _.IsDetailOpen = not isFixesOpen

    member this.ToggleFixes() =
        isFixesOpen <- not isFixesOpen
        for name in [ "IsFixesOpen"; "IsDetailOpen"; "FixRows"; "FixesButtonText" ] do this.RaisePropertyChanged(name)

    member _.FixesButtonText = if isFixesOpen then "BACK TO GAME" else "GAME FIXES"

    member _.FixRows =
        let mine = settings.Fixes |> Array.mapi (fun i f -> EnablerFixRow(i, f.Game, f.Dll, f.Note, true)) |> List.ofArray
        let theirs = catalog.Fixes |> List.map (fun f -> EnablerFixRow(-1, f.Game, f.Dll, f.Note, false))
        mine @ theirs |> Array.ofList

    member _.FixDllOptions = proxyNames |> Array.ofList

    member this.NewFixGame
        with get () = newFixGame
        and set v =
            newFixGame <- v
            this.RaisePropertyChanged("NewFixGame")

    member this.NewFixDllIndex
        with get () = newFixDllIndex
        and set v =
            newFixDllIndex <- v
            this.RaisePropertyChanged("NewFixDllIndex")

    member this.NewFixNote
        with get () = newFixNote
        and set v =
            newFixNote <- v
            this.RaisePropertyChanged("NewFixNote")

    member this.AddFix() =
        let game = if isNull newFixGame then "" else newFixGame.Trim()
        if game.Length >= 3 then
            let dll = List.item (max 0 (min 3 newFixDllIndex)) proxyNames
            let kept = settings.Fixes |> Array.filter (fun f -> not (String.Equals(f.Game, game, StringComparison.OrdinalIgnoreCase)))
            settings <- { settings with Fixes = Array.append kept [| { Game = game; Dll = dll; Note = (if isNull newFixNote then "" else newFixNote.Trim()) } |] }
            saveSettings ()
            this.NewFixGame <- ""
            this.NewFixNote <- ""
            this.RaisePropertyChanged("FixRows")

    member this.RemoveFix(index: int) =
        if index >= 0 && index < settings.Fixes.Length then
            settings <- { settings with Fixes = settings.Fixes |> Array.removeAt index }
            saveSettings ()
            this.RaisePropertyChanged("FixRows")

/// A line in the game fixes list.
and EnablerFixRow(index: int, game: string, dll: string, note: string, isMine: bool) =
    member _.Index = index
    member _.Game = game
    member _.Dll = dll
    member _.Note = note
    member _.HasNote = note <> ""
    member _.IsMine = isMine
    member _.Source = if isMine then "yours" else "catalog"
    override _.ToString() = sprintf "%s -> %s" game dll
