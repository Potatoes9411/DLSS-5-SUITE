namespace DLSS_5_MANAGER.Services

open System
open System.IO
open System.Reflection

/// "Start with Windows": a shortcut to SUITE in the user's own Startup folder
/// (shell:startup). Nothing goes into the registry - the shortcut is the whole
/// setting, so deleting it by hand turns the option off just the same, and the
/// switch in Settings reads whether it is there.
///
/// SUITE started this way gets "--startup" and opens minimized.
module WindowsStartup =

    /// The argument the shortcut passes.
    let startupArg = "--startup"

    let shortcutName = "DLSS 5 SUITE.lnk"

    /// The user's own Startup folder. Tests point the helpers below at a
    /// folder of their own instead and never touch this one.
    let realStartupFolder () =
        Environment.GetFolderPath(Environment.SpecialFolder.Startup)

    let shortcutPathIn (folder: string) = Path.Combine(folder, shortcutName)

    let isEnabledIn (folder: string) =
        try
            not (String.IsNullOrWhiteSpace(folder)) && File.Exists(shortcutPathIn folder)
        with _ ->
            false

    /// The program the shortcut should start: this very executable.
    let currentExecutable () =
        let p = Environment.ProcessPath
        if String.IsNullOrWhiteSpace(p) then "" else p

    /// Writes a .lnk through the shell's own WScript.Shell object, exactly as
    /// Explorer would make one.
    let private writeShortcut (path: string) (target: string) (arguments: string) =
        let shellType = Type.GetTypeFromProgID("WScript.Shell")
        if isNull shellType then failwith "WScript.Shell is not available on this system."
        let shell = Activator.CreateInstance(shellType)

        try
            let link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [| box path |])
            let linkType = link.GetType()

            let set (name: string) (value: string) =
                linkType.InvokeMember(name, BindingFlags.SetProperty, null, link, [| box value |]) |> ignore

            set "TargetPath" target
            set "Arguments" arguments
            set "WorkingDirectory" (Path.GetDirectoryName(target))
            set "Description" "DLSS 5 SUITE"
            set "IconLocation" (target + ",0")
            // 7 = start minimized.
            linkType.InvokeMember("WindowStyle", BindingFlags.SetProperty, null, link, [| box 7 |]) |> ignore
            linkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, [||]) |> ignore
            try Runtime.InteropServices.Marshal.FinalReleaseComObject(link) |> ignore with _ -> ()
        finally
            try Runtime.InteropServices.Marshal.FinalReleaseComObject(shell) |> ignore with _ -> ()

    /// Turns the option on or off in `folder` for `target`. Returns whether it
    /// ended up in the state asked for.
    let setIn (folder: string) (target: string) (enabled: bool) : bool =
        try
            let path = shortcutPathIn folder

            if enabled then
                if String.IsNullOrWhiteSpace(target) || not (File.Exists(target)) then
                    false
                else
                    Directory.CreateDirectory(folder) |> ignore
                    writeShortcut path target startupArg
                    File.Exists(path)
            else
                if File.Exists(path) then File.Delete(path)
                not (File.Exists(path))
        with ex ->
            AppLog.error "Start with Windows" ex
            false

    let isEnabled () = isEnabledIn (realStartupFolder ())

    let set (enabled: bool) = setIn (realStartupFolder ()) (currentExecutable ()) enabled

    /// After SUITE moved or updated, a shortcut that is there points at the
    /// executable running now.
    let refreshIn (folder: string) (target: string) =
        if isEnabledIn folder then setIn folder target true |> ignore

    let refresh () = refreshIn (realStartupFolder ()) (currentExecutable ())

    /// Whether this run was started by the shortcut.
    let launchedAtStartup (args: string[]) =
        not (isNull args) && args |> Array.exists (fun a -> String.Equals(a, startupArg, StringComparison.OrdinalIgnoreCase))
