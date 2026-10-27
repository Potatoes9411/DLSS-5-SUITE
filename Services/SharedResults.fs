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
        | _ -> None

    let private cachePath () =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DLSS5Suite", "Cache", "shared-results.json")

    let private http =
        lazy
            (let h = new HttpClient(Timeout = TimeSpan.FromSeconds(20.0))
             h.DefaultRequestHeaders.UserAgent.ParseAdd("DLSS5-SUITE")
             h)

    /// The feed, fresh for six hours; the last good copy when offline.
    let private feed (network: bool) : string =
        let path = cachePath ()
        let fresh = File.Exists path && File.GetLastWriteTimeUtc path > DateTime.UtcNow.AddHours(-6.0)
        if fresh || (not network && File.Exists path) then File.ReadAllText path
        elif not network then ""
        else
            try
                let text = http.Value.GetStringAsync(FeedUrl).Result
                use _ = JsonDocument.Parse(text)
                Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
                File.WriteAllText(path, text)
                text
            with _ -> if File.Exists path then File.ReadAllText path else ""

    let private tally (e: JsonElement) =
        let n (name: string) = match e.TryGetProperty(name) with | true, v when v.ValueKind = JsonValueKind.Number -> v.GetInt32() | _ -> 0
        n "worked", n "failed"

    /// Results for the game's executable, when enough people reported it.
    let private lookup (network: bool) (game: GameItem) : GameResults option =
        try
            let text = feed network
            if text = "" || String.IsNullOrWhiteSpace game.TargetExecutablePath then None
            else
                use doc = JsonDocument.Parse(text)
                let exe = Path.GetFileName(game.TargetExecutablePath).ToLowerInvariant()
                match doc.RootElement.GetProperty("games").TryGetProperty(exe) with
                | true, g ->
                    let routes =
                        match g.TryGetProperty("routes") with
                        | true, rs ->
                            [ for p in rs.EnumerateObject() do
                                  match suiteRoute p.Name with
                                  | Some(key, label) ->
                                      let w, f = tally p.Value
                                      yield { Route = key; Label = label; Worked = w; Failed = f }
                                  | None -> () ]
                        | _ -> []
                    let total = routes |> List.sumBy (fun r -> r.Worked + r.Failed)
                    if total < MinReports then None
                    else
                        let bad =
                            match g.TryGetProperty("drivers") with
                            | true, ds ->
                                [ for p in ds.EnumerateObject() do
                                      let w, f = tally p.Value
                                      if f >= 5 && f > 2 * w then yield p.Name ]
                            | _ -> []
                        let name = match g.TryGetProperty("name") with | true, v -> v.GetString() | _ -> game.Title
                        Some
                            { Name = name
                              Routes = routes |> List.sortByDescending (fun r -> float r.Worked / float (max 1 (r.Worked + r.Failed)), r.Worked)
                              BadDrivers = bad }
                | _ -> None
        with _ -> None

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
