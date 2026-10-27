namespace DLSS_5_MANAGER.Services

open System
open System.IO
open System.Net.Http
open System.Text.Json
open DLSS_5_MANAGER.Models

/// What other people found: the public results DLSS 5 Autopilot's users
/// report (one file, rebuilt from their reports, read-only). Its routes are
/// mapped onto SUITE's where they are the same thing; the ones SUITE has no
/// match for are left out rather than shown as if they applied.
module SharedResults =

    [<Literal>]
    let private FeedUrl = "https://raw.githubusercontent.com/Kizzuwatnaa/DLSS5-Autopilot/main/docs/compatibility.json"

    /// SUITE's own shared results, from its site; same file shape.
    [<Literal>]
    let private SuiteFeedUrl = "https://dlss5.potatoes-dev.com/api/compatibility.json"

    /// Below this many reports for a game, nothing is said about it.
    [<Literal>]
    let MinReports = 5

    type RouteTally =
        { Route: string
          Label: string
          Worked: int
          Failed: int }

    type GameResults =
        { Name: string
          Routes: RouteTally list
          /// Driver versions where failures clearly outnumber successes.
          BadDrivers: string list }

    /// Autopilot route -> SUITE route key and label. Routes with no SUITE
    /// counterpart (bridge, standalone, renodx-dlss, remix) are dropped.
    let private suiteRoute (r: string) =
        match r with
        | "optiscaler" -> Some("optiscaler", "OptiScaler")
        | "native" -> Some("dx12", "ReShade + RenoDX")
        | "feeder" -> Some("feeder", "ReShade + Feeder")
        | "upstream" -> Some("upstream", "Neural upstream add-on")
        | "dx11" -> Some("dx11", "ReShade + RenoDX (DX11)")
        | "dx12" -> Some("dx12", "ReShade + RenoDX")
        | "dx9" -> Some("dx9", "ReShade (DX9)")
        | "emulator" -> Some("emulator", "Emulator route")
        | _ -> None

    let private cachePath (name: string) =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DLSS5Suite", "Cache", name)

    let private http =
        lazy
            (let h = new HttpClient(Timeout = TimeSpan.FromSeconds(20.0))
             h.DefaultRequestHeaders.UserAgent.ParseAdd("DLSS5-SUITE")
             h)

    /// The feed, fresh for six hours; the last good copy when offline.
    let private feedFrom (url: string) (cacheName: string) (network: bool) : string =
        let path = cachePath cacheName
        let fresh = File.Exists path && File.GetLastWriteTimeUtc path > DateTime.UtcNow.AddHours(-6.0)
        if fresh || (not network && File.Exists path) then File.ReadAllText path
        elif not network then ""
        else
            try
                let text = http.Value.GetStringAsync(url).Result
                use _ = JsonDocument.Parse(text)
                Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
                File.WriteAllText(path, text)
                text
            with _ -> if File.Exists path then File.ReadAllText path else ""

    let private tally (e: JsonElement) =
        let n (name: string) = match e.TryGetProperty(name) with | true, v when v.ValueKind = JsonValueKind.Number -> v.GetInt32() | _ -> 0
        n "worked", n "failed"

    /// Results for the game's executable, when enough people reported it.
    /// One feed's entry for an exe: (name, route tallies, driver tallies).
    let private entry (text: string) (exe: string) =
        try
            if text = "" then None
            else
                use doc = JsonDocument.Parse(text)
                match doc.RootElement.GetProperty("games").TryGetProperty(exe) with
                | true, g ->
                    let read (prop: string) =
                        match g.TryGetProperty(prop) with
                        | true, xs -> [ for p in xs.EnumerateObject() -> p.Name, tally p.Value ]
                        | _ -> []
                    let name = match g.TryGetProperty("name") with | true, v -> v.GetString() | _ -> ""
                    Some(name, read "routes", read "drivers")
                | _ -> None
        with _ -> None

    /// Autopilot's results and SUITE's own, added up.
    let private lookup (network: bool) (game: GameItem) : GameResults option =
        if String.IsNullOrWhiteSpace game.TargetExecutablePath then None
        else
            let exe = Path.GetFileName(game.TargetExecutablePath).ToLowerInvariant()
            let found =
                [ entry (feedFrom FeedUrl "shared-results.json" network) exe
                  entry (feedFrom SuiteFeedUrl "shared-results-suite.json" network) exe ]
                |> List.choose id
            if found.IsEmpty then None
            else
                let sumUp (pairs: (string * (int * int)) list) =
                    pairs |> List.groupBy fst |> List.map (fun (k, xs) -> k, (xs |> List.sumBy (snd >> fst), xs |> List.sumBy (snd >> snd)))
                let routes =
                    found
                    |> List.collect (fun (_, rs, _) -> rs |> List.choose (fun (r, t) -> suiteRoute r |> Option.map (fun (k, l) -> (k + "|" + l), t)))
                    |> sumUp
                    |> List.map (fun (kl, (w, f)) ->
                        let parts = kl.Split('|')
                        { Route = parts.[0]; Label = parts.[1]; Worked = w; Failed = f })
                let total = routes |> List.sumBy (fun r -> r.Worked + r.Failed)
                if total < MinReports then None
                else
                    let bad =
                        found |> List.collect (fun (_, _, ds) -> ds) |> sumUp
                        |> List.filter (fun (_, (w, f)) -> f >= 5 && f > 2 * w) |> List.map fst
                    let name = found |> List.map (fun (n, _, _) -> n) |> List.tryFind (fun n -> n <> "") |> Option.defaultValue game.Title
                    Some
                        { Name = name
                          Routes = routes |> List.sortByDescending (fun r -> float r.Worked / float (max 1 (r.Worked + r.Failed)), r.Worked)
                          BadDrivers = bad }

    /// Downloads the feed when the saved copy is old - call off the UI thread.
    let forGame (game: GameItem) = lookup true game

    /// The saved copy only; never touches the network.
    let forGameCached (game: GameItem) = lookup false game

    /// One line for the Manage sheet.
    let summary (r: GameResults) =
        let parts = r.Routes |> List.map (fun t -> sprintf "%s worked for %d of %d" t.Label t.Worked (t.Worked + t.Failed))
        let drivers =
            if r.BadDrivers.IsEmpty then ""
            else sprintf " Driver %s failed for most people here." (String.Join(", ", r.BadDrivers))
        sprintf "Other people's results: %s.%s" (String.Join("; ", parts)) drivers

    /// The SUITE route key that worked best for other people, if one clearly did.
    let bestRoute (r: GameResults) =
        r.Routes
        |> List.tryHead
        |> Option.filter (fun t -> t.Worked >= 3 && t.Worked > t.Failed)
        |> Option.map (fun t -> t.Route)
