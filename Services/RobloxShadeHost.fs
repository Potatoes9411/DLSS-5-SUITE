namespace DLSS_5_MANAGER.Services

open System
open System.Diagnostics
open System.IO

/// Optional Roblox processing method. The host observes Roblox through Windows
/// Graphics Capture and runs ReShade beside it; it never injects into Roblox.
module RobloxShadeHost =
    let private folder () = Path.Combine(AppContext.BaseDirectory, "methods", "RobloxShadeHost")
    let executablePath () = Path.Combine(folder (), "RobloxShadeHost.exe")
    let isAvailable () = File.Exists(executablePath ())

    type Host() =
        let mutable child: Process option = None
        let stateChanged = Event<string>()

        member _.StateChanged = stateChanged.Publish
        member _.IsRunning =
            match child with
            | Some p -> not p.HasExited
            | None -> false

        member this.Start() =
            if not this.IsRunning && isAvailable () then
                let info = ProcessStartInfo(executablePath (), UseShellExecute = true, WorkingDirectory = folder ())
                let started = Process.Start(info)
                if not (isNull started) then
                    child <- Some started
                    stateChanged.Trigger("RobloxShadeHost started. Waiting for Roblox...")
                    true
                else false
            else false

        member _.Stop() =
            match child with
            | Some p ->
                try
                    if not p.HasExited then p.CloseMainWindow() |> ignore
                    if not p.HasExited then p.Kill(true)
                with _ -> ()
                p.Dispose()
                child <- None
                stateChanged.Trigger("RobloxShadeHost stopped.")
            | None -> ()

        interface IDisposable with
            member this.Dispose() = this.Stop()
