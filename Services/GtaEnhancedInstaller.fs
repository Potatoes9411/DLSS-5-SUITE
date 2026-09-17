namespace DLSS_5_MANAGER.Services

open System
open System.IO
open System.IO.Compression
open System.Net.Http
open System.Threading.Tasks
open System.Text.RegularExpressions

module GtaEnhancedInstaller =
    type Progress = string -> float -> unit

    let private scriptHookVPage = "https://www.dev-c.com/gtav/scripthookv/"
    let private scriptHookVFallback = "https://www.dev-c.com/files/ScriptHookV_3889.0_1158.13.zip"
    let private scriptHookNet = "https://www.gta5-mods.com/tools/script-hook-v-net-enhanced/download/198638"
    let private directStorageFix = "https://www.gta5-mods.com/scripts/directstoragefix/download/191410"
    let private ultimateAsiLoader = "https://github.com/ThirteenAG/Ultimate-ASI-Loader/releases/download/v9.7.4/Ultimate-ASI-Loader-NoPDB_x64.zip"

    let private expectedScriptHookNet = [| "ScriptHookVDotNet.asi"; "ScriptHookVDotNet2.dll"; "ScriptHookVDotNet3.dll"; "ScriptHookVDotNet.ini"; "MinHook.x64.dll" |]

    let private resolveScriptHookV (client: HttpClient) =
        task {
            try
                let! html = client.GetStringAsync(scriptHookVPage)
                let matchResult = Regex.Match(html, "(?:https?://[^\"' ]+)?/files/ScriptHookV_[^\"' ]+\\.zip", RegexOptions.IgnoreCase)
                if matchResult.Success then
                    return if matchResult.Value.StartsWith("http", StringComparison.OrdinalIgnoreCase) then matchResult.Value else "https://www.dev-c.com" + matchResult.Value
                else return scriptHookVFallback
            with _ ->
                return scriptHookVFallback
        }

    let private resolveGta5ModsFile (client: HttpClient) (landingUrl: string) =
        task {
            let marker = landingUrl.LastIndexOf("/download/", StringComparison.OrdinalIgnoreCase)
            let pageUrl = if marker > 0 then landingUrl.Substring(0, marker) else landingUrl
            let! _ = client.GetStringAsync(pageUrl)
            use request = new HttpRequestMessage(HttpMethod.Get, landingUrl)
            request.Headers.Referrer <- Uri(pageUrl)
            use! response = client.SendAsync(request)
            response.EnsureSuccessStatusCode() |> ignore
            let! html = response.Content.ReadAsStringAsync()
            let matchResult = Regex.Match(html, "https://files\\.gta5-mods\\.com/uploads/[^\"' ]+", RegexOptions.IgnoreCase)
            if matchResult.Success then return matchResult.Value
            else return raise (InvalidDataException("The GTA5-Mods download page did not contain a file URL."))
        }

    let private download (client: HttpClient) (url: string) (referrer: string) (target: string) (label: string) (report: Progress) =
        task {
            use request = new HttpRequestMessage(HttpMethod.Get, url)
            if not (String.IsNullOrWhiteSpace(referrer)) then request.Headers.Referrer <- Uri(referrer)
            use! response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
            response.EnsureSuccessStatusCode() |> ignore
            let contentType = if isNull response.Content.Headers.ContentType then "" else response.Content.Headers.ContentType.MediaType
            let! input = response.Content.ReadAsStreamAsync()
            use input = input
            Directory.CreateDirectory(Path.GetDirectoryName(target)) |> ignore
            use output = File.Create(target)
            let buffer = Array.zeroCreate<byte> 131072
            let mutable total = 0L
            let length = response.Content.Headers.ContentLength
            let mutable reading = true
            while reading do
                let! count = input.ReadAsync(buffer, 0, buffer.Length)
                if count = 0 then reading <- false
                else
                    do! output.WriteAsync(buffer, 0, count)
                    total <- total + int64 count
                    let percent = length |> Option.ofNullable |> Option.map (fun n -> float total * 100.0 / float n) |> Option.defaultValue 0.0
                    report (sprintf "Downloading %s... %.1f MB" label (float total / 1048576.0)) percent
            if total < 1024L || contentType.Equals("text/html", StringComparison.OrdinalIgnoreCase) then
                failwith (sprintf "%s did not return a downloadable archive." label)
        }

    let private extractedFiles (archive: string) (root: string) =
        ZipFile.ExtractToDirectory(archive, root, true)
        Directory.GetFiles(root, "*", SearchOption.AllDirectories)

    let private findFile (files: string[]) (name: string) =
        files |> Array.tryFind (fun path -> String.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase))

    let private backupAndCopyNamed (targetFolder: string) (backupFolder: string) (name: string) (source: string) =
        let target = Path.Combine(targetFolder, name)
        if File.Exists(target) then
            Directory.CreateDirectory(backupFolder) |> ignore
            File.Copy(target, Path.Combine(backupFolder, name), true)
        File.Copy(source, target, true)
        File.SetAttributes(target, File.GetAttributes(target) &&& ~~~FileAttributes.ReadOnly)

    let private backupAndCopy (targetFolder: string) (backupFolder: string) (source: string) =
        backupAndCopyNamed targetFolder backupFolder (Path.GetFileName(source)) source

    let install (targetFolder: string) (report: Progress) =
        task {
            if String.IsNullOrWhiteSpace(targetFolder) || not (Directory.Exists(targetFolder)) then
                failwith "The GTA V Enhanced game folder was not found."
            let cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DLSS5Suite", "Downloads", "GtaEnhanced")
            let work = Path.Combine(cache, "extract-" + Guid.NewGuid().ToString("N"))
            let backup = Path.Combine(cache, "backups", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"))
            Directory.CreateDirectory(work) |> ignore
            use client = new HttpClient()
            client.Timeout <- TimeSpan.FromMinutes(10.0)
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) DLSS5-SUITE/1.2.3")
            try
                let shvZip = Path.Combine(cache, "ScriptHookV.zip")
                let netZip = Path.Combine(cache, "ScriptHookV-Net-Enhanced.zip")
                let dsfZip = Path.Combine(cache, "DirectStorageFix.zip")
                let asiLoaderZip = Path.Combine(cache, "Ultimate-ASI-Loader-x64.zip")
                let! currentScriptHookV = resolveScriptHookV client
                do! download client currentScriptHookV scriptHookVPage shvZip "Script Hook V" report
                let shvFiles = extractedFiles shvZip (Path.Combine(work, "shv"))
                let! currentScriptHookNet = resolveGta5ModsFile client scriptHookNet
                do! download client currentScriptHookNet scriptHookNet netZip "Script Hook V .NET Enhanced" report
                let netFiles = extractedFiles netZip (Path.Combine(work, "net"))
                let! currentDirectStorageFix = resolveGta5ModsFile client directStorageFix
                do! download client currentDirectStorageFix directStorageFix dsfZip "DirectStorageFix" report
                let dsfFiles = extractedFiles dsfZip (Path.Combine(work, "dsf"))
                do! download client ultimateAsiLoader "https://github.com/ThirteenAG/Ultimate-ASI-Loader/releases" asiLoaderZip "Ultimate ASI Loader" report
                let asiLoaderFiles = extractedFiles asiLoaderZip (Path.Combine(work, "asi-loader"))
                let required = [| "ScriptHookV.dll"; "dinput8.dll" |]
                for name in required do
                    match findFile shvFiles name with Some source -> () | None -> failwith ("Script Hook V archive is missing " + name)
                for name in expectedScriptHookNet do
                    match findFile netFiles name with Some source -> () | None -> failwith ("Script Hook V .NET Enhanced archive is missing " + name)
                match findFile dsfFiles "DirectStorageFix.asi" with Some _ -> () | None -> failwith "DirectStorageFix archive is missing DirectStorageFix.asi"
                match findFile asiLoaderFiles "dinput8.dll" with Some _ -> () | None -> failwith "Ultimate ASI Loader archive is missing its x64 loader."
                let! _ = Task.Run(fun () ->
                    let copy name files = findFile files name |> Option.iter (fun source -> backupAndCopy targetFolder backup source)
                    for name in required do copy name shvFiles
                    for name in expectedScriptHookNet do copy name netFiles
                    copy "DirectStorageFix.asi" dsfFiles
                    findFile asiLoaderFiles "dinput8.dll"
                    |> Option.iter (backupAndCopyNamed targetFolder backup "xinput1_4.dll")
                    report "GTA V Enhanced fix installed. Disable BattlEye before launching." 100.0)
                return ()
            finally
                try Directory.Delete(work, true) with _ -> ()
        }
