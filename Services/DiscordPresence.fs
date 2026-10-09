namespace DLSS_5_MANAGER.Services

open System
open System.IO
open System.IO.Pipes
open System.Reflection
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading

/// Discord Rich Presence: "Playing DLSS 5 SUITE" with the app's own icon and a
/// download button on the user's Discord profile.
///
/// Spoken directly over Discord's local IPC pipe rather than through a
/// library: the DiscordRichPresence package crashed reading Discord's reply
/// whenever the icon was a web address, and the presence never stuck. The
/// protocol itself is small - a handshake, one SET_ACTIVITY, then keep the
/// pipe open, because Discord clears the activity when the pipe closes.
///
/// Discord not running is the normal case, not an error: a background loop
/// tries again every 15 seconds, silently. It can be turned off in Settings;
/// the choice is kept in its own small file so it never touches settings.json.
module DiscordPresence =

    /// The Discord application whose name appears as "Playing ...". Created at
    /// discord.com/developers/applications under the DLSS 5 SUITE name.
    [<Literal>]
    let ApplicationId = "1557952269583130764"

    /// Discord accepts an HTTPS image URL in place of an uploaded art asset,
    /// so the icon comes straight from the repository.
    [<Literal>]
    let private IconUrl = "https://raw.githubusercontent.com/Potatoes9411/DLSS-5-SUITE/main/Assets/logo_square.png"

    [<Literal>]
    let private DownloadUrl = "https://dlss5.potatoes-dev.com/"

    let private startedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
    let mutable private cancel: CancellationTokenSource option = None
    let mutable private pipe: NamedPipeClientStream option = None
    let private gate = obj ()

    let private flagPath () = Path.Combine(AppLog.dataFolder (), "discord-presence-off")

    let isEnabled () = not (File.Exists(flagPath ()))

    let private version () =
        match Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>() with
        | null -> ""
        | a -> a.InformationalVersion.Split('+').[0]

    // ----- the wire format: opcode, length, JSON, all little-endian -----
    let private send (stream: Stream) (opcode: int) (payload: JsonNode) =
        let body = Encoding.UTF8.GetBytes(payload.ToJsonString())
        let frame = Array.zeroCreate<byte> (8 + body.Length)
        BitConverter.GetBytes(opcode).CopyTo(frame, 0)
        BitConverter.GetBytes(body.Length).CopyTo(frame, 4)
        body.CopyTo(frame, 8)
        stream.Write(frame, 0, frame.Length)
        stream.Flush()

    let private readExactly (stream: Stream) (count: int) =
        let buffer = Array.zeroCreate<byte> count
        let mutable read = 0
        while read < count do
            let n = stream.Read(buffer, read, count - read)
            if n <= 0 then raise (EndOfStreamException("Discord closed the connection."))
            read <- read + n
        buffer

    /// One frame: (opcode, JSON text).
    let private receive (stream: Stream) =
        let header = readExactly stream 8
        let length = BitConverter.ToInt32(header, 4)
        BitConverter.ToInt32(header, 0), Encoding.UTF8.GetString(readExactly stream length)

    /// The game being played with DLSS 5 SUITE, and when it was first seen.
    let mutable private playing: (string * int64) option = None

    let private activity () =
        let v = version ()
        let details, state, since =
            match playing with
            | Some(title, since) -> "Playing " + title, "with DLSS 5 SUITE", since
            | None -> "Using DLSS 5 SUITE", (if v = "" then "by Potatoes9411" else "Version " + v + " · by Potatoes9411"), startedAt
        // Discord rejects a details line longer than 128 characters.
        let details = if details.Length > 128 then details.Substring(0, 125) + "..." else details
        JsonObject(
            [ Collections.Generic.KeyValuePair("details", JsonValue.Create(details) :> JsonNode)
              Collections.Generic.KeyValuePair("state", JsonValue.Create(state) :> JsonNode)
              Collections.Generic.KeyValuePair("timestamps", JsonObject([ Collections.Generic.KeyValuePair("start", JsonValue.Create(since) :> JsonNode) ]) :> JsonNode)
              Collections.Generic.KeyValuePair(
                  "assets",
                  JsonObject(
                      [ Collections.Generic.KeyValuePair("large_image", JsonValue.Create(IconUrl) :> JsonNode)
                        Collections.Generic.KeyValuePair("large_text", JsonValue.Create("DLSS 5 SUITE by Potatoes9411") :> JsonNode) ]) :> JsonNode)
              Collections.Generic.KeyValuePair(
                  "buttons",
                  JsonArray(
                      JsonObject(
                          [ Collections.Generic.KeyValuePair("label", JsonValue.Create("Download DLSS 5 SUITE") :> JsonNode)
                            Collections.Generic.KeyValuePair("url", JsonValue.Create(DownloadUrl) :> JsonNode) ]) :> JsonNode) :> JsonNode) ])

    /// Sends the current activity. Writes are serialised: the game watcher can
    /// call this from another thread while the session thread is reading.
    let private sendActivity (p: Stream) =
        let args = JsonObject()
        args.["pid"] <- JsonValue.Create(Diagnostics.Process.GetCurrentProcess().Id)
        args.["activity"] <- activity ()
        let command = JsonObject()
        command.["cmd"] <- JsonValue.Create("SET_ACTIVITY")
        command.["args"] <- args
        command.["nonce"] <- JsonValue.Create(Guid.NewGuid().ToString())
        lock gate (fun () -> send p 1 command)

    /// Discord listens on the first free of discord-ipc-0 .. discord-ipc-9.
    let private connect () =
        [ 0 .. 9 ]
        |> List.tryPick (fun i ->
            let p = new NamedPipeClientStream(".", sprintf "discord-ipc-%d" i, PipeDirection.InOut, PipeOptions.Asynchronous)
            try
                p.Connect(500)
                Some p
            with _ ->
                p.Dispose()
                None)

    /// Connects, sets the activity, then sits reading until Discord goes away.
    let private session (token: CancellationToken) =
        match connect () with
        | None -> ()
        | Some p ->
            lock gate (fun () -> pipe <- Some p)
            try
                try
                    send p 0 (JsonNode.Parse(sprintf """{"v":1,"client_id":"%s"}""" ApplicationId))
                    let _, ready = receive p
                    if not (ready.Contains("\"READY\"")) then failwith ("Discord refused the handshake: " + ready)

                    sendActivity p
                    let _, reply = receive p
                    if reply.Contains("\"evt\":\"ERROR\"") then failwith ("Discord rejected the activity: " + reply)
                    AppLog.info "Discord Rich Presence is showing"

                    // Kept open: closing the pipe is what clears the activity.
                    while not token.IsCancellationRequested do
                        let opcode, text = receive p
                        // 3 is Discord's ping; answering keeps the link alive.
                        if opcode = 3 then lock gate (fun () -> send p 4 (JsonNode.Parse(text)))
                        elif text.Contains("\"evt\":\"ERROR\"") then AppLog.warn ("Discord rejected an activity update: " + text)
                with
                | :? EndOfStreamException
                | :? IOException
                | :? ObjectDisposedException -> ()
                | ex -> AppLog.error "Discord Rich Presence" ex
            finally
                lock gate (fun () -> pipe <- None)
                try p.Dispose() with _ -> ()

    let start () =
        if cancel.IsNone && isEnabled () then
            let cts = new CancellationTokenSource()
            cancel <- Some cts
            let worker =
                Thread(
                    (fun () ->
                        while not cts.IsCancellationRequested do
                            session cts.Token
                            if not cts.IsCancellationRequested then
                                cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(15.0)) |> ignore),
                    IsBackground = true,
                    Name = "Discord Rich Presence")
            worker.Start()

    let stop () =
        match cancel with
        | Some cts ->
            cts.Cancel()
            cancel <- None
            // Closing the pipe both unblocks the reader and clears the activity.
            lock gate (fun () -> pipe |> Option.iter (fun p -> try p.Dispose() with _ -> ()))
        | None -> ()

    /// Called by the game watcher: the title of a running game SUITE installed
    /// DLSS 5 into, or None when there is none. Only a real change is sent.
    let setPlaying (title: string option) =
        let current = playing |> Option.map fst
        if current <> title then
            playing <- title |> Option.map (fun t -> t, DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            match title with
            | Some t -> AppLog.info ("Discord Rich Presence: playing " + t)
            | None -> ()
            let open_ = lock gate (fun () -> pipe)
            match open_ with
            | Some p -> try sendActivity p with ex -> AppLog.error "Updating Discord Rich Presence" ex
            | None -> ()

    let setEnabled (on: bool) =
        try
            Directory.CreateDirectory(AppLog.dataFolder ()) |> ignore
            if on then
                if File.Exists(flagPath ()) then File.Delete(flagPath ())
                start ()
            else
                File.WriteAllText(flagPath (), "")
                stop ()
        with ex ->
            AppLog.error "Changing the Discord Rich Presence setting failed" ex
