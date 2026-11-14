namespace DLSS_5_MANAGER.Views

open System
open Avalonia.Controls
open Avalonia.Interactivity
open Avalonia.Markup.Xaml
open Avalonia.Platform.Storage
open Avalonia.Threading
open DLSS_5_MANAGER.ViewModels

/// The Enabler Dock page. Its own control, so the edition stays out of
/// MainWindow's files apart from the one line that places it; every click
/// goes straight to EnablerDockViewModel.
type EnablerDockView() as this =
    inherit UserControl()

    do AvaloniaXamlLoader.Load(this)

    member private this.WithVm(f: EnablerDockViewModel -> unit) =
        match this.DataContext with
        | :? EnablerDockViewModel as vm -> f vm
        | _ -> ()

    member this.OnRowClicked(sender: obj, e: RoutedEventArgs) =
        match sender with
        | :? Control as c ->
            match c.DataContext with
            | :? EnablerRowViewModel as row -> this.WithVm(fun vm -> vm.Select(row))
            | _ -> ()
        | _ -> ()

    member this.OnInstallClicked(sender: obj, e: RoutedEventArgs) = this.WithVm(fun vm -> vm.Install())
    member this.OnUninstallClicked(sender: obj, e: RoutedEventArgs) = this.WithVm(fun vm -> vm.Uninstall())
    member this.OnOpenFolderClicked(sender: obj, e: RoutedEventArgs) = this.WithVm(fun vm -> vm.OpenSelectedFolder())
    member this.OnUpdateListClicked(sender: obj, e: RoutedEventArgs) = this.WithVm(fun vm -> vm.UpdateCatalog())
    member this.OnFixesClicked(sender: obj, e: RoutedEventArgs) = this.WithVm(fun vm -> vm.ToggleFixes())
    member this.OnAddFixClicked(sender: obj, e: RoutedEventArgs) = this.WithVm(fun vm -> vm.AddFix())
    member this.OnRemoveFolderClicked(sender: obj, e: RoutedEventArgs) = this.WithVm(fun vm -> vm.RemoveSelectedFolder())
    member this.OnResetSettingsClicked(sender: obj, e: RoutedEventArgs) = this.WithVm(fun vm -> vm.ResetSettings())
    member this.OnCopySettingsClicked(sender: obj, e: RoutedEventArgs) = this.WithVm(fun vm -> vm.CopySettingsToAll())

    member this.OnRemoveFixClicked(sender: obj, e: RoutedEventArgs) =
        match sender with
        | :? Control as c ->
            match c.DataContext with
            | :? EnablerFixRow as fix -> this.WithVm(fun vm -> vm.RemoveFix(fix.Index))
            | _ -> ()
        | _ -> ()

    member this.OnAddFolderClicked(sender: obj, e: RoutedEventArgs) =
        async {
            let top = TopLevel.GetTopLevel(this)
            let options = FolderPickerOpenOptions(Title = "Pick the game's folder", AllowMultiple = false)
            let! folders = top.StorageProvider.OpenFolderPickerAsync(options) |> Async.AwaitTask
            if not (isNull folders) && folders.Count > 0 then
                let path = folders.[0].TryGetLocalPath()
                if not (String.IsNullOrEmpty path) then
                    Dispatcher.UIThread.Post(fun () -> this.WithVm(fun vm -> vm.AddFolder(path)))
        }
        |> Async.StartImmediate

    member this.OnAddArchiveClicked(sender: obj, e: RoutedEventArgs) =
        async {
            let top = TopLevel.GetTopLevel(this)
            let kind = FilePickerFileType("DLSS Enabler or Streamline archive", Patterns = [| "*.rar"; "*.zip" |])
            let options = FilePickerOpenOptions(Title = "Pick a DLSS Enabler or Streamline archive", AllowMultiple = false, FileTypeFilter = [| kind |])
            let! files = top.StorageProvider.OpenFilePickerAsync(options) |> Async.AwaitTask
            if not (isNull files) && files.Count > 0 then
                let path = files.[0].TryGetLocalPath()
                if not (String.IsNullOrEmpty path) then
                    Dispatcher.UIThread.Post(fun () -> this.WithVm(fun vm -> vm.ImportArchive(path)))
        }
        |> Async.StartImmediate
