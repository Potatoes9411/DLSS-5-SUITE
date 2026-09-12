namespace DLSS_5_MANAGER.Services

open System
open System.Net.Http
open System.Text.Json
open System.Text.RegularExpressions
open System.IO
open System.Security.Cryptography

/// Compares the running build against the published GitHub releases and points
/// the user at the official download page when a newer version exists.
module UpdateChecker =

    [<Literal>]
    let CurrentVersion = "1.2.1-suite.4"

    // =====================================================================
    // WHERE UPDATES COME FROM
    // =====================================================================
    // The only two lines to change if the repository is ever renamed or
    // moved. Everything below is built from them.
    //
    // How this works, so releases get tagged correctly: the check reads the
    // repository's releases, takes the newest one's tag, strips a leading "v"
    // and compares it numerically against CurrentVersion above. So a release
    // tagged "v1.2.1" or "1.2.1" both register as newer than 1.2.0, while
    // "latest" or "release-3" parse as nothing and are ignored. Tag each
    // release with the version number and attach the setup executable to it.
    [<Literal>]
    let RepoOwner = "Potatoes9411"

    [<Literal>]
    let RepoName = "DLSS-5-SUITE"

    [<Literal>]
    let ReleasesApiUrl =
        "https://api.github.com/repos/" + RepoOwner + "/" + RepoName + "/releases"

    [<Literal>]
    let ReleasesPageUrl =
        "https://github.com/" + RepoOwner + "/" + RepoName + "/releases"

    /// Where an update to *this* build is downloaded from.
    [<Literal>]
    let DownloadPageUrl =
        "https://github.com/" + RepoOwner + "/" + RepoName + "/releases/latest"

    // =====================================================================
    // THE ORIGINAL
    // =====================================================================
    // A condition of the permission NODIX TECH gave for this modified build:
    // the original's download location is not to be changed or replaced, and
    // this link has to stay in the application. It is not a fallback and not
    // a courtesy - leaving it out breaks the terms this build exists under.
    [<Literal>]
    let OriginalDownloadUrl = "https://numidiastudios.com/dlss-5-manager/"

    /// The community guides site NODIX TECH is building, which will cover the
    /// original and the modified builds alike. Linking to it once it exists is
    /// the other standing condition. Fill this in and the button appears by
    /// itself - nothing else needs changing.
    [<Literal>]
    let GuidesUrl = "https://dlss5manager.numidiastudios.com/tutorials"

    type UpdateResult =
        { HasUpdate: bool
          LatestVersion: string
          Message: string }

    let private client =
        let c = new HttpClient()
        c.Timeout <- TimeSpan.FromMinutes(30.0)
        c.DefaultRequestHeaders.UserAgent.ParseAdd("DLSS5Manager-Updater/1.0")
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json")
        c

    /// "v1.2.3" / "1.2.3-beta" -> [1; 2; 3]
    let private parseVersion (raw: string) : int list =
        if String.IsNullOrWhiteSpace(raw) then
            []
        else
            let cleaned = Regex.Match(raw.Trim().TrimStart('v', 'V'), @"^\d+(\.\d+)*")

            if not cleaned.Success then
                []
            else
                let core = 
                    cleaned.Value.Split('.')
                    |> Array.map (fun p ->
                        match Int32.TryParse(p) with
                        | true, v -> v
                        | _ -> 0)
                    |> Array.toList
                let suiteMatch = Regex.Match(raw, @"(?i)(?:^|[-_.])suite[.-]?(\d+)")
                let suiteRevision = 
                    if suiteMatch.Success then
                        match Int32.TryParse(suiteMatch.Groups.[1].Value) with
                        | true, v -> v
                        | _ -> 0
                    else 0
                core @ [suiteRevision]

    /// Positive when `a` is newer than `b`.
    let private compareVersions (a: string) (b: string) : int =
        let va = parseVersion a
        let vb = parseVersion b
        let length = max va.Length vb.Length

        let pad (list: int list) =
            list @ List.replicate (length - list.Length) 0

        compare (pad va) (pad vb)

    let private readTag (element: JsonElement) : string * bool =
        let getString (name: string) : string =
            match element.TryGetProperty(name) with
            | true, (v: JsonElement) when v.ValueKind = JsonValueKind.String -> v.GetString()
            | _ -> ""

        let getBool (name: string) : bool =
            match element.TryGetProperty(name) with
            | true, (v: JsonElement) -> v.ValueKind = JsonValueKind.True
            | _ -> false

        let tag =
            let t = getString "tag_name"
            if String.IsNullOrWhiteSpace(t) then getString "name" else t

        (tag, getBool "draft" || getBool "prerelease")

    /// Newest published (non-draft, non-prerelease) tag, or "" when none exist.
    let private fetchLatestTag () : Async<string> =
        async {
            let! json = client.GetStringAsync(ReleasesApiUrl) |> Async.AwaitTask
            use document = JsonDocument.Parse(json)
            let root = document.RootElement

            if root.ValueKind <> JsonValueKind.Array then
                return ""
            else
                let mutable best = ""

                for item in root.EnumerateArray() do
                    let (tag, isPreview) = readTag item

                    if not isPreview && not (String.IsNullOrWhiteSpace(tag)) then
                        if best = "" || compareVersions tag best > 0 then best <- tag

                return best
        }

    let check () : Async<UpdateResult> =
        async {
            try
                let! latest = fetchLatestTag ()

                if String.IsNullOrWhiteSpace(latest) then
                    return
                        { HasUpdate = false
                          LatestVersion = CurrentVersion
                          Message = sprintf "You are on the latest version (v%s)." CurrentVersion }
                elif compareVersions latest CurrentVersion > 0 then
                    return
                        { HasUpdate = true
                          LatestVersion = latest.TrimStart('v', 'V')
                          Message = sprintf "Version %s is available." (latest.TrimStart('v', 'V')) }
                else
                    return
                        { HasUpdate = false
                          LatestVersion = CurrentVersion
                          Message = sprintf "You are on the latest version (v%s)." CurrentVersion }
            with ex ->
                return
                    { HasUpdate = false
                      LatestVersion = CurrentVersion
                      Message = "Could not reach the update server. " + ex.Message }
        }

    /// Downloads the full setup from the latest trusted GitHub release and
    /// verifies the release asset's GitHub-published SHA-256 digest.
    let downloadLatestSetup (progress: string -> float -> unit) : Async<string> = async {
        // GitHub's release API supplies the authoritative asset and digest.
        use! metadataResponse = client.GetAsync(sprintf "https://api.github.com/repos/%s/%s/releases/latest" RepoOwner RepoName) |> Async.AwaitTask
        metadataResponse.EnsureSuccessStatusCode() |> ignore
        let! json = metadataResponse.Content.ReadAsStringAsync() |> Async.AwaitTask
        use document = JsonDocument.Parse(json)
        let tag = document.RootElement.GetProperty("tag_name").GetString().TrimStart('v', 'V')
        let expectedName = sprintf "DLSS 5 SUITE Setup v%s.exe" tag
        let githubNormalizedName = expectedName.Replace(" ", ".")
        let asset =
            document.RootElement.GetProperty("assets").EnumerateArray()
            |> Seq.tryFind (fun item ->
                let name = item.GetProperty("name").GetString()
                String.Equals(name, expectedName, StringComparison.OrdinalIgnoreCase)
                || String.Equals(name, githubNormalizedName, StringComparison.OrdinalIgnoreCase))
            |> Option.defaultWith (fun () -> failwith "The latest release has no full setup asset.")
        let name = asset.GetProperty("name").GetString()
        let url = asset.GetProperty("browser_download_url").GetString()
        let uri = Uri(url)
        if uri.Scheme <> Uri.UriSchemeHttps || uri.Host <> "github.com" then
            failwith "GitHub returned an untrusted setup download address."
        let digest =
            match asset.TryGetProperty("digest") with
            | true, value when not (String.IsNullOrWhiteSpace(value.GetString())) -> value.GetString().Replace("sha256:", "").ToUpperInvariant()
            | _ -> failwith "GitHub did not publish a SHA-256 digest for the setup."
        if digest.Length <> 64 then failwith "The setup has an invalid SHA-256 digest."
        let destination = Path.Combine(Path.GetTempPath(), name)
        use! downloadResponse = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead) |> Async.AwaitTask
        downloadResponse.EnsureSuccessStatusCode() |> ignore
        let total = downloadResponse.Content.Headers.ContentLength |> Option.ofNullable |> Option.defaultValue 0L
        use! input = downloadResponse.Content.ReadAsStreamAsync() |> Async.AwaitTask
        use output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None)
        let buffer = Array.zeroCreate<byte> (128 * 1024)
        let mutable doneBytes = 0L
        let mutable reading = true
        while reading do
            let! count = input.ReadAsync(buffer, 0, buffer.Length) |> Async.AwaitTask
            if count = 0 then reading <- false
            else
                do! output.WriteAsync(buffer, 0, count) |> Async.AwaitTask
                doneBytes <- doneBytes + int64 count
                progress (sprintf "Downloading %s" name) (if total > 0 then float doneBytes / float total else 0.0)
        output.Flush(true)
        output.Close()
        output.Close()
        use verify = File.OpenRead(destination)
        use sha = SHA256.Create()
        let actual = sha.ComputeHash(verify) |> Convert.ToHexString
        if not (String.Equals(actual, digest, StringComparison.OrdinalIgnoreCase)) then
            File.Delete(destination)
            failwith "The downloaded setup failed SHA-256 verification."
        return destination
    }
