namespace DLSS_5_MANAGER.Services

open System
open System.IO
open System.Text
open System.Text.Json
open DLSS_5_MANAGER.Models

/// Frame generation on RTX 20 / 30 from files the person already has. These
/// projects cannot be downloaded for them, so SUITE takes the files they
/// pick, works out which project they are from by the bytes inside, keeps a
/// copy, and puts them where that project needs them - and takes them out
/// again on Remove.
///
/// - dlssg_for_sm86 (sdli1995): a proxy DLL (version.dll, or another name
///   the person picked it under) and dlssg_sm86.ini beside the game.
/// - DLSS Enabler: its version.dll becomes dlss-enabler-headless.dll inside
///   OptiScaler's folder, so it needs the OptiScaler route installed.
module OwnFrameGen =

    type Recipe =
        | Sm86
        | Enabler

    let recipeLabel r =
        match r with
        | Sm86 -> "dlssg_for_sm86"
        | Enabler -> "DLSS Enabler"

    let private store () =
        let d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DLSS5Suite", "own-fg")
        Directory.CreateDirectory(d) |> ignore
        d

    let private contains (bytes: byte[]) (text: string) =
        ReadOnlySpan<byte>(bytes).IndexOf(ReadOnlySpan<byte>(Encoding.ASCII.GetBytes(text))) >= 0

    let private isPe64 (path: string) =
        try
            use fs = File.OpenRead(path)
            use br = new BinaryReader(fs)
            if br.ReadUInt16() <> 0x5A4Dus then false
            else
                fs.Seek(0x3CL, SeekOrigin.Begin) |> ignore
                fs.Seek(int64 (br.ReadInt32()), SeekOrigin.Begin) |> ignore
                br.ReadUInt32() = 0x4550u && br.ReadUInt16() = 0x8664us
        with _ -> false

    /// Which project a picked DLL is from, read from at most 96 MB of it.
    let identify (dll: string) : Recipe option =
        try
            if not (isPe64 dll) || FileInfo(dll).Length > 96L * 1024L * 1024L then None
            else
                let bytes = File.ReadAllBytes(dll)
                let lower = Encoding.ASCII.GetString(bytes).ToLowerInvariant() |> Encoding.ASCII.GetBytes
                if [ "dlssg_sm86"; "dlssg_for_sm86" ] |> List.exists (contains lower) then Some Sm86
                elif [ "dlss-enabler"; "dlss enabler"; "dlssenabler" ] |> List.exists (contains lower) then Some Enabler
                else None
        with _ -> None

    let private sm86Names = set [ "version.dll"; "winmm.dll"; "dxgi.dll"; "d3d12.dll"; "dbghelp.dll" ]

    let private recordPath (game: GameItem) =
        Path.Combine(Path.GetDirectoryName(game.TargetExecutablePath), "dlss5-suite-own-fg.json")

    let isInstalled (game: GameItem) = File.Exists(recordPath game)

    /// Places the picked files. Returns what was done, in words.
    let install (game: GameItem) (picked: string list) : Result<string, string> =
        try
            let exeDir = Path.GetDirectoryName(game.TargetExecutablePath)
            let dlls = picked |> List.filter (fun p -> p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            let found = dlls |> List.choose (fun d -> identify d |> Option.map (fun r -> d, r))
            match found with
            | [] -> Error "None of those files is from dlssg_for_sm86 or DLSS Enabler. Pick the project's .dll (and its .ini)."
            | (dll, recipe) :: _ ->
                // Keep a copy, so the files stay usable if the download folder
                // is cleaned out.
                let keep = Path.Combine(store (), recipeLabel recipe)
                Directory.CreateDirectory(keep) |> ignore
                for p in picked do File.Copy(p, Path.Combine(keep, Path.GetFileName p), true)
                let placed = ResizeArray<string>()
                let put (src: string) (dst: string) =
                    if File.Exists dst then failwithf "%s is already in the game folder - SUITE will not overwrite it" (Path.GetFileName dst)
                    Directory.CreateDirectory(Path.GetDirectoryName dst) |> ignore
                    File.Copy(src, dst)
                    placed.Add dst
                match recipe with
                | Sm86 ->
                    let name = Path.GetFileName(dll).ToLowerInvariant()
                    let target = if sm86Names.Contains name then name else "version.dll"
                    let optiTaken =
                        ModInstaller.installedTargets game |> List.exists (fun t -> Path.GetFileName(t).Equals(target, StringComparison.OrdinalIgnoreCase))
                    if optiTaken then failwithf "%s is taken by this game's DLSS 5 install - rename the file to winmm.dll or dbghelp.dll and pick it again" target
                    put dll (Path.Combine(exeDir, target))
                    match picked |> List.tryFind (fun p -> Path.GetFileName(p).Equals("dlssg_sm86.ini", StringComparison.OrdinalIgnoreCase)) with
                    | Some ini -> put ini (Path.Combine(exeDir, "dlssg_sm86.ini"))
                    | None -> ()
                | Enabler ->
                    if ModInstaller.installedMode game <> "optiscaler" then
                        failwith "DLSS Enabler loads through OptiScaler - install the OptiScaler route first"
                    put dll (Path.Combine(exeDir, "OptiScaler", "dlss-enabler-headless.dll"))
                File.WriteAllText(recordPath game, JsonSerializer.Serialize(placed.ToArray()))
                AppLog.info (sprintf "Own frame generation (%s) in %s" (recipeLabel recipe) game.Title)
                Ok(sprintf "%s is in. Turn DLSS Frame Generation on in the game. This is experimental - if the game will not start, press Remove." (recipeLabel recipe))
        with ex -> Error ex.Message

    let remove (game: GameItem) : Result<string, string> =
        try
            let r = recordPath game
            if File.Exists r then
                for f in JsonSerializer.Deserialize<string[]>(File.ReadAllText r) do
                    if File.Exists f then File.Delete f
                File.Delete r
            Ok "Your frame generation files are out of the game (SUITE keeps its copy)."
        with ex -> Error ex.Message
