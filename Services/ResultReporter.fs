namespace DLSS_5_MANAGER.Services

open System
open System.IO
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open Microsoft.Win32
open DLSS_5_MANAGER.Models

/// Sends one anonymous "did DLSS 5 work in this game" record to the DLSS 5
/// SUITE site, which adds everyone's up into the shared results SUITE reads
/// back. No browser, no account. The record names the game's exe, the
/// route, the card and driver - never a path, a user name or a machine id.
///
/// A result the person gives with the buttons is always sent. One SUITE
/// works out by itself after the game closes is sent only when the person
/// has switched that on in Settings.
module ResultReporter =

    [<Literal>]
    let private Endpoint = "https://dlss5.potatoes-dev.com/api/results"

    let private autoFlag () =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DLSS5Suite", "share-automatic-results")

    let isAutoSharing () = File.Exists(autoFlag ())

    let setAutoSharing (on: bool) =
        try
            if on then File.WriteAllText(autoFlag (), "") elif File.Exists(autoFlag ()) then File.Delete(autoFlag ())
        with _ -> ()

    /// The NVIDIA card and its driver as "621.01" - read directly, because on
    /// a hybrid laptop the first adapter listed is the integrated one.
    let private nvidiaCard =
        lazy
            (try
                use cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}")
                cls.GetSubKeyNames()
                |> Array.filter (fun n -> n.Length = 4 && Char.IsDigit n.[0])
                |> Array.tryPick (fun n ->
                    use k = cls.OpenSubKey(n)
                    let desc = string (k.GetValue("DriverDesc", ""))
                    if desc.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) then
                        let raw = (string (k.GetValue("DriverVersion", ""))).Replace(".", "")
                        let driver = if raw.Length >= 5 then raw.Substring(raw.Length - 5, 3) + "." + raw.Substring(raw.Length - 2) else ""
                        Some(desc, driver)
                    else None)
             with _ -> None)

    let private sm (gpu: string) =
        let g = gpu.ToUpperInvariant()
        if Regex.IsMatch(g, @"RTX\s*50\d\d") then "sm_120"
        elif Regex.IsMatch(g, @"RTX\s*40\d\d") || g.Contains "ADA" then "sm_89"
        elif Regex.IsMatch(g, @"RTX\s*30\d\d") then "sm_86"
        elif Regex.IsMatch(g, @"RTX\s*20\d\d") || g.Contains "GTX 16" then "sm_75"
        else ""

    /// SUITE's route as the site names it.
    let private routeOf (m: ModInstaller.InstallManifest) =
        if m.Mode = "optiscaler" then "optiscaler"
        elif m.Neural = "1" then "upstream"
        elif m.Feeder = "1" || (not (isNull m.Files) && m.Files |> Array.exists (fun f -> Path.GetFileName(f.TargetPath).StartsWith("dlss5-feed.addon", StringComparison.OrdinalIgnoreCase))) then "feeder"
        elif List.contains m.Mode [ "dx12"; "dx11"; "dx9"; "emulator" ] then m.Mode
        else ""

    let private http =
        lazy
            (let h = new HttpClient(Timeout = TimeSpan.FromSeconds(15.0))
             h.DefaultRequestHeaders.UserAgent.ParseAdd("DLSS5-SUITE")
             h.DefaultRequestHeaders.Add("X-DLSS5-SUITE", "1")
             h)

    let private clip (n: int) (s: string) =
        let clean = Regex.Replace(s, @"[\p{C}]", "").Trim()
        if clean.Length > n then clean.Substring(0, n).Trim() else clean

    /// Sends the record. Ok on 204; Error with a reason the UI can show.
    let send (game: GameItem) (worked: bool) (byPerson: bool) : Result<unit, string> =
        try
            match ModInstaller.readManifest game, nvidiaCard.Value with
            | None, _ -> Error "Nothing of SUITE's is installed in this game."
            | _, None -> Error "No NVIDIA card was found to report against."
            | Some(m, _), Some(gpu, driver) ->
                let route = routeOf m
                let exe = Path.GetFileName(if String.IsNullOrWhiteSpace m.ExecutablePath then game.TargetExecutablePath else m.ExecutablePath).ToLowerInvariant()
                let api =
                    match (try GameAnalyzer.detectGraphicsApi game.TargetExecutablePath with _ -> "") with
                    | "dx9" -> "DX9"
                    | "dx11" | "dx10" -> "DX11"
                    | "dx12" -> "DX12"
                    | "vulkan" -> "VULKAN"
                    | "opengl" -> "OPENGL"
                    | _ -> ""
                let version = Regex.Match(UpdateChecker.CurrentVersion, @"\d+\.\d+\.\d+").Value
                if route = "" || api = "" || not (Regex.IsMatch(driver, @"^\d{3}\.\d{2}$")) || version = "" then
                    Error "This install cannot be described in a shared result."
                elif not (Regex.IsMatch(exe, @"^[a-z0-9 ._\-()+]{1,80}\.exe$")) then
                    Error "This game's exe name cannot be shared."
                else
                    let up =
                        let s = game.UpscaleStatus
                        if s.Contains "DLSS" then "dlss" elif s.Contains "FSR" then "fsr" elif s.Contains "XeSS" then "xess" else "none"
                    let record = Collections.Generic.Dictionary<string, obj>()
                    record.["v"] <- box 1
                    record.["exe"] <- box exe
                    record.["game"] <- box (clip 80 game.Title)
                    record.["api"] <- box api
                    record.["route"] <- box route
                    let fork = OptiForks.choiceFor game
                    if route = "optiscaler" && fork <> "" then record.["build"] <- box (clip 40 fork)
                    let s = sm gpu
                    if s <> "" then record.["sm"] <- box s
                    record.["gpu"] <- box (clip 40 gpu)
                    record.["driver"] <- box driver
                    record.["tool"] <- box version
                    record.["result"] <- box (if worked then "worked" else "failed")
                    record.["by"] <- box (if byPerson then "person" else "check")
                    record.["up"] <- box up
                    use content = new StringContent(JsonSerializer.Serialize(record), Encoding.UTF8, "application/json")
                    use resp = http.Value.PostAsync(Endpoint, content).Result
                    match int resp.StatusCode with
                    | 204 ->
                        AppLog.info (sprintf "Shared a result for %s (%s, %s)" game.Title route (if worked then "worked" else "failed"))
                        Ok()
                    | 429 -> Error "Already shared a moment ago - thanks, one is enough."
                    | 400 ->
                        let body = resp.Content.ReadAsStringAsync().Result
                        AppLog.warn ("The results service refused a record: " + body)
                        Error "The results service did not accept this result."
                    | code when code >= 500 -> Error "The results service is not answering right now - try later."
                    | code -> Error(sprintf "The results service answered %d." code)
        with ex ->
            AppLog.warn ("Sharing a result failed: " + ex.Message)
            Error "The results service could not be reached."
