namespace DLSS_5_MANAGER.Services

open System.Collections.Generic

/// One overlay at a time. SUITE's Screen Engine (and NeuralScreen /
/// RobloxShadeHost on the same page) and Motion Forge's engine each capture
/// the screen and draw a topmost, click-through window over it; two at once
/// would capture each other's work twice over and stack their pictures. So
/// each owner says here how to stop it, and starting one stops the others.
///
/// Only within this SUITE: an engine some other program started is not ours
/// to stop.
module OverlayArbiter =
    let private gate = obj ()
    let private owners = Dictionary<string, unit -> bool>()

    /// `stop` is called (on whatever thread claims) when another owner starts.
    /// It stops the owner's overlay if it is running and says whether it was.
    let register (owner: string) (stop: unit -> bool) =
        lock gate (fun () -> owners[owner] <- stop)

    /// Called before `owner` starts its overlay: every other one is stopped.
    /// Returns the owners that were running, for the caller's status line.
    let claim (owner: string) : string list =
        let others =
            lock gate (fun () ->
                owners |> Seq.filter (fun kv -> kv.Key <> owner) |> Seq.map (fun kv -> kv.Key, kv.Value) |> List.ofSeq)
        others
        |> List.filter (fun (_, stop) ->
            try stop () with ex -> AppLog.error "Stopping an overlay for another" ex; false)
        |> List.map fst
