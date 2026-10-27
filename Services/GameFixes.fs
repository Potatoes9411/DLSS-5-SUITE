namespace DLSS_5_MANAGER.Services

open System
open System.IO
open System.IO.Compression
open System.Net.Http
open System.Text.Json
open Microsoft.Win32
open DLSS_5_MANAGER.Models

/// Small fixes an install needs in particular games or on particular PCs,
/// applied after the install itself and taken back out by uninstall:
///
/// - Hybrid-graphics laptops: Windows' per-app GPU preference set to the
///   NVIDIA card for the game (and the feeder's 64-bit helper), because DLSS
///   cannot run on the integrated GPU the game would otherwise start on.
/// - RE Engine games (Resident Evil 2/3/4 remakes, 7, Village, Requiem, Devil
///   May Cry 5, Monster Hunter...): ReShade with add-ons crashes them unless
///   REFramework is loaded first, so its dinput8.dll goes in beside the exe.
///
/// What was done is kept in a small record per game, so uninstall removes
/// exactly that and nothing a person set up themselves.
module GameFixes =

    [<CLIMutable>]
    type FixRecord =
        { GpuPreference: string[]
          ReFramework: string
          ReFrameworkBackup: string }

    let private empty = { GpuPreference = [||]; ReFramework = ""; ReFrameworkBackup = "" }

    let private recordPath (game: GameItem) =
        let dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DLSS5Suite", "Fixes")
        Directory.CreateDirectory(dir) |> ignore
        let id = Text.RegularExpressions.Regex.Replace((if String.IsNullOrWhiteSpace game.AppId then game.Title else game.AppId), @"[^A-Za-z0-9_\-]", "_")
        Path.Combine(dir, id + ".json")

    let private load (game: GameItem) =
        try
            let p = recordPath game
            if File.Exists p then
                let r = JsonSerializer.Deserialize<FixRecord>(File.ReadAllText p)
                if isNull (box r) then empty
                else { r with GpuPreference = (if isNull r.GpuPreference then [||] else r.GpuPreference) }
            else empty
        with _ -> empty

    let private save (game: GameItem) (r: FixRecord) =
        try
            let p = recordPath game
            if r.GpuPreference.Length = 0 && r.ReFramework = "" then
                if File.Exists p then File.Delete p
            else File.WriteAllText(p, JsonSerializer.Serialize(r, JsonSerializerOptions(WriteIndented = true)))
        with ex -> AppLog.warn ("Could not save the game-fix record: " + ex.Message)

    // ------------------------------------------------------- GPU preference

    /// True on a PC with an NVIDIA card and an Intel or AMD one beside it.
    let isHybrid =
        lazy
            (try
                use cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}")
                if isNull cls then false
                else
                    let vendors =
                        [ for n in cls.GetSubKeyNames() do
                              if n.Length = 4 && Char.IsDigit n.[0] then
                                  try
                                      use k = cls.OpenSubKey(n)
                                      let id = string (k.GetValue("MatchingDeviceId", ""))
                                      if id <> "" then yield id.ToUpperInvariant()
                                  with _ -> () ]
                    let hasNvidia = vendors |> List.exists (fun v -> v.Contains "VEN_10DE")
                    let hasOther = vendors |> List.exists (fun v -> v.Contains "VEN_8086" || v.Contains "VEN_1002" || v.Contains "VEN_1022")
                    hasNvidia && hasOther
             with _ -> false)

    [<Literal>]
    let private GpuKey = @"Software\Microsoft\DirectX\UserGpuPreferences"

    /// Sets "GpuPreference=2;" (high performance) for one exe, keeping any
    /// other tokens in the value. A preference the person already chose is
    /// never overridden. Returns true when this call added it.
    let private preferNvidia (exe: string) =
        try
            use k = Registry.CurrentUser.CreateSubKey(GpuKey)
            let current = string (k.GetValue(exe, ""))
            if current.Contains("GpuPreference=") then false
            else
                k.SetValue(exe, current + "GpuPreference=2;", RegistryValueKind.String)
                true
        with _ -> false

    let private clearPreference (exe: string) =
        try
            use k = Registry.CurrentUser.OpenSubKey(GpuKey, true)
            if not (isNull k) then
                let current = string (k.GetValue(exe, ""))
                if current.Contains("GpuPreference=2;") then
                    let rest = current.Replace("GpuPreference=2;", "")
                    if rest = "" then k.DeleteValue(exe, false) else k.SetValue(exe, rest, RegistryValueKind.String)
        with _ -> ()

    // ---------------------------------------------------------- REFramework

    /// RE Engine ships its data as re_chunk_000.pak beside the game.
    let isReEngine (exePath: string) =
        try
            let dir = Path.GetDirectoryName(exePath)
            File.Exists(Path.Combine(dir, "re_chunk_000.pak"))
        with _ -> false

    let private isReFrameworkDll (path: string) =
        try
            File.Exists path
            && FileInfo(path).Length > 1048576L
            && (let bytes = File.ReadAllBytes(path) in
                let needle = Text.Encoding.ASCII.GetBytes("REFramework")
                let span = ReadOnlySpan<byte>(bytes)
                span.IndexOf(ReadOnlySpan<byte>(needle)) >= 0)
        with _ -> false

    let private http =
        lazy
            (let h = new HttpClient(Timeout = TimeSpan.FromMinutes(5.0))
             h.DefaultRequestHeaders.UserAgent.ParseAdd("DLSS5-SUITE")
             h)

    /// praydog's nightly REFramework.zip ships a universal dinput8.dll that
    /// covers every RE Engine game; only that file is taken from it.
    let private fetchReFramework () =
        let cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DLSS5Suite", "Downloads", "REFramework")
        Directory.CreateDirectory(cache) |> ignore
        let dll = Path.Combine(cache, "dinput8.dll")
        if File.Exists dll && File.GetLastWriteTimeUtc dll > DateTime.UtcNow.AddDays(-14.0) then dll
        else
            let zip = Path.Combine(cache, "REFramework.zip")
            let url = "https://github.com/praydog/REFramework-nightly/releases/latest/download/REFramework.zip"
            use s = http.Value.GetStreamAsync(url).Result
            use f = File.Create(zip)
            s.CopyTo(f)
            f.Close()
            use archive = ZipFile.OpenRead(zip)
            let entry =
                archive.Entries
                |> Seq.tryFind (fun e -> e.FullName.Replace('\\', '/').Equals("dinput8.dll", StringComparison.OrdinalIgnoreCase))
                |> Option.orElse (archive.Entries |> Seq.tryFind (fun e -> e.Name.Equals("dinput8.dll", StringComparison.OrdinalIgnoreCase)))
            match entry with
            | None -> failwith "REFramework.zip had no dinput8.dll"
            | Some e ->
                e.ExtractToFile(dll + ".part", true)
                File.Move(dll + ".part", dll, true)
                dll

    // ---------------------------------------------------------------- apply

    /// After a successful install. reshadeRoute: false for OptiScaler, which
    /// does not load ReShade and does not need REFramework.
    let afterInstall (game: GameItem) (exePath: string) (reshadeRoute: bool) (report: string -> unit) : string list =
        let notes = ResizeArray<string>()
        let mutable r = load game
        if isHybrid.Value then
            let exeDir = Path.GetDirectoryName(exePath)
            let targets =
                [ yield exePath
                  let helper = Path.Combine(exeDir, "host64", "dlss5-feed-host64.exe")
                  if File.Exists helper then yield helper ]
            let added = targets |> List.filter preferNvidia
            if not added.IsEmpty then
                r <- { r with GpuPreference = Array.append r.GpuPreference (Array.ofList added) |> Array.distinct }
                notes.Add "Windows will start the game on the NVIDIA GPU."
        if reshadeRoute && isReEngine exePath then
            let target = Path.Combine(Path.GetDirectoryName(exePath), "dinput8.dll")
            if isReFrameworkDll target then
                notes.Add "REFramework is already in the game."
            else
                try
                    report "RE Engine game: adding REFramework so ReShade's add-ons can load..."
                    let src = fetchReFramework ()
                    let backup =
                        if File.Exists target then
                            let b = target + ".dlss5-suite-backup"
                            File.Copy(target, b, true)
                            b
                        else ""
                    File.Copy(src, target, true)
                    r <- { r with ReFramework = target; ReFrameworkBackup = backup }
                    notes.Add "REFramework was added - RE Engine games need it for ReShade's add-ons."
                with ex ->
                    AppLog.error "Adding REFramework" ex
                    notes.Add("REFramework could not be added (" + ex.Message + "). RE Engine games may crash with ReShade's add-ons until it is.")
        save game r
        List.ofSeq notes

    /// After an uninstall: takes back exactly what afterInstall did.
    let afterUninstall (game: GameItem) =
        let r = load game
        for exe in r.GpuPreference do
            clearPreference exe
        if r.ReFramework <> "" then
            try
                if r.ReFrameworkBackup <> "" && File.Exists r.ReFrameworkBackup then
                    File.Move(r.ReFrameworkBackup, r.ReFramework, true)
                elif File.Exists r.ReFramework && isReFrameworkDll r.ReFramework then
                    File.Delete r.ReFramework
            with ex -> AppLog.warn ("REFramework could not be removed: " + ex.Message)
        save game empty
