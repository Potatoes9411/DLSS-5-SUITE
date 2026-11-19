namespace DLSS_5_MANAGER.ViewModels

open System
open System.Collections.Generic
open System.Collections.ObjectModel
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Avalonia
open Avalonia.Media
open Avalonia.Media.Imaging
open Avalonia.Threading
open DLSS_5_MANAGER.Services
open DLSS_5_MANAGER.Services.StorageCompressor

module internal SpacePressNative =
    [<System.Runtime.InteropServices.DllImport("user32.dll")>]
    extern nativeint GetForegroundWindow()

    [<System.Runtime.InteropServices.DllImport("user32.dll")>]
    extern uint32 GetWindowThreadProcessId(nativeint window, uint32& processId)

    /// The process that owns the window in front, or -1.
    let foregroundProcessId () =
        try
            let mutable pid = 0u
            GetWindowThreadProcessId(GetForegroundWindow(), &pid) |> ignore
            int pid
        with _ -> -1

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
      SkipPackages: bool
      /// Off switch for the hourly check. Stored as "off" so a settings file
      /// from before the check existed reads as on.
      MonitorOff: bool
      /// "AUTO", "GENTLE", "BALANCED" or "FULL"; missing reads as Auto.
      Speed: string }

/// Something Space Press can squish: a library game, an installed app, or a
/// folder the user added.
type StorageEntry =
    { Key: string
      Title: string
      Folder: string
      /// Shown under the title: the launcher, "APP" or "FOLDER".
      Source: string
      /// The exe's name without ".exe", to spot it running ("" if unknown).
      ProcessName: string
      CoverPath: string
      IsApp: bool }

/// A folder added by hand. Public on purpose: System.Text.Json writes a
/// private F# record as "{}".
type SpacePressFolder = { Path: string; IsApp: bool }

/// One row in the Space Press list.
type StorageRowViewModel(entry: StorageEntry) =
    inherit ViewModelBase()

    let mutable info: FolderInfo option = None
    let mutable isMeasuring = false
    let mutable isWorking = false
    let mutable workVerb = ""
    let mutable progress = 0.0
    let mutable progressText = ""
    let mutable isSelected = false
    let mutable message = ""
    let mutable thumbnail: Bitmap option = None
    let mutable thumbnailPending = false

    let brush (hex: string) = SolidColorBrush(Color.Parse(hex)) :> IBrush

    member _.Entry = entry
    member _.Key = entry.Key
    member _.Title = entry.Title
    member _.Folder = entry.Folder
    member _.Launcher = entry.Source
    member _.IsApp = entry.IsApp
    member _.ProcessName = entry.ProcessName

    /// The first letter, shown when there is no cover (apps and folders).
    member _.Initial = if String.IsNullOrEmpty(entry.Title) then "?" else entry.Title.Substring(0, 1).ToUpperInvariant()

    member _.OpenFolder() =
        try Process.Start(ProcessStartInfo(entry.Folder, UseShellExecute = true)) |> ignore with _ -> ()

    /// What squishing this has saved, as a share of its full size (0-100) -
    /// the length of its bar in the statistics.
    member _.OnDiskPercent =
        match info with
        | Some i when i.OriginalBytes > 0L -> float i.OnDiskBytes / float i.OriginalBytes * 100.0
        | _ -> 100.0

    /// A small copy of the cover, decoded once at the size it is shown and
    /// off the UI thread - making 63 of them on the spot froze the page for a
    /// second when it opened. Null until it is ready, then the row updates.
    member this.Cover: Bitmap =
        match thumbnail with
        | Some t -> t
        | None ->
            if not thumbnailPending then
                thumbnailPending <- true
                let path = entry.CoverPath
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

    member this.Info
        with get () = info
        and set value =
            info <- value
            for name in [ "Info"; "SizeText"; "OriginalText"; "SavedText"; "HasSaved"; "EstimateText"; "StatusText"; "OnDiskPercent"
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
        and set value =
            workVerb <- value
            this.RaisePropertyChanged("WorkVerb")
            this.RaisePropertyChanged("StatusText")

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
        if isWorking then
            if workVerb = "Paused" then sprintf "Paused %.0f%%" progress
            else sprintf "%s %.0f%%" workVerb progress
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

/// One drive in the statistics: how full it is now, and how full it would be
/// without what Space Press saved on it.
type DriveStat =
    { Name: string
      /// 0-100: used now.
      UsedPercent: float
      /// 0-100 (capped): used if nothing were squished.
      WithoutPercent: float
      Detail: string
      SavedText: string
      /// Without squishing, this drive would not even hold what is on it.
      WouldOverflow: bool }

/// The Space Press edition: the library, sized up, with squish and unsquish.
///
/// Everything here is meant to go unnoticed while you use the PC: sizes come
/// from file metadata on a background-mode thread, compact.exe runs at idle
/// priority, a squish pauses itself while a game from the library is
/// running, and the hourly check walks one game at a time with gaps between.
/// One line on the CLEAN UP tab: a kind of SUITE clutter, its size and
/// exactly which files it is, ticked or not.
type CleanRowViewModel(item: SuiteCleaner.CleanItem, changed: unit -> unit) =
    inherit ViewModelBase()

    let mutable selected = item.DefaultOn

    member _.Item = item
    member _.Label = item.Label
    member _.Detail = item.Detail
    member _.SizeText = SuiteCleaner.formatBytes item.Bytes
    member _.CountText =
        match item.Paths.Length with
        | 1 -> "1 item"
        | n -> sprintf "%d items" n

    /// Every path that would go, for the tooltip - shown before anything is removed.
    member _.PathsTip =
        let shown = item.Paths |> List.truncate 15
        let more = if item.Paths.Length > 15 then sprintf "%s... and %d more" Environment.NewLine (item.Paths.Length - 15) else ""
        "Will be removed:" + Environment.NewLine + String.Join(Environment.NewLine, shown) + more

    member this.IsSelected
        with get () = selected
        and set value =
            if this.SetProperty(&selected, value) then changed ()
type SpacePressViewModel(library: ObservableCollection<GameCardViewModel>) as this =
    inherit ViewModelBase()

    let rows = ObservableCollection<StorageRowViewModel>()
    /// What the list shows: the current tab, searched and sorted.
    let visibleRows = ObservableCollection<StorageRowViewModel>()
    let driveStats = ObservableCollection<DriveStat>()
    let topSaved = ObservableCollection<StorageRowViewModel>()
    /// "games", "apps" or "stats".
    let mutable view = "games"
    let mutable search = ""
    let mutable sortIndex = 0
    let mutable appsScanned = false
    /// The CLEAN UP tab: SUITE's own clutter, measured when the tab opens.
    let cleanRows = ObservableCollection<CleanRowViewModel>()
    let mutable cleanBusy = false
    let mutable cleanStatus = ""
    let mutable cleanNote = ""
    let mutable notice = ""
    let mutable selected: StorageRowViewModel option = None
    let mutable job: Job option = None
    let mutable working: StorageRowViewModel option = None
    let mutable jobVerb = ""
    let mutable batchQueue: StorageRowViewModel list = []
    let mutable isBatch = false
    let mutable batchTotal = 0
    let mutable batchDone = 0
    let mutable batchIsUpkeep = false
    let mutable pausedForGame = false
    let mutable measureStatus = ""
    let mutable started = false
    let mutable isActive = false
    let mutable isMonitoring = false
    let mutable lastMonitor = "Not yet"

    let settingsPath () = Path.Combine(AppLog.dataFolder (), "SpacePress", "settings.json")
    let cachePath () = Path.Combine(AppLog.dataFolder (), "SpacePress", "sizes.json")
    let foldersPath () = Path.Combine(AppLog.dataFolder (), "SpacePress", "folders.json")

    let loadFolders () : SpacePressFolder list =
        try
            if File.Exists(foldersPath ()) then
                match JsonSerializer.Deserialize<SpacePressFolder[]>(File.ReadAllText(foldersPath ())) with
                | null -> []
                | items -> items |> Array.filter (fun f -> not (isNull f.Path)) |> List.ofArray
            else []
        with _ -> []

    let mutable addedFolders = loadFolders ()

    let saveFolders () =
        try
            Directory.CreateDirectory(Path.GetDirectoryName(foldersPath ())) |> ignore
            File.WriteAllText(foldersPath (), JsonSerializer.Serialize(Array.ofList addedFolders))
        with ex -> AppLog.error "Saving the Space Press folders failed" ex

    let folderKey (path: string) = (try Path.GetFullPath(path) with _ -> path).TrimEnd('\\').ToLowerInvariant()

    let keyOf (prefix: string) (path: string) =
        prefix + Convert.ToHexString(Security.Cryptography.SHA256.HashData(Text.Encoding.UTF8.GetBytes(folderKey path))).Substring(0, 16).ToLowerInvariant()

    let folderEntry (f: SpacePressFolder) =
        { Key = keyOf "folder_" f.Path
          Title = (let n = Path.GetFileName(f.Path.TrimEnd('\\')) in if String.IsNullOrEmpty(n) then f.Path else n)
          Folder = f.Path
          Source = "FOLDER"
          ProcessName = ""
          CoverPath = ""
          IsApp = f.IsApp }

    /// Words that mark an uninstall entry as a runtime, driver, launcher or
    /// tool rather than an app worth squishing - from Squish It's own list.
    let notAnApp =
        [ "steamworks"; "redistributable"; "redist"; "directx"; "vcredist"; "visual c++"; "easyanticheat"; "battleye"
          "epic online services"; "unreal engine prerequisites"; "dotnet"; ".net"; "framework"; "runtime"; "sdk"
          "steamvr"; "prerequisites"; "prereq"; "installer"; "dependencies"; "ubisoft connect"; "ea app"; "origin"
          "nvidia"; "intel"; "amd "; "realtek"; "synaptics"; "driver"; "chipset"; "firmware"; "bios"; "defender"
          "update assistant"; "uninstall"; "setup" ]

    /// Installed Windows programs, from the same uninstall entries that
    /// Settings > Apps lists. Read only; nothing is opened or changed.
    let scanApps (gameFolders: HashSet<string>) : StorageEntry list =
        let found = Dictionary<string, StorageEntry>()
        let read (root: Microsoft.Win32.RegistryKey) (path: string) =
            try
                use uninstall = root.OpenSubKey(path)
                if not (isNull uninstall) then
                    for name in uninstall.GetSubKeyNames() do
                        try
                            use k = uninstall.OpenSubKey(name)
                            let text (v: string) = match k.GetValue(v) with :? string as s -> s.Trim() | _ -> ""
                            let display = text "DisplayName"
                            let system = match k.GetValue("SystemComponent") with :? int as i -> i = 1 | _ -> false
                            let lower = display.ToLowerInvariant()
                            if display <> "" && not system && not (notAnApp |> List.exists lower.Contains) then
                                let location =
                                    let l = text "InstallLocation"
                                    if l <> "" then l.Trim('"')
                                    else
                                        let icon = (text "DisplayIcon").Split(',').[0].Trim('"')
                                        if icon <> "" then (try Path.GetDirectoryName(icon) with _ -> "") else ""
                                if location <> "" && Directory.Exists(location) && not (isProtected location) then
                                    let key = folderKey location
                                    let insideGame = gameFolders |> Seq.exists (fun g -> key = g || key.StartsWith(g + "\\") || g.StartsWith(key + "\\"))
                                    let hasExe =
                                        try Directory.EnumerateFiles(location, "*.exe", SearchOption.TopDirectoryOnly) |> Seq.isEmpty |> not
                                        with _ -> false
                                    if not insideGame && hasExe && not (found.ContainsKey(key)) then
                                        let exe =
                                            let icon = (text "DisplayIcon").Split(',').[0].Trim('"')
                                            if icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) then Path.GetFileNameWithoutExtension(icon) else ""
                                        found.[key] <-
                                            { Key = keyOf "app_" location
                                              Title = display
                                              Folder = location
                                              Source = "APP"
                                              ProcessName = exe
                                              CoverPath = ""
                                              IsApp = true }
                        with _ -> ()
            with _ -> ()
        let hklm = Microsoft.Win32.Registry.LocalMachine
        let hkcu = Microsoft.Win32.Registry.CurrentUser
        read hklm @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"
        read hklm @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        read hkcu @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"
        found.Values |> Seq.sortBy (fun e -> e.Title) |> List.ofSeq

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
    let mutable monitorOn = initial |> Option.map (fun s -> not s.MonitorOff) |> Option.defaultValue true
    let mutable speed =
        initial
        |> Option.bind (fun s -> speeds |> List.tryFind (fun x -> speedName x = s.Speed))
        |> Option.defaultValue Auto

    let saveSettings () =
        try
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath ())) |> ignore
            let s =
                { Algorithm = algorithmName algorithm
                  SkipMedia = skipMedia
                  SkipPackages = skipPackages
                  MonitorOff = not monitorOn
                  Speed = speedName speed }
            File.WriteAllText(settingsPath (), JsonSerializer.Serialize(s))
        with ex -> AppLog.error "Saving the Space Press settings failed" ex

    let cache = Dictionary<string, SpacePressCached>()
    let cacheLock = obj ()

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
            let text = lock cacheLock (fun () -> JsonSerializer.Serialize(cache))
            File.WriteAllText(cachePath (), text)
        with ex -> AppLog.error "Saving the Space Press sizes failed" ex

    let ui (f: unit -> unit) = Dispatcher.UIThread.Post(fun () -> f ())

    /// What each library program had used, and when - to tell a game being
    /// played from one just sitting there.
    let cpuSeen = Dictionary<int, TimeSpan * DateTime>()
    let cpuLock = obj ()

    /// Is a game from the library being played? Running is not enough: some
    /// library entries are tools left open in the tray all day (Borderless
    /// Gaming, Lossless Scaling), and waiting on those would mean never
    /// squishing at all. Played means it is the window in front, or it has
    /// been using more than a twentieth of a core since we last looked.
    let gamePlaying (names: HashSet<string>) =
        if names.Count = 0 then false
        else
            let front = SpacePressNative.foregroundProcessId ()
            let now = DateTime.UtcNow
            lock cpuLock (fun () ->
                let mutable playing = false
                for p in Process.GetProcesses() do
                    try
                        if names.Contains(p.ProcessName) then
                            let used = p.TotalProcessorTime
                            let busy =
                                match cpuSeen.TryGetValue(p.Id) with
                                | true, (before, at) ->
                                    let seconds = (now - at).TotalSeconds
                                    seconds > 0.5 && (used - before).TotalSeconds / seconds > 0.05
                                | _ -> false
                            cpuSeen.[p.Id] <- (used, now)
                            if p.Id = front || busy then playing <- true
                    with _ -> ()
                    p.Dispose()
                playing)

    let libraryProcessNames () =
        let names = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        for card in library do
            try
                let n = Path.GetFileNameWithoutExtension(card.Game.TargetExecutablePath)
                if not (String.IsNullOrWhiteSpace(n)) then names.Add(n) |> ignore
            with _ -> ()
        names

    // While a squish runs, look every 15 s for a game being played and step
    // aside until it closes.
    let gameWatch = DispatcherTimer(Interval = TimeSpan.FromSeconds(15.0))

    // The upkeep check: first ten minutes after start, then every hour.
    let monitorTimer = DispatcherTimer(Interval = TimeSpan.FromMinutes(10.0))

    do
        gameWatch.Tick.Add(fun _ ->
            this.WatchForGames()
            // Auto's pace follows whether you are at the PC; keep the strip honest.
            if job.IsSome then this.RaisePropertyChanged("JobHeader"))
        gameWatch.Start()
        monitorTimer.Tick.Add(fun _ ->
            monitorTimer.Interval <- TimeSpan.FromHours(1.0)
            this.RunUpkeep())
        monitorTimer.Start()

    member _.Rows = rows

    member this.Selected
        with get () = selected
        and set (value: StorageRowViewModel option) =
            selected |> Option.iter (fun r -> r.IsSelected <- false)
            selected <- value
            selected |> Option.iter (fun r -> r.IsSelected <- true)
            for name in [ "Selected"; "HasSelected"; "SelectedRow"; "CanSquish"; "CanUnsquish"; "SelectedReason" ] do
                this.RaisePropertyChanged(name)

    member _.HasSelected = selected.IsSome
    member _.SelectedRow = match selected with Some r -> box r | None -> null

    member _.SelectedReason =
        match selected with
        | Some r -> r.Message
        | None -> ""

    // ---- the job in progress (the strip at the top of the page) ----
    member _.IsBusy = job.IsSome || working.IsSome
    /// The strip shows from the first click to the last game of a batch,
    /// including the moments between games.
    member _.HasJob = job.IsSome || working.IsSome || isBatch
    member _.WorkingRow = match working with Some r -> box r | None -> null
    member _.IsPaused = match job with Some j -> j.IsPaused | None -> false
    member this.PauseText = if this.IsPaused then "RESUME" else "PAUSE"

    member this.JobHeader =
        let verb =
            if pausedForGame then "PAUSED WHILE YOU PLAY"
            elif this.IsPaused then "PAUSED"
            else jobVerb.ToUpperInvariant()
        let pace = if job.IsSome && not this.IsPaused then "  ·  " + (paceOf speed).Label else ""
        let place = if isBatch && batchTotal > 1 then sprintf "  ·  GAME %d OF %d" (batchDone + 1) batchTotal else ""
        let why = if batchIsUpkeep then "  ·  KEEPING UPDATED GAMES SQUISHED" else ""
        verb + pace + place + why

    member _.SpeedOptions = speeds |> List.map speedBlurb |> Array.ofList

    /// How hard to push the PC. Takes effect at once, even mid-squish.
    member this.SpeedIndex
        with get () = speeds |> List.findIndex ((=) speed)
        and set (i: int) =
            if i >= 0 && i < speeds.Length && speeds.[i] <> speed then
                speed <- speeds.[i]
                job |> Option.iter (fun j -> j.Speed <- speed)
                saveSettings ()
                this.RaisePropertyChanged("SpeedIndex")
                this.RaisePropertyChanged("JobHeader")

    member this.CanSquish = not this.IsBusy && (match selected with Some r -> r.Info.IsSome && not r.IsSquished | None -> false)
    member this.CanUnsquish = not this.IsBusy && (match selected with Some r -> (match r.Info with Some i -> i.Status <> Plain | None -> false) | None -> false)

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

    /// The hourly check: keeps sizes current and re-squishes what a game
    /// update put back plain.
    member this.MonitorOn
        with get () = monitorOn
        and set value =
            if monitorOn <> value then
                monitorOn <- value
                saveSettings ()
                this.RaisePropertyChanged("MonitorOn")

    member _.MonitorText = sprintf "Checks every hour · last: %s" lastMonitor

    member _.MeasureStatus = measureStatus

    /// A one-line message under the options (apps search, a refused folder).
    member this.Notice
        with get () = notice
        and set (value: string) =
            notice <- value
            this.RaisePropertyChanged("Notice")
            this.RaisePropertyChanged("HasNotice")

    member _.HasNotice = not (String.IsNullOrWhiteSpace(notice))
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
        if isBatch then "STOP SQUISH ALL"
        else
            let wantApps = (view = "apps")
            let n = rows |> Seq.filter (fun r -> r.IsPlain && r.IsApp = wantApps) |> Seq.length
            sprintf "SQUISH ALL (%d)" n

    member private this.RaiseJob() =
        for name in [ "IsBusy"; "HasJob"; "WorkingRow"; "IsPaused"; "PauseText"; "JobHeader"; "BatchText"
                      "CanSquish"; "CanUnsquish"; "SelectedReason" ] do
            this.RaisePropertyChanged(name)

    member private this.RaiseTotals() =
        for name in [ "ReclaimedText"; "FootprintText"; "SquishedCountText"; "PotentialText"; "BatchText" ] do
            this.RaisePropertyChanged(name)
        if view = "stats" then this.RefreshStats()
        this.RaiseJob()

    member private this.SetMeasureStatus(text: string) =
        measureStatus <- text
        this.RaisePropertyChanged("MeasureStatus")
        this.RaisePropertyChanged("IsMeasuring")

    member private this.Remember(row: StorageRowViewModel, info: FolderInfo) =
        row.Info <- Some info
        lock cacheLock (fun () -> cache.[row.Key] <- toCached info)

    /// Adds rows for entries not listed yet, sizes from the cache where known.
    ///
    /// One row per folder. Several library entries can point at the same
    /// place, and squishing it once per entry walked the same files over and
    /// over.
    member private this.AddEntries(entries: StorageEntry seq) =
        let known = rows |> Seq.map (fun r -> r.Key) |> HashSet
        let folders = rows |> Seq.map (fun r -> folderKey r.Folder) |> HashSet
        let added = ResizeArray<StorageRowViewModel>()
        for e in entries do
            if not (known.Contains(e.Key)) && Directory.Exists(e.Folder) && folders.Add(folderKey e.Folder) then
                let row = StorageRowViewModel(e)
                match cache.TryGetValue(e.Key) with
                | true, c -> row.Info <- Some(ofCached c)
                | _ -> ()
                rows.Add(row)
                added.Add(row)
        added

    /// The library's games and the folders added by hand.
    member private this.BuildRows() =
        if not started then
            started <- true
            loadCache ()

        let games =
            [ for card in library ->
                  { Key = card.Game.AppId
                    Title = card.Title
                    Folder = card.InstallDirectory
                    Source = card.LauncherType
                    ProcessName = (try Path.GetFileNameWithoutExtension(card.Game.TargetExecutablePath) with _ -> "")
                    CoverPath = card.Game.LocalBannerPath
                    IsApp = false } ]
        this.AddEntries(games @ (addedFolders |> List.map folderEntry)) |> ignore

    /// Builds the list and measures what is not known yet. Called each time
    /// the edition opens; cheap after the first time.
    member this.Activate() =
        isActive <- true
        this.BuildRows()
        this.Refilter()
        this.RaiseTotals()
        this.MeasureUnknown()

    /// Measures whatever on the current tab has no size yet.
    member private this.MeasureUnknown() =
        let wantApps = (view = "apps")
        let unknown = rows |> Seq.filter (fun r -> r.Info.IsNone && r.IsApp = wantApps) |> List.ofSeq
        if isActive && not unknown.IsEmpty && not this.IsMeasuring then
            this.Measure(unknown)

    // ---- tabs, search and sort ----
    member _.VisibleRows = visibleRows
    member _.IsGamesView = view = "games"
    member _.IsAppsView = view = "apps"
    member _.IsStatsView = view = "stats"
    member _.IsListView = view = "games" || view = "apps"
    member _.IsCleanView = view = "clean"

    // ---- CLEAN UP: SUITE's own caches, old downloads and logs ----
    member _.CleanRows = cleanRows
    member _.HasCleanRows = cleanRows.Count > 0
    member _.IsCleanBusy = cleanBusy
    member _.CleanStatus = cleanStatus

    member _.CleanSelectedBytes =
        cleanRows |> Seq.filter (fun r -> r.IsSelected) |> Seq.sumBy (fun r -> r.Item.Bytes)

    member this.CleanButtonText =
        let b = this.CleanSelectedBytes
        if b = 0L then "NOTHING SELECTED" else "CLEAN UP " + SuiteCleaner.formatBytes b

    member this.CanClean = not cleanBusy && this.CleanSelectedBytes > 0L

    member private this.RaiseClean() =
        for p in [ "HasCleanRows"; "IsCleanBusy"; "CleanStatus"; "CleanSelectedBytes"; "CleanButtonText"; "CanClean" ] do
            this.RaisePropertyChanged(p)

    /// Measures what could go. Nothing is removed here.
    member this.ScanCleanup() =
        if not cleanBusy then
            cleanBusy <- true
            cleanStatus <- "Measuring SUITE's own data..."
            this.RaiseClean()

            Task.Run(fun () -> try SuiteCleaner.find () with _ -> [])
                .ContinueWith(fun (t: Task<SuiteCleaner.CleanItem list>) ->
                    Dispatcher.UIThread.Post(fun () ->
                        cleanRows.Clear()
                        for item in t.Result do
                            cleanRows.Add(CleanRowViewModel(item, (fun () -> this.RaiseClean())))
                        cleanBusy <- false
                        cleanStatus <-
                            cleanNote
                            + (if cleanRows.Count = 0 then "Nothing to clean - SUITE's data is tidy."
                               else "Hover a line to see exactly what it removes. Game files are never touched.")
                        this.RaiseClean()))
            |> ignore

    /// Removes the ticked lines, then measures again.
    member this.CleanSelected() =
        let picked = cleanRows |> Seq.filter (fun r -> r.IsSelected) |> Seq.map (fun r -> r.Item) |> List.ofSeq
        if not cleanBusy && not picked.IsEmpty then
            cleanBusy <- true
            cleanStatus <- "Cleaning up..."
            this.RaiseClean()

            Task.Run(fun () -> try SuiteCleaner.clean picked with _ -> 0L)
                .ContinueWith(fun (t: Task<int64>) ->
                    Dispatcher.UIThread.Post(fun () ->
                        cleanBusy <- false
                        cleanNote <- "Freed " + SuiteCleaner.formatBytes t.Result + ". "
                        this.ScanCleanup()))
            |> ignore

    /// Switches between the games, the apps and the statistics.
    member this.ShowView(name: string) =
        if name <> view then
            view <- name
            for p in [ "IsGamesView"; "IsAppsView"; "IsStatsView"; "IsListView"; "IsCleanView"; "BatchText" ] do
                this.RaisePropertyChanged(p)
            if name = "apps" && not appsScanned then this.ScanApps()
            this.Refilter()
            if name = "stats" then this.RefreshStats()
            if name = "clean" then
                cleanNote <- ""
                this.ScanCleanup()
            elif name <> "stats" then this.MeasureUnknown()

    member this.SearchText
        with get () = search
        and set (value: string) =
            let v = if isNull value then "" else value
            if v <> search then
                search <- v
                this.RaisePropertyChanged("SearchText")
                this.Refilter()

    member _.SortOptions =
        [| "Size: largest first"; "Size: smallest first"; "Name: A-Z"; "Most space saved"; "Could save the most" |]

    member this.SortIndex
        with get () = sortIndex
        and set (i: int) =
            if i >= 0 && i <> sortIndex then
                sortIndex <- i
                this.RaisePropertyChanged("SortIndex")
                this.Refilter()

    /// Rebuilds what the list shows. Not on every measurement - rows jumping
    /// around while you look at them is worse than a list that is one game
    /// out of order until the next sort.
    member this.Refilter() =
        let wantApps = (view = "apps")
        let q = search.Trim()
        let size (r: StorageRowViewModel) = match r.Info with Some i -> i.OnDiskBytes | None -> -1L
        let estimate (r: StorageRowViewModel) = match r.Info with Some i when i.Status = Plain -> i.EstimatedSavingBytes | _ -> 0L
        let shown =
            rows
            |> Seq.filter (fun r -> r.IsApp = wantApps && (q = "" || r.Title.Contains(q, StringComparison.OrdinalIgnoreCase)))
            |> fun s ->
                match sortIndex with
                | 1 -> s |> Seq.sortBy (fun r -> let b = size r in if b < 0L then Int64.MaxValue else b)
                | 2 -> s |> Seq.sortBy (fun r -> r.Title.ToLowerInvariant())
                | 3 -> s |> Seq.sortByDescending (fun r -> r.SavedBytes)
                | 4 -> s |> Seq.sortByDescending estimate
                | _ -> s |> Seq.sortByDescending size
            |> List.ofSeq
        visibleRows.Clear()
        for r in shown do visibleRows.Add(r)
        this.RaisePropertyChanged("HasNoRows")

    member _.HasNoRows = visibleRows.Count = 0 && (view = "games" || view = "apps")

    /// Reads the installed programs once, off the UI thread.
    member private this.ScanApps() =
        appsScanned <- true
        this.Notice <- "Looking for installed apps..."
        let gameFolders = rows |> Seq.filter (fun r -> not r.IsApp) |> Seq.map (fun r -> folderKey r.Folder) |> HashSet
        Task.Run(fun () ->
            let apps = try scanApps gameFolders with _ -> []
            ui (fun () ->
                this.Notice <- (if apps.IsEmpty then "No installed apps worth squishing were found." else "")
                this.AddEntries(apps) |> ignore
                this.Refilter()
                this.MeasureUnknown()))
        |> ignore

    /// A folder picked by hand, added to the tab it was added from.
    member this.AddFolder(path: string) =
        if not (String.IsNullOrWhiteSpace(path)) && Directory.Exists(path) then
            if isProtected path then
                this.Notice <- "That is a Windows or system folder - Space Press leaves those alone."
            else
                let f = { Path = path; IsApp = (view = "apps") }
                if not (addedFolders |> List.exists (fun x -> folderKey x.Path = folderKey path)) then
                    addedFolders <- addedFolders @ [ f ]
                    saveFolders ()
                let added = this.AddEntries([ folderEntry f ])
                this.Refilter()
                if added.Count > 0 then this.Measure(List.ofSeq added)

    // ---- statistics ----
    member _.DriveStats = driveStats
    member _.TopSaved = topSaved
    member _.HasTopSaved = topSaved.Count > 0

    /// How much of everything measured squishing has taken off, 0-100.
    member _.SavedSharePercent =
        let saved = rows |> Seq.sumBy (fun r -> r.SavedBytes)
        let original = rows |> Seq.sumBy (fun r -> match r.Info with Some i -> i.OriginalBytes | None -> 0L)
        if original <= 0L then 0.0 else float saved / float original * 100.0

    member this.SavedShareText = sprintf "%.1f%% of everything measured" this.SavedSharePercent

    /// Per drive: full now, and full without what was saved on it.
    member this.RefreshStats() =
        driveStats.Clear()
        let byDrive =
            rows
            |> Seq.filter (fun r -> r.Info.IsSome)
            |> Seq.groupBy (fun r -> (try Path.GetPathRoot(r.Folder) with _ -> "").ToUpperInvariant())
        for (root, items) in byDrive do
            try
                let d = DriveInfo(root)
                if d.IsReady && d.TotalSize > 0L then
                    let total = d.TotalSize
                    let used = total - d.TotalFreeSpace
                    let saved = items |> Seq.sumBy (fun r -> r.SavedBytes)
                    let without = used + saved
                    driveStats.Add(
                        { Name = sprintf "%s  %s" (root.TrimEnd('\\')) (if String.IsNullOrWhiteSpace(d.VolumeLabel) then "" else "· " + d.VolumeLabel)
                          UsedPercent = float used / float total * 100.0
                          WithoutPercent = min 100.0 (float without / float total * 100.0)
                          Detail =
                            sprintf "%s of %s used now  ·  %s without Space Press  ·  %s free"
                                (formatBytes used) (formatBytes total) (formatBytes without) (formatBytes d.TotalFreeSpace)
                          SavedText = sprintf "%s saved" (formatBytes saved)
                          WouldOverflow = without > total })
            with _ -> ()
        topSaved.Clear()
        for r in rows |> Seq.filter (fun r -> r.HasSaved) |> Seq.sortByDescending (fun r -> r.SavedBytes) |> Seq.truncate 15 do
            topSaved.Add(r)
        for p in [ "HasTopSaved"; "SavedSharePercent"; "SavedShareText" ] do
            this.RaisePropertyChanged(p)

    /// Leaving the edition: stop measuring after the game in hand.
    member this.Deactivate() = isActive <- false

    /// Walks each folder one game at a time on a background-mode thread, and
    /// only while the page is open - leaving stops it between games, and
    /// coming back picks up the ones still unmeasured.
    member this.Measure(targets: StorageRowViewModel list) =
        let total = targets.Length
        this.SetMeasureStatus(sprintf "Measuring 0 of %d..." total)
        let worker =
            Thread(
                (fun () ->
                    let mutable n = 0
                    for row in targets do
                        if isActive then
                            n <- n + 1
                            let shown = n
                            ui (fun () ->
                                row.IsMeasuring <- true
                                this.SetMeasureStatus(sprintf "Measuring %d of %d..." shown total))
                            let info = try Some(inBackground (fun () -> analyse row.Folder)) with _ -> None
                            ui (fun () ->
                                row.IsMeasuring <- false
                                info |> Option.iter (fun i -> this.Remember(row, i))
                                this.RaiseTotals())
                            // Saved as it goes: closing halfway keeps what was measured.
                            if info.IsSome then saveCache ()
                    ui (fun () -> this.SetMeasureStatus(""))),
                IsBackground = true,
                Priority = ThreadPriority.Lowest,
                Name = "Space Press measuring")
        worker.Start()

    /// Why this game cannot be squished right now, or None. Touches the disk
    /// and the process list, so it is only ever called off the UI thread.
    member private _.Blocker(row: StorageRowViewModel) =
        match (try refusal row.Folder with _ -> None) with
        | Some reason -> Some reason
        | None -> if isRunning row.ProcessName then Some "The game is running - close it first." else None

    member this.Select(row: StorageRowViewModel) =
        this.Selected <- Some row
        if job.IsNone then
            Task.Run(fun () ->
                let reason = this.Blocker(row)
                ui (fun () ->
                    row.Message <- defaultArg reason ""
                    this.RaisePropertyChanged("SelectedReason")))
            |> ignore

    member this.Refresh() =
        match selected with
        | Some row when job.IsNone -> this.Measure([ row ])
        | _ -> ()

    member private this.NextInBatch() =
        match batchQueue with
        | next :: rest when isBatch ->
            batchQueue <- rest
            batchDone <- batchDone + 1
            this.Start(next, true)
        | _ ->
            isBatch <- false
            batchIsUpkeep <- false
            batchQueue <- []
            this.RaiseTotals()

    member private this.Finish (row: StorageRowViewModel) (ok: bool) =
        // The strip stays up while the result is measured, saying so.
        job <- None
        pausedForGame <- false
        jobVerb <- "Checking"
        row.WorkVerb <- "Checking"
        row.ProgressText <- "Measuring how much was saved..."
        this.RaiseJob()
        // Measure what actually happened on disk rather than trusting the job.
        Task.Run(fun () ->
            let info = try Some(inBackground (fun () -> analyse row.Folder)) with _ -> None
            if info.IsSome then saveCache ()
            ui (fun () ->
                info |> Option.iter (fun i -> this.Remember(row, i))
                row.IsWorking <- false
                row.ProgressText <- ""
                working <- None
                if not ok then row.Message <- "Stopped. What was already squished stays squished - Squish carries on from there, Unsquish undoes it."
                if ok then this.NextInBatch()
                else
                    isBatch <- false
                    batchIsUpkeep <- false
                    batchQueue <- []
                this.RaiseTotals()))
        |> ignore

    member private this.Start(row: StorageRowViewModel, squishIt: bool) =
        // Busy from this moment, so a second click cannot start a second job;
        // the checks themselves run off the UI thread.
        let j = Job()
        j.Speed <- speed
        job <- Some j
        working <- Some row
        jobVerb <- (if squishIt then "Squishing" else "Unsquishing")
        row.Message <- ""
        row.WorkVerb <- jobVerb
        row.Progress <- 0.0
        row.ProgressText <- "Preparing..."
        row.IsWorking <- true
        this.RaiseJob()
        Task.Run(fun () ->
            let reason = this.Blocker(row)
            ui (fun () ->
                match reason with
                | Some why ->
                    job <- None
                    working <- None
                    row.IsWorking <- false
                    row.ProgressText <- ""
                    row.Message <- why
                    this.RaiseJob()
                    // In a batch, skip it and carry on with the rest.
                    this.NextInBatch()
                | None -> this.Run(row, squishIt, j)))
        |> ignore

    member private this.Run(row: StorageRowViewModel, squishIt: bool, j: Job) =
            let alg, media, packages = algorithm, skipMedia, skipPackages
            let report (p: Progress) =
                ui (fun () ->
                    row.Progress <- p.Percent
                    // A path is the file being worked on; anything else is a
                    // message from the preparation steps, shown as it is.
                    row.ProgressText <-
                        if String.IsNullOrEmpty(p.CurrentFile) then
                            if p.TotalFiles = 0 then "Preparing..." else sprintf "%s of %s files" (p.DoneFiles.ToString("N0")) (p.TotalFiles.ToString("N0"))
                        elif not (p.CurrentFile.Contains('\\')) then p.CurrentFile
                        else sprintf "%s  ·  %s of %s files" (Path.GetFileName(p.CurrentFile)) (p.DoneFiles.ToString("N0")) (p.TotalFiles.ToString("N0")))
            Task.Run(fun () ->
                let work () =
                    if squishIt then squish row.Folder alg media packages j report
                    else unsquish row.Folder j report
                let ok =
                    try
                        // The engine moves the folder walk in and out of
                        // background mode as the speed changes.
                        work ()
                    with ex ->
                        AppLog.error "Space Press failed" ex
                        false
                ui (fun () -> this.Finish row ok))
            |> ignore

    member this.SquishSelected() =
        match selected with
        | Some row when not this.IsBusy -> this.Start(row, true)
        | _ -> ()

    member this.UnsquishSelected() =
        match selected with
        | Some row when not this.IsBusy -> this.Start(row, false)
        | _ -> ()

    member this.PauseOrResume() =
        match job, working with
        | Some j, Some row ->
            if j.IsPaused then
                pausedForGame <- false
                j.Resume()
                row.WorkVerb <- jobVerb
            else
                j.Pause()
                row.WorkVerb <- "Paused"
            this.RaiseJob()
        | _ -> ()

    member this.StopJob() =
        isBatch <- false
        batchIsUpkeep <- false
        batchQueue <- []
        job |> Option.iter (fun j -> j.Stop())
        this.RaiseTotals()

    member private this.StartBatch(queue: StorageRowViewModel list, upkeep: bool) =
        match queue with
        | first :: rest when not this.IsBusy ->
            isBatch <- true
            batchIsUpkeep <- upkeep
            batchQueue <- rest
            batchTotal <- queue.Length
            batchDone <- 0
            this.Start(first, true)
        | _ -> ()

    /// Every game not yet squished, smallest first, one after another.
    member this.SquishAllOrStop() =
        if isBatch then this.StopJob()
        elif not this.IsBusy then
            // The tab being looked at: games or apps.
            visibleRows
            |> Seq.filter (fun r -> r.IsPlain)
            |> Seq.sortBy (fun r -> match r.Info with Some i -> i.OnDiskBytes | None -> Int64.MaxValue)
            |> List.ofSeq
            |> fun queue -> this.StartBatch(queue, false)

    member this.OpenSelectedFolder() =
        selected |> Option.iter (fun r -> r.OpenFolder())

    /// Steps a squish aside while a library game runs, and back in after.
    member private this.WatchForGames() =
        match job, working with
        | Some j, Some row ->
            let names = libraryProcessNames ()
            Task.Run(fun () ->
                let playing = gamePlaying names
                ui (fun () ->
                    match job with
                    | Some current when obj.ReferenceEquals(current, j) ->
                        if playing && not j.IsPaused then
                            pausedForGame <- true
                            j.Pause()
                            row.WorkVerb <- "Paused"
                            this.RaiseJob()
                        elif not playing && pausedForGame then
                            pausedForGame <- false
                            j.Resume()
                            row.WorkVerb <- jobVerb
                            this.RaiseJob()
                    | _ -> ()))
            |> ignore
        | _ -> ()

    /// The hourly check. Re-measures every game one at a time, in background
    /// mode with a pause between games, never while a game is running or a
    /// squish is under way - and squishes again whatever an update has put
    /// back plain in a game you squished.
    member this.RunUpkeep() =
        if monitorOn && not this.IsBusy && not isBatch && not isMonitoring && library.Count > 0 then
            this.BuildRows()
            let names = libraryProcessNames ()
            let targets = rows |> List.ofSeq
            isMonitoring <- true
            let worker =
                Thread(
                    (fun () ->
                        let regrown = ResizeArray<StorageRowViewModel>()
                        let mutable stopped = false
                        for row in targets do
                            if not stopped then
                                if job.IsSome || not monitorOn || gamePlaying names then stopped <- true
                                else
                                    let before = row.Info
                                    match (try Some(inBackground (fun () -> analyse row.Folder)) with _ -> None) with
                                    | Some now ->
                                        ui (fun () -> this.Remember(row, now))
                                        let grew =
                                            match before with
                                            | Some b -> now.OnDiskBytes > b.OnDiskBytes + 268435456L
                                            | None -> false
                                        // Only games Space Press finished squishing - one you
                                        // stopped halfway leaves no record and is left alone.
                                        let partly = match now.Status with Partly -> true | _ -> false
                                        if hasRecord row.Folder && (partly || grew) then
                                            regrown.Add(row)
                                    | None -> ()
                                    // Spread the work out; nobody is waiting on it.
                                    Thread.Sleep(1500)
                        saveCache ()
                        ui (fun () ->
                            isMonitoring <- false
                            lastMonitor <- DateTime.Now.ToString("HH:mm")
                            this.RaisePropertyChanged("MonitorText")
                            this.RaiseTotals()
                            if not stopped && regrown.Count > 0 && not this.IsBusy then
                                AppLog.info (sprintf "Space Press: squishing %d updated game(s) again" regrown.Count)
                                this.StartBatch(List.ofSeq regrown, true))),
                    IsBackground = true,
                    Priority = ThreadPriority.Lowest,
                    Name = "Space Press upkeep")
            worker.Start()
