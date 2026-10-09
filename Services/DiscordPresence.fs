namespace DLSS_5_MANAGER.Services

open System
open System.IO
open System.Reflection
open DiscordRPC

/// Discord Rich Presence: "Using DLSS 5 SUITE" with the app's own icon and a
/// download button on the user's Discord profile.
///
/// Discord not running is the normal case, not an error - the client keeps
/// quietly retrying in the background and nothing is ever shown to the user.
/// It can be turned off in Settings; the choice is kept in its own small file
/// so it never touches settings.json.
module DiscordPresence =

    /// The Discord application whose name appears as "Playing ...". Created at
    /// discord.com/developers/applications under the DLSS 5 SUITE name.
    [<Literal>]
    let ApplicationId = "1557952269583130764"

    /// Discord accepts an HTTPS image URL in place of an uploaded art asset,
    /// so the icon comes straight from the repository.
    [<Literal>]
    let private IconUrl = "https://raw.githubusercontent.com/Potatoes9411/DLSS-5-SUITE/main/Assets/logo.png"

    [<Literal>]
    let private DownloadUrl = "https://github.com/Potatoes9411/DLSS-5-SUITE/releases/latest"

    let mutable private client: DiscordRpcClient option = None
    let private startedAt = DateTime.UtcNow

    let private flagPath () = Path.Combine(AppLog.dataFolder (), "discord-presence-off")

    let isEnabled () = not (File.Exists(flagPath ()))

    let private version () =
        match Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>() with
        | null -> ""
        | a -> a.InformationalVersion.Split('+').[0]

    let private presence () =
        RichPresence(
            Details = "Using DLSS 5 SUITE",
            State = (let v = version () in if v = "" then "DLSS 5 for every game" else "Version " + v),
            Assets = Assets(LargeImageKey = IconUrl, LargeImageText = "DLSS 5 SUITE"),
            Timestamps = Timestamps(Start = Nullable startedAt),
            Buttons = [| Button(Label = "Download DLSS 5 SUITE", Url = DownloadUrl) |])

    let start () =
        if client.IsNone && isEnabled () && not (ApplicationId.StartsWith("DISCORD_")) then
            try
                let c = new DiscordRpcClient(ApplicationId)
                c.Initialize() |> ignore
                c.SetPresence(presence ())
                client <- Some c
                AppLog.info "Discord Rich Presence started"
            with ex ->
                AppLog.error "Starting Discord Rich Presence failed" ex

    let stop () =
        match client with
        | Some c ->
            try
                c.ClearPresence()
                c.Dispose()
            with _ -> ()
            client <- None
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
