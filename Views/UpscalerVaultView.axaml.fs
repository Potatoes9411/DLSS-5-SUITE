namespace DLSS_5_MANAGER.Views

open Avalonia.Controls
open Avalonia.Interactivity
open Avalonia.Markup.Xaml
open Avalonia.Platform.Storage
open DLSS_5_MANAGER.ViewModels

/// The Upscaler Vault page. Its own control, so the edition sits in one
/// file pair instead of spreading through the main window.
type UpscalerVaultView() as this =
    inherit UserControl()

    do AvaloniaXamlLoader.Load(this)

    member private this.WithVault(f: UpscalerVaultViewModel -> unit) =
        match this.DataContext with
        | :? UpscalerVaultViewModel as vm -> f vm
        | _ -> ()

    member this.OnRowClicked(sender: obj, e: RoutedEventArgs) =
        match sender with
        | :? Control as c ->
            match c.DataContext with
            | :? VaultGameRow as row -> this.WithVault(fun v -> v.Select(row))
            | _ -> ()
        | _ -> ()

    member this.OnApplyClicked(sender: obj, e: RoutedEventArgs) =
        match sender with
        | :? Control as c ->
            match c.DataContext with
            | :? VaultFileRow as row -> this.WithVault(fun v -> v.Apply(row))
            | _ -> ()
        | _ -> ()

    member this.OnRefreshClicked(sender: obj, e: RoutedEventArgs) = this.WithVault(fun v -> v.LoadCatalog(true))
    member this.OnRescanClicked(sender: obj, e: RoutedEventArgs) = this.WithVault(fun v -> v.Rescan())
    member this.OnCancelClicked(sender: obj, e: RoutedEventArgs) = this.WithVault(fun v -> v.Cancel())
    member this.OnUpdateAllClicked(sender: obj, e: RoutedEventArgs) = this.WithVault(fun v -> v.UpdateAll())
    member this.OnRestoreAllClicked(sender: obj, e: RoutedEventArgs) = this.WithVault(fun v -> v.RestoreAll())
    member this.OnOpenFolderClicked(sender: obj, e: RoutedEventArgs) = this.WithVault(fun v -> v.OpenSelectedFolder())

    member this.OnAddFolderClicked(sender: obj, e: RoutedEventArgs) =
        match TopLevel.GetTopLevel(this) with
        | null -> ()
        | top ->
            task {
                let! picked =
                    top.StorageProvider.OpenFolderPickerAsync(
                        FolderPickerOpenOptions(Title = "Pick a game folder", AllowMultiple = false))
                if picked.Count > 0 then
                    match picked.[0].TryGetLocalPath() with
                    | null -> ()
                    | path -> this.WithVault(fun v -> v.AddFolder(path))
            }
            |> ignore
