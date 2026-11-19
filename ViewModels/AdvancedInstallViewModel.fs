namespace DLSS_5_MANAGER.ViewModels

open System
open System.IO
open System.Collections.ObjectModel
open Avalonia.Media
open Avalonia.Threading
open DLSS_5_MANAGER.Models
open DLSS_5_MANAGER.Services

/// Everything the Manage sheet had chosen when Advanced install opened: the
/// game, the route and the route's switches. Advanced install works inside
/// that route; changing route is done in the Manage sheet itself.
type AdvancedContext =
    { Game: GameItem
      ExePath: string
      Plan: ModInstaller.InstallPlan option
      Mode: ModInstaller.InstallMode
      Arch: ModInstaller.InstallArch
      OptiApi: ModInstaller.OptiScalerApi
      Neural: bool
      Feeder: bool
      Options: ModInstaller.InstallOptions
      Overlay: ModInstaller.OverlayOptions
      RouteLabel: string }

/// One part row in the Advanced install sheet.
type AdvancedPartViewModel(info: AdvancedInstall.PartInfo, changed: unit -> unit) =
    inherit ViewModelBase()

    let mutable wanted = false
    let mutable state: AdvancedInstall.PartState option = None
    let mutable statusText = ""
    let mutable tone = "idle"

    member _.Id = info.Id
    member _.Label = info.Label
    member _.Description = info.Description
    member _.IsLocked = info.Locked
    member _.CanToggle = not info.Locked
    member _.State = state

    member this.IsWanted
        with get () = wanted
        and set value =
            if not info.Locked && this.SetProperty(&wanted, value) then
                changed ()

    /// Sets the tick without telling the sheet; used while it is being built.
    member this.SetWanted(value: bool) =
        if this.SetProperty(&wanted, value) then ()

    member this.SetState(s: AdvancedInstall.PartState option) =
        state <- s
        this.RaisePropertyChanged("FilesTip")
        this.RaisePropertyChanged("HasFiles")

    member _.HasFiles =
        match state with
        | Some s -> s.Files.Length > 0
        | None -> false

    member _.FilesTip =
        match state with
        | Some s when s.Files.Length > 0 ->
            let shown = s.Files |> Array.truncate 12 |> Array.map Path.GetFileName
            let more = if s.Files.Length > 12 then sprintf "\n... and %d more" (s.Files.Length - 12) else ""
            "Files on the game:\n" + String.Join("\n", shown) + more
        | _ -> info.Description

    member _.StatusText = statusText
    member _.StatusBrush: IBrush =
        let hex =
            match tone with
            | "on" -> "#86EFAC"
            | "add" -> "#7DD3FC"
            | "remove" -> "#F87171"
            | "warn" -> "#FBBF24"
            | _ -> "#94A3B8"
        SolidColorBrush(Color.Parse(hex)) :> IBrush

    member this.Recompute() =
        let (text, t) =
            match state with
            | Some s when info.Locked && (s.Installed || s.Incomplete) -> ("Installed - stays with the route", "on")
            | _ when info.Locked -> ("Comes with the route", "idle")
            | Some s when s.Incomplete && wanted -> ("Incomplete - will be repaired", "warn")
            | Some s when s.Installed && wanted -> ("Installed", "on")
            | Some _ when not wanted -> ("Will be removed", "remove")
            | _ when wanted && AdvancedInstall.dependsOnGame info.Id -> ("Comes with the install where the game has it", "idle")
            | _ when wanted -> ("Will be installed", "add")
            | _ -> ("Not installed", "idle")

        statusText <- text
        tone <- t
        this.RaisePropertyChanged("StatusText")
        this.RaisePropertyChanged("StatusBrush")

/// The Advanced install sheet, opened from a game's Manage sheet.
type AdvancedInstallViewModel(finished: unit -> unit) as this =
    inherit ViewModelBase()

    let parts = ObservableCollection<AdvancedPartViewModel>()
    let mutable context: AdvancedContext option = None
    let mutable isOpen = false
    let mutable isBusy = false
    let mutable progress = 0.0
    let mutable statusText = ""
    let mutable resultText = ""
    let mutable resultIsError = false
    let mutable state: Map<string, AdvancedInstall.PartState> = Map.empty

    let raiseSummary () =
        for p in parts do
            p.Recompute()
        this.RaisePropertyChanged("PendingCount")
        this.RaisePropertyChanged("ApplyText")
        this.RaisePropertyChanged("CanApply")
        this.RaisePropertyChanged("HasConflict")
        this.RaisePropertyChanged("IsCustomized")

    member _.Parts = parts

    member this.IsOpen
        with get () = isOpen
        and set value = this.SetProperty(&isOpen, value) |> ignore

    member this.IsBusy
        with get () = isBusy
        and set value =
            if this.SetProperty(&isBusy, value) then
                this.RaisePropertyChanged("IsReady")
                this.RaisePropertyChanged("CanApply")
                this.RaisePropertyChanged("CanRemoveAll")

    member _.IsReady = not isBusy

    member this.Progress
        with get () = progress
        and set value = this.SetProperty(&progress, value) |> ignore

    member this.StatusText
        with get () = statusText
        and set value = this.SetProperty(&statusText, value) |> ignore

    member this.ResultText
        with get () = resultText
        and set value =
            if this.SetProperty(&resultText, value) then
                this.RaisePropertyChanged("HasResult")

    member _.HasResult = not (String.IsNullOrWhiteSpace(resultText))

    member this.ResultIsError
        with get () = resultIsError
        and set value =
            if this.SetProperty(&resultIsError, value) then
                this.RaisePropertyChanged("ResultBrush")

    member _.ResultBrush: IBrush =
        if resultIsError then SolidColorBrush(Color.Parse("#F87171")) :> IBrush
        else SolidColorBrush(Color.Parse("#86EFAC")) :> IBrush

    member _.Title =
        match context with
        | Some c -> c.Game.Title
        | None -> ""

    member _.RouteText =
        match context with
        | Some c -> "Route: " + c.RouteLabel + "  ·  change it in the Manage sheet"
        | None -> ""

    member _.IsInstalled = not state.IsEmpty

    member private _.Shown = parts |> Seq.map (fun p -> p.Id) |> Set.ofSeq

    member private _.Wanted =
        parts |> Seq.filter (fun p -> p.IsWanted || p.IsLocked) |> Seq.map (fun p -> p.Id) |> Set.ofSeq

    member this.PendingCount =
        let (add, remove) = AdvancedInstall.changes this.Shown this.Wanted state
        add.Count + remove.Count

    member this.ApplyText =
        match this.PendingCount with
        | 0 -> "No changes"
        | n -> sprintf "Apply %d change(s)" n

    member this.CanApply = not isBusy && this.PendingCount > 0
    member this.CanRemoveAll = not isBusy && not state.IsEmpty

    /// RenoDX and deep-fried chicken take the same place in the chain.
    member _.HasConflict =
        let on id = parts |> Seq.exists (fun p -> p.Id = id && p.IsWanted)
        on "renodx" && on "deepfried"

    /// The ticks no longer match the one-click route.
    member this.IsCustomized =
        match context with
        | None -> false
        | Some c ->
            let route = AdvancedInstall.recommend c.Mode c.Arch c.Neural c.Feeder c.Options c.Overlay.Enabled
            parts |> Seq.exists (fun p -> not p.IsLocked && p.IsWanted <> route.Contains p.Id)

    /// Builds the rows for this route and game. An installed part starts
    /// ticked as it is; a fresh game starts on the route's own choice.
    member private this.Rebuild(c: AdvancedContext) =
        state <- AdvancedInstall.readState c.Game
        let recommended = AdvancedInstall.recommend c.Mode c.Arch c.Neural c.Feeder c.Options c.Overlay.Enabled
        let offered = Set.union (AdvancedInstall.offeredFor c.Mode c.Arch) (state |> Map.keys |> Set.ofSeq)

        parts.Clear()

        for info in AdvancedInstall.parts do
            if offered.Contains info.Id then
                let row = AdvancedPartViewModel(info, raiseSummary)

                let tick =
                    if state.IsEmpty then recommended.Contains info.Id
                    else
                        match state.TryFind info.Id with
                        | Some _ -> true
                        | None -> false

                row.SetWanted(tick || info.Locked)
                row.SetState(state.TryFind info.Id)
                parts.Add(row)

        this.RaisePropertyChanged("IsInstalled")
        this.RaisePropertyChanged("CanRemoveAll")
        raiseSummary ()

    member this.Open(c: AdvancedContext) =
        context <- Some c
        this.ResultText <- ""
        this.ResultIsError <- false
        this.StatusText <- ""
        this.Progress <- 0.0
        this.RaisePropertyChanged("Title")
        this.RaisePropertyChanged("RouteText")
        this.Rebuild(c)
        this.IsOpen <- true

    member this.Close() =
        if not isBusy then this.IsOpen <- false

    /// Back to exactly what the one-click install of this route puts in.
    member this.UseRouteChoice() =
        match context with
        | None -> ()
        | Some c ->
            let route = AdvancedInstall.recommend c.Mode c.Arch c.Neural c.Feeder c.Options c.Overlay.Enabled
            for p in parts do
                if not p.IsLocked then p.SetWanted(route.Contains p.Id)
            raiseSummary ()

    member private this.Run(label: string, work: AdvancedContext -> ModInstaller.Progress -> ModInstaller.InstallOutcome) =
        match context with
        | Some c when not isBusy ->
            this.IsBusy <- true
            this.ResultText <- ""
            this.StatusText <- label
            this.Progress <- 0.0

            let report: ModInstaller.Progress =
                fun text p ->
                    Dispatcher.UIThread.Post(fun () ->
                        this.StatusText <- text
                        this.Progress <- p * 100.0)

            System.Threading.Tasks.Task.Run(fun () ->
                let outcome =
                    try work c report
                    with ex -> { ModInstaller.InstallOutcome.Success = false; ModInstaller.InstallOutcome.Message = ex.Message }

                Dispatcher.UIThread.Post(fun () ->
                    this.IsBusy <- false
                    this.StatusText <- ""
                    this.Progress <- (if outcome.Success then 100.0 else 0.0)
                    this.ResultIsError <- not outcome.Success
                    this.ResultText <- outcome.Message
                    this.Rebuild(c)
                    finished ()))
            |> ignore
        | _ -> ()

    member this.Apply() =
        let shown = this.Shown
        let wanted = this.Wanted

        this.Run("Starting...", fun c report ->
            AdvancedInstall.apply c.Game c.ExePath c.Plan c.Mode c.Arch c.OptiApi c.Options c.Overlay shown wanted report)

    /// The same full removal as the Manage sheet's Remove button.
    member this.RemoveAll() =
        this.Run("Removing...", fun c report ->
            let o = ModInstaller.uninstall c.Game c.ExePath c.Plan report
            if o.Success && OpenXrLayer.isEnabled c.Game then OpenXrLayer.disable c.Game |> ignore
            o)

    /// Copies files of the user's own beside the game's exe, as part of its
    /// install. A folder keeps its own layout under the exe's folder.
    member this.AddUserFiles(picks: (string * string) list) =
        this.Run("Adding...", fun c _ ->
            let exeDir = Path.GetDirectoryName(c.ExePath)

            let results =
                picks
                |> List.map (fun (source, relative) ->
                    ModInstaller.addUserFile c.Game source (Path.Combine(exeDir, relative)))

            let failed = results |> List.filter (fun r -> not r.Success)

            if failed.IsEmpty then
                { ModInstaller.InstallOutcome.Success = true
                  ModInstaller.InstallOutcome.Message =
                    if results.Length = 1 then results.Head.Message
                    else sprintf "%d files added. They come off with the install." results.Length }
            else
                { ModInstaller.InstallOutcome.Success = false
                  ModInstaller.InstallOutcome.Message = failed.Head.Message })
