namespace DLSS_5_MANAGER.Views

open Avalonia
open Avalonia.Controls
open Avalonia.Interactivity
open Avalonia.Markup.Xaml
open Avalonia.Platform.Storage
open DLSS_5_MANAGER.ViewModels

/// The Motion Forge page. Its buttons call the view model; the file pickers
/// need the window's StorageProvider, so they are opened here.
type MotionForgeView() as this =
    inherit UserControl()

    do AvaloniaXamlLoader.Load(this)

    member private this.With(f: MotionForgeViewModel -> unit) =
        match this.DataContext with
        | :? MotionForgeViewModel as vm -> f vm
        | _ -> ()

    member private this.PickFiles(title: string, multiple: bool, onPicked: string list -> unit) =
        match TopLevel.GetTopLevel(this) with
        | null -> ()
        | top ->
            async {
                let dll = FilePickerFileType("DLL files", Patterns = [| "*.dll" |])
                let options = FilePickerOpenOptions(Title = title, AllowMultiple = multiple, FileTypeFilter = [| dll |])
                let! files = top.StorageProvider.OpenFilePickerAsync(options) |> Async.AwaitTask
                let paths = files |> Seq.map (fun f -> f.Path.LocalPath) |> List.ofSeq
                if not paths.IsEmpty then
                    Avalonia.Threading.Dispatcher.UIThread.Post(fun () -> onPicked paths)
            }
            |> Async.StartImmediate

    member this.OnStartClicked(_: obj, _: RoutedEventArgs) = this.With(fun vm -> vm.ToggleMotion())

    member this.OnPickXessClicked(_: obj, _: RoutedEventArgs) =
        this.PickFiles("Pick libxess_fg.dll and libxell.dll (Intel XeSS SDK)", true, fun paths -> this.With(fun vm -> vm.AcceptXess paths))

    member this.OnRemoveXessClicked(_: obj, _: RoutedEventArgs) = this.With(fun vm -> vm.RemoveXess())
    member this.OnInstallAiClicked(_: obj, _: RoutedEventArgs) = this.With(fun vm -> vm.InstallAi())
    member this.OnRemoveAiClicked(_: obj, _: RoutedEventArgs) = this.With(fun vm -> vm.RemoveAi())
    member this.OnOpenLogsClicked(_: obj, _: RoutedEventArgs) = this.With(fun vm -> vm.OpenLogFolder())
    member this.OnAddProfileClicked(_: obj, _: RoutedEventArgs) = this.With(fun vm -> vm.AddProfile())
    member this.OnRenameProfileClicked(_: obj, _: RoutedEventArgs) = this.With(fun vm -> vm.RenameProfile())
    member this.OnResetProfileClicked(_: obj, _: RoutedEventArgs) = this.With(fun vm -> vm.ResetProfile())
    member this.OnDeleteProfileClicked(_: obj, _: RoutedEventArgs) = this.With(fun vm -> vm.DeleteProfile())
    member this.OnImportClicked(_: obj, _: RoutedEventArgs) = this.With(fun vm -> vm.ImportFromSuperMotion())

    member this.OnForgetGameClicked(sender: obj, _: RoutedEventArgs) =
        match sender with
        | :? Control as c ->
            match c.DataContext with
            | :? LearnedRowViewModel as row -> this.With(fun vm -> vm.ForgetGame row)
            | _ -> ()
        | _ -> ()
