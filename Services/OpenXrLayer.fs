namespace DLSS_5_MANAGER.Services

open System
open System.IO
open System.IO.Compression
open System.Text.Json
open System.Text.Json.Nodes
open Microsoft.Win32
open DLSS_5_MANAGER.Models

/// DLSS 5 in a VR headset. A proxy DLL only reaches the desktop mirror, so
/// the headset never sees the neural pass; ReShade's OpenXR API layer hooks
/// the OpenXR swapchain itself. It is registered once per user as an implicit
/// layer, and the game's normal install supplies the add-ons it loads.
/// OpenVR / SteamVR-only games are not reachable this way. Experimental.
module OpenXrLayer =

    [<Literal>]
    let private LayerKey = @"Software\Khronos\OpenXR\1\ApiLayers\Implicit"

    let private layerDir () =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DLSS5Suite", "reshade-openxr")

    let private manifestPath () = Path.Combine(layerDir (), "ReShade64_XR.json")

    let private gamesPath () = Path.Combine(layerDir (), "games.json")

    /// A game is an OpenXR title when it ships the OpenXR loader.
    let usesOpenXr (game: GameItem) =
        try
            Directory.EnumerateFiles(game.InstallDirectory, "openxr_loader.dll", SearchOption.AllDirectories) |> Seq.isEmpty |> not
        with _ -> false

    /// An active ReShade OpenXR layer someone already registered.
    let private existing () =
        [ Registry.CurrentUser; Registry.LocalMachine ]
        |> List.tryPick (fun hive ->
            try
                use k = hive.OpenSubKey(LayerKey)
                if isNull k then None
                else
                    k.GetValueNames()
                    |> Array.tryFind (fun n ->
                        let b = Path.GetFileName(n).ToLowerInvariant()
                        b.Contains "reshade" && b.EndsWith ".json" && File.Exists n
                        && (match k.GetValue(n) with :? int as v -> v = 0 | _ -> false))
            with _ -> None)

    let private games () : string list =
        try
            if File.Exists(gamesPath ()) then JsonSerializer.Deserialize<string list>(File.ReadAllText(gamesPath ())) else []
        with _ -> []

    let private saveGames (ids: string list) =
        Directory.CreateDirectory(layerDir ()) |> ignore
        File.WriteAllText(gamesPath (), JsonSerializer.Serialize(ids))

    let isEnabled (game: GameItem) = games () |> List.contains game.AppId

    /// Registers ReShade's OpenXR layer (from SUITE's bundled ReShade setup)
    /// for this user and notes the game as one using it.
    let enable (game: GameItem) : Result<string, string> =
        try
            if not (ModInstaller.isInstalled game) then failwith "install DLSS 5 into the game first - the layer loads that install's add-ons"
            match existing () with
            | Some other when not (other.StartsWith(layerDir (), StringComparison.OrdinalIgnoreCase)) ->
                saveGames (game.AppId :: games () |> List.distinct)
                Ok "A ReShade OpenXR layer is already registered on this PC; the game uses that one."
            | _ ->
                let setup =
                    Directory.GetFiles(ModInstaller.modFilesRoot (), "ReShade_Setup*.exe")
                    |> Array.sortDescending
                    |> Array.tryHead
                match setup with
                | None -> failwith "SUITE's ReShade setup is missing from its mod files"
                | Some exe ->
                    Directory.CreateDirectory(layerDir ()) |> ignore
                    use archive = ZipFile.OpenRead(exe)
                    for name in [ "ReShade64.dll"; "ReShade64_XR.json" ] do
                        let entry = archive.GetEntry(name)
                        if isNull entry then failwithf "%s is not in the ReShade setup" name
                        try
                            entry.ExtractToFile(Path.Combine(layerDir (), name), true)
                        with :? IOException ->
                            failwith "a VR or Vulkan program is using the layer right now - close it and try again"
                    let node = JsonNode.Parse(File.ReadAllText(manifestPath ()))
                    node.["api_layer"].["library_path"] <- JsonValue.Create(".\\ReShade64.dll")
                    File.WriteAllText(manifestPath (), node.ToJsonString(JsonSerializerOptions(WriteIndented = true)))
                    use k = Registry.CurrentUser.CreateSubKey(LayerKey)
                    k.SetValue(manifestPath (), 0, RegistryValueKind.DWord)
                    saveGames (game.AppId :: games () |> List.distinct)
                    AppLog.info ("OpenXR layer registered for " + game.Title)
                    Ok "ReShade's OpenXR layer is registered - the headset now gets the neural pass. Experimental: it is unproven in many headsets."
        with ex ->
            AppLog.error "OpenXR layer" ex
            Error ex.Message

    /// Drops the game; the layer's registration goes with the last one.
    let disable (game: GameItem) : string =
        let rest = games () |> List.filter (fun g -> g <> game.AppId)
        saveGames rest
        if rest.IsEmpty then
            try
                use k = Registry.CurrentUser.OpenSubKey(LayerKey, true)
                if not (isNull k) then k.DeleteValue(manifestPath (), false)
            with _ -> ()
            "VR is off. No game uses the OpenXR layer now, so it is unregistered."
        else "VR is off for this game; other games still use the layer."
