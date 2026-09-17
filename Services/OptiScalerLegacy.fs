namespace DLSS_5_MANAGER.Services

open System
open System.IO
open System.Net.Http
open System.Text.Json
open System.Text.RegularExpressions
open Microsoft.Win32
open SharpCompress.Archives.SevenZip

/// A second, older OptiScaler build for RTX 20/30 series cards.
///
/// SUITE bundles the current OptiScaler (10.0.0-dev), which NODIX's own
/// testing found less stable on RTX 20/30 than the last build before that
/// branch - 0.7.9, upstream's own tag right before it. It is fetched from
/// OptiScaler's own GitHub releases on request, verified against the
/// SHA-256 GitHub itself publishes for the asset, and kept per-user rather
/// than under the install directory, the same lesson the NeuralScreen
/// add-on learned about Program Files being read-only.
module OptiScalerLegacy =
    [<Literal>]
    let private Repo = "optiscaler/OptiScaler"

    [<Literal>]
    let private Tag = "v0.7.9"

    [<Literal>]
    let DisplayVersion = "0.7.9"

    let private dataDir () =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DLSS5Suite", "OptiScalerLegacy")

    let folder () = Path.Combine(dataDir (), "build")

    let isAvailable () = File.Exists(Path.Combine(folder (), "OptiScaler.dll"))

    /// Whether the machine's best card is an RTX 20 or 30 series - the two
    /// generations NODIX found less stable on the current OptiScaler branch.
    /// Read straight from the registry rather than through NeuralScreen's own
    /// detection, which compiles after this module.
    let isOlderGeneration () =
        try
            use root =
                Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}")
            if isNull root then false
            else
                root.GetSubKeyNames()
                |> Array.filter (fun n -> n.Length = 4 && n |> Seq.forall Char.IsDigit)
                |> Array.exists (fun n ->
                    try
                        use sub = root.OpenSubKey(n)
                        match (if isNull sub then null else sub.GetValue("DriverDesc")) with
                        | :? string as s ->
                            let m = Regex.Match(s.ToUpperInvariant(), @"RTX\s*(\d{2})\d{2}")
                            m.Success && (let series = int m.Groups.[1].Value in series = 20 || series = 30)
                        | _ -> false
                    with _ -> false)
        with _ -> false

    /// The build OptiScaler installs from: the legacy one when it is present
    /// and the card is RTX 20/30, the bundled current one otherwise.
    let preferredRoot (bundledRoot: string) =
        if isOlderGeneration () && isAvailable () then folder () else bundledRoot

    let private client =
        let c = new HttpClient(Timeout = TimeSpan.FromMinutes(15.0))
        c.DefaultRequestHeaders.UserAgent.ParseAdd("DLSS5-SUITE-OptiScalerLegacy")
        c

    let private locate () = task {
        use! response = client.GetAsync(sprintf "https://api.github.com/repos/%s/releases/tags/%s" Repo Tag)
        response.EnsureSuccessStatusCode() |> ignore
        use! stream = response.Content.ReadAsStreamAsync()
        use document = JsonDocument.Parse(stream)
        let asset =
            document.RootElement.GetProperty("assets").EnumerateArray()
            |> Seq.tryFind (fun item -> item.GetProperty("name").GetString().EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
            |> Option.defaultWith (fun () -> failwith "OptiScaler's release did not carry a .7z build.")
        let digest =
            match asset.TryGetProperty("digest") with
            | true, value when not (String.IsNullOrWhiteSpace(value.GetString())) -> value.GetString().Replace("sha256:", "").ToUpperInvariant()
            | _ -> failwith "GitHub published no digest for this OptiScaler build; the download was refused."
        return
            asset.GetProperty("name").GetString(),
            asset.GetProperty("browser_download_url").GetString(),
            digest,
            asset.GetProperty("size").GetInt64()
    }

    /// Downloads, verifies, and unpacks OptiScaler's own .7z with a pure
    /// managed reader - nothing native, nothing that needs 7-Zip installed.
    let install (progress: string -> float -> unit) = task {
        let! (name, url, digest, size) = locate ()
        let cache = Path.Combine(dataDir (), "download")
        Directory.CreateDirectory(cache) |> ignore
        let archive = Path.Combine(cache, name)
        do! VerifiedDownload.download client name url digest size archive progress 0.0 85.0

        progress "Unpacking OptiScaler..." 88.0
        let target = folder ()
        if Directory.Exists(target) then Directory.Delete(target, true)
        Directory.CreateDirectory(target) |> ignore
        use sevenZip = SevenZipArchive.OpenArchive(archive)
        let entries = sevenZip.Entries |> Seq.filter (fun e -> not e.IsDirectory) |> Seq.toArray
        let mutable done_ = 0
        for entry in entries do
            done_ <- done_ + 1
            let relative = entry.Key.Replace('/', Path.DirectorySeparatorChar)
            let outPath = Path.Combine(target, relative)
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)) |> ignore
            use entryStream = entry.OpenEntryStream()
            use output = File.Create(outPath)
            entryStream.CopyTo(output)
            progress "Unpacking OptiScaler..." (88.0 + float done_ / float entries.Length * 10.0)

        try File.Delete(archive) with _ -> ()
        if not (isAvailable ()) then failwith "OptiScaler was unpacked but OptiScaler.dll is still missing."
        progress (sprintf "OptiScaler %s installed." DisplayVersion) 100.0
    }
