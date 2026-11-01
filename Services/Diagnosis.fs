namespace DLSS_5_MANAGER.Services

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.RegularExpressions
open DLSS_5_MANAGER.Models

/// "Did it work?" - reads what a DLSS 5 install left behind after the game
/// ran (ReShade's log, the feeder's, OptiScaler's, Windows' own crash record
/// and, while the game is up, the DLLs actually loaded in it) and says in one
/// line whether the neural pass ran, and when it did not, which part stopped
/// and what to do next.
///
/// Every rule here answers from evidence on disk; when there is none it says
/// so rather than guessing. The verdict lines are grouped into stages, and a
/// stage where a different install route could change the answer is what
/// autopilot and the "try another route" offer act on.
module Diagnosis =

    type Level =
        | Ok
        | Warn
        | Bad
        | Info

    type Finding =
        { Level: Level
          Title: string
          Detail: string }

    type Report =
        { Ran: bool
          Verdict: string
          Route: string
          Findings: Finding list
          LogTime: DateTime option
          /// The verdict rests on there being no log at all, so a Windows
          /// fault record from the same window may replace it.
          NeverRan: bool }

    // ------------------------------------------------------------------ text

    let private tail (path: string) (limit: int) =
        try
            if not (File.Exists path) then ""
            else
                use fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ||| FileShare.Delete)
                let start = max 0L (fs.Length - int64 limit)
                fs.Seek(start, SeekOrigin.Begin) |> ignore
                let buf = Array.zeroCreate<byte> (int (fs.Length - start))
                let mutable read = 0
                while read < buf.Length do
                    let n = fs.Read(buf, read, buf.Length - read)
                    if n <= 0 then read <- buf.Length else read <- read + n
                Encoding.UTF8.GetString(buf)
        with _ -> ""

    let private fresh (path: string) (installedAt: DateTime) =
        try File.Exists path && File.GetLastWriteTimeUtc(path) >= installedAt.AddSeconds(-60.0)
        with _ -> false

    let private has (text: string) (needle: string) = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
    let private rx (pattern: string) = Regex(pattern, RegexOptions.Multiline ||| RegexOptions.IgnoreCase)
    let private lastMatch (r: Regex) (text: string) =
        let ms = r.Matches(text)
        if ms.Count = 0 then None else Some ms.[ms.Count - 1]

    /// ReShade appends every launch to one file; only the last counts.
    let private reshadeLastSession (text: string) =
        let i = text.LastIndexOf("Initializing crosire's ReShade", StringComparison.Ordinal)
        if i > 0 then text.Substring(i) else text

    let private feedSession = rx @"^[\d:.]+\s+dlss5-feed\S*\s[^\n]*attached[.;]"

    let private feedLastSession (text: string) =
        match lastMatch feedSession text with
        | Some m when m.Index > 0 -> text.Substring(m.Index)
        | _ -> text

    /// NGX's own log is copied into the feeder's; its lines would otherwise
    /// read as the feeder's own.
    let private stripNgxBlock (text: string) =
        Regex.Replace(
            text,
            @"^[^\n]*===== NGX's own log[^\n]*\n(?:(?![^\n]*\[feed)[^\n]*\n)*?(?:[^\n]*(?i:end of NGX's own log)[^\n]*(?:\n|$)|(?=[^\n]*\[feed)|(?![^\n]*\[feed)[^\n]*\Z)",
            "",
            RegexOptions.Multiline)

    /// OptiScaler stamps lines with a clock only; a step back of more than a
    /// minute is a new run (allowing for midnight).
    let private optiLastRun (text: string) =
        let mutable cut = 0
        let mutable prev = -1
        for m in Regex.Matches(text, @"^\[(\d{2}):(\d{2}):(\d{2})", RegexOptions.Multiline) do
            let s = int m.Groups.[1].Value * 3600 + int m.Groups.[2].Value * 60 + int m.Groups.[3].Value
            if prev >= 0 && s < prev - 60 && not (prev > 86100 && s < 300) then cut <- m.Index
            prev <- s
        text.Substring(cut)

    // ------------------------------------------------------- windows crashes

    type Crash =
        { When: DateTime
          Exe: string
          Module: string
          Code: string }

    /// The newest Application Error for this exe since a moment, from the
    /// Windows event log. Hangs carry no module and are left out.
    let lastCrash (exeName: string) (since: DateTime) : Crash option =
        if String.IsNullOrWhiteSpace exeName || not (Regex.IsMatch(exeName, @"^[\w .\-()+]{1,120}$")) then None
        else
            try
                let script =
                    "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; $ErrorActionPreference='SilentlyContinue';"
                    + "Get-WinEvent -FilterHashtable @{LogName='Application';ProviderName=@('Application Error','.NET Runtime');StartTime=(Get-Date).AddDays(-14)} -MaxEvents 60 | "
                    + "ForEach-Object { $p=$_.Properties; ($_.TimeCreated.ToUniversalTime().ToString('s')) + '|' + $_.ProviderName + '|' + (($p | ForEach-Object { $_.Value }) -join '~') }"
                let psi =
                    ProcessStartInfo(
                        "powershell.exe",
                        sprintf "-NoProfile -NonInteractive -Command \"%s\"" (script.Replace("\"", "\\\"")),
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8)
                use p = Process.Start(psi)
                let out = p.StandardOutput.ReadToEndAsync()
                if not (p.WaitForExit(8000)) then
                    (try p.Kill() with _ -> ())
                    None
                else
                    out.Result.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
                    |> Array.tryPick (fun line ->
                        let parts = line.Split('|', 3)
                        if parts.Length < 3 then None
                        else
                            let fields = parts.[2].Split('~')
                            let isExe (i: int) = i < fields.Length && fields.[i].Equals(exeName, StringComparison.OrdinalIgnoreCase)
                            match DateTime.TryParse(parts.[0], Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.AssumeUniversal ||| Globalization.DateTimeStyles.AdjustToUniversal) with
                            | true, at when (isExe 0 || isExe 1) && at >= since.AddSeconds(-60.0) ->
                                let modName =
                                    [ 3; 4; 5 ]
                                    |> List.tryPick (fun i ->
                                        if i < fields.Length && Regex.IsMatch(fields.[i], @"\.(dll|exe|sys|addon64|addon32)$", RegexOptions.IgnoreCase) then Some fields.[i] else None)
                                match modName with
                                | None -> None
                                | Some m ->
                                    let code =
                                        if fields.Length > 6 && Regex.IsMatch(fields.[6], "^(0x)?[0-9A-Fa-f]{8}$") then
                                            "0x" + fields.[6].Replace("0x", "").ToUpperInvariant()
                                        else ""
                                    Some { When = at; Exe = exeName; Module = m; Code = code }
                            | _ -> None)
            with _ -> None

    /// Who a faulting module belongs to, in words.
    let private madeBy (modName: string) =
        let m = modName.ToLowerInvariant()
        if m = "nvngx.dll_dlssnr.dll" then "OptiScaler"
        elif m.StartsWith "dlss5-feed" then "the DLSS 5 Feeder"
        elif m.StartsWith "renodx" then "the RenoDX DLSS 5 add-on"
        elif m.StartsWith "nvngx.dll.addon" then "the neural-upstream add-on"
        elif m.StartsWith "reshade" then "ReShade"
        elif m.StartsWith "optiscaler" then "OptiScaler"
        elif m.StartsWith "nvngx_dlssg" then "NVIDIA's DLSS Frame Generation"
        elif m.StartsWith "nvngx" || m = "_nvngx.dll" then "NVIDIA's DLSS runtime"
        else ""

    let describeCrash (c: Crash) (ourFiles: string list) : Finding =
        let m = Path.GetFileName(c.Module).ToLowerInvariant()
        let code = if c.Code = "" then "" else sprintf " (%s)" c.Code
        let ours =
            ourFiles |> List.exists (fun f -> Path.GetFileName(f).Equals(m, StringComparison.OrdinalIgnoreCase))
            || [ "reshade"; "dlss5"; "renodx"; "optiscaler" ] |> List.exists m.Contains
            || m.StartsWith "nvngx" || m = "_nvngx.dll"
        if m = c.Exe.ToLowerInvariant() then
            { Level = Bad
              Title = sprintf "Windows recorded %s faulting in its own code%s." c.Exe code
              Detail = sprintf "At %s. The crash is inside the game itself." (c.When.ToLocalTime().ToString("dd MMM HH:mm")) }
        elif ours then
            let who = madeBy m
            { Level = Bad
              Title = sprintf "Windows recorded %s faulting in %s%s." c.Exe c.Module code
              Detail =
                (if who = "" then "" else who + ". ")
                + sprintf "At %s. If frame generation is on in the game, turn it off and test again; then try another route." (c.When.ToLocalTime().ToString("dd MMM HH:mm")) }
        else
            { Level = Bad
              Title = sprintf "Windows recorded %s faulting in %s%s." c.Exe c.Module code
              Detail = "That module is not part of this install. Close overlays (RivaTuner, Discord, GeForce Experience) and test again." }

    // --------------------------------------------------------- live process

    /// What a running copy of the game has loaded from its folder. None when
    /// Windows will not show its DLL list (an anti-cheat or store build).
    let loadedModules (game: GameItem) : (string * string list) option =
        try
            let stem = Path.GetFileNameWithoutExtension(game.TargetExecutablePath)
            Process.GetProcessesByName(stem)
            |> Array.tryPick (fun p ->
                try
                    let mods = [ for m in p.Modules -> m.FileName ]
                    Some(p.ProcessName + ".exe", mods)
                with _ -> None)
        with _ -> None

    // ---------------------------------------------------------------- stages

    /// Which part stopped. A stage in routeStages is one where installing
    /// through another route can change the answer.
    let private stages: (int * string list) list =
        [ 1, [ "The install never finished"; "Nothing is installed"; "OptiScaler is not in the game folder" ]
          2, [ "is missing from the folder - reinstall"; "Files the install wrote are gone"; "OptiScaler.ini is missing"
               "Everything the install put in the game is gone"; "OptiScaler's log is off" ]
          3, [ "Not started since the install"; "Installed after the last run"; "Not run yet, or OptiScaler"
               "It started, and nothing here recorded the session"; "has loaded nothing from this folder"
               "loaded nothing from this folder"; "from another folder - ours is never"
               "It looks as though it ran and nothing this install wrote was loaded"
               "the proxy is reached and ReShade is not"; "ReShade found a Direct3D 9 device"; ", and the install went beside"
               "ReShade is too old for the DLSS 5 add-on" ]
          4, [ "The game refused OptiScaler's swapchain"; "The game's DLSS call was never hooked" ]
          5, [ "Inconclusive - the feed did not get far enough"; "It started and closed during start-up"; "The add-on runs but never produces a frame" ]
          6, [ "Set up correctly, but not switched on yet"; "OptiScaler loaded; neural rendering not switched on"
               "the neural pass is switched off"; "ReShade never gave it an effect runtime"; "the game's own upscaler has to be on" ]
          7, [ "and no log was written."; "Inconclusive - open the overlay"; "Add-ons loaded. Confirm in"; "Add-ons loaded and the switch is on" ]
          8, [ "The feed crashed after starting"; "The crash is in the"; "the add-on crashed creating the feature"
               "It started, then the feed stopped"; "It ran, and then the game crashed"; "the game crashed before the add-on got going"
               "Neural rendering ran; a Present failed later"; "The game crashed with OptiScaler loaded" ]
          9, [ "OptiScaler loaded, but the model refused or failed"; "Neural rendering stopped after it started"
               "DLSS could not start inside this game's process"; "NGX refused to create DLSS" ]
          10, [ "The driver has no DLSS 5 entry point"; "faults inside NGX" ]
          0, [ "Working" ] ]

    let stage (verdict: string) =
        if String.IsNullOrWhiteSpace verdict then -1
        else
            stages
            |> List.tryPick (fun (s, frags) -> if frags |> List.exists (has verdict) then Some s else None)
            |> Option.defaultValue -1

    let private notARoute =
        [ "ReShade found a Direct3D 9 device"; ", and the install went beside"; "Inconclusive - the feed did not get far enough" ]

    /// True when another install route could plausibly do better.
    let routeFailed (verdict: string) =
        not (notARoute |> List.exists (has verdict)) && List.contains (stage verdict) [ 3; 4; 5; 8; 9 ]

    let isWorking (verdict: string) = verdict.StartsWith("Working", StringComparison.Ordinal)

    // ------------------------------------------------------------- analysis

    let private ok t d = { Level = Ok; Title = t; Detail = d }
    let private warn t d = { Level = Warn; Title = t; Detail = d }
    let private bad t d = { Level = Bad; Title = t; Detail = d }
    let private info t d = { Level = Info; Title = t; Detail = d }

    /// OptiScaler route: SUITE installs no ReShade, so only its own log counts.
    let private analyseOptiScaler (folder: string) (installedAt: DateTime) (findings: ResizeArray<Finding>) : bool * string * bool =
        let logPath =
            let direct = Path.Combine(folder, "OptiScaler.log")
            if File.Exists direct then direct
            else
                try
                    Directory.GetFiles(Path.Combine(folder, "Logs"), "*.log")
                    |> Array.sortByDescending File.GetLastWriteTimeUtc
                    |> Array.tryHead
                    |> Option.defaultValue direct
                with _ -> direct
        let iniPath = Path.Combine(folder, "OptiScaler.ini")
        let text = tail logPath 400000
        if text = "" then
            if not (File.Exists iniPath) then false, "OptiScaler.ini is missing - install again.", true
            elif not (Regex.IsMatch(File.ReadAllText iniPath, @"^\s*LogToFile\s*=\s*true\b", RegexOptions.Multiline ||| RegexOptions.IgnoreCase)) then
                false, "OptiScaler's log is off - install again to switch it on, then play once.", true
            else false, "Not run yet, or OptiScaler did not load.", true
        else
            if not (fresh logPath installedAt) then
                findings.Add(warn "OptiScaler.log is older than the install." "It may be from before this install.")
            let run = optiLastRun text
            let lines = run.Split('\n') |> Array.map (fun l -> l.TrimEnd('\r'))
            let nr = lines |> Array.filter (fun l -> has l "DLSS-NR" || has l "dlssnr")
            let failedWords = [ "create failed"; "unavailable"; "did not run"; "not found beside"; "would not load"; "disabling for this session"; "refused" ]
            let failed =
                nr |> Array.filter (fun l -> failedWords |> List.exists (has l) && not (has l "NR diagnostic") && not (has l "falling back"))
            let settings = nr |> Array.filter (fun l -> Regex.IsMatch(l, @"DlssNr\.\w+:"))
            let isRunning (l: string) =
                not (Array.contains l failed) && not (Array.contains l settings)
                && not ([ "skip"; "fail"; "refus"; "abort"; "cancel"; "no motion" ] |> List.exists (has l))
                && (has l "running at"
                    || (has l "DLSS-NR" && Regex.IsMatch(l, @"\d+(?:[.,]\d+)?\s*ms\b"))
                    || Regex.IsMatch(l, @"finished picture:\s*[1-9]\d* frames", RegexOptions.IgnoreCase))
            let running = nr |> Array.filter isRunning
            let failed =
                if running.Length > 0 then failed |> Array.filter (fun l -> not (has l "did not run" && has l "this frame")) else failed
            if has run "Vulkan is creating swapchain" then
                findings.Add(warn "The game is on Vulkan." "Install with the OptiScaler Vulkan API instead.")
            let present = lastMatch (rx @"Original present result:\s*([0-9A-Fa-f]{8})") run |> Option.filter (fun m -> not (m.Groups.[1].Value.StartsWith "0"))
            let lastRunningAt =
                if running.Length = 0 then -1 else run.LastIndexOf(running.[running.Length - 1], StringComparison.Ordinal)
            match present with
            | Some m ->
                findings.Add(bad (sprintf "A Present failed (%s)." m.Groups.[1].Value) "887A0005-7 means the graphics device was lost.")
                if lastRunningAt >= 0 && lastRunningAt < m.Index then
                    true, "Neural rendering ran; a Present failed later - see below.", false
                else true, "The game refused OptiScaler's swapchain - try another route.", false
            | None ->
                let dump = has run "[writeMiniDump] Exception detected"
                if dump && running.Length = 0 then
                    true, "The game crashed with OptiScaler loaded - Streamline caught the exception.", false
                elif running.Length > 0
                     && (failed |> Array.forall (fun f -> run.LastIndexOf(f, StringComparison.Ordinal) < lastRunningAt)) then
                    findings.Add(ok "Neural rendering ran." running.[running.Length - 1])
                    true, "Working.", false
                elif failed.Length > 0 then
                    findings.Add(bad "Neural rendering failed." failed.[failed.Length - 1])
                    if failed |> Array.exists (fun l -> has l "refuse" || has l "unavailable") then
                        findings.Add(info "Neural rendering needs NVIDIA driver 616.56 or newer." "")
                    if running.Length > 0 then true, "Neural rendering stopped after it started.", false
                    else true, "OptiScaler loaded, but the model refused or failed.", false
                elif nr.Length > 0 && nr.Length = settings.Length then
                    true, "Neural rendering never ran - the game's own upscaler has to be on for this route.", false
                elif nr.Length > 0 then
                    true, "Inconclusive - open the overlay (Insert) and read the status under the Neural Rendering checkbox.", false
                else
                    true, "OptiScaler loaded; neural rendering not switched on.", false

    let private addonName = rx "Registered add-on \"([^\"]+)\" v(\\S+)"
    let private apiRefused = rx @"requested API version \((\d+)\) is not supported \((\d+)\)"

    /// The ReShade routes: dx12 (renodx-dlss5), dx11 / dx9 / emulator with
    /// or without the feeder, neural upstream.
    let private analyseReShade (folder: string) (installedAt: DateTime) (feeder: bool) (neural: bool) (ourFiles: string list) (findings: ResizeArray<Finding>) : bool * string * bool =
        // An ASI loader can carry ReShade instead of a proxy DLL: GTA V with
        // NaturalVision Enhanced does, and SUITE's GTA route keeps it and sets
        // the feed aside. An older install record still names the proxy and
        // the feed, so neither counts as missing then.
        let asiReShade = File.Exists(Path.Combine(folder, "ReShade.asi"))
        let feedAside (f: string) =
            let n = Path.GetFileName(f).ToLowerInvariant()
            asiReShade && (n.StartsWith "dlss5-feed." || n = "dlss5_feed.fx")
        let feeder = feeder && not (asiReShade && not (File.Exists(Path.Combine(folder, "dlss5-feed.addon64"))))
        let ourFiles = ourFiles |> List.filter (fun f -> not (feedAside f && not (File.Exists f)))
        let reshadeLog = Path.Combine(folder, "ReShade.log")
        let feedLog = Path.Combine(folder, "dlss5-feed.log")
        let hostLog = Path.Combine(folder, "host64", "dlss5-feed-host.log")
        let mutable rtext = reshadeLastSession (tail reshadeLog 250000)
        let text = if feeder then stripNgxBlock (feedLastSession (tail feedLog 400000)) else ""
        let htext = if feeder then stripNgxBlock (tail hostLog 150000) else ""
        if String.IsNullOrWhiteSpace rtext then rtext <- ""
        let staleReshade = rtext <> "" && not (fresh reshadeLog installedAt)
        if staleReshade && text = "" && htext = "" then rtext <- ""
        if text <> "" && not (fresh feedLog installedAt) then
            findings.Add(warn "The feeder's log predates the current install." "")

        // The files that make the neural pass run. The overlay add-on and
        // the optional shaders are not among them.
        let essential (f: string) =
            let n = Path.GetFileName(f).ToLowerInvariant()
            n.StartsWith "dlss5-feed.addon" || n = "renodx-dlss5.addon64" || n = "nvngx.dll.addon64"
            || n = "nvngx_dlssnr.dll" || n = "dlss5_feed.fx"
        let missing = ourFiles |> List.filter (fun f -> essential f && not (File.Exists f))
        // ReShade itself: the proxy DLL the install put beside the exe.
        let proxyGone =
            ourFiles
            |> List.filter (fun f ->
                let n = Path.GetFileName(f).ToLowerInvariant()
                not asiReShade
                && List.contains n [ "dxgi.dll"; "d3d11.dll"; "d3d12.dll"; "d3d9.dll"; "opengl32.dll" ]
                && Path.GetDirectoryName(f).Equals(folder, StringComparison.OrdinalIgnoreCase))
            |> List.filter (fun f -> not (File.Exists f))
        let goneShare =
            if ourFiles.IsEmpty then 0.0
            else float (ourFiles |> List.filter (fun f -> not (File.Exists f)) |> List.length) / float ourFiles.Length
        if goneShare > 0.8 then
            findings.Add(bad "Almost everything this install put in the game is gone." "A game update or a file check in the launcher removes added files.")
            false, "Everything the install put in the game is gone - a game update or file check removed it; install again.", false
        elif not proxyGone.IsEmpty then
            false, sprintf "ReShade's %s is missing from the folder - reinstall." (Path.GetFileName proxyGone.Head), false
        elif rtext = "" && text = "" && htext = "" then
            if not missing.IsEmpty then
                findings.Add(bad "Files the install wrote are gone." (String.Join(", ", missing |> List.truncate 4 |> List.map Path.GetFileName)))
                false, "Files the install wrote are gone from the folder - restore them and exclude the folder in your antivirus before reinstalling.", false
            elif staleReshade then
                findings.Add(warn "ReShade.log is older than the install." "")
                false, "Installed after the last run - play once and check again.", true
            else
                false, "Not started since the install - run the game once, then check again.", true
        else
            for f in missing do
                findings.Add(bad (sprintf "Add-on missing from the folder: %s." (Path.GetFileName f)) "An antivirus most likely removed it.")

            // A ReShade older than the add-on refuses it outright, and the log
            // says so: NaturalVision Enhanced's ReShade.asi 6.7.3 (API 18)
            // against RenoDX's add-on and the feed (API 20).
            let refused = lastMatch apiRefused rtext
            refused |> Option.iter (fun m ->
                findings.Add(bad (sprintf "ReShade refused an add-on: it needs API %s, this ReShade has %s." m.Groups.[1].Value m.Groups.[2].Value)
                                 "A ReShade 6.8 build with full add-on support is needed."))
            let asi = Path.Combine(folder, "ReShade.asi")
            match (try Some(Version(FileVersionInfo.GetVersionInfo(asi).FileVersion)) with _ -> None) with
            | Some v when File.Exists asi && v < Version(6, 8) ->
                findings.Add(warn (sprintf "ReShade.asi in the game folder is ReShade %O, which cannot load DLSS 5." v)
                                  "NaturalVision Enhanced ships this one; for GTA V Enhanced use SUITE's DLSS 5 Neural Rendering button, which upgrades it.")
            | _ -> ()

            let loaded =
                [ for m in addonName.Matches(rtext) -> m.Groups.[1].Value, m.Groups.[2].Value ]
                |> List.distinct
            for (n, v) in loaded do
                findings.Add(ok (sprintf "ReShade loaded add-on: %s %s" n v) "")
            let names = loaded |> List.map fst
            let renodxOurs =
                ourFiles |> List.exists (fun f -> Path.GetFileName(f).Equals("renodx-dlss5.addon64", StringComparison.OrdinalIgnoreCase))
            let want = if neural then "DLSS5 NR Pre-Upscale" else "DLSS 5 Neural Rendering"
            let isOurs (n: string) =
                if neural then n.Equals(want, StringComparison.OrdinalIgnoreCase) || has n "pre-upscale" || has n "upstream"
                // SUITE's renodx-dlss5.addon64 registers as "RenoDX DLSS".
                else n.Equals(want, StringComparison.OrdinalIgnoreCase) || (renodxOurs && n.Equals("RenoDX DLSS", StringComparison.OrdinalIgnoreCase))
            let ours = names |> List.filter isOurs
            let others = names |> List.filter (fun n -> not (isOurs n) && not (has n "Feed") && not (has n "Bridge") && not (has n "overlay"))
            // RenoDX's "DLSS Fix" (GTA V Enhanced) only corrects the game's own
            // DLSS inputs and runs fine beside ours - not a competing hook.
            let foreign =
                others
                |> List.filter (fun n ->
                    Regex.IsMatch(n, @"dlss|\bngx\b|nvngx|neural|optiscaler", RegexOptions.IgnoreCase)
                    && not (n.Equals("DLSS Fix", StringComparison.OrdinalIgnoreCase)))
            if not names.IsEmpty && ours.IsEmpty && not (names |> List.exists (fun n -> has n "Feed")) then
                findings.Add(bad (sprintf "The '%s' add-on did not load." want) "")
            if not foreign.IsEmpty then
                findings.Add(bad (sprintf "Another DLSS hook was loaded beside ours: %s." (String.Join(", ", foreign))) "Move it out of the game folder and test with ours alone.")
            elif not others.IsEmpty then
                findings.Add(warn (sprintf "Other ReShade add-ons are loaded: %s." (String.Join(", ", others))) "")

            let d3d9Only = has rtext "Direct3DCreate9" && not (has rtext "CreateSwapChain")
            for m in Regex.Matches(rtext, @"Failed to (compile|load) ([^\n]{0,200})") do
                let file = Regex.Match(m.Groups.[2].Value, @"([^\\/'""]+\.(?:fxh?|addon64|addon32|dll))\b")
                let name = if file.Success then file.Groups.[1].Value else m.Groups.[2].Value.Trim()
                let essential = [ "dlss5_feed.fx"; "lumenite_kernel.fx"; "lumenite_quantmotion.fx" ]
                if name.EndsWith(".fx", StringComparison.OrdinalIgnoreCase) && not (List.contains (name.ToLowerInvariant()) essential) then ()
                else findings.Add(bad (sprintf "ReShade failed to %s: %s" m.Groups.[1].Value name) "")

            let joined = text + "\n" + htext + "\n" + rtext
            // Feeder evidence.
            if text <> "" then
                match lastMatch (rx @"DLSS5_Feed\.fx technique (found|MISSING)") text with
                | Some m when m.Groups.[1].Value = "found" -> findings.Add(ok "DLSS5_Feed.fx loaded and its textures were found." "")
                | Some _ -> findings.Add(bad "DLSS5_Feed.fx never loaded." "Tick 'DLSS 5 Feed' in the ReShade overlay (Home).")
                | None -> ()
                match lastMatch (rx @"MV probe[^\n]*?(\d+)% non-zero") text with
                | Some m when m.Groups.[1].Value = "0" -> findings.Add(warn "Motion vectors are all zero." "The motion-vector shader is not producing anything.")
                | Some m -> findings.Add(ok (sprintf "Motion vectors look alive (%s%% non-zero)." m.Groups.[1].Value) "")
                | None -> ()
                if has text "sampled depth is flat" then
                    findings.Add(warn "The depth buffer reads flat." "In ReShade's Add-ons tab, pick the depth buffer that changes as you move.")

            let crashRec = rx @"### CRASH RECORDED ###\s+exception (0x[0-9A-Fa-f]+)[^\n]*?last doing: ([^\n]+)"
            let delivered = Regex.Matches(joined, @"frame (\d+) (?:delivered|evaluated)", RegexOptions.IgnoreCase)
            let ready = Regex.IsMatch(joined, @"feature ready[:\s]", RegexOptions.IgnoreCase)
            let createCrash = Regex.Match(joined, @"CreateFeature raised exception (0x[0-9A-Fa-f]+)")
            let oldCompiler = Regex.IsMatch(joined, @"is too old for Shader Model 5\.1|rejects cs_5_1|unrecognized compiler target 'cs_5_1'")
            if has joined "SuperSampling.Available=0" then findings.Add(bad "The driver reports DLSS as NOT available." "")
            let hookFail = Regex.Match(rtext, @"vtable::Hook\(Failed to find (NVSDK_NGX_\w+_EvaluateFeature\w*)")
            let verdict =
                if refused.IsSome && ours.IsEmpty then
                    "This game's ReShade is too old for the DLSS 5 add-on - it refused to load it. Replace it with ReShade 6.8 (full add-on build)."
                elif Regex.IsMatch(joined, @"evaluate raised 0xC0000005") && Regex.IsMatch(joined, @"D3D12Core\.dll\s*<-\s*nvngx_dlssnr\.dll\s*<-\s*_nvngx\.dll") then
                    "Driver 616.64+ faults inside NGX with this add-on - roll the driver back to 616.56, or try the OptiScaler route."
                else
                    match lastMatch crashRec joined with
                    | Some m ->
                        let chain =
                            lastMatch (rx @"crash stack, by module \(innermost first\):\s*([^\n]+)") joined
                            |> Option.map (fun c -> c.Groups.[1].Value.Split("<-") |> Array.map (fun s -> s.Trim().ToLowerInvariant()))
                            |> Option.defaultValue [||]
                        findings.Add(bad (sprintf "The feeder recorded a crash (%s)." m.Groups.[1].Value) ("Last doing: " + m.Groups.[2].Value))
                        let innermost = if chain.Length > 0 then chain.[0] else ""
                        if [ "nvngx_dlssnr.dll"; "_nvngx.dll"; "nvngx.dll"; "nvngx_dlss.dll"; "d3d12core.dll" ] |> List.contains innermost then
                            "The crash is in the graphics runtime, not in the feed - try another route."
                        elif innermost <> "" && not (chain |> Array.exists (fun c -> c.Contains "dlss5" || c.Contains "renodx" || c.Contains "addon")) then
                            "The crash is in the game's own code - no add-on in the stack."
                        else "The feed crashed after starting - a feeder bug; try another route."
                    | None ->
                        if hookFail.Success && delivered.Count = 0 && not ready then
                            "The driver has no DLSS 5 entry point - update the graphics driver to 616.56 or newer."
                        elif createCrash.Success then
                            findings.Add(bad (sprintf "Creating the DLSS feature crashed (%s)." createCrash.Groups.[1].Value) "")
                            "DLSS never started - the add-on crashed creating the feature."
                        elif delivered.Count > 0 then
                            let last = delivered.[delivered.Count - 1].Groups.[1].Value
                            findings.Add(ok (sprintf "Frames are being processed (%d logged, last was frame %s)." delivered.Count last) "")
                            let perf = lastMatch (rx @"(\d+) frames: feed CPU ([\d.]+) ms/frame[^\n]*?\(([\d.]+) fps\)") joined
                            perf |> Option.iter (fun p ->
                                findings.Add(info (sprintf "Running at %s fps; the feed costs %s ms of CPU a frame." p.Groups.[3].Value p.Groups.[2].Value) ""))
                            let after = joined.Substring(delivered.[delivered.Count - 1].Index)
                            let stop = Regex.Match(after, @"stopped: ([^\n]+)")
                            if oldCompiler then "Frames flow, but neural rendering is silently doing nothing - old d3dcompiler_47.dll in the game folder."
                            elif stop.Success then
                                findings.Add(bad (sprintf "The feed stopped after frame %s: %s" last (stop.Groups.[1].Value.Split(" -- ").[0])) "")
                                "It started, then the feed stopped - see why below."
                            else "Working."
                        elif ready then "Set up correctly, but not switched on yet."
                        elif oldCompiler then "The neural pass cannot compile - old d3dcompiler_47.dll in the game folder."
                        elif not feeder then
                            // The dx12 route logs no frames; ReShade's own
                            // lines are all there is.
                            let panel = if neural then "'NR Pre-Upscale' tab" else "DLSS 5 Neural Rendering panel"
                            let iniSwitch =
                                try
                                    let ini = File.ReadAllText(Path.Combine(folder, "ReShade.ini"))
                                    let m = Regex.Match(ini, @"\[RenoDX\.DLSS5\][^\[]*?^\s*NeuralUplift\s*=\s*(\S*)", RegexOptions.Multiline ||| RegexOptions.IgnoreCase ||| RegexOptions.Singleline)
                                    if m.Success then m.Groups.[1].Value.ToLowerInvariant() else ""
                                with _ -> ""
                            if ours.IsEmpty then "Inconclusive - the feed did not get far enough to tell."
                            elif [ "0"; "false"; "off"; "no" ] |> List.contains iniSwitch then
                                findings.Add(bad "The neural pass is switched OFF in the add-on itself." "")
                                "The add-ons are loaded and the neural pass is switched off in the add-on itself - turn it on in the overlay (Home)."
                            elif iniSwitch <> "" then
                                sprintf "Add-ons loaded and the switch is on. This route logs no frames, so the %s is the only live picture." panel
                            else sprintf "Add-ons loaded. Confirm in the %s - this route does not log frames." panel
                        elif d3d9Only then
                            "ReShade found a Direct3D 9 device, not the D3D11/12 this install set up - install with the DX9 route."
                        elif Regex.IsMatch(rtext, "initializing crosire", RegexOptions.IgnoreCase)
                             && not ([ "registered add-on"; "initialized runtime"; "swap chain"; "swapchain"; "compiled"; "exiting"; "effect"; "created" ] |> List.exists (has rtext)) then
                            "It started and closed during start-up - ReShade attached and nothing else got to run."
                        else "Inconclusive - the feed did not get far enough to tell."
            true, verdict, false

    /// Reads everything this game's install left behind and answers.
    let analyse (game: GameItem) : Report =
        let findings = ResizeArray<Finding>()
        match ModInstaller.readManifest game with
        | None ->
            { Ran = false
              Verdict = "Nothing is installed in this game - install first."
              Route = ""
              Findings = [ bad "SUITE has no install record for this game." "" ]
              LogTime = None
              NeverRan = true }
        | Some(m, installedAt) ->
            let folder =
                if not (String.IsNullOrWhiteSpace m.ExecutablePath) then Path.GetDirectoryName(m.ExecutablePath)
                else
                    try Path.GetDirectoryName(game.TargetExecutablePath) with _ -> game.InstallDirectory
            let route = if isNull m.Mode then "" else m.Mode
            let ourFiles = if isNull m.Files then [] else [ for f in m.Files -> f.TargetPath ]
            let ran, verdict, neverRan =
                if route = "optiscaler" then analyseOptiScaler folder installedAt findings
                else
                    // Records from before the Feeder flag existed leave it
                    // empty; the files say whether the feeder went in.
                    let feeder =
                        m.Feeder = "1"
                        || ourFiles |> List.exists (fun f -> Path.GetFileName(f).StartsWith("dlss5-feed.addon", StringComparison.OrdinalIgnoreCase))
                    analyseReShade folder installedAt feeder (m.Neural = "1" || m.Api = "neural") ourFiles findings
            let logTime =
                [ "dlss5-feed.log"; "ReShade.log"; "OptiScaler.log" ]
                |> List.map (fun n -> Path.Combine(folder, n))
                |> List.filter File.Exists
                |> List.map File.GetLastWriteTime
                |> List.sortDescending
                |> List.tryHead

            // While the game is up, the DLL list says more than any log.
            let mutable ran, verdict, neverRan = ran, verdict, neverRan
            if neverRan then
                match loadedModules game with
                | Some(exe, mods) ->
                    let names = mods |> List.map (fun p -> Path.GetFileName(p).ToLowerInvariant()) |> set
                    let wanted =
                        ourFiles
                        |> List.filter (fun f -> let e = Path.GetExtension(f).ToLowerInvariant() in e = ".dll" || e = ".addon64" || e = ".addon32")
                        |> List.map (fun f -> Path.GetFileName(f).ToLowerInvariant())
                    let fromHere = wanted |> List.filter names.Contains
                    ran <- true
                    neverRan <- false
                    verdict <-
                        if fromHere.IsEmpty then sprintf "%s is running and has loaded nothing from this folder - try another route." exe
                        else sprintf "Loaded into %s, and no log was written - the proxy is reached and ReShade is not initialising." exe
                | None -> ()

            // A Windows fault record from after the install.
            let exe = Path.GetFileName(if String.IsNullOrWhiteSpace m.ExecutablePath then game.TargetExecutablePath else m.ExecutablePath)
            match lastCrash exe installedAt with
            | Some c ->
                findings.Add(describeCrash c ourFiles)
                let inMod = sprintf " in %s" c.Module
                if isWorking verdict then verdict <- sprintf "It ran, and then the game crashed - Windows recorded the fault%s." inMod
                elif neverRan then
                    verdict <- sprintf "It started, and nothing here recorded the session - Windows recorded the fault%s." inMod
                    ran <- true
                    neverRan <- false
                elif verdict.EndsWith("play once and check again.") then
                    verdict <- sprintf "ReShade loaded, and the game crashed before the add-on got going - Windows recorded the fault%s." inMod
            | None -> ()

            { Ran = ran
              Verdict = verdict
              Route = route
              Findings = List.ofSeq findings
              LogTime = logTime
              NeverRan = neverRan }
