namespace DLSS_5_MANAGER.Services

open System
open System.Diagnostics
open System.IO
open System.IO.Compression
open System.Net.Http
open System.Security.Cryptography
open System.Text.Json
open System.Threading.Tasks

module ShaderGlassDetector =

    type Status = | Missing | Ready of string | NeedsUpdate of string
    type ReleaseAsset = { Tag: string; Name: string; Url: string; Digest: string }

    let private root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DLSS5Manager", "Compatibility", "ShaderGlass")
    let private manifestPath = Path.Combine(root, "suite-component-manifest.json")
    let private client = new HttpClient()
    do client.DefaultRequestHeaders.UserAgent.ParseAdd("DLSS5-SUITE/1.2.1")

    let private hash path =
        use input = File.OpenRead(path)
        use sha = SHA256.Create()
        sha.ComputeHash(input) |> Convert.ToHexString

    let private assetFromRelease (repo: string) (acceptName: string -> bool) = task {
        let uri = sprintf "https://api.github.com/repos/%s/releases/latest" repo
        use! response = client.GetAsync(uri)
        response.EnsureSuccessStatusCode() |> ignore
        use! stream = response.Content.ReadAsStreamAsync()
        use doc = JsonDocument.Parse(stream)
        let rootElement = doc.RootElement
        let tag = rootElement.GetProperty("tag_name").GetString()
        let asset =
            rootElement.GetProperty("assets").EnumerateArray()
            |> Seq.tryFind (fun item -> acceptName (item.GetProperty("name").GetString()))
            |> Option.defaultWith (fun () -> failwithf "Expected release asset was not found in %s" repo)
        let digest =
            match asset.TryGetProperty("digest") with
            | true, value when not (String.IsNullOrWhiteSpace(value.GetString())) -> value.GetString()
            | _ -> failwithf "%s did not publish a digest; download refused" repo
        return { Tag = tag; Name = asset.GetProperty("name").GetString(); Url = asset.GetProperty("browser_download_url").GetString(); Digest = digest.Replace("sha256:", "").ToUpperInvariant() }
    }

    let private download (asset: ReleaseAsset) path (progress: string -> float -> unit) phaseStart phaseSize = task {
        use! response = client.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead)
        response.EnsureSuccessStatusCode() |> ignore
        let total = response.Content.Headers.ContentLength |> Option.ofNullable |> Option.defaultValue 0L
        use! source = response.Content.ReadAsStreamAsync()
        use target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None)
        let buffer = Array.zeroCreate<byte> (128 * 1024)
        let mutable received = 0L
        let mutable reading = true
        while reading do
            let! count = source.ReadAsync(buffer, 0, buffer.Length)
            if count = 0 then reading <- false
            else
                do! target.WriteAsync(buffer, 0, count)
                received <- received + int64 count
                let fraction = if total > 0L then float received / float total else 0.0
                progress (sprintf "Downloading %s" asset.Name) (phaseStart + fraction * phaseSize)
        target.Flush(true)
        let actual = hash path
        if not (String.Equals(actual, asset.Digest, StringComparison.OrdinalIgnoreCase)) then
            File.Delete(path)
            failwithf "SHA-256 mismatch for %s" asset.Name
    }

    let private extractSafe zipPath destination =
        let fullDestination = Path.GetFullPath(destination) + string Path.DirectorySeparatorChar
        use archive = ZipFile.OpenRead(zipPath)
        for entry in archive.Entries do
            let relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar)
            let output = Path.GetFullPath(Path.Combine(destination, relative))
            if not (output.StartsWith(fullDestination, StringComparison.OrdinalIgnoreCase)) then
                failwith "Unsafe path found in downloaded archive"
            if String.IsNullOrEmpty(entry.Name) then Directory.CreateDirectory(output) |> ignore
            else
                Directory.CreateDirectory(Path.GetDirectoryName(output)) |> ignore
                entry.ExtractToFile(output, true)

    let private exePath () = Path.Combine(root, "ShaderGlass.exe")

    let discover () =
        if not (File.Exists(exePath())) then Missing
        elif not (File.Exists(manifestPath)) then NeedsUpdate(exePath())
        else Ready(exePath())

    let describe = function
        | Missing -> "ShaderGlass compatibility is not installed. Setup downloads verified current files automatically."
        | NeedsUpdate _ -> "ShaderGlass exists, but its component record is missing. Run setup to verify and repair it."
        | Ready path -> sprintf "ShaderGlass compatibility is installed and update-aware.\n%s" path

    let setupLatest (progress: string -> float -> unit) = task {
        Directory.CreateDirectory(root) |> ignore
        let cache = Path.Combine(root, ".downloads")
        Directory.CreateDirectory(cache) |> ignore

        progress "Checking official ShaderGlass release" 2.0
        let! shaderGlass = assetFromRelease "mausimus/ShaderGlass" (fun n -> n.StartsWith("ShaderGlass-") && n.EndsWith("-win-x64.zip"))
        let shaderZip = Path.Combine(cache, shaderGlass.Name)
        do! download shaderGlass shaderZip progress 4.0 20.0

        let staging = Path.Combine(root, ".staging")
        if Directory.Exists(staging) then Directory.Delete(staging, true)
        Directory.CreateDirectory(staging) |> ignore
        progress "Extracting verified ShaderGlass" 27.0
        extractSafe shaderZip staging
        let stagedExe = Directory.GetFiles(staging, "ShaderGlass.exe", SearchOption.AllDirectories) |> Array.tryHead |> Option.defaultWith (fun () -> failwith "ShaderGlass.exe missing from official archive")
        let stagedRoot = Path.GetDirectoryName(stagedExe)
        for file in Directory.GetFiles(stagedRoot, "*", SearchOption.AllDirectories) do
            let relative = Path.GetRelativePath(stagedRoot, file)
            let destination = Path.Combine(root, relative)
            Directory.CreateDirectory(Path.GetDirectoryName(destination)) |> ignore
            File.Copy(file, destination, true)

        progress "Checking official DLSS5-Feeder release" 32.0
        let! feeder = assetFromRelease "jlrouzies-fr/DLSS5-Feeder" (fun n -> n.StartsWith("DLSS5-Feeder-") && n.EndsWith(".zip"))
        let feederZip = Path.Combine(cache, feeder.Name)
        do! download feeder feederZip progress 34.0 6.0

        // The release ZIP digest authenticates the payload. The installer is
        // fetched at that immutable release tag and sanity-checked before use.
        let scriptUrl = sprintf "https://raw.githubusercontent.com/jlrouzies-fr/DLSS5-Feeder/%s/tools/Install-DLSS5Feeder.ps1" feeder.Tag
        let scriptPath = Path.Combine(cache, sprintf "Install-DLSS5Feeder-%s.ps1" feeder.Tag)
        progress "Downloading the release-matched installer" 42.0
        let! scriptText = client.GetStringAsync(scriptUrl)
        if not (scriptText.Contains("[CmdletBinding()]") && scriptText.Contains("$GameExe") && scriptText.Contains("$Consumer")) then
            failwith "Downloaded Feeder installer did not match the expected script structure"
        do! File.WriteAllTextAsync(scriptPath, scriptText)

        progress "Installing current verified components" 45.0
        let psi = ProcessStartInfo("powershell.exe")
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.ArgumentList.Add("-NoProfile")
        psi.ArgumentList.Add("-ExecutionPolicy")
        psi.ArgumentList.Add("Bypass")
        psi.ArgumentList.Add("-File")
        psi.ArgumentList.Add(scriptPath)
        psi.ArgumentList.Add(exePath())
        psi.ArgumentList.Add("-Api")
        psi.ArgumentList.Add("D3D")
        psi.ArgumentList.Add("-Consumer")
        psi.ArgumentList.Add("RenoDX")
        psi.ArgumentList.Add("-Yes")
        psi.ArgumentList.Add("-NoElevate")
        psi.ArgumentList.Add("-NoPause")
        psi.ArgumentList.Add("-Downloads")
        psi.ArgumentList.Add(cache)
        use installerProcess = new Process(StartInfo = psi)
        installerProcess.Start() |> ignore
        let mutable line = installerProcess.StandardOutput.ReadLine()
        let mutable step = 46.0
        while not (isNull line) do
            if not (String.IsNullOrWhiteSpace(line)) then
                step <- min 94.0 (step + 0.35)
                progress line step
            line <- installerProcess.StandardOutput.ReadLine()
        let! errors = installerProcess.StandardError.ReadToEndAsync()
        do! installerProcess.WaitForExitAsync()
        if installerProcess.ExitCode <> 0 then failwithf "Automatic component setup failed: %s" errors

        let required = [ "dxgi.dll"; "dlss5-feed.addon64"; "renodx-dlss5.addon64"; "nvngx_dlssnr.dll"; "nvngx_dlss.dll"; "ReShade.ini" ]
        let missing = required |> List.filter (fun name -> not (File.Exists(Path.Combine(root, name))))
        if not missing.IsEmpty then failwithf "Setup finished but required files are missing: %s" (String.Join(", ", missing))

        let manifest = JsonSerializer.Serialize({| shaderGlassTag = shaderGlass.Tag; shaderGlassSha256 = hash (exePath()); feederTag = feeder.Tag; feederAssetSha256 = feeder.Digest; checkedAtUtc = DateTime.UtcNow |}, JsonSerializerOptions(WriteIndented = true))
        do! File.WriteAllTextAsync(manifestPath, manifest)
        if Directory.Exists(staging) then Directory.Delete(staging, true)
        progress "ShaderGlass compatibility is ready" 100.0
        return exePath()
    }
