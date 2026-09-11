namespace DLSS_5_MANAGER.ViewModels

open System
open System.Collections.ObjectModel
open Avalonia
open Avalonia.Media
open Avalonia.Media.Imaging
open Avalonia.Platform
open Avalonia.Threading
open DLSS_5_MANAGER.Models
open DLSS_5_MANAGER.Services

/// One row in the payload list: a bundled file the user can swap for their
/// own, and (except OptiScaler, which has no alternatives worth switching off)
/// leave out of an install entirely.
type PayloadRowViewModel(key: string, describe: unit -> string, canDisable: bool) =
    inherit ViewModelBase()

    let mutable state = describe ()
    let mutable enabled = not canDisable || ExtrasStore.isPayloadEnabled key

    member _.Key = key
    member _.Name = key
    member _.CanDisable = canDisable
    member _.State = state

    member this.Refresh() =
        state <- describe ()
        enabled <- not canDisable || ExtrasStore.isPayloadEnabled key
        this.RaisePropertyChanged("State")
        this.RaisePropertyChanged("IsEnabled")

    /// Off means the installer walks past it - not that anything is wrong.
    member this.IsEnabled
        with get () = enabled
        and set value =
            if canDisable && enabled <> value then
                enabled <- value
                ExtrasStore.setPayloadEnabled key value
                this.RaisePropertyChanged("IsEnabled")

/// One batch of user-added files or folders - everything picked in a single
/// go - copied next to the game on install.
///
/// The whole batch shares one switch and one route selection, so adding a
/// folder's worth of files produces one entry to reason about rather than
/// twenty identical ones.
type ExtraRowViewModel(key: string, items: ExtrasStore.ExtraItem list) =
    inherit ViewModelBase()

    let first = List.head items
    let fileNames = items |> List.map (fun i -> i.Name)

    let mutable enabled = first.Enabled

    let mutable modes =
        if isNull (box first.Modes) then [||] else first.Modes

    let has (k: string) =
        modes |> Array.exists (fun m -> String.Equals(m, k, StringComparison.OrdinalIgnoreCase))

    /// A route that already deploys one of these filenames cannot also take
    /// the extra - they would be fighting over the same slot.
    let allowed (routeKey: string) =
        not (ModInstaller.conflictsWithRoute fileNames routeKey)

    member _.Key = key

    member _.Title =
        match items with
        | [ single ] -> single.Name
        | _ -> sprintf "%d items" items.Length

    /// The folder they came from, which is what tells them apart at a glance.
    member _.SourceLabel =
        try
            let dir = System.IO.Path.GetDirectoryName(first.SourcePath.TrimEnd('\\', '/'))
            if String.IsNullOrWhiteSpace(dir) then first.SourcePath else dir
        with _ ->
            first.SourcePath

    member _.FilesLabel =
        match items with
        | [ single ] -> (if single.IsFolder then "Folder" else "File")
        | _ -> String.Join(", ", fileNames)

    member _.HasManyFiles = items.Length > 1

    member this.IsEnabled
        with get () = enabled
        and set value =
            if enabled <> value then
                enabled <- value
                ExtrasStore.setGroupEnabled key value
                this.RaisePropertyChanged("IsEnabled")

    // ---- Which routes may carry it ---------------------------------------
    member _.IsOptiDx12Allowed = allowed "optiscaler"
    member _.IsOptiVulkanAllowed = allowed "optiscaler"
    member _.IsDx12Allowed = allowed "dx12"
    member _.IsDx11Allowed = allowed "dx11"
    member _.IsDx9Allowed = allowed "dx9"
    member _.IsAmdAllowed = allowed "amd"
    member _.IsEmulatorAllowed = allowed "emulator"

    /// Named when at least one route had to be ruled out, so the greyed-out
    /// chips are never a mystery.
    member this.ConflictText =
        let blocked =
            [ "OptiScaler", allowed "optiscaler"
              "DX12", allowed "dx12"
              "DX11", allowed "dx11"
              "DX9", allowed "dx9"
              "AMD", allowed "amd"
              "Emulator", allowed "emulator" ]
            |> List.filter (snd >> not)
            |> List.map fst

        if blocked.IsEmpty then
            ""
        else
            "Not available for " + String.Join(", ", blocked) + " - those install a file of the same name."

    member this.HasConflict = this.ConflictText <> ""

    // ---- Route selection -------------------------------------------------
    // The routes that come in variants are listed as those variants, using the
    // same short names the cards wear, rather than hiding them behind a second
    // level of controls.
    member _.IsAllModes = modes.Length = 0
    member _.IsOptiDx12Mode = has "optiscaler-dx12"
    member _.IsOptiVulkanMode = has "optiscaler-vulkan"
    member _.IsOptiNeuralMode = has "optiscaler-neural"
    member _.IsDx12Mode = has "dx12"
    member _.IsDx1164Mode = has "dx11-64"
    member _.IsDx1132Mode = has "dx11-32"
    member _.IsDx964Mode = has "dx9-64"
    member _.IsDx932Mode = has "dx9-32"
    member _.IsAmdMode = has "amd"
    member _.IsEmulatorMode = has "emulator"

    /// A short line under the name saying where this ends up.
    member this.ModesText =
        if modes.Length = 0 then
            "Every install mode"
        else
            "Only: "
            + String.Join(
                ", ",
                modes
                |> Array.map (fun m ->
                    match m with
                    | "optiscaler"
                    | "optiscaler-dx12" -> "OptiScaler"
                    | "optiscaler-vulkan" -> "OptiScaler Vulkan"
                    | "optiscaler-neural" -> "OptiScaler neural"
                    | "dx12" -> "DX12"
                    | "dx11" -> "DX11"
                    | "dx11-64" -> "DX11 64-bit"
                    | "dx11-32" -> "DX11 32-bit"
                    | "dx9" -> "DX9"
                    | "dx9-64" -> "DX9 64-bit"
                    | "dx9-32" -> "DX9 32-bit"
                    | "amd" -> "AMD"
                    | "emulator" -> "Emulator"
                    | other -> other)
            )

    member this.ToggleMode(modeKey: string) =
        if modeKey = "all" then
            ExtrasStore.clearGroupModes key
        else
            ExtrasStore.toggleGroupMode key modeKey

        modes <-
            ExtrasStore.list ()
            |> List.tryFind (fun e -> String.Equals(ExtrasStore.groupKey e, key, StringComparison.OrdinalIgnoreCase))
            |> Option.map (fun e -> if isNull (box e.Modes) then [||] else e.Modes)
            |> Option.defaultValue [||]

        for name in
            [ "IsAllModes"; "IsOptiDx12Mode"; "IsOptiVulkanMode"; "IsOptiNeuralMode"; "IsDx12Mode"
              "IsDx1164Mode"; "IsDx1132Mode"; "IsDx964Mode"; "IsDx932Mode"
              "IsAmdMode"; "IsEmulatorMode"; "ModesText" ] do
            this.RaisePropertyChanged(name)

/// One entry in the colour atmosphere picker.
///
/// `Key` is the identity - the English name that gets saved and matched - and
/// `Display` is only what the user reads. Changing language rewrites `Display`
/// on the existing objects instead of handing the picker a new list, because
/// swapping the list drops its selection and leaves the box blank.
type AtmosphereOption(key: string) =
    inherit ViewModelBase()

    let mutable display = key

    member _.Key = key

    member this.Display
        with get () = display
        and set value = this.SetProperty(&display, value) |> ignore

    override _.ToString() = display

/// One game in the batch sheet.
///
/// The route is decided the same way the single-game sheet decides it, from
/// the API the analyzer recorded: Vulkan can only be OptiScaler because a
/// Vulkan title never loads dxgi.dll, DX9 and DX11 have exactly one route
/// each, and DX12 is the only case where there is a real choice to offer -
/// OptiScaler hooking the game directly, or ReShade + RenoDX. Anything the
/// analyzer could not read is treated as DX12, which is what the single-game
/// path does too.
type BatchRowViewModel(card: GameCardViewModel) =
    inherit ViewModelBase()

    let analysis = AnalysisStore.tryGet card.Game

    let api =
        match analysis with
        | Some a when not (isNull (box a.GraphicsApi)) -> a.GraphicsApi
        | _ -> ""

    /// Whether THIS app has a mod installed on the game, read from the install
    /// manifest - exactly what the card badge in the grid reports.
    ///
    /// Not Dlss5Present: that only says nvngx_dlssnr.dll is somewhere in the
    /// folder, which is true of every game that ships DLSS itself, so it
    /// marked most of the library as installed when nothing had been done to
    /// it. The manifest is the only thing that knows what this app put there.
    let readInstalled () =
        try
            fst (ModInstaller.installedRouteAndArch card.Game) <> ""
        with _ ->
            false

    let mutable installed = readInstalled ()

    let exePath =
        match analysis with
        | Some a -> a.ExecutablePath
        | None -> ""

    let mutable selected = false
    // Only ever read on the DX12 route, where both options are real.
    let mutable useOptiScaler = true

    member _.Card = card
    member _.Game = card.Game
    member _.Title = card.Game.Title
    member _.ExecutablePath = exePath
    member _.Analysis = analysis
    member _.DetectedApi = api
    member _.IsInstalled = installed

    /// Re-read after a batch run changes the manifest.
    member this.RefreshInstalled() =
        installed <- readInstalled ()
        this.RaisePropertyChanged("IsInstalled")

    /// Nothing to install into: the resolver never found an executable.
    member _.IsRunnable = not (String.IsNullOrWhiteSpace(exePath))

    member _.ApiLabel = if api = "" then "UNKNOWN" else api.ToUpperInvariant()

    /// Same localized badge the card grid uses.
    member _.BadgeInstalled = Localization.current.BadgeInstalled

    member this.Selected
        with get () = selected
        and set value = this.SetProperty(&selected, value) |> ignore

    /// DX12 is the only route with a genuine choice. Everything else is forced
    /// by the API, so the picker is hidden rather than shown disabled.
    member _.CanChooseRoute = (api = "dx12" || api = "")

    member this.UseOptiScaler
        with get () = useOptiScaler
        and set value =
            if this.SetProperty(&useOptiScaler, value) then
                this.RaisePropertyChanged("RouteLabel")

    member this.UseReShade
        with get () = not useOptiScaler
        and set value = this.UseOptiScaler <- not value

    /// What will actually be run for this game.
    member _.Mode =
        if api = "dx9" then ModInstaller.Dx9
        elif api = "dx10" || api = "dx11" then ModInstaller.Dx11
        elif api = "vulkan" || api = "opengl" then ModInstaller.OptiScalerMode
        elif useOptiScaler then ModInstaller.OptiScalerMode
        else ModInstaller.Dx12Auto

    member this.OptiApi =
        if api = "vulkan" || api = "opengl" then ModInstaller.OptiVulkan
        else ModInstaller.OptiDx12

    /// Read from the executable, the same way the single-game sheet does it.
    /// The analysis cache does not carry it, and a 32-bit game given the
    /// 64-bit payload cannot load it.
    member _.Arch =
        if String.IsNullOrWhiteSpace(exePath) then ModInstaller.Bit64
        elif GameAnalyzer.detectArchitecture exePath = "32" then ModInstaller.Bit32
        else ModInstaller.Bit64

    member this.RouteLabel =
        match this.Mode with
        | ModInstaller.OptiScalerMode when api = "vulkan" || api = "opengl" -> "OptiScaler (Vulkan)"
        | ModInstaller.OptiScalerMode -> "OptiScaler"
        | ModInstaller.Dx12Auto -> "ReShade + RenoDX"
        | ModInstaller.Dx11 -> "ReShade (DX11)"
        | ModInstaller.Dx9 -> "ReShade (DX9)"
        | _ -> "OptiScaler"

    member val Status = "" with get, set

    member this.SetStatus(text: string) =
        this.Status <- text
        this.RaisePropertyChanged("Status")

type MainViewModel() as this =
    inherit ViewModelBase()

    /// Backdrop images, decoded once each and held for the life of the app.
    ///
    /// `static let`, deliberately. Written as `static member AssetCache = ...`
    /// this is a property, and F# runs a property's body on every read - so
    /// each lookup built a brand new empty dictionary, missed, and decoded a
    /// 3840x2160 JPEG all over again. The cache has to be a binding, not a
    /// member, or it is not a cache.
    static let assetCache = Collections.Generic.Dictionary<string, IImage>()

    let allGames = ObservableCollection<GameCardViewModel>()
    let filteredGames = ObservableCollection<GameCardViewModel>()

    /// Emulators are a hand-curated list, kept apart from the scanned library.
    let allEmulators = ObservableCollection<GameCardViewModel>()
    let filteredEmulators = ObservableCollection<GameCardViewModel>()

    /// "games" | "emulators" | "settings" - the page on screen.
    let mutable activeSection = "games"

    /// True while the Manage sheet is showing an emulator, which installs by a
    /// single fixed route and needs none of the game-side choices.
    let mutable isEmulatorTarget = false

    let mutable searchText = ""
    let mutable isSearchOpen = false
    let mutable isScanning = false
    let mutable scanStatusText = "Ready"
    let mutable isDraggingCard = false
    let mutable draggedCard: GameCardViewModel option = None
    let mutable isSidebarLayout = false
    let mutable isSettingsOpen = false
    let mutable isCheckingLosslessScaling = false
    let mutable losslessScalingStatus = "Check Steam libraries for an existing Lossless Scaling installation."
    let mutable losslessScalingProgress = 0.0
    let mutable shaderGlassStatus = "Check for the verified official ShaderGlass v1.3.0 build."
    let mutable verifiedShaderGlassPath: string option = None
    let mutable isSettingUpShaderGlass = false
    let mutable shaderGlassProgress = 0.0
    let mutable totalGamesCount = 0
    let mutable showNonGameApps = false

    /// The community section. Built with the window so the tab can switch to it
    /// instantly; it does not touch the network until the tab is opened.
    let community = CommunityViewModel()

    // ---- Manage sheet state ---------------------------------------------
    let mutable isManageOpen = false
    let mutable manageCard: GameCardViewModel option = None
    let mutable manageAnalysis: AnalysisStore.GameAnalysis option = None
    let mutable manageTitle = ""
    let mutable manageExePath = ""
    let mutable manageFolder = ""
    let mutable manageReShadeText = "Checking..."
    let mutable manageDlssText = "Checking..."
    let mutable manageStreamlineText = "Checking..."
    let mutable isAnalyzing = false
    let mutable isInstalling = false
    let mutable isModInstalled = false
    let mutable dlss5Present = false
    let mutable dlss5Complete = false
    let mutable dlss5Missing: string[] = [||]
    let mutable installProgress = 0.0
    let mutable installStatusText = ""
    let mutable installResultText = ""
    let mutable installResultIsError = false

    /// Install route. DX12 + OptiScaler is the recommended default; the other
    /// two are the ReShade routes and cannot coexist with it.
    let mutable installMode = ModInstaller.OptiScalerMode

    // ---- Batch sheet -----------------------------------------------------
    let batchRows = ObservableCollection<BatchRowViewModel>()
    let mutable isBatchOpen = false
    let mutable isBatchRunning = false
    let mutable batchProgress = 0.0
    let mutable batchStatusText = ""
    let mutable batchResultText = ""

    /// Which build of the mod to deploy. DX11 defaults to 64-bit and DX9 to
    /// 32-bit, matching what those two eras of games actually are.
    let mutable installArch = ModInstaller.Bit64

    /// OptiScaler only: which API the game renders with, or the neural
    /// upstream build of OptiScaler itself.
    let mutable optiApi = ModInstaller.OptiDx12

    /// Neural upstream on the DX12 / DX11 / DX9 and AMD routes: one extra
    /// add-on next to the game. Off unless the user asks for it.
    let mutable useNeuralAddon = false

    /// The route and build recorded in the manifest for the open game, "" when
    /// this app did not install it. Drives the Install / Switch / Remove button.
    let mutable installedRoute = ""
    let mutable installedArch = ""
    let mutable installedApi = ""
    let mutable installedNeural = false

    /// True once the user has picked a route in the open sheet. Detection then
    /// stops overriding it - see SetInstallMode.
    let mutable routeChosenByUser = false

    /// True when the sheet opened on a game that already had an install.
    ///
    /// Automatic routing is for a game nobody has modded yet: opening one of
    /// those on the option that suits it is the whole point. A game that has
    /// been installed already has an answer, and removing that install must not
    /// turn it back into a question - so this stays true for the life of the
    /// sheet even after the manifest is gone.
    let mutable sheetOpenedInstalled = false

    /// The detected-target readout starts folded away: it is reference
    /// information, and the sheet is about choosing and installing.
    let mutable isTargetDetailsOpen = false

    // ---- What the game itself turned out to be ---------------------------
    let mutable detectedApi = ""
    let mutable detectedArch = ""
    let mutable detectedDlss = false

    /// The executable the detection above describes. Opening a sheet asks twice
    /// - once immediately, once after the deep scan settles the path - and the
    /// second pass is skipped when nothing moved.
    let mutable detectedFor = ""

    // ---- Custom mod payload state ---------------------------------------
    let mutable payloadStatusText = ""

    /// The swappable payload files. OptiScaler is not here: it is a folder,
    /// and it has no switch because there is nothing to install without it.
    let payloadRows =
        ObservableCollection<PayloadRowViewModel>(
            [ ModInstaller.feedAddonName
              ModInstaller.feedAddon32Name
              ModInstaller.renodxAddonName
              ModInstaller.neuralAddonName
              GameAnalyzer.dlssnrFileName ]
            |> List.map (fun key ->
                PayloadRowViewModel(key, (fun () -> ModInstaller.Payload.describe key), true))
        )

    let extraRows = ObservableCollection<ExtraRowViewModel>()

    /// Which settings sections are unfolded. All start closed; a search that
    /// matches a section opens it so the result is actually readable.
    let openSections = System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)

    // ---- Update check state ---------------------------------------------
    let mutable isCheckingUpdates = false
    let mutable updateStatusText = ""
    let mutable updateDownloadProgress = 0.0
    let mutable hasUpdateAvailable = false
    let mutable latestVersionFound = ""

    let mutable selectedColorAtmosphere = "Neon Emerald"
    let mutable selectedGeometricMotif = "Orbital Spheres"

    /// AMD RDNA 4 route. Off until the user turns it on, and then every game
    /// installs through that one payload instead of the usual routes.
    let mutable isAmdMode = false

    // ---- Support prompt --------------------------------------------------
    /// The version whose prompt has already been seen on this machine.
    let mutable supportPromptVersion = ""
    let mutable isSupportPromptVisible = false

    /// Ten minutes in, once, and never again for this build.
    let supportPromptTimer =
        DispatcherTimer(Interval = TimeSpan.FromMinutes(10.0))

    // ---- Language --------------------------------------------------------
    let mutable languageCode = "en"
    let mutable loc = Localization.Strings("en")

    /// The atmosphere identities as stored and matched. Only the labels the
    /// user reads are translated, so a theme survives a language change.
    let atmosphereKeys =
        [| "Neon Emerald"; "Obsidian Onyx"; "Supernova Flare"; "Cyber Nebula"; "Emerald Horizon"
           "Midnight Titanium"; "Frost Glacier"; "Eclipse Crimson"; "Deep Astral" |]

    /// Built once and never rebuilt - see AtmosphereOption.
    let atmosphereOptions =
        ObservableCollection<AtmosphereOption>(atmosphereKeys |> Array.map AtmosphereOption)

    let motifKeys =
        [| "Orbital Spheres"; "Prism Auroras"; "Floating Crystals"
           "Stardust Particles"; "Cyber Flux"; "Minimal Clean" |]

    let motifOptions =
        ObservableCollection<AtmosphereOption>(motifKeys |> Array.map AtmosphereOption)

    /// Rewrites the labels in place for the language now in use.
    let relabelAtmospheres (strings: Localization.Strings) =
        let names = strings.AtmosphereNames

        atmosphereOptions
        |> Seq.iteri (fun i option -> if i < names.Length then option.Display <- names.[i])

        let motifNames = strings.MotifNames

        motifOptions
        |> Seq.iteri (fun i option -> if i < motifNames.Length then option.Display <- motifNames.[i])

    let mutable bgBaseBrush: IBrush = SolidColorBrush(Color.Parse("#080D0A"))
    let mutable bgOrb1Brush: IBrush = null
    let mutable bgOrb2Brush: IBrush = null
    let mutable bgVignetteBrush: IBrush = null
    let mutable motifAccentBrush: IBrush = SolidColorBrush(Color.Parse("#35D22B"))

    /// What the space backdrop is filled with when the atmosphere is not
    /// Neon Emerald. Deeper than the motif accent, because the accent is
    /// tuned to read as a highlight against the background rather than to
    /// be the background.
    let mutable bgNebulaTintBrush: IBrush = SolidColorBrush(Color.Parse("#2A9E76"))

    /// Background animations run only while the window is in front.
    let mutable isWindowActive = true

    /// Performance mode: no moving background, no card hover animation.
    let mutable isPerformanceMode = false

    /// The in-game overlay, and the look it wears. Deployed with every install
    /// on the routes that can host it - see `ModInstaller.overlaySupported`.
    let mutable isOverlayEnabled = true
    let mutable overlayTheme = ModInstaller.overlayThemes.[0]
    let mutable overlayHotkey = ModInstaller.overlayHotkeys.[0]

    /// How the library grid is ordered. One of GameScanner.sortModes.
    let mutable sortMode = GameScanner.sortModes.[0]

    let createRadialBrush (centerHex: string) =
        let brush = RadialGradientBrush()
        brush.Center <- RelativePoint(0.5, 0.5, RelativeUnit.Relative)
        brush.GradientOrigin <- RelativePoint(0.5, 0.5, RelativeUnit.Relative)
        brush.RadiusX <- RelativeScalar(0.5, RelativeUnit.Relative)
        brush.RadiusY <- RelativeScalar(0.5, RelativeUnit.Relative)
        brush.GradientStops.Add(GradientStop(Color.Parse(centerHex), 0.0))
        brush.GradientStops.Add(GradientStop(Color.FromArgb(0uy, 0uy, 0uy, 0uy), 1.0))
        brush

    let createVignetteBrush (edgeHex: string) =
        let brush = RadialGradientBrush()
        brush.Center <- RelativePoint(0.5, 0.5, RelativeUnit.Relative)
        brush.GradientOrigin <- RelativePoint(0.5, 0.5, RelativeUnit.Relative)
        brush.RadiusX <- RelativeScalar(0.8, RelativeUnit.Relative)
        brush.RadiusY <- RelativeScalar(0.8, RelativeUnit.Relative)
        brush.GradientStops.Add(GradientStop(Color.FromArgb(0uy, 0uy, 0uy, 0uy), 0.4))
        brush.GradientStops.Add(GradientStop(Color.Parse(edgeHex), 1.0))
        brush

    /// Pushes the current accent into the application's resources.
    ///
    /// Styles in App.axaml cannot see a view-model property, so anything themed
    /// from XAML styles - the scrollbar, toggles, focus rings - used to be a
    /// hardcoded green and stayed green on every other colour scheme. Publishing
    /// the accent here lets those styles say {DynamicResource AppAccentBrush}
    /// and follow the scheme like everything else does.
    let publishAccent () =
        match Application.Current with
        | null -> ()
        | app ->
            let c =
                match motifAccentBrush with
                | :? SolidColorBrush as b -> b.Color
                | _ -> Color.Parse("#35D22B")

            let tint (a: byte) = SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B)) :> IBrush
            app.Resources.["AppAccentColor"] <- box c
            app.Resources.["AppAccentBrush"] <- box (SolidColorBrush(c) :> IBrush)
            app.Resources.["AppAccentStrongBrush"] <- box (tint 220uy)
            app.Resources.["AppAccentSoftBrush"] <- box (tint 120uy)
            app.Resources.["AppAccentGlowBrush"] <- box (tint 60uy)
            app.Resources.["AppAccentFaintBrush"] <- box (tint 28uy)

            // The stock ToggleSwitch reads these by key, so overwriting them is
            // enough to make every switch in the app follow the scheme - no
            // control templates to re-declare.
            app.Resources.["ToggleSwitchFillOn"] <- box (tint 235uy)
            app.Resources.["ToggleSwitchFillOnPointerOver"] <- box (SolidColorBrush(c) :> IBrush)
            app.Resources.["ToggleSwitchFillOnPressed"] <- box (tint 200uy)
            app.Resources.["ToggleSwitchStrokeOn"] <- box (tint 255uy)

            // BoxShadow and Effect are parsed from strings, so a DynamicResource
            // cannot carry a colour into them from XAML. Building them here and
            // publishing the finished objects is what lets the API chip's glow
            // follow the scheme instead of staying green on a blue theme.
            let hex = sprintf "%02X%02X%02X" c.R c.G c.B
            app.Resources.["AppAccentChipShadow"] <-
                box (BoxShadows.Parse(sprintf "0 2 10 0 #99000000, 0 0 12 0 #44%s" hex))
            app.Resources.["AppAccentPipShadow"] <-
                box (BoxShadows.Parse(sprintf "0 0 6 0 #CC%s" hex))
            app.Resources.["AppAccentTextGlow"] <-
                box (Effect.Parse(sprintf "drop-shadow(0 0 5 #AA%s)" hex))

    let applyAtmosphere (colorTheme: string) =
        selectedColorAtmosphere <- colorTheme
        match colorTheme with
        | "Obsidian Onyx" ->
            bgBaseBrush <- SolidColorBrush(Color.Parse("#0C0E14"))
            bgOrb1Brush <- createRadialBrush("#283552")
            bgOrb2Brush <- createRadialBrush("#1E2038")
            bgVignetteBrush <- createVignetteBrush("#5004030A")
            bgNebulaTintBrush <- SolidColorBrush(Color.Parse("#2E7FB8"))
            motifAccentBrush <- SolidColorBrush(Color.Parse("#38BDF8"))
        | "Supernova Flare" ->
            bgBaseBrush <- SolidColorBrush(Color.Parse("#140A0C"))
            bgOrb1Brush <- createRadialBrush("#5C1A22")
            bgOrb2Brush <- createRadialBrush("#3D101C")
            bgVignetteBrush <- createVignetteBrush("#550E0305")
            bgNebulaTintBrush <- SolidColorBrush(Color.Parse("#B32B44"))
            motifAccentBrush <- SolidColorBrush(Color.Parse("#F43F5E"))
        | "Cyber Nebula" ->
            bgBaseBrush <- SolidColorBrush(Color.Parse("#0D0B16"))
            bgOrb1Brush <- createRadialBrush("#3D1E5A")
            bgOrb2Brush <- createRadialBrush("#1C2454")
            bgVignetteBrush <- createVignetteBrush("#5007030F")
            bgNebulaTintBrush <- SolidColorBrush(Color.Parse("#8B5CC4"))
            motifAccentBrush <- SolidColorBrush(Color.Parse("#C084FC"))
        | "Emerald Horizon" ->
            bgBaseBrush <- SolidColorBrush(Color.Parse("#081010"))
            bgOrb1Brush <- createRadialBrush("#14423A")
            bgOrb2Brush <- createRadialBrush("#0E3140")
            bgVignetteBrush <- createVignetteBrush("#50020808")
            bgNebulaTintBrush <- SolidColorBrush(Color.Parse("#2A9E76"))
            motifAccentBrush <- SolidColorBrush(Color.Parse("#34D399"))
        | "Midnight Titanium" ->
            bgBaseBrush <- SolidColorBrush(Color.Parse("#0A0A0C"))
            bgOrb1Brush <- createRadialBrush("#2D2E38")
            bgOrb2Brush <- createRadialBrush("#1C1D24")
            bgVignetteBrush <- createVignetteBrush("#50000000")
            bgNebulaTintBrush <- SolidColorBrush(Color.Parse("#8A93A3"))
            motifAccentBrush <- SolidColorBrush(Color.Parse("#E2E8F0"))
        | "Frost Glacier" ->
            bgBaseBrush <- SolidColorBrush(Color.Parse("#091017"))
            bgOrb1Brush <- createRadialBrush("#18435C")
            bgOrb2Brush <- createRadialBrush("#162842")
            bgVignetteBrush <- createVignetteBrush("#50030A12")
            bgNebulaTintBrush <- SolidColorBrush(Color.Parse("#2E7FB8"))
            motifAccentBrush <- SolidColorBrush(Color.Parse("#38BDF8"))
        | "Eclipse Crimson" ->
            bgBaseBrush <- SolidColorBrush(Color.Parse("#12070A"))
            bgOrb1Brush <- createRadialBrush("#4C111F")
            bgOrb2Brush <- createRadialBrush("#2E0814")
            bgVignetteBrush <- createVignetteBrush("#550F0206")
            bgNebulaTintBrush <- SolidColorBrush(Color.Parse("#A81638"))
            motifAccentBrush <- SolidColorBrush(Color.Parse("#E11D48"))
        | "Deep Astral" ->
            bgBaseBrush <- SolidColorBrush(Color.Parse("#070B14"))
            bgOrb1Brush <- createRadialBrush("#152C4E")
            bgOrb2Brush <- createRadialBrush("#0F1A30")
            bgVignetteBrush <- createVignetteBrush("#5002060D")
            bgNebulaTintBrush <- SolidColorBrush(Color.Parse("#4675B8"))
            motifAccentBrush <- SolidColorBrush(Color.Parse("#60A5FA"))
        | _ ->
            // Neon Emerald: signature DLSS 5 SUITE atmosphere (matches the logo mark)
            selectedColorAtmosphere <- "Neon Emerald"
            bgBaseBrush <- SolidColorBrush(Color.Parse("#080D0A"))
            bgOrb1Brush <- createRadialBrush("#14401C")
            bgOrb2Brush <- createRadialBrush("#12291F")
            bgVignetteBrush <- createVignetteBrush("#55020604")
            bgNebulaTintBrush <- SolidColorBrush(Color.Parse("#35D22B"))
            motifAccentBrush <- SolidColorBrush(Color.Parse("#35D22B"))

        publishAccent ()

    /// A settings card answers for itself whether the query is about it.
    /// Keywords carry both the English names and whatever the current language
    /// shows, so searching works without having to think in English.
    let matchesCard (query: string) (keywords: string seq) =
        if String.IsNullOrWhiteSpace(query) then
            true
        else
            let needle = query.Trim().ToLowerInvariant()

            keywords
            |> Seq.exists (fun (k: string) ->
                not (String.IsNullOrWhiteSpace(k)) && k.ToLowerInvariant().Contains(needle))

    /// Orders the library.
    ///
    /// Install time is the install folder's creation date, not the Steam
    /// manifest's LastUpdated - that one moves every time a game patches, so
    /// sorting on it put whatever updated last night at the top regardless of
    /// when it was actually installed.
    ///
    /// Release date comes from the Steam store and is cached with the rest of
    /// the analysis. Anything without one - a non-Steam title, or a lookup
    /// that failed - sorts to the end in both directions rather than pretending
    /// to be either the oldest or the newest thing in the library.
    let analysisDate (pick: AnalysisStore.GameAnalysis -> string) (card: GameCardViewModel) =
        match AnalysisStore.tryGet card.Game with
        | Some a ->
            let raw = pick a
            if String.IsNullOrWhiteSpace(raw) then None
            else
                match DateTime.TryParse(raw, Globalization.CultureInfo.InvariantCulture,
                                        Globalization.DateTimeStyles.RoundtripKind) with
                | true, d -> Some d
                | _ -> None
        | None -> None

    let sortCards (cards: GameCardViewModel seq) =
        let byDate pick newestFirst =
            let dated, undated =
                cards
                |> Seq.map (fun c -> c, analysisDate pick c)
                |> Seq.toList
                |> List.partition (fun (_, d) -> d.IsSome)

            let ordered =
                if newestFirst then dated |> List.sortByDescending (fun (_, d) -> d.Value)
                else dated |> List.sortBy (fun (_, d) -> d.Value)

            // Undated titles trail the list, in name order so they are at
            // least predictable among themselves.
            (ordered |> List.map fst)
            @ (undated |> List.map fst |> List.sortBy (fun c -> c.Title.ToLowerInvariant()))
            |> Seq.ofList

        match sortMode with
        | "Name (Z-A)" -> cards |> Seq.sortByDescending (fun c -> c.Title.ToLowerInvariant())
        | "Recently installed" -> byDate (fun a -> a.InstalledAtUtc) true
        | "Oldest installed" -> byDate (fun a -> a.InstalledAtUtc) false
        | "Newest release" -> byDate (fun a -> a.ReleaseDateUtc) true
        | "Oldest release" -> byDate (fun a -> a.ReleaseDateUtc) false
        | _ -> cards |> Seq.sortBy (fun c -> c.Title.ToLowerInvariant())

    let filterGamesList () =
        filteredGames.Clear()
        let query = searchText.Trim().ToLowerInvariant()

        allGames
        |> Seq.filter (fun card ->
            (showNonGameApps || not (NonGameAppClassifier.isUtility card.Game))
            && (if String.IsNullOrWhiteSpace(query) then true
            else
                card.Title.ToLowerInvariant().Contains(query)
                || card.LauncherType.ToLowerInvariant().Contains(query)))
        |> sortCards
        |> Seq.iter filteredGames.Add

    let filterEmulatorsList () =
        filteredEmulators.Clear()
        let query = searchText.Trim().ToLowerInvariant()

        for card in allEmulators do
            if String.IsNullOrWhiteSpace(query) || card.Title.ToLowerInvariant().Contains(query) then
                filteredEmulators.Add(card)

    do
        // 0. Restore saved user preferences (layout + theme atmosphere + motif)
        let settings = GameScanner.loadSettings ()
        isSidebarLayout <- settings.IsSidebarLayout
        showNonGameApps <- settings.ShowNonGameApps
        selectedGeometricMotif <- settings.GeometricMotif
        applyAtmosphere settings.ColorAtmosphere

        languageCode <- if Localization.isKnownLanguage settings.Language then settings.Language else "en"
        loc <- Localization.Strings(languageCode)
        Localization.current <- Localization.Strings(languageCode)
        relabelAtmospheres loc

        isAmdMode <- settings.AmdMode
        isPerformanceMode <- settings.PerformanceMode
        isOverlayEnabled <- not settings.OverlayDisabled

        overlayTheme <-
            if ModInstaller.overlayThemes |> Array.exists (fun t -> t = settings.OverlayTheme) then
                settings.OverlayTheme
            else
                ModInstaller.overlayThemes.[0]

        overlayHotkey <-
            if ModInstaller.overlayHotkeys |> Array.exists (fun k -> k = settings.OverlayHotkey) then
                settings.OverlayHotkey
            else
                ModInstaller.overlayHotkeys.[0]

        sortMode <-
            if GameScanner.sortModes |> Array.exists (fun m -> m = settings.SortMode) then
                settings.SortMode
            else
                GameScanner.sortModes.[0]

        for (key, items) in ExtrasStore.groups () do
            extraRows.Add(ExtraRowViewModel(key, items))

        supportPromptVersion <-
            if isNull (box settings.SupportPromptVersion) then "" else settings.SupportPromptVersion

        // Only arm it when this build has not asked yet. A single tick, then
        // the timer stops for good.
        if supportPromptVersion <> UpdateChecker.CurrentVersion then
            supportPromptTimer.Tick.Add(fun _ ->
                supportPromptTimer.Stop()
                this.IsSupportPromptVisible <- true

                supportPromptVersion <- UpdateChecker.CurrentVersion

                GameScanner.saveSettings
                    { IsSidebarLayout = isSidebarLayout
                      ColorAtmosphere = selectedColorAtmosphere
                      GeometricMotif = selectedGeometricMotif
                      Language = languageCode
                      SupportPromptVersion = supportPromptVersion
                      AmdMode = isAmdMode
                      PerformanceMode = isPerformanceMode
                      OverlayDisabled = not isOverlayEnabled
                      OverlayTheme = overlayTheme
                      OverlayHotkey = overlayHotkey
                      SortMode = sortMode
                      ShowNonGameApps = showNonGameApps })

            supportPromptTimer.Start()

        // Emulators are hand-curated, so they simply come back as they were.
        for e in GameScanner.loadCachedEmulators () do
            let card = GameCardViewModel(e)
            allEmulators.Add(card)
            filteredEmulators.Add(card)

        // 1. Instant load from the local cache (0 ms perceived startup)
        let cached = GameScanner.loadCachedGames ()

        // A cache written by an older build may be missing whatever this one
        // learned to detect, so the first launch after an update re-reads the
        // library by itself. StartScanAsync keeps hand-added games.
        let firstRunOfThisBuild =
            GameScanner.readCacheVersion () <> UpdateChecker.CurrentVersion

        if cached.Length > 0 then
            for g in cached do
                let card = GameCardViewModel(g)
                allGames.Add(card)
                filteredGames.Add(card)
            totalGamesCount <- allGames.Count
            scanStatusText <- sprintf "%d Games Ready" cached.Length
            isScanning <- false

        if firstRunOfThisBuild then
            // 2. Just updated: the cards above are shown straight away so the
            //    window is never blank, and the real scan replaces them.
            GameScanner.writeCacheVersion UpdateChecker.CurrentVersion
            this.StartScanAsync()
        elif cached.Length > 0 then
            // Top up the deep-scan cache for anything added before this feature
            // existed. Does nothing (and shows nothing) when everything is cached.
            this.AnalyzePendingGames(cached)
        else
            // First run / empty cache: full library scan
            this.StartScanAsync()

        // 3. Look for a newer build every launch. It runs off the UI thread and
        //    stays silent unless there really is one, so startup is unaffected.
        this.CheckForUpdatesOnStartup()

    // ---------------------------------------------------------------------
    // THEME / ATMOSPHERE
    // ---------------------------------------------------------------------
    member this.BgBaseBrush = bgBaseBrush
    member this.BgOrb1Brush = bgOrb1Brush
    member this.BgOrb2Brush = bgOrb2Brush
    member this.BgVignetteBrush = bgVignetteBrush
    member this.MotifAccentBrush = motifAccentBrush
    member this.BgNebulaTintBrush = bgNebulaTintBrush

    /// Neon Emerald shows the space plate as painted - it is the signature
    /// look and the one the artwork was made for. Every other atmosphere
    /// shows the same plate as a mask filled with its own colour, so the
    /// backdrop follows the interface instead of leaving a green wash
    /// under a crimson or ice-blue one.
    member this.IsEmeraldAtmosphere = (selectedColorAtmosphere = "Neon Emerald")
    member this.IsTintedAtmosphere = (selectedColorAtmosphere <> "Neon Emerald")

    /// Decodes a backdrop image once, then hands out the same one forever.
    ///
    /// These used to be bound as strings. Avalonia resolves a string Source
    /// through its asset loader every time the binding is evaluated, so a 4K
    /// JPEG was being decoded again and again while the window sat still -
    /// which is what pegged a core and leaked about a megabyte a second.
    /// Binding an already-decoded IImage does the work once per theme, and
    /// coming back to a theme later costs nothing.
    static member private LoadAsset(fileName: string) : IImage =
        match assetCache.TryGetValue(fileName) with
        | true, image -> image
        | _ ->
            let image =
                try
                    let uri = Uri("avares://DLSS 5 SUITE/Assets/" + fileName)
                    use stream = AssetLoader.Open(uri)
                    new Bitmap(stream) :> IImage
                with _ ->
                    null

            assetCache.[fileName] <- image
            image

    /// The atmosphere's asset suffix. One place decides it, so the nebula and
    /// the mark can never disagree about which theme is on screen.
    member private this.AtmosphereSlug =
        match selectedColorAtmosphere with
        | "Obsidian Onyx" -> "_onyx"
        | "Supernova Flare" -> "_supernova"
        | "Cyber Nebula" -> "_cyber"
        | "Emerald Horizon" -> "_emeraldh"
        | "Midnight Titanium" -> "_titanium"
        | "Frost Glacier" -> "_frost"
        | "Eclipse Crimson" -> "_eclipse"
        | "Deep Astral" -> "_astral"
        | _ -> ""

    /// The space plate for this atmosphere, decoded once and kept.
    ///
    /// Pre-tinted rather than masked. Filling a Border with the accent colour
    /// through a 4K OpacityMask cost an offscreen compositor pass every frame
    /// and kept a second 4K texture resident purely to be a stencil - and it
    /// threw away the artwork's own shading in the process. Nine images on
    /// disk is the cheaper trade: only the bound one is ever decoded, and each
    /// keeps the nebula's real structure in its theme's colour.
    member this.NebulaSource : IImage = MainViewModel.LoadAsset("bg_nebula" + this.AtmosphereSlug + ".jpg")

    /// The mark, recoloured for the current atmosphere.
    /// Returns the decoded bitmap, not a path - see LoadAsset.
    ///
    /// This used to be a Border filled with the accent colour and masked to
    /// the mark's shape. That reproduced the silhouette and nothing else - no
    /// bevel, no rim light - so on every theme but Neon Emerald it vanished
    /// against the shadow behind it. These are the real artwork with its
    /// luminance mapped onto each theme's accent, so the shape keeps its
    /// modelling whatever colour it is wearing.
    member this.NvidiaMarkSource : IImage =
        MainViewModel.LoadAsset("bg_nvidia_mark" + this.AtmosphereSlug + ".png")

    /// Drives the background animations: only while the window is the active
    /// one, and never in performance mode.
    member this.IsBackgroundMotionOn = isWindowActive && not isPerformanceMode

    // ---------------------------------------------------------------------
    // LIBRARY ORDER
    // ---------------------------------------------------------------------
    /// The labels shown in the sort picker, in the same order as
    /// GameScanner.sortModes. The modes themselves stay in English because they
    /// are what gets written to settings.json - translating those would mean a
    /// saved sort stopped being recognised the moment the language changed.
    member this.SortOptions =
        [| loc.SortNameAz
           loc.SortNameZa
           loc.SortRecentlyInstalled
           loc.SortOldestInstalled
           loc.SortNewestRelease
           loc.SortOldestRelease |]

    member this.SelectedSortIndex
        with get () =
            GameScanner.sortModes
            |> Array.tryFindIndex ((=) sortMode)
            |> Option.defaultValue 0
        and set (index: int) =
            // A list swap momentarily reports -1; that is not a user choice.
            if index >= 0 && index < GameScanner.sortModes.Length then
                let value = GameScanner.sortModes.[index]

                if value <> sortMode then
                    sortMode <- value
                    filterGamesList ()
                    this.RaisePropertyChanged("SelectedSortIndex")

                    GameScanner.saveSettings
                        { IsSidebarLayout = isSidebarLayout
                          ColorAtmosphere = selectedColorAtmosphere
                          GeometricMotif = selectedGeometricMotif
                          Language = languageCode
                          SupportPromptVersion = supportPromptVersion
                          AmdMode = isAmdMode
                          PerformanceMode = isPerformanceMode
                          OverlayDisabled = not isOverlayEnabled
                          OverlayTheme = overlayTheme
                          OverlayHotkey = overlayHotkey
                          SortMode = sortMode
                          ShowNonGameApps = showNonGameApps }

    member this.IsWindowActive
        with get () = isWindowActive
        and set value =
            if isWindowActive <> value then
                isWindowActive <- value
                this.RaisePropertyChanged("IsBackgroundMotionOn")

    /// Turns off the moving background and the card hover animation. The look
    /// is unchanged at rest; only the motion goes.
    member this.IsPerformanceMode
        with get () = isPerformanceMode
        and set value =
            if isPerformanceMode <> value then
                isPerformanceMode <- value
                this.RaisePropertyChanged("IsPerformanceMode")
                this.RaisePropertyChanged("IsBackgroundMotionOn")

                GameScanner.saveSettings
                    { IsSidebarLayout = isSidebarLayout
                      ColorAtmosphere = selectedColorAtmosphere
                      GeometricMotif = selectedGeometricMotif
                      Language = languageCode
                      SupportPromptVersion = supportPromptVersion
                      AmdMode = isAmdMode
                      PerformanceMode = isPerformanceMode
                      OverlayDisabled = not isOverlayEnabled
                      OverlayTheme = overlayTheme
                      OverlayHotkey = overlayHotkey
                      SortMode = sortMode
                      ShowNonGameApps = showNonGameApps }

    /// Shown translated, stored in English: `AtmosphereOption.Key` is the
    /// identity. The collection instance is stable for the life of the window.
    member this.ColorAtmospheres = atmosphereOptions

    /// The picker binds to the index, not the text. Changing language replaces
    /// every item in the list, and a selection held by value would no longer
    /// match anything and come back blank; the index survives untouched.
    member this.SelectedAtmosphereIndex
        with get () =
            atmosphereKeys
            |> Array.tryFindIndex ((=) selectedColorAtmosphere)
            |> Option.defaultValue 0
        and set (index: int) =
            // A list swap momentarily reports -1; that is not a user choice.
            if index >= 0 && index < atmosphereKeys.Length then
                let value = atmosphereKeys.[index]

                if value <> selectedColorAtmosphere then
                    applyAtmosphere value
                    this.RaisePropertyChanged("BgBaseBrush")
                    this.RaisePropertyChanged("BgOrb1Brush")
                    this.RaisePropertyChanged("BgOrb2Brush")
                    this.RaisePropertyChanged("BgVignetteBrush")
                    this.RaisePropertyChanged("MotifAccentBrush")
                    this.RaisePropertyChanged("BgNebulaTintBrush")
                    this.RaisePropertyChanged("IsEmeraldAtmosphere")
                    this.RaisePropertyChanged("IsTintedAtmosphere")
                    this.RaisePropertyChanged("NvidiaMarkSource")
                    this.RaisePropertyChanged("NebulaSource")
                    this.RaisePropertyChanged("SelectedAtmosphereIndex")

                    GameScanner.saveSettings
                        { IsSidebarLayout = isSidebarLayout
                          ColorAtmosphere = value
                          GeometricMotif = selectedGeometricMotif
                          Language = languageCode
                          SupportPromptVersion = supportPromptVersion
                          AmdMode = isAmdMode
                          PerformanceMode = isPerformanceMode
                          OverlayDisabled = not isOverlayEnabled
                          OverlayTheme = overlayTheme
                          OverlayHotkey = overlayHotkey
                          SortMode = sortMode
                          ShowNonGameApps = showNonGameApps }

    member this.SelectedColorAtmosphere
        with get () =
            let index = atmosphereKeys |> Array.tryFindIndex ((=) selectedColorAtmosphere)
            loc.AtmosphereNames.[defaultArg index 0]
        and set (display: string) =
            let value =
                loc.AtmosphereNames
                |> Array.tryFindIndex ((=) display)
                |> Option.map (fun i -> atmosphereKeys.[i])
                |> Option.defaultValue display

            if not (String.IsNullOrWhiteSpace(value)) && value <> selectedColorAtmosphere then
                applyAtmosphere value
                this.RaisePropertyChanged("BgBaseBrush")
                this.RaisePropertyChanged("BgOrb1Brush")
                this.RaisePropertyChanged("BgOrb2Brush")
                this.RaisePropertyChanged("BgVignetteBrush")
                this.RaisePropertyChanged("MotifAccentBrush")
                this.RaisePropertyChanged("BgNebulaTintBrush")
                this.RaisePropertyChanged("IsEmeraldAtmosphere")
                this.RaisePropertyChanged("IsTintedAtmosphere")
                this.RaisePropertyChanged("NvidiaMarkSource")
                this.RaisePropertyChanged("NebulaSource")
                this.RaisePropertyChanged("SelectedColorAtmosphere")
                GameScanner.saveSettings
                    { IsSidebarLayout = isSidebarLayout
                      ColorAtmosphere = value
                      GeometricMotif = selectedGeometricMotif
                      Language = languageCode
                      SupportPromptVersion = supportPromptVersion
                      AmdMode = isAmdMode
                      PerformanceMode = isPerformanceMode
                      OverlayDisabled = not isOverlayEnabled
                      OverlayTheme = overlayTheme
                      OverlayHotkey = overlayHotkey
                      SortMode = sortMode
                      ShowNonGameApps = showNonGameApps }

    // ---------------------------------------------------------------------
    // LANGUAGE
    // ---------------------------------------------------------------------
    /// Every visible string is read through this one object. Swapping it and
    /// announcing the change is what repaints the whole window in the new
    /// language - no per-label notifications to forget.
    member this.Loc = loc

    member this.Languages = Localization.availableLanguages |> Array.map snd

    member this.SelectedLanguage
        with get () = Localization.displayName languageCode
        and set (display: string) =
            let code =
                Localization.availableLanguages
                |> Array.tryFind (fun (_, name) -> name = display)
                |> Option.map fst
                |> Option.defaultValue languageCode

            if code <> languageCode then
                languageCode <- code
                loc <- Localization.Strings(code)
                Localization.current <- Localization.Strings(code)

                this.RaisePropertyChanged("Loc")
                this.RaisePropertyChanged("SelectedLanguage")
                // Labels change, the list does not - so the picker keeps its
                // selection and simply redraws it in the new language.
                relabelAtmospheres loc
                this.RaisePropertyChanged("TotalGamesText")
                this.RaisePropertyChanged("InstallModeHintText")
                // Strings this build added, which are not part of Loc's own
                // change notification.
                this.RaisePropertyChanged("SortOptions")
                this.RaisePropertyChanged("SelectedSortIndex")
                this.RaisePropertyChanged("SupportPromptText")
                this.RaisePropertyChanged("DonateLabel")
                this.RaisePropertyChanged("OriginalDonateLabel")
                this.RaiseDlss5State()

                GameScanner.saveSettings
                    { IsSidebarLayout = isSidebarLayout
                      ColorAtmosphere = selectedColorAtmosphere
                      GeometricMotif = selectedGeometricMotif
                      Language = code
                      SupportPromptVersion = supportPromptVersion
                      AmdMode = isAmdMode
                      PerformanceMode = isPerformanceMode
                      OverlayDisabled = not isOverlayEnabled
                      OverlayTheme = overlayTheme
                      OverlayHotkey = overlayHotkey
                      SortMode = sortMode
                      ShowNonGameApps = showNonGameApps }

    /// "TOTAL GAMES: 42" in the current language.
    member this.TotalGamesText = loc.TotalGames(totalGamesCount)

    /// Same arrangement as the colour atmospheres: translated labels on stable
    /// objects, English keys underneath, so a language change cannot lose the
    /// selection or the saved value.
    member this.GeometricMotifs = motifOptions

    member this.SelectedMotifIndex
        with get () =
            motifKeys
            |> Array.tryFindIndex ((=) selectedGeometricMotif)
            |> Option.defaultValue 0
        and set (index: int) =
            // A list swap momentarily reports -1; that is not a user choice.
            if index >= 0 && index < motifKeys.Length then
                this.SelectedGeometricMotif <- motifKeys.[index]

    member this.SelectedGeometricMotif
        with get () = selectedGeometricMotif
        and set value =
            if not (String.IsNullOrWhiteSpace(value)) && value <> selectedGeometricMotif then
                selectedGeometricMotif <- value
                this.RaisePropertyChanged("SelectedGeometricMotif")
                this.RaisePropertyChanged("IsOrbitalSpheresVisible")
                this.RaisePropertyChanged("IsPrismAurorasVisible")
                this.RaisePropertyChanged("IsFloatingCrystalsVisible")
                this.RaisePropertyChanged("IsStardustParticlesVisible")
                this.RaisePropertyChanged("IsCyberFluxVisible")
                GameScanner.saveSettings
                    { IsSidebarLayout = isSidebarLayout
                      ColorAtmosphere = selectedColorAtmosphere
                      GeometricMotif = value
                      Language = languageCode
                      SupportPromptVersion = supportPromptVersion
                      AmdMode = isAmdMode
                      PerformanceMode = isPerformanceMode
                      OverlayDisabled = not isOverlayEnabled
                      OverlayTheme = overlayTheme
                      OverlayHotkey = overlayHotkey
                      SortMode = sortMode
                      ShowNonGameApps = showNonGameApps }

    member this.IsOrbitalSpheresVisible = selectedGeometricMotif = "Orbital Spheres"
    member this.IsPrismAurorasVisible = selectedGeometricMotif = "Prism Auroras"
    member this.IsFloatingCrystalsVisible = selectedGeometricMotif = "Floating Crystals"
    member this.IsStardustParticlesVisible = selectedGeometricMotif = "Stardust Particles"
    member this.IsCyberFluxVisible = selectedGeometricMotif = "Cyber Flux"

    // ---------------------------------------------------------------------
    // LAYOUT & NAVIGATION
    // ---------------------------------------------------------------------
    member this.IsSidebarLayout
        with get () = isSidebarLayout
        and set value =
            if this.SetProperty(&isSidebarLayout, value) then
                this.RaisePropertyChanged("IsTopBarLayout")
                GameScanner.saveSettings
                    { IsSidebarLayout = value
                      ColorAtmosphere = selectedColorAtmosphere
                      GeometricMotif = selectedGeometricMotif
                      Language = languageCode
                      SupportPromptVersion = supportPromptVersion
                      AmdMode = isAmdMode
                      PerformanceMode = isPerformanceMode
                      OverlayDisabled = not isOverlayEnabled
                      OverlayTheme = overlayTheme
                      OverlayHotkey = overlayHotkey
                      SortMode = sortMode
                      ShowNonGameApps = showNonGameApps }

    member this.IsTopBarLayout = not isSidebarLayout

    member this.SetLayoutMode(isSidebar: bool) = this.IsSidebarLayout <- isSidebar

    /// Three pages, one at a time. Everything else reads off this.
    member this.ActiveSection
        with get () = activeSection
        and set (value: string) =
            if activeSection <> value then
                activeSection <- value
                isSettingsOpen <- (value = "settings")

                this.RaisePropertyChanged("ActiveSection")
                this.RaisePropertyChanged("IsSettingsOpen")
                this.RaisePropertyChanged("IsGamesViewVisible")
                this.RaisePropertyChanged("IsEmulatorsViewVisible")
                this.RaisePropertyChanged("IsGamesTabActive")
                this.RaisePropertyChanged("IsEmulatorsTabActive")
                this.RaisePropertyChanged("IsCommunityViewVisible")
                this.RaisePropertyChanged("IsCommunityTabActive")
                this.RaisePropertyChanged("SearchPlaceholder")

                // The community grid is server-side, so it is fetched the first
                // time the section is opened and never before - a user who
                // never goes there makes no network call at all.
                if value = "community" then
                    community.ApplyQuery(searchText)
                    community.EnsureLoaded()

    member this.IsSettingsOpen
        with get () = isSettingsOpen
        and set value = this.ActiveSection <- (if value then "settings" else "games")

    /// One box, four jobs - it says which one it is doing right now.
    member this.SearchPlaceholder =
        match activeSection with
        | "settings" -> "Search settings..."
        | "emulators" -> "Search emulators..."
        | "community" -> "Search community..."
        | _ -> "Search games..."

    member this.IsGamesViewVisible = activeSection = "games"
    member this.IsEmulatorsViewVisible = activeSection = "emulators"
    member this.IsCommunityViewVisible = activeSection = "community"
    member this.IsGamesTabActive = activeSection = "games"
    member this.IsEmulatorsTabActive = activeSection = "emulators"
    member this.IsCommunityTabActive = activeSection = "community"

    /// The community section's own state. Exposed so the window can bind to it
    /// as `Community.X` rather than mirroring three dozen properties here.
    member _.Community = community

    member this.OpenSettings() = this.ActiveSection <- "settings"
    member _.LosslessScalingStatus = losslessScalingStatus
    member _.CanCheckLosslessScaling = not isCheckingLosslessScaling
    member _.IsSettingUpLosslessScaling = isCheckingLosslessScaling
    member _.LosslessScalingProgress = losslessScalingProgress
    member _.ShaderGlassStatus = shaderGlassStatus
    member _.CanLaunchShaderGlass = verifiedShaderGlassPath.IsSome
    member _.IsSettingUpShaderGlass = isSettingUpShaderGlass
    member _.ShaderGlassProgress = shaderGlassProgress

    member this.CheckShaderGlass() =
        let result = ShaderGlassDetector.discover ()
        verifiedShaderGlassPath <-
            match result with
            | ShaderGlassDetector.Ready path
            | ShaderGlassDetector.NeedsUpdate path -> Some path
            | _ -> None
        shaderGlassStatus <- ShaderGlassDetector.describe result
        this.RaisePropertyChanged("ShaderGlassStatus")
        this.RaisePropertyChanged("CanLaunchShaderGlass")

    member _.VerifiedShaderGlassPath = verifiedShaderGlassPath

    member this.SetupShaderGlass() = async {
        if not isSettingUpShaderGlass then
            isSettingUpShaderGlass <- true
            shaderGlassProgress <- 0.0
            this.RaisePropertyChanged("IsSettingUpShaderGlass")
            this.RaisePropertyChanged("ShaderGlassProgress")
            let! result = async {
                try
                    let report text value =
                        Dispatcher.UIThread.Post(fun () ->
                            shaderGlassStatus <- text
                            shaderGlassProgress <- value
                            this.RaisePropertyChanged("ShaderGlassStatus")
                            this.RaisePropertyChanged("ShaderGlassProgress"))
                    let! path = ShaderGlassDetector.setupLatest report |> Async.AwaitTask
                    verifiedShaderGlassPath <- Some path
                    return Some path
                with ex ->
                    shaderGlassStatus <- "Setup stopped safely: " + ex.Message
                    this.RaisePropertyChanged("ShaderGlassStatus")
                    return None
            }
            try return result
            finally
                isSettingUpShaderGlass <- false
                this.RaisePropertyChanged("IsSettingUpShaderGlass")
                this.RaisePropertyChanged("CanLaunchShaderGlass")
        else
            return None
    }

    member this.CheckLosslessScaling() =
        if not isCheckingLosslessScaling then
            isCheckingLosslessScaling <- true
            losslessScalingStatus <- "Checking Steam libraries..."
            this.RaisePropertyChanged("CanCheckLosslessScaling")
            this.RaisePropertyChanged("LosslessScalingStatus")
            async {
                let! status =
                    System.Threading.Tasks.Task.Run(fun () ->
                        try LosslessScalingDetector.discover () |> LosslessScalingDetector.describe
                        with ex -> "Could not complete discovery: " + ex.Message)
                    |> Async.AwaitTask
                Dispatcher.UIThread.Post(fun () ->
                    losslessScalingStatus <- status
                    isCheckingLosslessScaling <- false
                    this.RaisePropertyChanged("LosslessScalingStatus")
                    this.RaisePropertyChanged("CanCheckLosslessScaling"))
            } |> Async.Start

    member this.SetupLosslessScaling() = async {
        if isCheckingLosslessScaling then return None
        else
            isCheckingLosslessScaling <- true
            losslessScalingProgress <- 0.0
            this.RaisePropertyChanged("CanCheckLosslessScaling")
            this.RaisePropertyChanged("IsSettingUpLosslessScaling")
            this.RaisePropertyChanged("LosslessScalingProgress")
            let! result = async {
                try
                    let report text value =
                        Dispatcher.UIThread.Post(fun () ->
                            losslessScalingStatus <- text
                            losslessScalingProgress <- value
                            this.RaisePropertyChanged("LosslessScalingStatus")
                            this.RaisePropertyChanged("LosslessScalingProgress"))
                    let! executable, monitors = LosslessScalingInstaller.setupLatest report |> Async.AwaitTask
                    let displayNote =
                        if monitors >= 2 then "Keep the game on display 1 and this Lossless Scaling window visible on display 2."
                        else "Only one display is detected. Enable a second or virtual phone display, keep the game on display 1, and place Lossless Scaling on display 2."
                    losslessScalingStatus <- "Setup complete. " + displayNote + " Apply scaling, click the Lossless Scaling window, then press Home to open ReShade. Do not minimize either window."
                    this.RaisePropertyChanged("LosslessScalingStatus")
                    return Some executable
                with ex ->
                    losslessScalingStatus <- "Setup stopped safely: " + ex.Message
                    this.RaisePropertyChanged("LosslessScalingStatus")
                    return None
            }
            isCheckingLosslessScaling <- false
            this.RaisePropertyChanged("CanCheckLosslessScaling")
            this.RaisePropertyChanged("IsSettingUpLosslessScaling")
            return result
    }
    member this.CloseSettings() = this.ActiveSection <- "games"
    member this.ShowGames() = this.ActiveSection <- "games"
    member this.ShowEmulators() = this.ActiveSection <- "emulators"
    member this.ShowCommunity() = this.ActiveSection <- "community"

    /// "Share result" in the Manage sheet. The post is built from the install
    /// this app made, so the route, API, bit-width and add-ons are already
    /// filled in and the user only picks the verdict.
    member this.ShareToCommunity() =
        match manageCard with
        | Some card ->
            this.IsManageOpen <- false

            community.OpenComposer(
                card.Game,
                isOverlayEnabled,
                ModInstaller.modeKey installMode,
                ModInstaller.optiApiKey optiApi,
                ModInstaller.archKey installArch,
                useNeuralAddon
            )

            this.ActiveSection <- "community"
        | None -> ()

    member this.ToggleSettings() =
        this.ActiveSection <- (if activeSection = "settings" then "games" else "settings")

    // ---------------------------------------------------------------------
    // LIBRARY
    // ---------------------------------------------------------------------
    member this.Games = filteredGames
    member this.ShowNonGameApps
        with get () = showNonGameApps
        and set value =
            if this.SetProperty(&showNonGameApps, value) then
                filterGamesList ()
                this.RaisePropertyChanged("HasGames")
                GameScanner.saveSettings
                    { IsSidebarLayout = isSidebarLayout
                      ColorAtmosphere = selectedColorAtmosphere
                      GeometricMotif = selectedGeometricMotif
                      Language = languageCode
                      SupportPromptVersion = supportPromptVersion
                      AmdMode = isAmdMode
                      PerformanceMode = isPerformanceMode
                      OverlayDisabled = not isOverlayEnabled
                      OverlayTheme = overlayTheme
                      OverlayHotkey = overlayHotkey
                      SortMode = sortMode
                      ShowNonGameApps = showNonGameApps }
    member this.AllGamesCount = totalGamesCount
    member this.HasGames = filteredGames.Count > 0

    member this.SearchText
        with get () = searchText
        and set value =
            if this.SetProperty(&searchText, value) then
                filterGamesList ()
                filterEmulatorsList ()
                this.RaisePropertyChanged("HasGames")
                this.RaisePropertyChanged("HasEmulators")
                // The same box filters whichever page is open. The community
                // list is filtered by the server, so it only re-queries while
                // that section is the one on screen.
                this.RaiseSettingsFilter()
                if activeSection = "community" then community.ApplyQuery(value)

    // ---------------------------------------------------------------------
    // COLLAPSIBLE SETTINGS SECTIONS
    // ---------------------------------------------------------------------
    /// The long sections start folded so the page reads as a short list.
    /// Support, AMD mode and About stay open - they are one line each.
    member this.IsInterfaceSectionOpen = openSections.Contains("interface")
    member this.IsAtmosphereSectionOpen = openSections.Contains("atmosphere")
    member this.IsLibrarySectionOpen = openSections.Contains("library")
    member this.IsPayloadSectionOpen = openSections.Contains("payload")

    member private this.Chevron(key: string) =
        if openSections.Contains(key) then "M 7,14 L 12,9 L 17,14 Z" else "M 7,10 L 12,15 L 17,10 Z"

    member this.InterfaceChevron = this.Chevron("interface")
    member this.AtmosphereChevron = this.Chevron("atmosphere")
    member this.LibraryChevron = this.Chevron("library")
    member this.PayloadChevron = this.Chevron("payload")

    member this.ToggleSection(key: string) =
        if openSections.Contains(key) then openSections.Remove(key) |> ignore
        else openSections.Add(key) |> ignore

        this.RaiseSectionState()

    member private this.RaiseSectionState() =
        for name in [ "Interface"; "Atmosphere"; "Library"; "Payload" ] do
            this.RaisePropertyChanged("Is" + name + "SectionOpen")
            this.RaisePropertyChanged(name + "Chevron")

    // ---------------------------------------------------------------------
    // SETTINGS SEARCH
    // ---------------------------------------------------------------------
    member this.ShowInterfaceCard =
        matchesCard searchText [ loc.InterfaceNavigation; loc.NavbarPosition; loc.TopBar; loc.SideBar
                                 "interface"; "navigation"; "layout"; "language"; "sidebar" ]

    member this.ShowAtmosphereCard =
        matchesCard searchText
            [ yield loc.VisualAtmosphere
              yield loc.ColorAtmosphere
              yield loc.GeometricMotif
              yield "visual"; yield "theme"; yield "color"; yield "motif"; yield "background"
              yield! loc.AtmosphereNames ]

    member this.ShowLibraryCard =
        matchesCard searchText [ loc.LibraryCache; loc.FastCacheGameIndex; loc.RescanLibrary; loc.ClearCache
                                 "library"; "cache"; "scan"; "rescan"; "games"; "index" ]

    member this.ShowPayloadCard =
        matchesCard searchText [ loc.ModPayloadFiles; loc.ModPayloadDesc; loc.Replace; loc.Restore
                                 "mod"; "payload"; "files"; "dlss5-feed.addon64"; "renodx-dlss.addon64"; "renodx-dlss5.addon64"
                                 "nvngx_dlssnr.dll"; "replace"; "restore" ]

    member this.ShowPerformanceCard =
        matchesCard searchText [ "performance"; "animation"; "animations"; "gpu"; "motion"; "background"; "fps" ]

    member this.ShowAmdCard =
        matchesCard searchText [ "amd"; "rdna"; "radeon"; "amd mode"; "beta"; "gpu" ]

    member this.ShowOverlayCard =
        matchesCard searchText [ loc.OverlaySection; loc.OverlayTitle; loc.OverlayTagline; loc.OverlayStyle
                                 "overlay"; "dynamic overlay"; "hud"; "fps"; "vram"; "telemetry"; "theme"; "in-game" ]

    /// The three feature switches share one card, so the card is on screen when
    /// any of its rows is - each row still hides itself on its own property.
    member this.ShowFeaturesCard =
        this.ShowOverlayCard || this.ShowAmdCard || this.ShowPerformanceCard

    member this.ShowSupportCard =
        matchesCard searchText [ "support"; "donate"; "ko-fi"; "kofi"; "tutorial"; "tutorials"; "guide"
                                 "youtube"; "video"; "help"; "channel"; "nodix" ]

    member this.ShowAboutCard =
        matchesCard searchText [ "about"; "update"; "updates"; "version"; "credits"; "copyright"
                                 "dlss 5 manager"; "nodix"; "numidia" ]

    member this.ShowUniversalCard =
        matchesCard searchText [ "universal"; "lossless"; "scaling"; "nr"; "neural"; "steam"; "readiness" ]

    /// Nothing on the settings page answers the query.
    member this.HasNoSettingsMatch =
        not (String.IsNullOrWhiteSpace(searchText))
        && not (
            this.ShowInterfaceCard
            || this.ShowAtmosphereCard
            || this.ShowLibraryCard
            || this.ShowPayloadCard
            || this.ShowPerformanceCard
            || this.ShowAmdCard
            || this.ShowOverlayCard
            || this.ShowSupportCard
            || this.ShowAboutCard
            || this.ShowUniversalCard
        )

    member private this.RaiseSettingsFilter() =
        // A hit the user cannot see is no hit at all, so a section that matches
        // the query unfolds itself and folds back when the box is cleared.
        if String.IsNullOrWhiteSpace(searchText) then
            openSections.Clear()
        else
            for (key, matched) in
                [ "interface", this.ShowInterfaceCard
                  "atmosphere", this.ShowAtmosphereCard
                  "library", this.ShowLibraryCard
                  "payload", this.ShowPayloadCard ] do
                if matched then openSections.Add(key) |> ignore else openSections.Remove(key) |> ignore

        this.RaiseSectionState()
        this.RaisePropertyChanged("ShowInterfaceCard")
        this.RaisePropertyChanged("ShowAtmosphereCard")
        this.RaisePropertyChanged("ShowLibraryCard")
        this.RaisePropertyChanged("ShowPayloadCard")
        this.RaisePropertyChanged("ShowPerformanceCard")
        this.RaisePropertyChanged("ShowAmdCard")
        this.RaisePropertyChanged("ShowOverlayCard")
        this.RaisePropertyChanged("ShowFeaturesCard")
        this.RaisePropertyChanged("ShowSupportCard")
        this.RaisePropertyChanged("ShowAboutCard")
        this.RaisePropertyChanged("ShowUniversalCard")
        this.RaisePropertyChanged("HasNoSettingsMatch")

    member this.IsSearchOpen
        with get () = isSearchOpen
        and set value =
            if this.SetProperty(&isSearchOpen, value) then
                this.RaisePropertyChanged("SearchBoxWidth")
                this.RaisePropertyChanged("SearchBoxOpacity")
                this.RaisePropertyChanged("SearchBoxMargin")

    member this.SearchBoxWidth = if isSearchOpen then 180.0 else 0.0
    member this.SearchBoxOpacity = if isSearchOpen then 1.0 else 0.0

    member this.SearchBoxMargin =
        if isSearchOpen then Avalonia.Thickness(4.0, 0.0, 4.0, 0.0) else Avalonia.Thickness(0.0)

    member this.ToggleSearch() = this.IsSearchOpen <- not this.IsSearchOpen

    member this.IsScanning
        with get () = isScanning
        and set value = this.SetProperty(&isScanning, value) |> ignore

    member this.ScanStatusText
        with get () = scanStatusText
        and set value = this.SetProperty(&scanStatusText, value) |> ignore

    member this.ClearCacheAndRescan() =
        GameScanner.clearCache ()
        AnalysisStore.clear ()
        allGames.Clear()
        filteredGames.Clear()
        totalGamesCount <- 0
        this.RaisePropertyChanged("AllGamesCount")
        this.RaisePropertyChanged("TotalGamesText")
        this.RaisePropertyChanged("HasGames")
        this.CloseSettings()
        this.StartScanAsync()

    // ---------------------------------------------------------------------
    // DRAG & DROP REORDERING
    // ---------------------------------------------------------------------
    member this.IsDraggingCard
        with get () = isDraggingCard
        and set value = this.SetProperty(&isDraggingCard, value) |> ignore

    member this.DraggedCard
        with get () = draggedCard
        and set (value: GameCardViewModel option) =
            draggedCard <- value
            this.RaisePropertyChanged("DraggedCardTitle")
            this.RaisePropertyChanged("DraggedCardImage")

    member this.DraggedCardTitle =
        match draggedCard with
        | Some c -> c.Title
        | None -> ""

    member this.DraggedCardImage =
        match draggedCard with
        | Some c -> c.BannerImage
        | None -> null

    member this.SwapCards(source: GameCardViewModel, target: GameCardViewModel) =
        if not (isNull (box source)) && not (isNull (box target)) && not (Object.ReferenceEquals(source, target)) then
            let srcAll = allGames.IndexOf(source)
            let tgtAll = allGames.IndexOf(target)
            if srcAll >= 0 && tgtAll >= 0 then
                let tempAll = allGames.[srcAll]
                allGames.[srcAll] <- allGames.[tgtAll]
                allGames.[tgtAll] <- tempAll

            let srcFilt = filteredGames.IndexOf(source)
            let tgtFilt = filteredGames.IndexOf(target)
            if srcFilt >= 0 && tgtFilt >= 0 then
                let tempFilt = filteredGames.[srcFilt]
                filteredGames.[srcFilt] <- filteredGames.[tgtFilt]
                filteredGames.[tgtFilt] <- tempFilt

            try
                GameScanner.saveGamesToCache (allGames |> Seq.map (fun c -> c.Game) |> Seq.toList)
            with _ -> ()

    member this.ReorderGames(source: GameCardViewModel, target: GameCardViewModel) =
        if not (isNull (box source)) && not (isNull (box target)) && not (Object.ReferenceEquals(source, target)) then
            let oldIdx = allGames.IndexOf(source)
            let newIdx = allGames.IndexOf(target)
            if oldIdx >= 0 && newIdx >= 0 then
                allGames.Move(oldIdx, newIdx)
                let oldFilt = filteredGames.IndexOf(source)
                let newFilt = filteredGames.IndexOf(target)
                if oldFilt >= 0 && newFilt >= 0 then filteredGames.Move(oldFilt, newFilt)
                try
                    GameScanner.saveGamesToCache (allGames |> Seq.map (fun c -> c.Game) |> Seq.toList)
                with _ -> ()

    // ---------------------------------------------------------------------
    // SCANNING & LIBRARY MUTATION
    // ---------------------------------------------------------------------
    member this.StartScanAsync() =
        this.CloseSettings()
        if not isScanning then
            this.IsScanning <- true
            this.ScanStatusText <- "Scanning games library..."

            System.Threading.Tasks.Task.Run(fun () ->
                try
                    let cachedCustomGames =
                        try
                            GameScanner.loadCachedGames () |> List.filter (fun g -> g.LauncherTypeName = "CUSTOM")
                        with _ -> []

                    let scannedGames =
                        GameScanner.scanAllGamesAsync (fun progress ->
                            Dispatcher.UIThread.Post(fun () -> this.ScanStatusText <- progress))
                        |> Async.RunSynchronously

                    let combined =
                        GameScanner.deduplicateGames (scannedGames @ cachedCustomGames)
                        |> List.sortBy (fun g -> g.Title)

                    GameScanner.saveGamesToCache combined

                    // Deep-scan every title once and store it, so opening a game
                    // and pressing Install are instant from now on.
                    AnalysisStore.refreshMany combined (fun index total title ->
                        Dispatcher.UIThread.Post(fun () ->
                            this.ScanStatusText <- sprintf "Analyzing %d/%d - %s" index total title))

                    Dispatcher.UIThread.Post(fun () ->
                        allGames.Clear()
                        for g in combined do
                            allGames.Add(GameCardViewModel(g))

                        totalGamesCount <- allGames.Count
                        this.RaisePropertyChanged("AllGamesCount")
                        this.RaisePropertyChanged("TotalGamesText")
                        filterGamesList ()
                        this.RaisePropertyChanged("HasGames")
                        this.ScanStatusText <- sprintf "%d Games Ready" combined.Length)

                    // The emulators are their own search, and it follows on in
                    // the same task with no gap - one press of Re-scan settles
                    // the whole library, games and emulators together.
                    let emulatorsAdded = this.RunEmulatorDetection()

                    Dispatcher.UIThread.Post(fun () ->
                        this.IsScanning <- false

                        this.ScanStatusText <-
                            if emulatorsAdded > 0 then
                                sprintf "%d Games Ready · %d emulator(s) added" combined.Length emulatorsAdded
                            else
                                sprintf "%d Games Ready" combined.Length)
                with ex ->
                    printfn "[DLSS5Manager Error] Scan failed: %s" (ex.ToString())
                    Dispatcher.UIThread.Post(fun () ->
                        this.IsScanning <- false
                        this.ScanStatusText <- "Ready"))
            |> ignore

    /// Deep-scans only the games that have never been analyzed. Used on startup
    /// (to top up an older library) and right after a game is added by hand, so
    /// the user never has to trigger a re-scan to make Manage / Install work.
    member this.AnalyzePendingGames(games: GameItem list) =
        let pending = games |> List.filter (fun g -> (AnalysisStore.tryGet g).IsNone)

        if not pending.IsEmpty then
            this.IsScanning <- true
            this.ScanStatusText <- "Analyzing games..."

            System.Threading.Tasks.Task.Run(fun () ->
                AnalysisStore.refreshMany pending (fun index total title ->
                    Dispatcher.UIThread.Post(fun () ->
                        this.ScanStatusText <- sprintf "Analyzing %d/%d - %s" index total title))

                Dispatcher.UIThread.Post(fun () ->
                    // These cards were built from the games cache before the
                    // analysis existed, so the graphics API they show was read
                    // from nothing. Now that it has been worked out, they are
                    // told to look again - otherwise the tag stays blank until
                    // the next launch.
                    for card in allGames do
                        card.RefreshModBadge()

                    this.IsScanning <- false
                    this.ScanStatusText <- sprintf "%d Games Ready" totalGamesCount))
            |> ignore

    /// Adding a game by hand only ever inspects the file the user picked. The
    /// whole thing runs off the UI thread and is wrapped, so a locked folder or
    /// an unreadable executable shows a message instead of taking the app down.
    member this.AddSingleGameExecutable(exePath: string) =
        if not (String.IsNullOrWhiteSpace(exePath)) && System.IO.File.Exists(exePath) then
            let fileName = System.IO.Path.GetFileName(exePath)
            this.IsScanning <- true
            this.ScanStatusText <- sprintf "Inspecting %s..." fileName

            System.Threading.Tasks.Task.Run(fun () ->
                let built =
                    try
                        let folderPath = System.IO.Path.GetDirectoryName(exePath)
                        let rawName = System.IO.Path.GetFileNameWithoutExtension(exePath)
                        let folderName = System.IO.Path.GetFileName(folderPath)

                        let title =
                            if folderName.Length > 2 && not (folderName.ToLowerInvariant().Contains("bin")) then
                                folderName
                            else
                                rawName

                        let item = GameScanner.createCustomGameItem title folderPath exePath
                        AnalysisStore.refreshExecutableOnly item |> ignore
                        Some item
                    with ex ->
                        printfn "[DLSS5Manager Error] Manual add failed: %s" (ex.ToString())
                        None

                Dispatcher.UIThread.Post(fun () ->
                    this.IsScanning <- false

                    match built with
                    | Some item ->
                        allGames.Add(GameCardViewModel(item))
                        totalGamesCount <- allGames.Count
                        this.RaisePropertyChanged("AllGamesCount")
                        this.RaisePropertyChanged("TotalGamesText")
                        filterGamesList ()
                        this.RaisePropertyChanged("HasGames")

                        try
                            GameScanner.saveGamesToCache [ for c in allGames -> c.Game ]
                        with _ ->
                            ()

                        this.ScanStatusText <- sprintf "%d Games Ready" totalGamesCount
                    | None -> this.ScanStatusText <- sprintf "Could not read %s" fileName))
            |> ignore

    // ---------------------------------------------------------------------
    // MANAGE SHEET
    // ---------------------------------------------------------------------
    member this.IsManageOpen
        with get () = isManageOpen
        and set value = this.SetProperty(&isManageOpen, value) |> ignore

    member this.ManageCard = manageCard
    member this.ManageTitle = manageTitle
    member this.ManageExePath = manageExePath
    member this.ManageFolder = manageFolder
    member this.ManageReShadeText = manageReShadeText
    member this.ManageDlssText = manageDlssText
    member this.ManageStreamlineText = manageStreamlineText

    member this.HasExecutable = not (String.IsNullOrWhiteSpace(manageExePath))

    member this.IsAnalyzing
        with get () = isAnalyzing
        and set value =
            if this.SetProperty(&isAnalyzing, value) then
                this.RaisePropertyChanged("IsManageReady")

    member this.IsInstalling
        with get () = isInstalling
        and set value =
            if this.SetProperty(&isInstalling, value) then
                this.RaisePropertyChanged("IsManageReady")
                this.RaiseDlss5State()

    /// Buttons are only live when nothing is running.
    member this.IsManageReady = not isInstalling && not isAnalyzing

    /// The three install routes. They are mutually exclusive - OptiScaler and
    /// ReShade cannot hook the same game at the same time.
    member this.IsOptiScalerMode = (installMode = ModInstaller.OptiScalerMode)
    member this.IsDx12Mode = (installMode = ModInstaller.Dx12Auto)
    member this.IsDx11Mode = (installMode = ModInstaller.Dx11)
    member this.IsDx9Mode = (installMode = ModInstaller.Dx9)

    /// The user picked this route themselves, so detection must stop having an
    /// opinion. Without this, removing an install cleared `installedRoute` and
    /// the next analysis pass moved the sheet to whatever the game looked like
    /// - jumping the user somewhere they had not asked to go.
    member this.SetInstallMode(mode: ModInstaller.InstallMode) =
        routeChosenByUser <- true
        this.ApplyInstallMode(mode)

    /// The same change made on the app's own initiative, which leaves the
    /// user's claim on the route alone.
    member private this.ApplyInstallMode(mode: ModInstaller.InstallMode) =
        if installMode <> mode then
            installMode <- mode

            // Each route opens on the build its era of games actually shipped:
            // DX11 is 64-bit, DX9 is 32-bit. Either can still be overridden.
            installArch <-
                match mode with
                | ModInstaller.Dx9 -> ModInstaller.Bit32
                | _ -> ModInstaller.Bit64

            this.RaiseInstallModeState()

    /// An emulator installs one way only, so the sheet drops the route picker,
    /// the build picker and the detection chips for it. AMD mode is the same
    /// shape: one payload, no choices.
    member this.IsEmulatorTarget = isEmulatorTarget
    member this.IsGameTarget = not isEmulatorTarget && not isAmdMode

    /// AMD RDNA 4 route, set in Settings and applied to every game.
    member this.IsAmdModeEnabled
        with get () = isAmdMode
        and set value =
            if isAmdMode <> value then
                isAmdMode <- value
                this.RaisePropertyChanged("IsAmdModeEnabled")
                this.RaisePropertyChanged("IsGameTarget")
                this.RaisePropertyChanged("IsAmdRouteActive")

                GameScanner.saveSettings
                    { IsSidebarLayout = isSidebarLayout
                      ColorAtmosphere = selectedColorAtmosphere
                      GeometricMotif = selectedGeometricMotif
                      Language = languageCode
                      SupportPromptVersion = supportPromptVersion
                      AmdMode = isAmdMode
                      PerformanceMode = isPerformanceMode
                      OverlayDisabled = not isOverlayEnabled
                      OverlayTheme = overlayTheme
                      OverlayHotkey = overlayHotkey
                      SortMode = sortMode
                      ShowNonGameApps = showNonGameApps }

    /// True while the open sheet will install through the AMD payload.
    member this.IsAmdRouteActive = isAmdMode && not isEmulatorTarget

    // ---------------------------------------------------------------------
    // IN-GAME OVERLAY
    // ---------------------------------------------------------------------
    /// Off means no install deploys the overlay. It does not touch a game that
    /// already has one - that comes off with the mod, like everything else.
    member this.IsOverlayEnabled
        with get () = isOverlayEnabled
        and set value =
            if isOverlayEnabled <> value then
                isOverlayEnabled <- value
                this.RaisePropertyChanged("IsOverlayEnabled")
                this.SaveOverlaySettings()

    /// The theme names, shown as-is: they are identities the add-on resolves,
    /// not labels, so they stay English in every language exactly like the
    /// colour atmospheres do.
    member this.OverlayThemes = ModInstaller.overlayThemes

    member this.SelectedOverlayTheme
        with get () = overlayTheme
        and set (value: string) =
            if not (String.IsNullOrWhiteSpace(value)) && overlayTheme <> value then
                overlayTheme <- value
                this.RaisePropertyChanged("SelectedOverlayTheme")
                this.SaveOverlaySettings()

    /// Which key opens the overlay in game. The overlay's own Settings tab can
    /// change it too; whichever was set last is what the next install writes.
    member this.OverlayHotkeys = ModInstaller.overlayHotkeys

    member this.SelectedOverlayHotkey
        with get () = overlayHotkey
        and set (value: string) =
            if not (String.IsNullOrWhiteSpace(value)) && overlayHotkey <> value then
                overlayHotkey <- value
                this.RaisePropertyChanged("SelectedOverlayHotkey")
                this.SaveOverlaySettings()

    member private this.SaveOverlaySettings() =
        GameScanner.saveSettings
            { IsSidebarLayout = isSidebarLayout
              ColorAtmosphere = selectedColorAtmosphere
              GeometricMotif = selectedGeometricMotif
              Language = languageCode
              SupportPromptVersion = supportPromptVersion
              AmdMode = isAmdMode
              PerformanceMode = isPerformanceMode
              OverlayDisabled = not isOverlayEnabled
              OverlayTheme = overlayTheme
              OverlayHotkey = overlayHotkey
              SortMode = sortMode
              ShowNonGameApps = showNonGameApps }

    /// What the current sheet would install, so the manage sheet can say
    /// whether the overlay is coming along.
    member this.IsOverlayRouteSupported =
        ModInstaller.overlaySupported installMode optiApi

    member this.OverlayOptions: ModInstaller.OverlayOptions =
        { Enabled = isOverlayEnabled
          Theme = overlayTheme
          Hotkey = overlayHotkey }

    /// The 32-bit and DX9 routes only exist for DX11 and DX9.
    member this.IsArchChoiceVisible =
        not isEmulatorTarget && ModInstaller.archMatters installMode

    /// OptiScaler is the only route where the game's API changes anything.
    member this.IsOptiApiChoiceVisible =
        not isEmulatorTarget && installMode = ModInstaller.OptiScalerMode

    member this.IsOptiDx12 = (optiApi = ModInstaller.OptiDx12)
    member this.IsOptiVulkan = (optiApi = ModInstaller.OptiVulkan)
    member this.IsOptiNeural = (optiApi = ModInstaller.OptiNeural)

    member this.SetOptiApi(api: ModInstaller.OptiScalerApi) =
        if optiApi <> api then
            optiApi <- api
            this.RaiseInstallModeState()

    /// Marks the API an existing OptiScaler install actually used.
    member this.IsOptiDx12Installed =
        installedRoute = "optiscaler" && installedApi = "dx12"

    member this.IsOptiVulkanInstalled =
        installedRoute = "optiscaler" && installedApi = "vulkan"

    member this.IsOptiNeuralInstalled =
        installedRoute = "optiscaler" && installedApi = "neural"

    /// Neural upstream is an extra add-on on the ReShade and AMD routes;
    /// OptiScaler has its own build of it and offers that as an API instead.
    member this.IsNeuralAddonVisible =
        not isEmulatorTarget
        && (isAmdMode
            || installMode = ModInstaller.Dx12Auto
            || installMode = ModInstaller.Dx11
            || installMode = ModInstaller.Dx9)

    member this.IsNeuralAddonOn = useNeuralAddon
    member this.IsNeuralAddonOff = not useNeuralAddon

    member this.SetNeuralAddon(on: bool) =
        if useNeuralAddon <> on then
            useNeuralAddon <- on
            this.RaiseInstallModeState()

    /// Marks the add-on as live only while the route it was recorded against
    /// is the one on screen.
    member this.IsNeuralAddonInstalled =
        installedNeural
        && installedRoute <> ""
        && installedRoute = ModInstaller.modeKey installMode

    member this.IsBit64 = (installArch = ModInstaller.Bit64)
    member this.IsBit32 = (installArch = ModInstaller.Bit32)

    member this.SetInstallArch(arch: ModInstaller.InstallArch) =
        if installArch <> arch then
            installArch <- arch
            this.RaiseInstallModeState()

    member private this.RaiseInstallModeState() =
        this.RaisePropertyChanged("IsOptiScalerMode")
        this.RaisePropertyChanged("IsDx12Mode")
        this.RaisePropertyChanged("IsDx11Mode")
        this.RaisePropertyChanged("IsDx9Mode")
        this.RaisePropertyChanged("IsArchChoiceVisible")
        this.RaisePropertyChanged("IsOptiApiChoiceVisible")
        this.RaisePropertyChanged("IsOptiDx12")
        this.RaisePropertyChanged("IsOptiVulkan")
        this.RaisePropertyChanged("IsOptiNeural")
        this.RaisePropertyChanged("IsNeuralAddonVisible")
        this.RaisePropertyChanged("IsOverlayRouteSupported")
        this.RaisePropertyChanged("IsNeuralAddonOn")
        this.RaisePropertyChanged("IsNeuralAddonOff")
        this.RaisePropertyChanged("IsBit64")
        this.RaisePropertyChanged("IsBit32")
        this.RaisePropertyChanged("InstallModeHintText")
        // Picking a different route turns Install / Remove into Switch.
        this.RaiseDlss5State()

    // ---------------------------------------------------------------------
    // WHAT THE GAME IS
    // ---------------------------------------------------------------------
    member this.DetectedApiText =
        match detectedApi with
        | "dx12" -> "DirectX 12"
        | "dx11" -> "DirectX 11"
        | "dx10" -> "DirectX 10"
        | "dx9" -> "DirectX 9"
        | "vulkan" -> "Vulkan"
        | "opengl" -> "OpenGL"
        | _ -> "Unknown API"

    member this.DetectedArchText =
        match detectedArch with
        | "32" -> "32-bit"
        | "64" -> "64-bit"
        | _ -> ""

    /// Folds the executable / ReShade / DLSS / Streamline readout away.
    member this.IsTargetDetailsOpen
        with get () = isTargetDetailsOpen
        and set value =
            if this.SetProperty(&isTargetDetailsOpen, value) then
                this.RaisePropertyChanged("TargetDetailsToggleIcon")

    member this.ToggleTargetDetails() =
        this.IsTargetDetailsOpen <- not isTargetDetailsOpen

    /// Chevron: pointing down when closed, up when open.
    member this.TargetDetailsToggleIcon =
        if isTargetDetailsOpen then "M 7,14 L 12,9 L 17,14 Z" else "M 7,10 L 12,15 L 17,10 Z"

    member this.HasDetectedArch = detectedArch <> ""
    member this.HasDlssUpscaling = detectedDlss

    /// Reads the executable and its folder, then opens the sheet on the route
    /// that suits what was found. An install this app already made outranks it.
    member private this.DetectTarget(respectExisting: bool) =
        // Re-reading the same executable would only produce the same answer.
        let sameTarget = String.Equals(detectedFor, manageExePath, StringComparison.OrdinalIgnoreCase)

        if not sameTarget then
            detectedFor <- manageExePath
            detectedApi <- GameAnalyzer.detectGraphicsApi manageExePath
            detectedArch <- GameAnalyzer.detectArchitecture manageExePath

        let dlssFilesFound =
            match manageAnalysis with
            | Some a -> not (isNull (box a.DlssDirs)) && a.DlssDirs.Length > 0
            | None ->
                try
                    let dir = System.IO.Path.GetDirectoryName(manageExePath)

                    not (String.IsNullOrWhiteSpace(dir))
                    && GameAnalyzer.dlssFileNames
                       |> Array.exists (fun n -> System.IO.File.Exists(System.IO.Path.Combine(dir, n)))
                with _ ->
                    false

        // DLSS did not exist in the Direct3D 9 or 10 era, so a runtime sitting
        // in one of those folders is something we put there on an earlier run,
        // not the game shipping its own. Treating it as the game's would send
        // every modded DX9 title to OptiScaler.
        detectedDlss <-
            dlssFilesFound && detectedApi <> "dx9" && detectedApi <> "dx10"

        this.RaisePropertyChanged("DetectedApiText")
        this.RaisePropertyChanged("DetectedArchText")
        this.RaisePropertyChanged("HasDetectedArch")
        this.RaisePropertyChanged("HasDlssUpscaling")

        if not (respectExisting && (installedRoute <> "" || routeChosenByUser || sheetOpenedInstalled)) then
            // The API is the hard constraint, so it is read first: a DX9 title
            // can only take the DX9 route whatever else is lying in its
            // folder, and a Vulkan title never loads dxgi.dll at all, which
            // rules out every ReShade route and leaves OptiScaler. Past that,
            // a game already shipping DLSS upscaling is what OptiScaler is
            // for, and anything newer than DX11 - or unreadable - takes the
            // DX12 route.
            let mode =
                match NeedForSpeedProfiles.tryRecommendedRoute manageTitle with
                | Some "dx11" -> ModInstaller.Dx11
                | Some "dx12" -> ModInstaller.Dx12Auto
                | _ ->
                    if detectedApi = "dx9" then ModInstaller.Dx9
                    elif detectedApi = "dx10" then ModInstaller.Dx11
                    elif detectedApi = "vulkan" then ModInstaller.OptiScalerMode
                    elif detectedDlss then ModInstaller.OptiScalerMode
                    elif detectedApi = "dx11" then ModInstaller.Dx11
                    else ModInstaller.Dx12Auto

            this.ApplyInstallMode(mode)

            // The OptiScaler API choice is per game, so it is re-decided here
            // too. Without this it kept whichever value the previously opened
            // game left behind, and a DX12 title could open on the Vulkan
            // hook - which would install into a slot it never loads.
            if mode = ModInstaller.OptiScalerMode then
                this.SetOptiApi(
                    if detectedApi = "vulkan" then ModInstaller.OptiVulkan
                    elif optiApi = ModInstaller.OptiNeural then ModInstaller.OptiNeural
                    else ModInstaller.OptiDx12
                )

            this.SetInstallArch(if detectedArch = "32" then ModInstaller.Bit32 else ModInstaller.Bit64)

    member this.InstallModeHintText =
        let routeHint =
            match installMode with
            | ModInstaller.OptiScalerMode when optiApi = ModInstaller.OptiNeural ->
                "The neural upstream build of OptiScaler, hooked the same way. ReShade is not used."
            | ModInstaller.OptiScalerMode -> "OptiScaler hooks the game directly. ReShade is not used."
            | ModInstaller.Dx12Auto -> "ReShade + RenoDX with the DLSS 5 effects."
            | ModInstaller.Dx11 -> "ReShade + RenoDX with the DLSS 5 effects."
            | ModInstaller.Dx9 -> "dgVoodoo translates Direct3D 9; ReShade moves to the dxgi slot."
            | ModInstaller.Emulator -> "ReShade on Vulkan with the DLSS 5 emulator payload."
            | ModInstaller.AmdMode -> "AMD RDNA 4: the AMD payload and the ray reconstruction model, nothing else."
        match NeedForSpeedProfiles.tryDescribe manageTitle detectedApi detectedArch with
        | Some profile -> routeHint + "\n\n" + profile
        | None -> routeHint

    member this.IsModInstalled
        with get () = isModInstalled
        and set value = this.SetProperty(&isModInstalled, value) |> ignore

    /// Three mutually exclusive actions driven purely by what is on disk:
    ///   nothing installed          -> Install DLSS 5
    ///   installed but files missing -> Complete Installation
    ///   installed and healthy       -> Remove DLSS 5
    /// True when a route this app installed is on the game and the user has
    /// since picked a different one. The routes cannot coexist, so the primary
    /// action becomes "switch": remove the old one, then install the new one.
    member this.IsSwitchingRoute =
        dlss5Present
        && installedRoute <> ""
        && (installedRoute <> ModInstaller.modeKey installMode
            || (ModInstaller.archMatters installMode
                && installedArch <> ModInstaller.archKey installArch)
            || (installMode = ModInstaller.OptiScalerMode
                && installedApi <> ModInstaller.optiApiKey optiApi)
            // Turning the neural upstream add-on on or off changes what is on
            // the game, so it is a switch like any other.
            || (this.IsNeuralAddonVisible && installedNeural <> useNeuralAddon))

    member this.ShowInstallButton = not dlss5Present && not isInstalling
    member this.ShowSwitchButton = this.IsSwitchingRoute && not isInstalling

    member this.ShowCompleteButton =
        dlss5Present && not dlss5Complete && not this.IsSwitchingRoute && not isInstalling

    member this.ShowUninstallButton =
        dlss5Present && dlss5Complete && not this.IsSwitchingRoute && not isInstalling

    /// Marks the route that is actually on the game right now, so browsing the
    /// other two never loses track of which one is live.
    member this.IsOptiScalerInstalled = installedRoute = "optiscaler"
    member this.IsDx12Installed = installedRoute = "dx12"
    member this.IsDx11Installed = installedRoute = "dx11"
    member this.IsDx9Installed = installedRoute = "dx9"

    /// Marks the build in use, but only for the route that is actually on.
    member this.IsBit64Installed =
        installedArch = "64"
        && installedRoute = ModInstaller.modeKey installMode

    member this.IsBit32Installed =
        installedArch = "32"
        && installedRoute = ModInstaller.modeKey installMode

    member this.SwitchButtonText =
        let route =
            match installMode with
            | ModInstaller.OptiScalerMode ->
                if optiApi = ModInstaller.OptiNeural then "OptiScaler neural" else "OptiScaler"
            | ModInstaller.Dx12Auto -> "DX12"
            | ModInstaller.Dx11 -> "DX11"
            | ModInstaller.Dx9 -> "DX9"

            | ModInstaller.Emulator -> "Emulator"


            | ModInstaller.AmdMode -> "AMD mode"

        // The add-on is part of what gets installed, so the button has to say
        // which way the switch is going.
        let neural =
            if this.IsNeuralAddonVisible && useNeuralAddon then " + neural" else ""

        if ModInstaller.archMatters installMode then
            sprintf "Switch to %s %s-bit%s" route (ModInstaller.archKey installArch) neural
        else
            "Switch to " + route + neural

    member this.ManageDlss5Text =
        if not dlss5Present then loc.NotInstalled
        elif dlss5Complete then (if isModInstalled then loc.Installed else loc.Installed + " (external)")
        else
            let missing = if isNull (box dlss5Missing) then [||] else dlss5Missing
            if missing.Length = 0 then "Incomplete"
            else "Missing: " + String.Join(", ", missing)

    member this.ManageDlss5Brush: IBrush =
        if not dlss5Present then SolidColorBrush(Color.Parse("#CBD5E1")) :> IBrush
        elif dlss5Complete then SolidColorBrush(Color.Parse("#86EFAC")) :> IBrush
        else SolidColorBrush(Color.Parse("#FBBF24")) :> IBrush

    member private this.RaiseDlss5State() =
        this.RaisePropertyChanged("ShowInstallButton")
        this.RaisePropertyChanged("ShowSwitchButton")
        this.RaisePropertyChanged("ShowCompleteButton")
        this.RaisePropertyChanged("ShowUninstallButton")
        this.RaisePropertyChanged("SwitchButtonText")
        this.RaisePropertyChanged("IsSwitchingRoute")
        this.RaisePropertyChanged("IsOptiScalerInstalled")
        this.RaisePropertyChanged("IsDx12Installed")
        this.RaisePropertyChanged("IsDx11Installed")
        this.RaisePropertyChanged("IsDx9Installed")
        this.RaisePropertyChanged("IsBit64Installed")
        this.RaisePropertyChanged("IsBit32Installed")
        this.RaisePropertyChanged("IsOptiDx12Installed")
        this.RaisePropertyChanged("IsOptiVulkanInstalled")
        this.RaisePropertyChanged("IsOptiNeuralInstalled")
        this.RaisePropertyChanged("IsNeuralAddonInstalled")
        this.RaisePropertyChanged("ManageDlss5Text")
        this.RaisePropertyChanged("ManageDlss5Brush")

    member this.InstallProgress
        with get () = installProgress
        and set value = this.SetProperty(&installProgress, value) |> ignore

    member this.InstallStatusText
        with get () = installStatusText
        and set value = this.SetProperty(&installStatusText, value) |> ignore

    member this.InstallResultText
        with get () = installResultText
        and set value =
            if this.SetProperty(&installResultText, value) then
                this.RaisePropertyChanged("HasInstallResult")

    member this.HasInstallResult = not (String.IsNullOrWhiteSpace(installResultText))

    member this.InstallResultIsError
        with get () = installResultIsError
        and set value =
            if this.SetProperty(&installResultIsError, value) then
                this.RaisePropertyChanged("InstallResultBrush")

    member this.InstallResultBrush: IBrush =
        if installResultIsError then SolidColorBrush(Color.Parse("#F87171")) :> IBrush
        else SolidColorBrush(Color.Parse("#86EFAC")) :> IBrush

    member private this.RaiseManageInfo() =
        this.RaisePropertyChanged("ManageTitle")
        this.RaisePropertyChanged("ManageExePath")
        this.RaisePropertyChanged("ManageFolder")
        this.RaisePropertyChanged("ManageReShadeText")
        this.RaisePropertyChanged("ManageDlssText")
        this.RaisePropertyChanged("ManageStreamlineText")
        this.RaisePropertyChanged("HasExecutable")

    /// Paints the sheet from a cached deep-scan record - no disk walking.
    member private this.ApplyAnalysis(analysis: AnalysisStore.GameAnalysis) =
        manageAnalysis <- Some analysis

        let (route, arch) =
            match manageCard with
            | Some card -> ModInstaller.installedRouteAndArch card.Game
            | None -> ("", "")

        installedRoute <- route
        installedArch <- arch

        installedApi <-
            match manageCard with
            | Some card -> ModInstaller.installedOptiApi card.Game
            | None -> ""

        installedNeural <-
            match manageCard with
            | Some card -> ModInstaller.installedNeuralAddon card.Game
            | None -> false

        if not (String.IsNullOrWhiteSpace(analysis.ExecutablePath)) then
            manageExePath <- analysis.ExecutablePath
            manageFolder <- analysis.ExecutableFolder

        // On the emulator route ReShade hooks through the Vulkan layer, which
        // leaves no DLL beside the executable - the shared detector would
        // always report "Not installed" and read as a fault when it is not.
        manageReShadeText <-
            if analysis.ReShadeInstalled then loc.Installed
            elif isEmulatorTarget && installedRoute = "emulator" then loc.Installed + " (Vulkan layer)"
            else loc.NotInstalled
        manageDlssText <- analysis.DlssText
        manageStreamlineText <- analysis.StreamlineText
        dlss5Present <- analysis.Dlss5Present
        dlss5Complete <- analysis.Dlss5Complete
        dlss5Missing <- (if isNull (box analysis.Dlss5Missing) then [||] else analysis.Dlss5Missing)
        this.IsModInstalled <- analysis.ModInstalled
        this.RaiseManageInfo()
        this.RaiseDlss5State()

        // The deep scan may have corrected the executable, so re-read what the
        // game is from the file we now believe in. Emulators have one route.
        if not isEmulatorTarget && not isAmdMode then this.DetectTarget(true)

    /// Re-runs the deep scan for the open game and refreshes the stored record.
    member this.AnalyzeManageTarget() =
        match manageCard with
        | None -> ()
        | Some card ->
            this.IsAnalyzing <- true
            manageReShadeText <- "Checking..."
            manageDlssText <- "Checking..."
            manageStreamlineText <- "Checking..."
            this.RaiseManageInfo()

            let game = card.Game

            System.Threading.Tasks.Task.Run(fun () ->
                let analysis = AnalysisStore.refresh game

                Dispatcher.UIThread.Post(fun () ->
                    this.ApplyAnalysis(analysis)
                    this.IsAnalyzing <- false))
            |> ignore

    member this.OpenManage(card: GameCardViewModel) =
        manageCard <- Some card
        manageAnalysis <- None
        manageTitle <- card.Title
        manageExePath <- card.ExecutablePath

        manageFolder <-
            if String.IsNullOrWhiteSpace(card.ExecutablePath) then card.InstallDirectory
            else
                try System.IO.Path.GetDirectoryName(card.ExecutablePath)
                with _ -> card.InstallDirectory

        this.InstallResultText <- ""
        this.InstallProgress <- 0.0
        this.InstallStatusText <- ""
        this.IsManageOpen <- true

        // Preselect whichever route a managed install already used, so the
        // sheet opens on "Remove" rather than offering to switch to itself.
        let (route, arch) = ModInstaller.installedRouteAndArch card.Game
        installedRoute <- route
        installedArch <- arch
        installedApi <- ModInstaller.installedOptiApi card.Game
        installedNeural <- ModInstaller.installedNeuralAddon card.Game

        // Open on whatever the recorded install actually used, so the sheet
        // offers Remove rather than a switch to itself.
        useNeuralAddon <- installedNeural

        // A fresh sheet: nothing has been picked in it yet, and whether this
        // game arrived with an install is what decides if detection may route
        // it at all.
        routeChosenByUser <- false
        sheetOpenedInstalled <- route <> ""

        if route = "optiscaler" then
            this.SetOptiApi(
                match installedApi with
                | "vulkan" -> ModInstaller.OptiVulkan
                | "neural" -> ModInstaller.OptiNeural
                | _ -> ModInstaller.OptiDx12
            )

        // An emulator has exactly one route, so none of the game-side choices
        // apply and the detection pass is skipped entirely.
        isEmulatorTarget <- card.Game.LauncherTypeName = "EMULATOR"
        this.RaisePropertyChanged("IsEmulatorTarget")
        this.RaisePropertyChanged("IsGameTarget")

        this.RaisePropertyChanged("IsAmdRouteActive")

        if isEmulatorTarget then
            this.ApplyInstallMode(ModInstaller.Emulator)
        elif isAmdMode then
            // AMD mode overrides detection entirely: one payload, no choices.
            this.ApplyInstallMode(ModInstaller.AmdMode)
        else
            match route with
            | "optiscaler" -> this.ApplyInstallMode(ModInstaller.OptiScalerMode)
            | "dx12" -> this.ApplyInstallMode(ModInstaller.Dx12Auto)
            | "dx11" -> this.ApplyInstallMode(ModInstaller.Dx11)
            | "dx9" -> this.ApplyInstallMode(ModInstaller.Dx9)
            | _ -> ()

            // SetInstallMode resets the build to that route's default, so the
            // recorded one is restored afterwards.
            if route <> "" then
                this.SetInstallArch(if arch = "32" then ModInstaller.Bit32 else ModInstaller.Bit64)

            // Nothing installed yet? Then what the game actually is decides.
            this.DetectTarget(true)

        // The setters above only notify when their value moved, and opening a
        // second game can leave them where they already were while the target
        // itself changed. One sweep settles the whole sheet.
        this.RaiseInstallModeState()

        // Everything was already measured during the scan, so the sheet opens
        // fully populated. Only a game we have never analyzed pays the cost.
        match AnalysisStore.tryGet card.Game with
        | Some cached ->
            this.ApplyAnalysis(cached)
            this.IsAnalyzing <- false
        | None ->
            this.RaiseManageInfo()
            this.AnalyzeManageTarget()

    member this.CloseManage() =
        if not isInstalling then
            this.IsManageOpen <- false
            manageCard <- None

    member this.SetManageExecutable(path: string) =
        match manageCard with
        | Some card when not (String.IsNullOrWhiteSpace(path)) ->
            card.SetExecutable(path)
            manageExePath <- path

            manageFolder <-
                try System.IO.Path.GetDirectoryName(path)
                with _ -> manageFolder

            this.RaiseManageInfo()
            GameScanner.saveGamesToCache [ for c in allGames -> c.Game ]
            this.AnalyzeManageTarget()
        | _ -> ()

    /// Switching routes is a removal followed by an install: OptiScaler and
    /// ReShade hook the same game in incompatible ways, so whatever the previous
    /// route put there - our ReShade included - comes out first.
    member this.StartSwitch() =
        match manageCard with
        | None -> ()
        | Some card ->
            let game = card.Game
            let exePath = manageExePath
            let target = installMode
            let targetArch = installArch
            let targetApi = optiApi
            let targetNeural = useNeuralAddon
            let targetOverlay = this.OverlayOptions

            this.InstallResultText <- ""
            this.InstallResultIsError <- false
            this.InstallProgress <- 0.0
            this.InstallStatusText <- "Removing the previous install..."
            this.IsInstalling <- true

            // The removal owns the first third of the bar, the install the rest.
            let report: ModInstaller.Progress =
                fun text progress ->
                    Dispatcher.UIThread.Post(fun () ->
                        this.InstallStatusText <- text
                        this.InstallProgress <- progress * 100.0)

            let scaled (offset: float) (span: float) : ModInstaller.Progress =
                fun text progress -> report text (offset + span * progress)

            let plan =
                manageAnalysis
                |> Option.map (fun a ->
                    { ModInstaller.InstallPlan.DlssDirs = a.DlssDirs
                      ModInstaller.InstallPlan.StreamlineDirs = a.StreamlineDirs })

            System.Threading.Tasks.Task.Run(fun () ->
                let (succeeded, message) =
                    try
                        let removal = ModInstaller.uninstall game exePath plan (scaled 0.0 0.33)

                        if not removal.Success then
                            (false, "Could not remove the previous install: " + removal.Message)
                        else
                            let outcome =
                                ModInstaller.install game exePath plan target targetArch targetApi targetNeural targetOverlay (scaled 0.33 0.67)
                            (outcome.Success, "Switched. " + outcome.Message)
                    with ex ->
                        (false, ex.Message)

                Dispatcher.UIThread.Post(fun () ->
                    this.IsInstalling <- false
                    this.InstallResultIsError <- not succeeded
                    this.InstallResultText <- message
                    this.InstallProgress <- (if succeeded then 100.0 else 0.0)
                    this.InstallStatusText <- ""

                    // The card badge is written from the manifest, which the
                    // run just changed.
                    match manageCard with
                    | Some c -> c.RefreshModBadge()
                    | None -> ()

                    this.AnalyzeManageTarget()))
            |> ignore

    member private this.RunModTask(isInstallAction: bool) =
        match manageCard with
        | None -> ()
        | Some card ->
            let game = card.Game
            let exePath = manageExePath

            this.InstallResultText <- ""
            this.InstallResultIsError <- false
            this.InstallProgress <- 0.0
            this.InstallStatusText <- if isInstallAction then "Starting..." else "Removing..."
            this.IsInstalling <- true

            let report: ModInstaller.Progress =
                fun text progress ->
                    Dispatcher.UIThread.Post(fun () ->
                        this.InstallStatusText <- text
                        this.InstallProgress <- progress * 100.0)

            // Folder locations come straight from the cached deep scan.
            let plan =
                manageAnalysis
                |> Option.map (fun a ->
                    { ModInstaller.InstallPlan.DlssDirs = a.DlssDirs
                      ModInstaller.InstallPlan.StreamlineDirs = a.StreamlineDirs })

            System.Threading.Tasks.Task.Run(fun () ->
                let (succeeded, message) =
                    try
                        let outcome =
                            if isInstallAction then
                                ModInstaller.install game exePath plan installMode installArch optiApi useNeuralAddon this.OverlayOptions report
                            else
                                ModInstaller.uninstall game exePath plan report

                        (outcome.Success, outcome.Message)
                    with ex ->
                        (false, ex.Message)

                Dispatcher.UIThread.Post(fun () ->
                    this.IsInstalling <- false
                    this.InstallResultIsError <- not succeeded
                    this.InstallResultText <- message
                    this.InstallProgress <- (if succeeded then 100.0 else 0.0)
                    this.InstallStatusText <- ""

                    // The card badge is written from the manifest, which the
                    // run just changed.
                    match manageCard with
                    | Some c -> c.RefreshModBadge()
                    | None -> ()

                    this.AnalyzeManageTarget()))
            |> ignore

    // =====================================================================
    // BATCH INSTALL / UNINSTALL
    // =====================================================================
    // One sheet that runs the same installer over many games in turn. The
    // route per game is not a setting the user picks once for the batch - a
    // Vulkan title and a DX11 title need different routes and always did - so
    // each row decides its own from the API the analyzer recorded, and the
    // only choice offered is the one that is genuinely open: DX12 titles can
    // take either OptiScaler or ReShade + RenoDX.
    //
    // Runs sequentially on one worker. The installer copies into game folders
    // and rewrites manifests; doing several at once would just contend on the
    // disk and make a failure much harder to attribute to a game.

    member this.BatchRows = batchRows

    member this.IsBatchOpen
        with get () = isBatchOpen
        and set value =
            if this.SetProperty(&isBatchOpen, value) then
                this.RaisePropertyChanged("IsBatchClosed")

    member this.IsBatchClosed = not isBatchOpen

    member this.IsBatchRunning
        with get () = isBatchRunning
        and set value =
            if this.SetProperty(&isBatchRunning, value) then
                this.RaisePropertyChanged("IsBatchIdle")
                this.RaiseBatchState()

    member this.IsBatchIdle = not isBatchRunning

    member this.BatchProgress
        with get () = batchProgress
        and set value = this.SetProperty(&batchProgress, value) |> ignore

    member this.BatchStatusText
        with get () = batchStatusText
        and set value = this.SetProperty(&batchStatusText, value) |> ignore

    member this.BatchResultText
        with get () = batchResultText
        and set value = this.SetProperty(&batchResultText, value) |> ignore

    member private this.RaiseBatchState() =
        this.RaisePropertyChanged("SelectedBatchCount")
        this.RaisePropertyChanged("BatchSummaryText")
        this.RaisePropertyChanged("CanRunBatch")

    member this.SelectedBatchCount =
        batchRows |> Seq.filter (fun r -> r.Selected) |> Seq.length

    member this.CanRunBatch = not isBatchRunning && this.SelectedBatchCount > 0

    member this.BatchSummaryText =
        let n = this.SelectedBatchCount
        if n = 0 then "No games selected"
        elif n = 1 then "1 game selected"
        else sprintf "%d games selected" n

    /// Rebuilt every time the sheet opens, so it reflects the current library
    /// and the latest scan rather than whatever was there last time.
    member this.OpenBatch() =
        batchRows.Clear()

        for card in this.Games do
            let row = BatchRowViewModel(card)
            if row.IsRunnable && not (NonGameAppClassifier.isUtility card.Game) then batchRows.Add(row)

        this.BatchResultText <- ""
        this.BatchStatusText <- ""
        this.BatchProgress <- 0.0
        this.IsBatchOpen <- true
        this.RaiseBatchState()

    member this.CloseBatch() =
        if not isBatchRunning then this.IsBatchOpen <- false

    member this.SetAllBatchSelected(value: bool) =
        for r in batchRows do
            r.Selected <- value
        this.RaiseBatchState()

    member this.SelectAllBatch() = this.SetAllBatchSelected(true)
    member this.ClearBatchSelection() = this.SetAllBatchSelected(false)

    /// Called by the row checkboxes so the counter and the run button keep up.
    member this.NotifyBatchSelectionChanged() = this.RaiseBatchState()

    member private this.RunBatch(isInstallAction: bool) =
        let targets = batchRows |> Seq.filter (fun r -> r.Selected) |> Seq.toArray

        if targets.Length > 0 && not isBatchRunning then
            this.IsBatchRunning <- true
            this.BatchResultText <- ""
            this.BatchProgress <- 0.0

            for r in targets do
                r.SetStatus("Waiting")

            let overlay = this.OverlayOptions
            let neural = useNeuralAddon

            System.Threading.Tasks.Task.Run(fun () ->
                let mutable ok = 0
                let mutable failed = 0

                targets
                |> Array.iteri (fun i row ->
                    let label = row.Title

                    Dispatcher.UIThread.Post(fun () ->
                        row.SetStatus(if isInstallAction then "Installing..." else "Removing...")
                        this.BatchStatusText <- sprintf "(%d/%d) %s" (i + 1) targets.Length label)

                    // Per-game progress folds into the overall bar: each game
                    // owns one slice of it, so the bar advances smoothly across
                    // the whole run instead of resetting per game.
                    let report: ModInstaller.Progress =
                        fun _ progress ->
                            let overall = (float i + progress) / float targets.Length
                            Dispatcher.UIThread.Post(fun () -> this.BatchProgress <- overall * 100.0)

                    let plan =
                        row.Analysis
                        |> Option.map (fun a ->
                            { ModInstaller.InstallPlan.DlssDirs = a.DlssDirs
                              ModInstaller.InstallPlan.StreamlineDirs = a.StreamlineDirs })

                    let (succeeded, message) =
                        try
                            let outcome =
                                if isInstallAction then
                                    ModInstaller.install
                                        row.Game row.ExecutablePath plan
                                        row.Mode row.Arch row.OptiApi neural overlay report
                                else
                                    ModInstaller.uninstall row.Game row.ExecutablePath plan report

                            (outcome.Success, outcome.Message)
                        with ex ->
                            (false, ex.Message)

                    if succeeded then ok <- ok + 1 else failed <- failed + 1

                    Dispatcher.UIThread.Post(fun () ->
                        row.SetStatus(if succeeded then "Done" else "Failed: " + message)
                        row.Card.RefreshModBadge()
                        row.RefreshInstalled()
                        this.BatchProgress <- (float (i + 1) / float targets.Length) * 100.0))

                Dispatcher.UIThread.Post(fun () ->
                    this.IsBatchRunning <- false
                    this.BatchStatusText <- ""
                    this.BatchProgress <- 100.0

                    this.BatchResultText <-
                        if failed = 0 then
                            sprintf "%d of %d completed." ok targets.Length
                        else
                            sprintf "%d completed, %d failed." ok failed))
            |> ignore

    member this.StartBatchInstall() = this.RunBatch(true)
    member this.StartBatchUninstall() = this.RunBatch(false)

    member this.StartInstall() = this.RunModTask(true)
    member this.StartUninstall() = this.RunModTask(false)

    // ---------------------------------------------------------------------
    // UPDATES
    // ---------------------------------------------------------------------
    member this.AppVersionText = "v" + UpdateChecker.CurrentVersion
    member this.DownloadPageUrl = UpdateChecker.DownloadPageUrl

    /// Where the app points people who want to say thanks, and where the
    /// walkthroughs live. Both open in the system browser.
    // ---------------------------------------------------------------------
    // SUPPORT PROMPT
    // ---------------------------------------------------------------------
    member this.IsSupportPromptVisible
        with get () = isSupportPromptVisible
        and set value = this.SetProperty(&isSupportPromptVisible, value) |> ignore

    member this.DismissSupportPrompt() = this.IsSupportPromptVisible <- false

    member this.SupportPromptTitle = "Enjoying DLSS 5 SUITE?"

    member this.SupportPromptText = loc.SupportPromptText

    // Two donation targets, by agreement with the original developer: tips are
    // split 50-50 between this fork and the author of DLSS 5 MANAGER. Both
    // links are shown side by side and each is labelled with whose it is, so a
    // user always knows who they are actually tipping.
    member this.DonateUrl = "https://ko-fi.com/potatoes9411"
    member this.DonateLabel = loc.BtnSupportThisBuild

    member this.OriginalDonateUrl = "https://ko-fi.com/nodix"
    member this.OriginalDonateLabel = loc.BtnSupportOriginal

    // The two homepages, kept beside the two Ko-fi links so it is obvious
    // which project each one belongs to.
    member this.SiteUrl = "https://potatoes-dev.com"
    member this.SiteLabel = "potatoes-dev.com"

    member this.OriginalSiteUrl = "https://numidiastudios.com"
    member this.OriginalSiteLabel = "numidiastudios.com"
    /// Like DonateUrl, this belongs to the original creator - the button is
    /// labelled "Watch tutorials" and its tooltip names youtube.com/@Nodix-Tech.
    member this.TutorialsUrl = "https://youtube.com/@Nodix-Tech"

    // Authorship & rights, shown in the About card.
    //
    // This is a modified build, so the credit runs in two lines: what it was
    // built from and who made that, then who made this. Dropping the first
    // line would take the original authors' name off work that is still
    // largely theirs, so it stays above our own.
    member this.OriginalCreditsText =
        "Modified version of DLSS 5 MANAGER · Built by NODIX TECH · Published by Numidia Studios"

    member this.CreditsText = "Built by Potatoes9411 · Published by Potatoes-dev"

    // ---------------------------------------------------------------------
    // THE ORIGINAL, AND THE TERMS THIS BUILD EXISTS UNDER
    // ---------------------------------------------------------------------
    // NODIX TECH gave permission for this modified build on conditions, and
    // two of them are things the application itself has to carry: the
    // original's download location must not be changed or replaced, and its
    // link must be present; and once their community guides site is live, it
    // must be linked from here too. Both live in UpdateChecker so there is
    // one place to look.
    member this.OriginalDownloadUrl = UpdateChecker.OriginalDownloadUrl

    member this.OriginalDownloadText = "Get the original DLSS 5 MANAGER"

    member this.GuidesUrl = UpdateChecker.GuidesUrl

    /// The guides button hides itself until there is a site to point at, so
    /// shipping before it launches costs nothing and launching it is a
    /// one-line change.
    member this.HasGuidesLink = not (String.IsNullOrWhiteSpace(UpdateChecker.GuidesUrl))

    member this.GuidesText = "Community guides"

    member this.CopyrightText =
        sprintf "© %d Potatoes-dev. Based on DLSS 5 MANAGER, © %d Numidia Studios. All rights reserved."
            DateTime.Now.Year DateTime.Now.Year

    member this.IsCheckingUpdates
        with get () = isCheckingUpdates
        and set value =
            if this.SetProperty(&isCheckingUpdates, value) then
                this.RaisePropertyChanged("UpdateButtonText")
                this.RaisePropertyChanged("CanCheckUpdates")

    member this.CanCheckUpdates = not isCheckingUpdates

    member this.UpdateDownloadProgress
        with get () = updateDownloadProgress
        and set value = this.SetProperty(&updateDownloadProgress, value) |> ignore

    member this.UpdateStatusText
        with get () = updateStatusText
        and set value =
            if this.SetProperty(&updateStatusText, value) then
                this.RaisePropertyChanged("HasUpdateStatus")

    member this.HasUpdateStatus = not (String.IsNullOrWhiteSpace(updateStatusText))

    member this.HasUpdateAvailable
        with get () = hasUpdateAvailable
        and set value =
            if this.SetProperty(&hasUpdateAvailable, value) then
                this.RaisePropertyChanged("UpdateButtonText")
                this.RaisePropertyChanged("UpdateStatusBrush")
                this.RaisePropertyChanged("UpdateBadgeText")

    /// The header pill, shown only once a newer build is confirmed.
    member this.UpdateBadgeText =
        if String.IsNullOrWhiteSpace(latestVersionFound) then "UPDATE AVAILABLE"
        else "UPDATE v" + latestVersionFound

    member this.UpdateButtonText =
        if isCheckingUpdates && updateDownloadProgress > 0.0 then "DOWNLOADING..."
        elif isCheckingUpdates then "CHECKING..."
        elif hasUpdateAvailable then "DOWNLOAD UPDATE"
        else "CHECK FOR UPDATES"

    member this.UpdateStatusBrush: IBrush =
        if hasUpdateAvailable then SolidColorBrush(Color.Parse("#86EFAC")) :> IBrush
        else SolidColorBrush(Color.Parse("#94A3B8")) :> IBrush

    member this.CheckForUpdates() =
        if not isCheckingUpdates then
            this.IsCheckingUpdates <- true
            this.UpdateStatusText <- "Contacting update server..."

            async {
                let! result = UpdateChecker.check ()

                Dispatcher.UIThread.Post(fun () ->
                    this.IsCheckingUpdates <- false
                    latestVersionFound <- result.LatestVersion
                    this.HasUpdateAvailable <- result.HasUpdate
                    this.UpdateStatusText <- result.Message)
            }
            |> Async.Start

    member this.DownloadUpdate() : Async<string option> = async {
        if isCheckingUpdates then return None
        else
            this.IsCheckingUpdates <- true
            this.UpdateDownloadProgress <- 0.0
            this.UpdateStatusText <- "Preparing the verified update..."
            let! result =
                async {
                    try
                        let report message fraction =
                            Dispatcher.UIThread.Post(fun () ->
                                this.UpdateStatusText <- message
                                this.UpdateDownloadProgress <- Math.Clamp(fraction * 100.0, 0.0, 100.0)
                                this.RaisePropertyChanged("UpdateButtonText"))
                        let! path = UpdateChecker.downloadLatestSetup report
                        return Ok path
                    with ex -> return Error ex.Message
                }
            this.IsCheckingUpdates <- false
            match result with
            | Ok path ->
                this.UpdateDownloadProgress <- 100.0
                this.UpdateStatusText <- "Update verified. Starting setup..."
                return Some path
            | Error message ->
                this.UpdateDownloadProgress <- 0.0
                this.UpdateStatusText <- "Update failed safely: " + message
                return None
    }

    /// Runs once on every launch, quietly. Only a confirmed newer build says
    /// anything - a failed check must never greet the user with an error.
    member this.CheckForUpdatesOnStartup() =
        async {
            let! result = UpdateChecker.check ()

            Dispatcher.UIThread.Post(fun () ->
                if result.HasUpdate then
                    latestVersionFound <- result.LatestVersion
                    this.HasUpdateAvailable <- true
                    this.UpdateStatusText <- result.Message)
        }
        |> Async.Start

    member this.AddCustomFolder(folderPath: string) =
        if not (String.IsNullOrWhiteSpace(folderPath)) && System.IO.Directory.Exists(folderPath) then
            this.IsScanning <- true
            this.ScanStatusText <- sprintf "Inspecting %s..." (System.IO.Path.GetFileName(folderPath.TrimEnd('\\', '/')))

            System.Threading.Tasks.Task.Run(fun () ->
                let scanned =
                    try
                        GameScanner.scanCustomFolder folderPath
                    with ex ->
                        printfn "[DLSS5Manager Error] Folder add failed: %s" (ex.ToString())
                        []

                Dispatcher.UIThread.Post(fun () ->
                    this.IsScanning <- false
                    let added = ResizeArray<GameItem>()

                    for g in scanned do
                        let isAlreadyAdded =
                            allGames
                            |> Seq.exists (fun c ->
                                String.Equals(
                                    c.Game.InstallDirectory,
                                    g.InstallDirectory,
                                    StringComparison.OrdinalIgnoreCase
                                ))

                        if not isAlreadyAdded then
                            allGames.Add(GameCardViewModel(g))
                            added.Add(g)

                    totalGamesCount <- allGames.Count
                    this.RaisePropertyChanged("AllGamesCount")
                    this.RaisePropertyChanged("TotalGamesText")
                    filterGamesList ()
                    this.RaisePropertyChanged("HasGames")
                    this.ScanStatusText <- sprintf "%d Games Ready" totalGamesCount

                    try
                        GameScanner.saveGamesToCache [ for c in allGames -> c.Game ]
                    with _ ->
                        ()

                    this.AnalyzePendingGames(List.ofSeq added)))
            |> ignore

    // ---------------------------------------------------------------------
    // EMULATORS
    // ---------------------------------------------------------------------
    member this.Emulators = filteredEmulators
    member this.HasEmulators = filteredEmulators.Count > 0
    member this.AllEmulatorsCount = allEmulators.Count

    /// Emulators are only ever added by hand, one executable at a time. Same
    /// inspection as a manual game add: the picked file and nothing else.
    member this.AddEmulatorExecutable(exePath: string) =
        if not (String.IsNullOrWhiteSpace(exePath)) && System.IO.File.Exists(exePath) then
            let fileName = System.IO.Path.GetFileName(exePath)
            this.IsScanning <- true
            this.ScanStatusText <- sprintf "Inspecting %s..." fileName

            System.Threading.Tasks.Task.Run(fun () ->
                let built =
                    try
                        let folderPath = System.IO.Path.GetDirectoryName(exePath)
                        let rawName = System.IO.Path.GetFileNameWithoutExtension(exePath)

                        let item =
                            { GameScanner.createCustomGameItem rawName folderPath exePath with
                                LauncherTypeName = "EMULATOR" }

                        AnalysisStore.refreshExecutableOnly item |> ignore
                        Some item
                    with ex ->
                        printfn "[DLSS5Manager Error] Emulator add failed: %s" (ex.ToString())
                        None

                Dispatcher.UIThread.Post(fun () ->
                    this.IsScanning <- false

                    match built with
                    | Some item ->
                        let alreadyThere =
                            allEmulators
                            |> Seq.exists (fun c ->
                                String.Equals(c.ExecutablePath, exePath, StringComparison.OrdinalIgnoreCase))

                        if not alreadyThere then allEmulators.Add(GameCardViewModel(item))

                        this.RaisePropertyChanged("AllEmulatorsCount")
                        filterEmulatorsList ()
                        this.RaisePropertyChanged("HasEmulators")

                        try
                            GameScanner.saveEmulatorsToCache [ for c in allEmulators -> c.Game ]
                        with _ ->
                            ()

                        this.ScanStatusText <- sprintf "%d Emulators Ready" allEmulators.Count
                    | None -> this.ScanStatusText <- sprintf "Could not read %s" fileName))
            |> ignore

    /// Finds the emulators the app knows by name, in the handful of folders
    /// they are actually installed in. It only ever adds: anything already in
    /// the list is left exactly as the user arranged it, so running this twice
    /// is harmless.
    /// The emulator half of a scan, without any of the busy-state bookkeeping:
    /// find what the catalogue knows, build a card for each new one and hand
    /// them to the UI thread. Returns how many were new.
    ///
    /// It reads the list it is comparing against from `emulators_cache.json`
    /// rather than the on-screen collection, because that file is written
    /// every time the collection changes and this runs off the UI thread.
    ///
    /// Both callers run it inside a background task; the caller owns
    /// `IsScanning` and the closing status line.
    member private this.RunEmulatorDetection() : int =
        let alreadyKnown =
            try
                GameScanner.loadCachedEmulators ()
                |> List.map (fun e ->
                    if isNull e.TargetExecutablePath then "" else e.TargetExecutablePath.ToLowerInvariant())
                |> Set.ofList
            with _ ->
                Set.empty

        let found =
            try
                EmulatorCatalog.scan ()
                |> List.filter (fun f -> not (alreadyKnown.Contains(f.ExePath.ToLowerInvariant())))
            with _ ->
                []

        let built = System.Collections.Generic.List<GameItem>()

        for entry in found do
            Dispatcher.UIThread.Post(fun () ->
                this.ScanStatusText <- sprintf "Reading %s (%s)..." entry.Display entry.System)

            try
                let folder = System.IO.Path.GetDirectoryName(entry.ExePath)

                let item =
                    { GameScanner.createCustomGameItem entry.Display folder entry.ExePath with
                        LauncherTypeName = "EMULATOR" }

                AnalysisStore.refreshExecutableOnly item |> ignore
                built.Add(item)
            with _ ->
                ()

        if built.Count > 0 then
            Dispatcher.UIThread.Post(fun () ->
                for item in built do
                    let alreadyThere =
                        allEmulators
                        |> Seq.exists (fun c ->
                            String.Equals(c.ExecutablePath, item.TargetExecutablePath, StringComparison.OrdinalIgnoreCase))

                    if not alreadyThere then allEmulators.Add(GameCardViewModel(item))

                this.RaisePropertyChanged("AllEmulatorsCount")
                filterEmulatorsList ()
                this.RaisePropertyChanged("HasEmulators")

                try
                    GameScanner.saveEmulatorsToCache [ for c in allEmulators -> c.Game ]
                with _ ->
                    ())

        built.Count

    /// The emulator scan on its own, from the Detect Emulators button.
    member this.DetectEmulators() =
        if not isScanning then
            this.IsScanning <- true
            this.ScanStatusText <- "Looking for emulators..."

            System.Threading.Tasks.Task.Run(fun () ->
                let added = this.RunEmulatorDetection()

                Dispatcher.UIThread.Post(fun () ->
                    this.IsScanning <- false

                    this.ScanStatusText <-
                        if added = 0 then
                            "No new emulators found"
                        else
                            sprintf "%d emulator(s) added" added))
            |> ignore

    member this.RemoveEmulator(card: GameCardViewModel) =
        if not (isNull (box card)) then
            if isManageOpen && (match manageCard with Some c -> Object.ReferenceEquals(c, card) | None -> false) then
                this.CloseManage()

            allEmulators.Remove(card) |> ignore
            filteredEmulators.Remove(card) |> ignore
            this.RaisePropertyChanged("AllEmulatorsCount")
            this.RaisePropertyChanged("HasEmulators")

            try
                AnalysisStore.remove card.Game
                AnalysisStore.save ()
                GameScanner.saveEmulatorsToCache [ for c in allEmulators -> c.Game ]
            with _ ->
                ()

    /// Right-click on a card: put the user's own artwork on it. The picked file
    /// is copied into our poster cache first, so moving or deleting the
    /// original later cannot leave the card blank.
    member this.SetGameCover(card: GameCardViewModel, sourcePath: string) =
        if not (isNull (box card))
           && not (String.IsNullOrWhiteSpace(sourcePath))
           && System.IO.File.Exists(sourcePath) then
            try
                let dir =
                    System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "DLSS5Manager",
                        "Cache",
                        "Posters"
                    )

                System.IO.Directory.CreateDirectory(dir) |> ignore

                // Named after the game, so re-picking replaces the old file
                // instead of piling copies up in the cache.
                let key =
                    let raw =
                        if String.IsNullOrWhiteSpace(card.Game.AppId) then card.Game.Title else card.Game.AppId

                    Text.RegularExpressions.Regex.Replace(raw, @"[^A-Za-z0-9_\-]", "_")

                let extension =
                    let e = System.IO.Path.GetExtension(sourcePath)
                    if String.IsNullOrWhiteSpace(e) then ".png" else e

                let target = System.IO.Path.Combine(dir, "custom_" + key + extension)

                // Clear out a previous pick that used a different extension.
                for stale in System.IO.Directory.GetFiles(dir, "custom_" + key + ".*") do
                    if not (String.Equals(stale, target, StringComparison.OrdinalIgnoreCase)) then
                        try System.IO.File.Delete(stale) with _ -> ()

                System.IO.File.Copy(sourcePath, target, true)
                card.SetBanner(target)

                GameScanner.saveGamesToCache [ for c in allGames -> c.Game ]
            with _ ->
                ()

    /// Right-click on a card: drop the title from the library without touching
    /// anything on disk. Its cached analysis goes with it.
    member this.RemoveGame(card: GameCardViewModel) =
        if not (isNull (box card)) then
            if isManageOpen && (match manageCard with Some c -> Object.ReferenceEquals(c, card) | None -> false) then
                this.CloseManage()

            allGames.Remove(card) |> ignore
            filteredGames.Remove(card) |> ignore
            totalGamesCount <- allGames.Count
            this.RaisePropertyChanged("AllGamesCount")
            this.RaisePropertyChanged("TotalGamesText")
            this.RaisePropertyChanged("HasGames")

            try
                AnalysisStore.remove card.Game
                AnalysisStore.save ()
                GameScanner.saveGamesToCache [ for c in allGames -> c.Game ]
            with _ ->
                ()

    // ---------------------------------------------------------------------
    // CUSTOM MOD PAYLOAD FILES
    // ---------------------------------------------------------------------
    member this.FeedAddonName = ModInstaller.feedAddonName
    member this.FeedAddon32Name = ModInstaller.feedAddon32Name
    member this.FeedAddon32State = ModInstaller.Payload.describe ModInstaller.feedAddon32Name
    member this.RenodxAddonName = ModInstaller.renodxAddonName
    member this.DlssnrFileName = GameAnalyzer.dlssnrFileName

    member this.FeedAddonState = ModInstaller.Payload.describe ModInstaller.feedAddonName
    member this.RenodxAddonState = ModInstaller.Payload.describe ModInstaller.renodxAddonName
    member this.DlssnrState = ModInstaller.Payload.describe GameAnalyzer.dlssnrFileName

    member this.PayloadStatusText
        with get () = payloadStatusText
        and set value =
            if this.SetProperty(&payloadStatusText, value) then
                this.RaisePropertyChanged("HasPayloadStatus")

    member this.HasPayloadStatus = not (String.IsNullOrWhiteSpace(payloadStatusText))

    member private this.RaisePayloadState() =
        this.RaisePropertyChanged("FeedAddonState")
        this.RaisePropertyChanged("FeedAddon32State")
        this.RaisePropertyChanged("RenodxAddonState")
        this.RaisePropertyChanged("DlssnrState")
        for row in payloadRows do row.Refresh()

    member this.ReplacePayloadFile(fileName: string, sourcePath: string) =
        let (ok, message) = ModInstaller.Payload.replaceWith fileName sourcePath
        this.PayloadStatusText <- message
        if ok then this.RaisePayloadState()

    member this.RestorePayloadFile(fileName: string) =
        let (ok, message) = ModInstaller.Payload.restore fileName
        this.PayloadStatusText <- message
        if ok then this.RaisePayloadState()

    // ---------------------------------------------------------------------
    // PAYLOAD ROWS, RESHADE SETUP, OPTISCALER
    // ---------------------------------------------------------------------
    /// The swappable payload files, each with its own on/off switch.
    member this.PayloadRows = payloadRows

    member this.ReShadeSetupState = ModInstaller.Payload.describeReShade ()

    /// Off means no route runs the ReShade installer. A game that already has
    /// ReShade keeps it; one that does not simply goes without.
    member this.IsReShadeEnabled
        with get () = ExtrasStore.isPayloadEnabled ModInstaller.reShadeSetupKey
        and set value =
            if ExtrasStore.isPayloadEnabled ModInstaller.reShadeSetupKey <> value then
                ExtrasStore.setPayloadEnabled ModInstaller.reShadeSetupKey value
                this.RaisePropertyChanged("IsReShadeEnabled")
    member this.OptiScalerState = ModInstaller.Payload.describeOptiScaler ()

    /// The neural upstream build of OptiScaler, swapped the same way and just
    /// as unswitchable - the neural API has nothing to install without it.
    member this.OptiScalerNeuralState = ModInstaller.Payload.describeOptiScalerNeural ()

    member this.ReplaceReShadeSetup(sourcePath: string) =
        let (ok, message) = ModInstaller.Payload.replaceReShade sourcePath
        this.PayloadStatusText <- message
        if ok then this.RaisePropertyChanged("ReShadeSetupState")

    member this.RestoreReShadeSetup() =
        let (ok, message) = ModInstaller.Payload.restoreReShade ()
        this.PayloadStatusText <- message
        if ok then this.RaisePropertyChanged("ReShadeSetupState")

    /// The folder has to be the one OptiScaler was extracted into - the
    /// service checks for OptiScaler.dll and refuses anything else.
    member this.ReplaceOptiScaler(sourceDir: string) =
        let (ok, message) = ModInstaller.Payload.replaceOptiScaler sourceDir
        this.PayloadStatusText <- message
        if ok then this.RaisePropertyChanged("OptiScalerState")

    member this.ReplaceOptiScalerNeural(sourceDir: string) =
        let (ok, message) = ModInstaller.Payload.replaceOptiScalerNeural sourceDir
        this.PayloadStatusText <- message
        if ok then this.RaisePropertyChanged("OptiScalerNeuralState")

    member this.RestoreOptiScalerNeural() =
        let (ok, message) = ModInstaller.Payload.restoreOptiScalerNeural ()
        this.PayloadStatusText <- message
        if ok then this.RaisePropertyChanged("OptiScalerNeuralState")

    /// The AMD payload: replaceable, never switchable - the route cannot
    /// install without it.
    member this.AmdPayloadState = ModInstaller.Payload.describeAmd ()

    member this.ReplaceAmdPayload(sourcePaths: string list) =
        let (ok, message) = ModInstaller.Payload.replaceAmdFiles sourcePaths
        this.PayloadStatusText <- message
        if ok then this.RaisePropertyChanged("AmdPayloadState")

    member this.RestoreAmdPayload() =
        let (ok, message) = ModInstaller.Payload.restoreAmd ()
        this.PayloadStatusText <- message
        if ok then this.RaisePropertyChanged("AmdPayloadState")

    member this.RestoreOptiScaler() =
        let (ok, message) = ModInstaller.Payload.restoreOptiScaler ()
        this.PayloadStatusText <- message
        if ok then this.RaisePropertyChanged("OptiScalerState")

    // ---------------------------------------------------------------------
    // USER EXTRAS
    // ---------------------------------------------------------------------
    member this.Extras = extraRows
    member this.HasExtras = extraRows.Count > 0

    member private this.ReloadExtras() =
        extraRows.Clear()

        for (key, items) in ExtrasStore.groups () do
            extraRows.Add(ExtraRowViewModel(key, items))

        this.RaisePropertyChanged("HasExtras")

    /// Everything picked in one go becomes a single entry, so a folder's worth
    /// of files does not turn into a wall of identical rows.
    member this.AddExtras(picks: (string * bool) list) =
        let added = ExtrasStore.addBatch picks

        if added > 0 then
            this.ReloadExtras()

            this.PayloadStatusText <-
                if added = 1 then "1 item will be installed with every mod from now on."
                else sprintf "%d items will be installed together with every mod from now on." added
        else
            this.PayloadStatusText <- "Those are already in the list."

    member this.RemoveExtra(row: ExtraRowViewModel) =
        if not (isNull (box row)) then
            ExtrasStore.removeGroup row.Key
            this.ReloadExtras()
            this.PayloadStatusText <- row.Title + " removed from the extras."
