namespace DLSS_5_MANAGER.Services

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading

/// Which NVIDIA DLSS Frame Generation (nvngx_dlssg.dll) the two engines that
/// run it through NVIDIA Streamline use: the Screen Engine's "NVIDIA frame
/// gen" and Motion Forge's DLSS MFG. One choice per engine, remembered.
///
/// - Bundled: the one shipped beside Streamline in SUITE's engine\ folder.
/// - Own file: an nvngx_dlssg.dll the person picks (from a game, say). It must
///   carry an intact Authenticode signature by NVIDIA Corporation; a copy is
///   kept in SUITE's data folder and the picked file is only read.
/// - Release: any DLSS FG build the Upscaler Vault's catalog lists (NVIDIA's
///   own DLSS SDK tags, and UPSCALE IT's list), downloaded on request into the
///   Vault's library and checked to be NVIDIA's there.
///
/// No NVIDIA file is changed, and nothing in engine\ is written. Streamline
/// takes its plugins - and NGX the nvngx_dlssg.dll beside them - from the
/// folder it is told (sl::Preferences::pathsToPlugins). So for anything but
/// the bundled build, SUITE puts copies of the five Streamline files and the
/// chosen nvngx_dlssg.dll in a folder of their own under its data folder,
/// and hands the engine that folder: Motion Forge's sl_dir, the Screen
/// Engine's --streamline-path. Both engines check NVIDIA's signature again
/// before they load anything from it.
module DlssgVersions =

    [<Literal>]
    let DlssgFile = "nvngx_dlssg.dll"

    /// Streamline 2.14.16's files the engines load, in the engines' order.
    let streamlinePlugins = [ "sl.interposer.dll"; "sl.common.dll"; "sl.dlss_g.dll"; "sl.reflex.dll"; "sl.pcl.dll" ]

    let streamlineFiles = streamlinePlugins @ [ DlssgFile ]

    type EngineKind =
        | ScreenEngine
        | MotionForge

    let engineKey =
        function
        | ScreenEngine -> "screen"
        | MotionForge -> "motion"

    type Choice =
        | Bundled
        /// The checked copy of the person's own file in SUITE's data folder.
        | OwnFile of version: string * path: string
        /// A catalog version, by its version text.
        | Release of version: string

    // ------------------------------------------------------------ pure logic

    /// How a choice is written down: "bundled", "own|<version>|<path>",
    /// "release|<version>".
    let encode =
        function
        | Bundled -> "bundled"
        | OwnFile(v, p) -> sprintf "own|%s|%s" v p
        | Release v -> sprintf "release|%s" v

    /// Anything unreadable is the bundled build. A version must be a clean
    /// version text (it ends up in a folder name).
    let decode (text: string) =
        let parts = (defaultArg (Option.ofObj text) "").Split('|', 3)
        match parts with
        | [| "own"; v; p |] when p.Trim() <> "" ->
            match UpscalerVault.cleanVersion v with
            | Some v -> OwnFile(v, p)
            | None -> Bundled
        | [| "release"; v |] ->
            match UpscalerVault.cleanVersion v with
            | Some v -> Release v
            | None -> Bundled
        | _ -> Bundled

    /// The version a choice runs, given the bundled build's.
    let versionOf (bundledVersion: string) =
        function
        | Bundled -> bundledVersion
        | OwnFile(v, _) -> v
        | Release v -> v

    /// An NVIDIA DLL is not necessarily frame generation: its version
    /// resource has to say DLSS-G (NVIDIA names it "NVIDIA DLSS-G ..." in
    /// every release since 3.1).
    let looksLikeDlssg (productName: string) (description: string) =
        let says (s: string) =
            let s = (defaultArg (Option.ofObj s) "").ToUpperInvariant()
            s.Contains("DLSS-G") || s.Contains("DLSSG") || s.Contains("FRAME GENERATION")
        says productName || says description

    /// One row of a picker.
    type Entry =
        { Label: string
          Choice: Choice
          /// The catalog offer to download first, for a release not yet in
          /// the library; None when the file is already on this PC.
          Download: UpscalerVault.Offer option }

    let private sourceText (o: UpscalerVault.Offer) =
        match o.Source with
        | UpscalerVault.Library -> "downloaded"
        | UpscalerVault.NvidiaSdk -> "NVIDIA SDK · download"
        | UpscalerVault.Catalog -> "UPSCALE IT list · download"

    /// The picker's rows: the bundled build, the person's own files, then
    /// every DLSS FG version on offer, newest first, one row per version (the
    /// library's checked copy before NVIDIA's own before the Worker's). The
    /// bundled version is not offered a second time as a release.
    let entries (bundledVersion: string) (own: (string * string) list) (offers: UpscalerVault.Offer list) : Entry list =
        let bundled =
            { Label = (if bundledVersion = "" then "Bundled with SUITE" else sprintf "Bundled · %s (with SUITE)" bundledVersion)
              Choice = Bundled
              Download = None }
        let owned =
            own
            |> List.distinctBy (fun (_, p) -> p.ToLowerInvariant())
            |> List.sortByDescending (fun (v, _) -> UpscalerVault.versionKey v)
            |> List.map (fun (v, p) -> { Label = sprintf "My own file · %s" v; Choice = OwnFile(v, p); Download = None })
        let releases =
            UpscalerVault.offersFor DlssgFile "dlssg" offers
            |> List.filter (fun o -> not (bundledVersion <> "" && UpscalerVault.sameVersion o.Version bundledVersion))
            |> List.map (fun o ->
                { Label = sprintf "%s · %s" o.Version (sourceText o)
                  Choice = Release o.Version
                  Download = (if o.Source = UpscalerVault.Library then None else Some o) })
        bundled :: owned @ releases

    /// Where a choice sits in a list of rows (a release by version, whatever
    /// its source); 0, the bundled build, when it is not there.
    let indexOf (choice: Choice) (rows: Entry list) =
        let same (a: Choice) (b: Choice) =
            match a, b with
            | Release x, Release y -> UpscalerVault.sameVersion x y
            | OwnFile(_, p), OwnFile(_, q) -> String.Equals(p, q, StringComparison.OrdinalIgnoreCase)
            | x, y -> x = y
        rows |> List.tryFindIndex (fun r -> same r.Choice choice) |> Option.defaultValue 0

    /// A staging folder's name: the DLSS-G version, then a hash of the DLL and
    /// of the Streamline build it goes with. The same pair always lands in
    /// the same folder, so a folder an engine has open is never rebuilt.
    let stagingName (version: string) (dlssgSha256: string) (streamlineStamp: string) =
        let stamp =
            Convert.ToHexString(SHA256.HashData(Text.Encoding.UTF8.GetBytes(dlssgSha256.ToLowerInvariant() + "|" + streamlineStamp))).ToLowerInvariant()
        let v = Regex.Replace(defaultArg (Option.ofObj version) "", @"[^0-9A-Za-z.\-_+]", "_")
        sprintf "%s-%s" (if v = "" then "dlssg" else v) (stamp.Substring(0, 16))

    /// The staging folders that may go: every one but those still chosen.
    let staleStaging (present: string list) (keep: string list) =
        let kept = keep |> List.map (fun k -> k.ToLowerInvariant()) |> set
        present |> List.filter (fun p -> not (kept.Contains(p.ToLowerInvariant())))

    // ----------------------------------------------------------------- disk

    let bundledDir () = Path.Combine(AppContext.BaseDirectory, "engine")
    let bundledPath () = Path.Combine(bundledDir (), DlssgFile)
    let bundledVersion () = if File.Exists(bundledPath ()) then DlssUpdater.fileVersion (bundledPath ()) else ""
    let hasBundledStreamline () = streamlineFiles |> List.forall (fun f -> File.Exists(Path.Combine(bundledDir (), f)))

    let private root () = Path.Combine(AppLog.dataFolder (), "dlssg")
    let ownRoot () = Path.Combine(root (), "own")
    let stagingRoot () = Path.Combine(root (), "run")
    let private choicePath () = Path.Combine(root (), "choice.json")

    let private dlssgFamily () =
        match UpscalerVault.familyByKey "dlssg" with
        | Some f -> f
        | None -> failwith "the Upscaler Vault has no DLSS Frame Generation family"

    let private sha256 (path: string) =
        use fs = File.OpenRead(path)
        Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant()

    /// NVIDIA's signature and a DLSS-G version resource. Ok carries the
    /// file's version.
    let check (path: string) : Result<string, string> =
        if not (File.Exists path) then Error "the file is not there"
        else
            match UpscalerVault.verify (dlssgFamily ()) path with
            | Error why -> Error why
            | Ok _ ->
                let info = FileVersionInfo.GetVersionInfo(path)
                if not (looksLikeDlssg info.ProductName info.FileDescription) then
                    Error(sprintf "it is NVIDIA's \"%s\", not DLSS Frame Generation" (if String.IsNullOrWhiteSpace info.ProductName then "unnamed file" else info.ProductName))
                else
                    match DlssUpdater.fileVersion path with
                    | "" -> Error "it has no version number"
                    | v -> Ok v

    // ------------------------------------------------------------- choices

    let private readChoices () : Map<string, string> =
        try
            if File.Exists(choicePath ()) then
                JsonSerializer.Deserialize<Collections.Generic.Dictionary<string, string>>(File.ReadAllText(choicePath ()))
                |> Seq.map (fun kv -> kv.Key, kv.Value)
                |> Map.ofSeq
            else Map.empty
        with _ -> Map.empty

    let load (engine: EngineKind) =
        readChoices () |> Map.tryFind (engineKey engine) |> Option.map decode |> Option.defaultValue Bundled

    let save (engine: EngineKind) (choice: Choice) =
        try
            Directory.CreateDirectory(root ()) |> ignore
            let all = readChoices () |> Map.add (engineKey engine) (encode choice)
            let tmp = choicePath () + ".tmp"
            File.WriteAllText(tmp, JsonSerializer.Serialize(dict (Map.toSeq all), JsonSerializerOptions(WriteIndented = true)))
            File.Move(tmp, choicePath (), true)
        with ex -> AppLog.warn ("DLSS-G version: the choice could not be saved: " + ex.Message)

    /// The person's own files already copied in, as (version, path).
    let ownFiles () : (string * string) list =
        try
            if not (Directory.Exists(ownRoot ())) then []
            else
                [ for d in Directory.EnumerateDirectories(ownRoot ()) do
                      let p = Path.Combine(d, DlssgFile)
                      if File.Exists p then
                          match DlssUpdater.fileVersion p with
                          | "" -> ()
                          | v -> yield v, p ]
        with _ -> []

    /// Copies the picked file into SUITE's data folder - the picked file is
    /// only read - and checks the copy, which is what is used.
    let importOwn (source: string) : Result<Choice * string, string> =
        try
            if not (File.Exists source) then Error "That file is not there."
            else
                Directory.CreateDirectory(ownRoot ()) |> ignore
                let incoming = Path.Combine(ownRoot (), Guid.NewGuid().ToString("N") + ".incoming")
                File.Copy(source, incoming, false)
                match check incoming with
                | Error why ->
                    (try File.Delete incoming with _ -> ())
                    Error(sprintf "%s was refused: %s." (Path.GetFileName source) why)
                | Ok version ->
                    let folder = Path.Combine(ownRoot (), stagingName version (sha256 incoming) "own")
                    Directory.CreateDirectory folder |> ignore
                    let target = Path.Combine(folder, DlssgFile)
                    File.Move(incoming, target, true)
                    Ok(OwnFile(version, target), sprintf "Your nvngx_dlssg.dll %s is signed by NVIDIA and kept in SUITE's data folder." version)
        with ex -> Error ex.Message

    /// Removes one of the person's copies (never the file they picked).
    let removeOwn (path: string) =
        try
            let dir = Path.GetDirectoryName(Path.GetFullPath path)
            if Path.GetFullPath(Path.GetDirectoryName dir).TrimEnd('\\').Equals(Path.GetFullPath(ownRoot ()).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) then
                Directory.Delete(dir, true)
        with _ -> ()

    /// The library's checked copy of a release, when it was downloaded.
    let releasePath (version: string) =
        UpscalerVault.libraryOffers ()
        |> List.tryFind (fun o -> o.FamilyKey = "dlssg" && UpscalerVault.sameVersion o.Version version
                                  && o.FileName.Equals(DlssgFile, StringComparison.OrdinalIgnoreCase))
        |> Option.map (fun o -> o.LocalPath)

    /// Downloads a catalog release into the Vault's library and checks it.
    let download (offer: UpscalerVault.Offer) (progress: string -> float -> unit) (cancel: CancellationToken) : Async<Result<Choice * string, string>> =
        async {
            try
                let! path, _ = UpscalerVault.prepare offer DlssgFile progress cancel
                match check path with
                | Ok v -> return Ok(Release offer.Version, sprintf "DLSS Frame Generation %s downloaded and checked: signed by NVIDIA." v)
                | Error why ->
                    let _ = (try File.Delete path; true with _ -> false)
                    return Error(sprintf "DLSS Frame Generation %s was refused: %s." offer.Version why)
            with ex -> return Error ex.Message
        }

    /// Every DLSS FG version the Vault's catalog has, and its status line.
    let catalog (force: bool) : Async<UpscalerVault.Offer list * string> =
        async {
            let! r = UpscalerVault.catalog force
            return r.Offers |> List.filter (fun o -> o.FamilyKey = "dlssg"), r.Status
        }

    // -------------------------------------------------------------- staging

    let private streamlineStamp (dir: string) =
        streamlinePlugins
        |> List.map (fun f ->
            let p = Path.Combine(dir, f)
            sprintf "%s:%d:%s" f (FileInfo(p).Length) (DlssUpdater.fileVersion p))
        |> String.concat ";"

    let private complete (folder: string) (sources: (string * string) list) =
        sources
        |> List.forall (fun (name, src) ->
            let p = Path.Combine(folder, name)
            File.Exists p && FileInfo(p).Length = FileInfo(src).Length)

    /// A folder holding the five Streamline files and this nvngx_dlssg.dll,
    /// built once per pair and kept while either engine has it chosen.
    let stage (dlssgPath: string) : Result<string, string> =
        try
            let sl = bundledDir ()
            match streamlinePlugins |> List.tryFind (fun f -> not (File.Exists(Path.Combine(sl, f)))) with
            | Some missing -> Error(sprintf "NVIDIA Streamline is incomplete in SUITE's engine folder (%s is missing)" missing)
            | None ->
                match check dlssgPath with
                | Error why -> Error(sprintf "the chosen nvngx_dlssg.dll was refused: %s" why)
                | Ok version ->
                    let name = stagingName version (sha256 dlssgPath) (streamlineStamp sl)
                    let folder = Path.Combine(stagingRoot (), name)
                    let sources = (streamlinePlugins |> List.map (fun f -> f, Path.Combine(sl, f))) @ [ DlssgFile, dlssgPath ]
                    if Directory.Exists folder && complete folder sources then Ok folder
                    else
                        let part = folder + ".part-" + Guid.NewGuid().ToString("N").Substring(0, 8)
                        Directory.CreateDirectory part |> ignore
                        for n, src in sources do File.Copy(src, Path.Combine(part, n), true)
                        if Directory.Exists folder then (try Directory.Delete(folder, true) with _ -> ())
                        if Directory.Exists folder then
                            (try Directory.Delete(part, true) with _ -> ())
                            if complete folder sources then Ok folder else Error "an older copy of these files is still in use - stop the other engine and try again"
                        else
                            Directory.Move(part, folder)
                            Ok folder
        with ex -> Error ex.Message

    /// The file a choice runs; None for the bundled build.
    let fileOf (choice: Choice) : Result<string option, string> =
        match choice with
        | Bundled -> Ok None
        | OwnFile(v, p) -> if File.Exists p then Ok(Some p) else Error(sprintf "your nvngx_dlssg.dll %s is no longer in SUITE's data folder" v)
        | Release v ->
            match releasePath v with
            | Some p -> Ok(Some p)
            | None -> Error(sprintf "DLSS Frame Generation %s is not downloaded" v)

    /// Staging folders no engine has chosen, removed (one in use stays: its
    /// files are open).
    let private tidy () =
        try
            if Directory.Exists(stagingRoot ()) then
                let keep =
                    [ ScreenEngine; MotionForge ]
                    |> List.choose (fun e ->
                        match fileOf (load e) with
                        | Ok(Some p) ->
                            match check p with
                            | Ok v ->
                                (try Some(stagingName v (sha256 p) (streamlineStamp (bundledDir ()))) with _ -> None)
                            | Error _ -> None
                        | _ -> None)
                let present = Directory.EnumerateDirectories(stagingRoot ()) |> Seq.map Path.GetFileName |> List.ofSeq
                for stale in staleStaging present keep do
                    try Directory.Delete(Path.Combine(stagingRoot (), stale), true) with _ -> ()
        with _ -> ()

    /// The Streamline folder an engine is to be given now, and what to tell
    /// the person when the choice could not be honoured (the bundled build
    /// is used then; frame generation still runs).
    let streamlineDirFor (engine: EngineKind) : string * string =
        let choice = load engine
        let result =
            match fileOf choice with
            | Ok None -> Ok(bundledDir ())
            | Ok(Some p) -> stage p
            | Error why -> Error why
        tidy ()
        match result with
        | Ok dir ->
            if choice <> Bundled then AppLog.info (sprintf "DLSS-G %s for the %s engine from %s" (versionOf "" choice) (engineKey engine) dir)
            dir, ""
        | Error why ->
            AppLog.warn (sprintf "DLSS-G version for the %s engine: %s; the bundled one is used" (engineKey engine) why)
            bundledDir (), sprintf "The chosen DLSS Frame Generation could not be used (%s), so the one bundled with SUITE runs." why
