namespace DLSS_5_MANAGER.Services

open System
open System.Diagnostics
open System.IO
open System.IO.Compression
open System.Net.Http
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text.Json

/// Installs the public LosslessProxy/LSP releases and the verified FeedKit
/// stack into an existing, licensed Steam copy of Lossless Scaling.
module LosslessScalingInstaller =

    type private Asset = { Tag: string; Name: string; Url: string; Digest: string }

    let private client = new HttpClient(Timeout = TimeSpan.FromMinutes(30.0))
    do client.DefaultRequestHeaders.UserAgent.ParseAdd("DLSS5-SUITE/1.2.1")

    [<DllImport("user32.dll")>]
    extern int private GetSystemMetrics(int index)

    let private hash path =
        use input = File.OpenRead(path)
        SHA256.HashData(input) |> Convert.ToHexString

    let private latestMatchingAsset repo description acceptName = task {
        use! response = client.GetAsync(sprintf "https://api.github.com/repos/%s/releases/latest" repo)
        response.EnsureSuccessStatusCode() |> ignore
        use! stream = response.Content.ReadAsStreamAsync()
        use doc = JsonDocument.Parse(stream)
        let tag = doc.RootElement.GetProperty("tag_name").GetString()
        let asset =
            doc.RootElement.GetProperty("assets").EnumerateArray()
            |> Seq.tryFind (fun item -> acceptName (item.GetProperty("name").GetString()))
            |> Option.defaultWith (fun () -> failwithf "%s release %s does not contain %s" repo tag description)
        let assetName = asset.GetProperty("name").GetString()
        let url = asset.GetProperty("browser_download_url").GetString()
        let uri = Uri(url)
        if uri.Scheme <> Uri.UriSchemeHttps || uri.Host <> "github.com" then failwith "Untrusted component download address"
        let digest =
            match asset.TryGetProperty("digest") with
            | true, value when not (String.IsNullOrWhiteSpace(value.GetString())) -> value.GetString().Replace("sha256:", "").ToUpperInvariant()
            | _ -> failwithf "%s did not publish a SHA-256 digest" repo
        if digest.Length <> 64 then failwith "Invalid component digest"
        return { Tag = tag; Name = assetName; Url = url; Digest = digest }
    }

    let private latestAsset repo expectedName =
        latestMatchingAsset repo expectedName (fun name -> String.Equals(name, expectedName, StringComparison.Ordinal))

    let private download (asset: Asset) destination progress start size = task {
        use! response = client.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead)
        response.EnsureSuccessStatusCode() |> ignore
        let total = response.Content.Headers.ContentLength |> Option.ofNullable |> Option.defaultValue 0L
        use! input = response.Content.ReadAsStreamAsync()
        use output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None)
        let buffer = Array.zeroCreate<byte> (128 * 1024)
        let mutable received = 0L
        let mutable reading = true
        while reading do
            let! count = input.ReadAsync(buffer, 0, buffer.Length)
            if count = 0 then reading <- false
            else
                do! output.WriteAsync(buffer, 0, count)
                received <- received + int64 count
                progress (sprintf "Downloading %s" asset.Name) (start + size * (if total > 0 then float received / float total else 0.0))
        output.Flush(true)
        if not (String.Equals(hash destination, asset.Digest, StringComparison.OrdinalIgnoreCase)) then
            File.Delete(destination)
            failwithf "SHA-256 verification failed for %s" asset.Name
    }

    let private extractSafe zipPath destination =
        let root = Path.GetFullPath(destination) + string Path.DirectorySeparatorChar
        use archive = ZipFile.OpenRead(zipPath)
        for entry in archive.Entries do
            let output = Path.GetFullPath(Path.Combine(destination, entry.FullName.Replace('/', Path.DirectorySeparatorChar)))
            if not (output.StartsWith(root, StringComparison.OrdinalIgnoreCase)) then failwith "Unsafe path in component archive"
            if String.IsNullOrEmpty(entry.Name) then Directory.CreateDirectory(output) |> ignore
            else
                Directory.CreateDirectory(Path.GetDirectoryName(output)) |> ignore
                entry.ExtractToFile(output, true)

    let private runFeederInstaller executable cache progress = task {
        let! feeder =
            latestMatchingAsset "jlrouzies-fr/DLSS5-Feeder" "the stable DLSS5-Feeder ZIP"
                (fun name -> name.StartsWith("DLSS5-Feeder-", StringComparison.Ordinal) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        let feederZip = Path.Combine(cache, feeder.Name)
        do! download feeder feederZip progress 34.0 8.0
        let scriptUrl = sprintf "https://raw.githubusercontent.com/jlrouzies-fr/DLSS5-Feeder/%s/tools/Install-DLSS5Feeder.ps1" feeder.Tag
        let script = Path.Combine(cache, sprintf "Install-DLSS5Feeder-%s.ps1" feeder.Tag)
        let! text = client.GetStringAsync(scriptUrl)
        if not (text.Contains("[CmdletBinding()]") && text.Contains("$GameExe") && text.Contains("$Consumer")) then
            failwith "The release-matched FeedKit installer failed validation"
        do! File.WriteAllTextAsync(script, text)
        let psi = ProcessStartInfo("powershell.exe", UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true)
        for arg in [ "-NoProfile"; "-ExecutionPolicy"; "Bypass"; "-File"; script; executable; "-Api"; "D3D"; "-Consumer"; "RenoDX"; "-Yes"; "-NoElevate"; "-NoPause"; "-Downloads"; cache ] do psi.ArgumentList.Add(arg)
        use proc = new Process(StartInfo = psi)
        proc.Start() |> ignore
        let mutable line = proc.StandardOutput.ReadLine()
        let mutable step = 44.0
        while not (isNull line) do
            if not (String.IsNullOrWhiteSpace(line)) then
                step <- min 94.0 (step + 0.3)
                progress line step
            line <- proc.StandardOutput.ReadLine()
        let! errors = proc.StandardError.ReadToEndAsync()
        do! proc.WaitForExitAsync()
        if proc.ExitCode <> 0 then failwithf "FeedKit setup failed: %s" errors
        return feeder
    }

    let setupLatest progress = task {
        let installation =
            (LosslessScalingDetector.discover()).Installations
            |> List.tryHead
            |> Option.defaultWith (fun () -> failwith "A licensed Steam installation of Lossless Scaling was not found")
        if Process.GetProcessesByName("LosslessScaling").Length > 0 then failwith "Close Lossless Scaling before setup"
        let directory = Path.GetDirectoryName(installation.Executable)
        let stateRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DLSS5Manager", "Compatibility", "LosslessScaling")
        let cache = Path.Combine(stateRoot, ".downloads")
        let backup = Path.Combine(stateRoot, "backup")
        Directory.CreateDirectory(cache) |> ignore
        Directory.CreateDirectory(backup) |> ignore

        progress "Checking official LosslessProxy releases" 2.0
        let! proxy = latestAsset "FrankBarretta/LosslessProxy" "Lossless.dll"
        let! reshadeAddon = latestAsset "FrankBarretta/LSP-ReShade" "LSP-ReShade.zip"
        let! windowedAddon = latestAsset "FrankBarretta/LSP-Windowed" "LSP-Windowed.zip"
        let proxyPath = Path.Combine(cache, proxy.Name)
        let reshadeZip = Path.Combine(cache, reshadeAddon.Name)
        let windowedZip = Path.Combine(cache, windowedAddon.Name)
        do! download proxy proxyPath progress 5.0 8.0
        do! download reshadeAddon reshadeZip progress 14.0 8.0
        do! download windowedAddon windowedZip progress 23.0 8.0

        let engine = Path.Combine(directory, "Lossless.dll")
        let original = Path.Combine(directory, "Lossless_original.dll")
        if not (File.Exists(engine)) then failwith "Lossless Scaling's engine DLL is missing; verify the app in Steam first"
        if not (File.Exists(original)) then
            if String.Equals(hash engine, proxy.Digest, StringComparison.OrdinalIgnoreCase) then
                failwith "LosslessProxy is present but the original engine backup is missing; verify Lossless Scaling in Steam first"
            File.Copy(engine, original, false)
        let backupEngine = Path.Combine(backup, "Lossless.dll")
        if not (File.Exists(backupEngine)) then File.Copy(original, backupEngine, false)
        File.Copy(proxyPath, engine, true)
        let addons = Path.Combine(directory, "addons")
        Directory.CreateDirectory(addons) |> ignore
        extractSafe reshadeZip addons
        extractSafe windowedZip addons

        let! feeder = runFeederInstaller installation.Executable cache progress
        let required = [ engine; original; Path.Combine(addons, "LSP-ReShade", "LSP_ReShade.dll"); Path.Combine(addons, "LSP-Windowed", "LSP_Windowed.dll"); Path.Combine(directory, "dxgi.dll"); Path.Combine(directory, "ReShade.ini") ]
        let missing = required |> List.filter (File.Exists >> not)
        if not missing.IsEmpty then failwithf "Setup completed incompletely; missing: %s" (String.Join(", ", missing |> List.map Path.GetFileName))
        let monitors = GetSystemMetrics(80)
        let manifest = JsonSerializer.Serialize({| proxyTag = proxy.Tag; proxySha256 = proxy.Digest; reshadeAddonTag = reshadeAddon.Tag; windowedAddonTag = windowedAddon.Tag; feederTag = feeder.Tag; installedAtUtc = DateTime.UtcNow |}, JsonSerializerOptions(WriteIndented = true))
        do! File.WriteAllTextAsync(Path.Combine(stateRoot, "suite-component-manifest.json"), manifest)
        progress "Lossless Scaling compatibility is ready" 100.0
        return installation.Executable, monitors
    }
