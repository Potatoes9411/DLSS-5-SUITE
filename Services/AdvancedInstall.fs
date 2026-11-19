namespace DLSS_5_MANAGER.Services

open System
open System.IO
open System.Collections.Generic
open DLSS_5_MANAGER.Models

/// Advanced install: the same install as the one-click button, cut into the
/// parts it is made of (ReShade, RenoDX, host64, OptiScaler, the DLSS runtime
/// and the rest), each one put on or taken off a game by itself.
///
/// It never writes anything the normal installer does not. A part goes on by
/// running ModInstaller's own install with the matching switches, and comes
/// off by reverting that part's entries in the install's manifest, so SUITE's
/// normal Remove takes an advanced install off exactly like any other.
module AdvancedInstall =

    /// One part of an install, as the sheet lists it.
    type PartInfo =
        { Id: string
          Label: string
          Description: string
          /// Kept with the route: only the full removal takes it off.
          Locked: bool }

    /// What one part looks like on a game right now.
    type PartState =
        { Id: string
          Files: string[]
          /// Every file the manifest lists for it is on disk.
          Installed: bool
          /// Some of its files are missing - a repair puts them back.
          Incomplete: bool }

    /// The part list in the order the sheet shows it.
    let parts: PartInfo list =
        let p id label desc locked = { Id = id; Label = label; Description = desc; Locked = locked }

        [ p "reshade" "ReShade" "The ReShade runtime, on the slot the route uses: dxgi.dll for DirectX 10-12, d3d9.dll for DirectX 9 (dxgi.dll under dgVoodoo), OpenGL, or the Vulkan layer." false
          p "effects" "Standard effects" "The standard effect package in the reshade-shaders folder, with ReShade.ini pointed at it." false
          p "dgvoodoo" "DirectX 9 wrapper (dgVoodoo)" "dgVoodoo on d3d9.dll: turns a DirectX 9 game's calls into a modern API that ReShade and DLSS 5 can use." false
          p "host64" "64-bit host (host64)" "The 64-bit helper a 32-bit game needs, in the host64 folder." false
          p "feed" "DLSS 5 feed add-on" "dlss5-feed, the add-on that hands the game's frames to DLSS 5." false
          p "renodx" "RenoDX DLSS 5" "RenoDX's DLSS 5 add-on beside the game (or its Multipass build). For 64-bit games." false
          p "deepfried" "Deep-fried chicken" "The deep-fried chicken payload. Beside a 64-bit game, inside host64 for a 32-bit one." false
          p "mfgunlock" "RenoDX MFG unlock" "RenoDX's MFG unlock add-on beside the game. For 64-bit games." false
          p "optiscaler" "OptiScaler" "OptiScaler and its files, hooked on the first free name." false
          p "amd" "AMD files" "The AMD RDNA 4 payload, hooked as version.dll or the next free name. Beta." false
          p "streamline" "NVIDIA Streamline" "NVIDIA Streamline, refreshed only where the game already ships 2.4 or newer." false
          p "dlss" "NVIDIA DLSS runtime" "The NVIDIA DLSS runtime: the game's older copies upgraded in place, and ours beside the game." false
          p "model" "DLSS 5 model" "nvngx_dlssnr.dll, the ray reconstruction model DLSS 5 runs on. Stays with the route; Remove everything takes it off." true
          p "modelspread" "Model in the game's DLSS folders" "The same model in the game's own DLSS and Streamline folders. Stays with the route." true
          p "neural" "Neural upstream" "The neural upstream add-on (nvngx.dll.addon64). Beta." false
          p "overlay" "DLSS 5 overlay" "The in-game DLSS 5 overlay, a ReShade add-on, with the style and key from Settings." false
          p "extras" "Dynamic extra files" "Your files from Settings (Extra Dynamic) set for this route." false
          p "userextra" "Files of your own" "Files you added to this game here. They come off with its install." false ]

    let partInfo (id: string) =
        parts |> List.tryFind (fun p -> p.Id = id)

    let isLocked (id: string) =
        match partInfo id with
        | Some p -> p.Locked
        | None -> false

    // ---------------------------------------------------------------------
    // WHICH PART A FILE BELONGS TO
    // ---------------------------------------------------------------------
    let private trimSlash (s: string) =
        if isNull s then "" else s.TrimEnd('\\', '/')

    let private sameDir (a: string) (b: string) =
        String.Equals(trimSlash a, trimSlash b, StringComparison.OrdinalIgnoreCase)

    let private eq (a: string) (b: string) =
        String.Equals(a, b, StringComparison.OrdinalIgnoreCase)

    let private inArray (name: string) (names: string[]) =
        names |> Array.exists (eq name)

    /// Every file under a payload folder, relative and lower case.
    let private payloadNames (dirName: string) : HashSet<string> =
        let set = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        try
            let root = ModInstaller.modFilesRoot ()
            if not (String.IsNullOrWhiteSpace(root)) then
                let dir = Path.Combine(root, dirName)
                if Directory.Exists(dir) then
                    for f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories) do
                        set.Add(f.Substring(dir.Length).TrimStart('\\', '/').ToLowerInvariant()) |> ignore
        with _ ->
            ()
        set

    let private optiNames =
        lazy
            (let s = payloadNames ModInstaller.optiScalerDirName
             s.UnionWith(payloadNames ModInstaller.optiScalerNeuralDirName)
             s.UnionWith(payloadNames ModInstaller.optiScalerRtx40DirName)
             // The proxy slot is the hook itself, not a payload file.
             for slot in ModInstaller.optiScalerSlots do
                 s.Remove(slot.ToLowerInvariant()) |> ignore
             s)

    let private amdNames = lazy (payloadNames ModInstaller.amdPayloadDirName)
    let private dx9Names = lazy (payloadNames ModInstaller.dx9PayloadDirName)

    let private reshadeSlotNames =
        [| "dxgi.dll"; "d3d9.dll"; "d3d10.dll"; "d3d11.dll"; "d3d12.dll"; "opengl32.dll"; "vulkan-1.dll" |]

    let private reshadeOwnNames =
        [| "ReShade.ini"; "ReShadePreset.ini"; "ReShade64.json"; "ReShade32.json"; "ReShade64.dll"; "ReShade32.dll" |]

    let private optiOwnNames =
        [| "OptiScaler.ini"; "OptiScaler.asi"; "Remove_OptiScaler.bat"; "setup_windows.bat" |]

    /// The part a file of an install belongs to, from where it sits and what
    /// it is called. `mode` is the manifest's route key; `userFiles` the
    /// manifest's own list of files the user added.
    let classify (mode: string) (exeDir: string) (userFiles: string seq) (path: string) : string =
        let mode = if isNull mode then "" else mode
        let name = Path.GetFileName(path)
        let dir = try Path.GetDirectoryName(path) with _ -> ""
        let host64 = Path.Combine(exeDir, ModInstaller.host64DirName)

        let rel =
            try
                let r = Path.GetRelativePath(exeDir, path)
                if r.StartsWith("..") || Path.IsPathRooted(r) then "" else r.ToLowerInvariant()
            with _ ->
                ""

        let isUser = userFiles |> Seq.exists (eq path)
        let deepFried = inArray name ModInstaller.deepFriedFileNames

        if isUser then "userextra"
        elif eq name GameAnalyzer.dlssnrFileName then
            if sameDir dir exeDir || sameDir dir host64 then "model" else "modelspread"
        elif rel.StartsWith(ModInstaller.reshadeShadersDirName.ToLowerInvariant() + "\\") then "effects"
        elif rel.StartsWith(ModInstaller.host64DirName.ToLowerInvariant() + "\\") then
            if deepFried then "deepfried"
            elif eq name ModInstaller.neuralAddonName then "neural"
            else "host64"
        elif deepFried then "deepfried"
        elif eq name ModInstaller.renodxAddonName || eq name ModInstaller.renodxAddonLegacyName
             || eq name ModInstaller.renodxMultipassName then "renodx"
        elif eq name ModInstaller.mfgUnlockAddonName then "mfgunlock"
        elif eq name ModInstaller.feedAddonName || eq name ModInstaller.feedAddon32Name then "feed"
        elif eq name ModInstaller.neuralAddonName then "neural"
        elif eq name ModInstaller.overlayAddonName || eq name ModInstaller.overlayAddon32Name
             || eq name ModInstaller.overlayConfigName then "overlay"
        elif inArray name reshadeOwnNames then "reshade"
        elif rel.StartsWith("optiscaler\\") || inArray name optiOwnNames then "optiscaler"
        elif eq mode "amd" && (amdNames.Value.Contains(rel) || inArray name ModInstaller.amdSlots) then "amd"
        elif optiNames.Value.Contains(rel) then "optiscaler"
        elif amdNames.Value.Contains(rel) then "amd"
        elif dx9Names.Value.Contains(rel) || (eq mode "dx9" && (eq name "d3d9.dll" || eq name "dgVoodoo.conf" || eq name "dgVoodooCpl.exe")) then "dgvoodoo"
        elif eq mode "optiscaler" && inArray name ModInstaller.optiScalerSlots then "optiscaler"
        elif inArray name reshadeSlotNames && not (eq mode "optiscaler") && not (eq mode "amd") then "reshade"
        elif inArray name GameAnalyzer.dlssFileNames then "dlss"
        elif name.StartsWith("sl.", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) then "streamline"
        else "extras"

    /// Each part of the recorded install with its files and whether they are
    /// all still there. Empty when nothing is installed.
    let stateOf (manifest: ModInstaller.InstallManifest) : Map<string, PartState> =
        let files = if isNull (box manifest.Files) then [||] else manifest.Files
        let userFiles = if isNull manifest.UserFiles then [||] else manifest.UserFiles
        let exeDir = try Path.GetDirectoryName(manifest.ExecutablePath) with _ -> ""

        files
        |> Array.filter (fun f -> not (String.IsNullOrWhiteSpace(f.TargetPath)))
        |> Array.groupBy (fun f -> classify manifest.Mode exeDir userFiles f.TargetPath)
        |> Array.map (fun (id, entries) ->
            let paths = entries |> Array.map (fun e -> e.TargetPath)
            let present = paths |> Array.filter File.Exists
            (id,
             { Id = id
               Files = paths
               Installed = present.Length = paths.Length
               Incomplete = present.Length < paths.Length }))
        |> Map.ofArray

    let readState (game: GameItem) : Map<string, PartState> =
        match ModInstaller.manifestOf game with
        | Some m -> stateOf m
        | None -> Map.empty

    // ---------------------------------------------------------------------
    // WHAT A ROUTE PUTS IN
    // ---------------------------------------------------------------------
    /// The parts the one-click install of this route would put in, with the
    /// Manage sheet's switches as they stand. Advanced install ticks exactly
    /// these to start with.
    let recommend
        (mode: ModInstaller.InstallMode)
        (arch: ModInstaller.InstallArch)
        (neural: bool)
        (feeder: bool)
        (options: ModInstaller.InstallOptions)
        (overlayOn: bool)
        : Set<string> =
        let (mfg, _multipass, friedHere, friedHost64) = ModInstaller.effectiveRenoDxOptions mode arch options
        let bit32 = arch = ModInstaller.Bit32 && (mode = ModInstaller.Dx11 || mode = ModInstaller.Dx9)
        let overlay = overlayOn && ModInstaller.overlaySupported mode ModInstaller.OptiDx12

        let route =
            match mode with
            | ModInstaller.OptiScalerMode -> [ "optiscaler"; "streamline"; "dlss"; "model" ]
            | ModInstaller.AmdMode -> [ "amd"; "model" ] @ (if neural then [ "neural" ] else [])
            | ModInstaller.Emulator ->
                [ "reshade"; "effects"; "dlss"; "model" ]
                @ (if feeder then [ "feed" ] else [])
                @ (if options.DeepFriedChicken then [ "deepfried" ] else [])
            | ModInstaller.Dx12Auto
            | ModInstaller.Dx11
            | ModInstaller.Dx9 ->
                [ "reshade"; "effects"; "streamline"; "dlss"; "model"; "modelspread" ]
                @ (if mode = ModInstaller.Dx9 then [ "dgvoodoo" ] else [])
                @ (if bit32 then [ "host64" ] else [])
                @ (if not bit32 && not friedHere then [ "renodx" ] else [])
                @ (if feeder then [ "feed" ] else [])
                @ (if friedHere || friedHost64 then [ "deepfried" ] else [])
                @ (if mfg then [ "mfgunlock" ] else [])
                @ (if neural then [ "neural" ] else [])

        Set.ofList (route @ (if overlay then [ "overlay" ] else []) @ [ "extras" ])

    /// The parts a route can take at all, ticked or not: the ones it puts in
    /// plus the switches it accepts.
    let offeredFor (mode: ModInstaller.InstallMode) (arch: ModInstaller.InstallArch) : Set<string> =
        let everything =
            { ModInstaller.defaultInstallOptions with MfgUnlockAddon = true }

        let a = recommend mode arch true true everything true
        let b = recommend mode arch true true { everything with DeepFriedChicken = true } true
        Set.union a b

    // ---------------------------------------------------------------------
    // APPLYING A CHOICE
    // ---------------------------------------------------------------------
    /// Parts that only come in when the game gives them something to do - a
    /// Streamline or DLSS copy to upgrade, its own DLSS folders, extras set
    /// for the route. Missing on their own, they are not a reason to install.
    let dependsOnGame (id: string) =
        id = "streamline" || id = "dlss" || id = "modelspread" || id = "extras"

    /// What Apply will do, from the parts the user ticked and the state of
    /// the game: (parts to put on, parts to take off). Locked parts are never
    /// taken off, and a part the sheet did not show is never touched.
    let changes (shown: Set<string>) (wanted: Set<string>) (state: Map<string, PartState>) : Set<string> * Set<string> =
        let present = state |> Map.filter (fun _ s -> s.Installed) |> Map.keys |> Set.ofSeq
        let onDisk = state |> Map.keys |> Set.ofSeq
        let incomplete = state |> Map.filter (fun _ s -> s.Incomplete) |> Map.keys |> Set.ofSeq

        let toInstall =
            Set.union (Set.difference wanted present |> Set.filter (fun id -> not (dependsOnGame id))) (Set.intersect wanted incomplete)
            |> Set.filter (fun id -> id <> "userextra")

        let toRemove =
            Set.difference (Set.intersect shown onDisk) wanted
            |> Set.filter (fun id -> not (isLocked id))

        (toInstall, toRemove)

    /// The install switches that carry the ticked parts.
    let switchesFor (wanted: Set<string>) (options: ModInstaller.InstallOptions) =
        let neural = wanted.Contains "neural"
        let feeder = wanted.Contains "feed"

        let options =
            { options with
                MfgUnlockAddon = wanted.Contains "mfgunlock"
                DeepFriedChicken = wanted.Contains "deepfried" }

        (neural, feeder, options, wanted.Contains "overlay")

    /// Puts the ticked parts on and takes the unticked ones off. A part goes
    /// on through the normal install, so anything the route needs comes with
    /// it; whatever that run brought that the user unticked goes again.
    let apply
        (game: GameItem)
        (exePath: string)
        (plan: ModInstaller.InstallPlan option)
        (mode: ModInstaller.InstallMode)
        (arch: ModInstaller.InstallArch)
        (optiApi: ModInstaller.OptiScalerApi)
        (options: ModInstaller.InstallOptions)
        (overlay: ModInstaller.OverlayOptions)
        (shown: Set<string>)
        (wanted: Set<string>)
        (report: ModInstaller.Progress)
        : ModInstaller.InstallOutcome =
        let state = readState game
        let (toInstall, toRemove) = changes shown wanted state

        if toInstall.IsEmpty && toRemove.IsEmpty then
            { ModInstaller.InstallOutcome.Success = true; ModInstaller.InstallOutcome.Message = "No changes - the game already has exactly these parts." }
        else
            let installed =
                if toInstall.IsEmpty then
                    { ModInstaller.InstallOutcome.Success = true; ModInstaller.InstallOutcome.Message = "" }
                else
                    let (neural, feeder, opts, overlayOn) = switchesFor wanted options
                    let overlay = { overlay with Enabled = overlayOn }
                    let scaled: ModInstaller.Progress = fun text p -> report text (p * 0.85)
                    ModInstaller.installWithOptions game exePath plan mode arch optiApi neural feeder opts overlay scaled

            if not installed.Success then
                installed
            else
                report "Taking off the parts you switched off..." 0.9
                let after = readState game
                let dropping =
                    Set.difference (Set.intersect shown (after |> Map.keys |> Set.ofSeq)) wanted
                    |> Set.filter (fun id -> not (isLocked id))

                let fullRoute = recommend mode arch true true options true
                let advanced =
                    not dropping.IsEmpty
                    || (after.ContainsKey "userextra")
                    || not (Set.isSubset (Set.intersect fullRoute shown) wanted)

                match ModInstaller.manifestOf game with
                | None -> installed
                | Some manifest ->
                    let exeDir = try Path.GetDirectoryName(manifest.ExecutablePath) with _ -> ""
                    let userFiles = if isNull manifest.UserFiles then [||] else manifest.UserFiles

                    let shouldGo (entry: ModInstaller.InstalledFile) =
                        dropping.Contains(classify manifest.Mode exeDir userFiles entry.TargetPath)

                    match ModInstaller.revertWhere game shouldGo advanced with
                    | Error e -> { ModInstaller.InstallOutcome.Success = false; ModInstaller.InstallOutcome.Message = e }
                    | Ok count ->
                        report "Done" 1.0
                        let added = if toInstall.IsEmpty then "" else sprintf "Put on: %s. " (String.Join(", ", toInstall))
                        let taken = if dropping.IsEmpty then "" else sprintf "Taken off: %s (%d file(s))." (String.Join(", ", dropping)) count
                        let tail = if installed.Message = "" then "" else " " + installed.Message
                        { ModInstaller.InstallOutcome.Success = true; ModInstaller.InstallOutcome.Message = (added + taken).Trim() + tail }
