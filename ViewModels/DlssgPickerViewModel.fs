namespace DLSS_5_MANAGER.ViewModels

open System
open System.Threading
open Avalonia.Threading
open DLSS_5_MANAGER.Services
open DLSS_5_MANAGER.Services.DlssgVersions

/// The DLSS Frame Generation version picker, one per engine (Motion Forge's
/// page, the Screen Engine window): the bundled build, the person's own
/// nvngx_dlssg.dll, or any NVIDIA release the Upscaler Vault's catalog lists.
/// The choice is remembered for that engine (DlssgVersions); `changed` tells
/// the engine's page, which restarts a running engine so the new one is used.
type DlssgPickerViewModel(engine: EngineKind, changed: unit -> unit) =
    inherit ViewModelBase()

    let bundled = bundledVersion ()
    let mutable choice = load engine
    let mutable offers: UpscalerVault.Offer list = UpscalerVault.libraryOffers () |> List.filter (fun o -> o.FamilyKey = "dlssg")
    let mutable rows = entries bundled (ownFiles ()) offers
    let mutable labels = rows |> List.map (fun r -> r.Label) |> Array.ofList
    let mutable busy = false
    let mutable progress = 0.0
    let mutable note = ""
    let mutable catalogStatus = ""
    let mutable catalogAsked = false
    // While the list is handed over anew the box reports its first row as
    // picked; that is not the person's choice and is passed over.
    let mutable swapping = false
    let mutable cancel: CancellationTokenSource = null

    member val Engine = engine

    member _.Items = labels

    member this.SelectedIndex
        with get () = indexOf choice rows
        and set (i: int) =
            if not swapping && not busy && i >= 0 && i < rows.Length && i <> indexOf choice rows then
                let row = rows.[i]
                match row.Download with
                | Some offer -> this.Download offer
                | None -> this.Choose(row.Choice, "")

    member _.IsBusy = busy
    member _.IsIdle = not busy
    member _.Progress = progress * 100.0
    member _.CatalogStatus = catalogStatus
    member _.HasOwnChosen = (match choice with OwnFile _ -> true | _ -> false)

    /// What runs now, and anything the person should know.
    member _.StatusText =
        let running =
            match choice, fileOf choice with
            | Bundled, _ ->
                if hasBundledStreamline () then sprintf "In use: DLSS Frame Generation %s, bundled with SUITE beside NVIDIA Streamline 2.14.16." bundled
                else "NVIDIA Streamline is not in SUITE's engine folder - reinstall SUITE."
            | OwnFile(v, _), Ok _ -> sprintf "In use: your own nvngx_dlssg.dll %s (a checked copy in SUITE's data folder)." v
            | Release v, Ok _ -> sprintf "In use: NVIDIA's DLSS Frame Generation %s (downloaded, signed by NVIDIA)." v
            | _, Error why -> sprintf "The bundled %s runs instead: %s." bundled why
        if note = "" then running else note + "  " + running

    member private this.Raise() =
        for n in [ "SelectedIndex"; "IsBusy"; "IsIdle"; "Progress"; "StatusText"; "CatalogStatus"; "HasOwnChosen" ] do
            this.RaisePropertyChanged n

    /// The rows anew (after a download, an import or the catalog), keeping
    /// the choice shown.
    member private this.Rebuild() =
        rows <- entries bundled (ownFiles ()) offers
        swapping <- true
        try
            labels <- rows |> List.map (fun r -> r.Label) |> Array.ofList
            this.RaisePropertyChanged "Items"
        finally
            swapping <- false
        Dispatcher.UIThread.Post((fun () -> this.RaisePropertyChanged "SelectedIndex"), DispatcherPriority.Background)

    member private this.Choose(next: Choice, said: string) =
        let before = choice
        choice <- next
        save engine next
        note <- said
        this.Raise()
        if next <> before then changed ()

    /// The catalog, once per run when the picker is first shown (the Vault
    /// keeps its lists for 30 minutes and six hours), or again on request.
    member this.LoadCatalog(force: bool) =
        if force || not catalogAsked then
            catalogAsked <- true
            catalogStatus <- "Looking up NVIDIA's DLSS Frame Generation releases…"
            this.RaisePropertyChanged "CatalogStatus"
            async {
                let! found, status = catalog force
                Dispatcher.UIThread.Post(fun () ->
                    offers <- found
                    let versions = UpscalerVault.offersFor DlssgFile "dlssg" found |> List.length
                    catalogStatus <- sprintf "%d DLSS FG versions on offer · %s" versions status
                    this.Rebuild()
                    this.RaisePropertyChanged "CatalogStatus")
            }
            |> Async.Start

    /// The list is opened: files the other engine's picker brought in (an own
    /// file, a download) are shown too, and the catalog is asked for once.
    member this.Opened() =
        if not busy then
            offers <- (UpscalerVault.libraryOffers () |> List.filter (fun o -> o.FamilyKey = "dlssg")) @ offers
            this.Rebuild()
        this.LoadCatalog false

    member this.Download(offer: UpscalerVault.Offer) =
        busy <- true
        progress <- 0.0
        note <- sprintf "Downloading DLSS Frame Generation %s…" offer.Version
        this.Raise()
        cancel <- new CancellationTokenSource()
        let token = cancel.Token
        async {
            let! result =
                download offer (fun text p ->
                    Dispatcher.UIThread.Post(fun () ->
                        note <- text
                        progress <- p
                        this.RaisePropertyChanged "Progress"
                        this.RaisePropertyChanged "StatusText")) token
            Dispatcher.UIThread.Post(fun () ->
                busy <- false
                progress <- 0.0
                offers <- (UpscalerVault.libraryOffers () |> List.filter (fun o -> o.FamilyKey = "dlssg")) @ offers
                this.Rebuild()
                match result with
                | Ok(next, said) -> this.Choose(next, said)
                | Error why ->
                    note <- why
                    this.Raise())
        }
        |> Async.Start

    member _.CancelDownload() = if not (isNull cancel) then (try cancel.Cancel() with _ -> ())

    /// "My own file…": the picked file is copied in and checked; a file not
    /// signed by NVIDIA is refused and nothing changes.
    member this.ImportOwn(path: string) =
        match importOwn path with
        | Ok(next, said) ->
            this.Rebuild()
            this.Choose(next, said)
        | Error why ->
            note <- why
            this.Raise()

    /// Drops the chosen own file's copy and goes back to the bundled build.
    member this.RemoveOwn() =
        match choice with
        | OwnFile(_, p) ->
            this.Choose(Bundled, "Your copy was removed from SUITE's data folder.")
            removeOwn p
            this.Rebuild()
        | _ -> ()

    member this.UseBundled() = this.Choose(Bundled, "")
