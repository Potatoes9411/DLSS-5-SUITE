namespace DLSS_5_MANAGER.Services

open System
open System.Diagnostics
open System.IO
open System.IO.Compression
open System.Net.Http
open System.Text.Json
open System.Text.RegularExpressions
open DLSS_5_MANAGER.Models

/// Community builds of OptiScaler's DLSS-NR route, each tuned for something
/// different - several passes, the model before or after DLSS upscaling,
/// RTX 40 frame generation. They share the layout of SUITE's bundled
/// DLSS-NR payload, so a chosen fork simply stands in for that folder.
///
/// Nothing is re-hosted: each one comes straight from its author's GitHub
/// releases, newest first, and is kept on this PC so an install does not
/// download it twice.
module OptiForks =

    type Fork =
        { Key: string
          Label: string
          Detail: string
          /// GitHub releases list.
          Api: string
          /// Archive name test, lower-cased.
          Pick: string -> bool
          /// Extra OptiScaler.ini settings: (section, key, value).
          Ini: (string * string * string) list
          /// Only offered on these cards ("" = any).
          Gpu: string }

    let private isArchive (n: string) = n.EndsWith ".zip" || n.EndsWith ".7z"

    let forks =
        [ { Key = "y4my4my4m"
            Label = "Multipass + MFG (y4my4my4m)"
            Detail = "Several model passes per frame and multi-frame generation; nightly builds."
            Api = "https://api.github.com/repos/y4my4my4m/OptiScaler_DLSSNR_Multipass_MFG/releases?per_page=10"
            Pick = (fun n -> isArchive n && n.Contains "optiscaler" && not (n.Contains "with_dlss"))
            Ini = []
            Gpu = "" }
          { Key = "wilsjo2"
            Label = "Pre-SR multipass (wilsjo2)"
            Detail = "Runs the model before DLSS upscales, at render resolution - the cheapest on most games."
            Api = "https://api.github.com/repos/wilsjo2/OptiScaler-DLSSNR-PreSR-Multipass/releases?per_page=10"
            Pick = (fun n -> isArchive n && n.Contains "optiscaler" && not (n.Contains "rtx40-mfg"))
            Ini = [ "DlssNr", "RunBeforeSR", "true" ]
            Gpu = "" }
          { Key = "wilsjo2-mfg"
            Label = "Pre-SR + RTX 40 MFG (wilsjo2)"
            Detail = "The pre-SR build with multi-frame generation unlocked on RTX 40 cards."
            Api = "https://api.github.com/repos/wilsjo2/OptiScaler-DLSSNR-PreSR-Multipass/releases?per_page=10"
            Pick = (fun n -> isArchive n && n.Contains "optiscaler" && n.Contains "rtx40-mfg")
            Ini = [ "DlssNr", "RunBeforeSR", "true"; "DLSSG", "AdaMfgUnlock", "true" ]
            Gpu = "rtx40" }
          { Key = "janblade"
            Label = "F5 multipass + DLSS FG (Janblade)"
            Detail = "Multipass with DLSS Frame Generation bundled; strong on DX11 and Vulkan games."
            Api = "https://api.github.com/repos/Janblade/OptiScaler-F5-DLSSNR-Multipass/releases?per_page=10"
            Pick = (fun n -> isArchive n && n.Contains "optiscaler" && not (n.Contains "-test-"))
            Ini = []
            Gpu = "" } ]

    let tryFind (key: string) = forks |> List.tryFind (fun f -> f.Key = key)

    // ------------------------------------------------------- choice per game

    let private choicesPath () =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DLSS5Suite", "optiscaler-forks.json")

    let private readChoices () : Collections.Generic.Dictionary<string, string> =
        try
            if File.Exists(choicesPath ()) then JsonSerializer.Deserialize<Collections.Generic.Dictionary<string, string>>(File.ReadAllText(choicesPath ()))
            else Collections.Generic.Dictionary<string, string>()
        with _ -> Collections.Generic.Dictionary<string, string>()

    /// "" = SUITE's bundled DLSS-NR build.
    let choiceFor (game: GameItem) =
        match readChoices().TryGetValue(game.AppId) with
        | true, v -> v
        | _ -> ""

    let setChoice (game: GameItem) (key: string) =
        let d = readChoices ()
        if key = "" then d.Remove(game.AppId) |> ignore else d.[game.AppId] <- key
        try File.WriteAllText(choicesPath (), JsonSerializer.Serialize(d)) with _ -> ()

    // -------------------------------------------------------------- download

    let private http =
        lazy
            (let h = new HttpClient(Timeout = TimeSpan.FromMinutes(20.0))
             h.DefaultRequestHeaders.UserAgent.ParseAdd("DLSS5-SUITE")
             h)

    let private root () =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DLSS5Suite", "OptiForks")

    /// The newest release with a matching archive: (tag, name, url).
    let private newest (f: Fork) =
        let json = http.Value.GetStringAsync(f.Api).Result
        use doc = JsonDocument.Parse(json)
        doc.RootElement.EnumerateArray()
        |> Seq.filter (fun r -> not (r.GetProperty("draft").GetBoolean()))
        |> Seq.tryPick (fun r ->
            let tag = r.GetProperty("tag_name").GetString()
            r.GetProperty("assets").EnumerateArray()
            |> Seq.map (fun a -> a.GetProperty("name").GetString(), a.GetProperty("browser_download_url").GetString())
            |> Seq.filter (fun (n, _) -> f.Pick(n.ToLowerInvariant()))
            |> Seq.sortByDescending fst
            |> Seq.tryHead
            |> Option.map (fun (n, u) -> tag, n, u))

    /// 7z archives go through Windows' own tar (libarchive reads 7z).
    let private extract (archive: string) (target: string) =
        Directory.CreateDirectory(target) |> ignore
        if archive.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) then
            let tar = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe")
            let psi = ProcessStartInfo(tar, sprintf "-xf \"%s\" -C \"%s\"" archive target, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true)
            use p = Process.Start(psi)
            let err = p.StandardError.ReadToEnd()
            p.WaitForExit()
            if p.ExitCode <> 0 then failwithf "the 7z archive could not be unpacked: %s" err
        else
            let full = Path.GetFullPath(target).TrimEnd('\\') + "\\"
            use z = ZipFile.OpenRead(archive)
            for e in z.Entries do
                if e.Name <> "" then
                    let dest = Path.GetFullPath(Path.Combine(target, e.FullName))
                    if not (dest.StartsWith(full, StringComparison.OrdinalIgnoreCase)) then failwithf "refused an entry outside the folder: %s" e.FullName
                    Directory.CreateDirectory(Path.GetDirectoryName dest) |> ignore
                    e.ExtractToFile(dest, true)

    /// The folder holding OptiScaler.dll inside an unpacked fork (some put
    /// everything one level down).
    let private payloadRoot (dir: string) =
        if File.Exists(Path.Combine(dir, "OptiScaler.dll")) then Some dir
        else
            Directory.GetFiles(dir, "OptiScaler.dll", SearchOption.AllDirectories)
            |> Array.sortBy (fun p -> p.Length)
            |> Array.tryHead
            |> Option.map Path.GetDirectoryName

    /// The fork's payload folder on this PC, downloading its newest build when
    /// there is none yet or the one here is more than a day older than a check.
    let ensure (f: Fork) (report: string -> unit) : string =
        let dir = Path.Combine(root (), f.Key)
        Directory.CreateDirectory(dir) |> ignore
        let stamp = Path.Combine(dir, "current.txt")
        let current =
            if File.Exists stamp then
                let p = File.ReadAllText(stamp).Trim()
                if Directory.Exists p then Some p else None
            else None
        let recent = File.Exists stamp && File.GetLastWriteTimeUtc stamp > DateTime.UtcNow.AddDays(-1.0)
        match current with
        | Some p when recent -> p
        | _ ->
            report (sprintf "Checking %s for its newest build..." f.Label)
            match (try newest f with ex -> (AppLog.warn ("Fork check failed: " + ex.Message); None)) with
            | None ->
                match current with
                | Some p -> p
                | None -> failwithf "%s has no release SUITE can use right now" f.Label
            | Some(tag, name, url) ->
                let safeTag = Regex.Replace(tag, @"[^A-Za-z0-9._-]", "_")
                let unpacked = Path.Combine(dir, safeTag)
                match (if Directory.Exists unpacked then payloadRoot unpacked else None) with
                | Some p ->
                    File.WriteAllText(stamp, p)
                    p
                | None ->
                    let archive = Path.Combine(dir, name)
                    report (sprintf "Downloading %s %s..." f.Label tag)
                    do
                        use s = http.Value.GetStreamAsync(url).Result
                        use fs = File.Create(archive + ".part")
                        s.CopyTo(fs)
                    File.Move(archive + ".part", archive, true)
                    report "Unpacking..."
                    if Directory.Exists unpacked then Directory.Delete(unpacked, true)
                    extract archive unpacked
                    File.Delete archive
                    match payloadRoot unpacked with
                    | None -> failwithf "%s %s has no OptiScaler.dll" f.Label tag
                    | Some p ->
                        File.WriteAllText(stamp, p)
                        AppLog.info (sprintf "OptiScaler fork %s %s ready at %s" f.Key tag p)
                        p
