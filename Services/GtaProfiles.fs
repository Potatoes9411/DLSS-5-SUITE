namespace DLSS_5_MANAGER.Services

open System
open System.IO

/// GTA V and FiveM guidance derived from the user's supplied community notes.
/// This is intentionally guidance-only for FiveM: SUITE does not copy unknown
/// Discord archives into CitizenFX or claim support for Rockstar GTA Online.
module GtaProfiles =
    let private contains (needle: string) (value: string) =
        not (String.IsNullOrWhiteSpace(value)) && value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0

    let isGta (title: string) =
        contains "grand theft auto v" title || contains "gta v" title || contains "gta 5" title || contains "fivem" title

    let isFiveM (title: string) = contains "fivem" title

    let isEnhanced (title: string) (executablePath: string) =
        isGta title && (contains "enhanced" title || contains "enhanced" executablePath)

    [<Literal>]
    let ScriptHookVUrl = "https://www.dev-c.com/gtav/scripthookv/"
    [<Literal>]
    let ScriptHookEnhancedUrl = "https://www.gta5-mods.com/tools/script-hook-v-net-enhanced"
    [<Literal>]
    let DirectStorageFixUrl = "https://www.gta5-mods.com/scripts/directstoragefix"

    /// -nobattleye as a launch option only works through Steam's own launch
    /// options field or the Rockstar Games Launcher's BattlEye checkbox -
    /// neither of which SUITE can reach from outside the process. What GTA V
    /// itself honours regardless of launcher is commandline.txt beside the
    /// executable, so the flag goes there instead. Community reports on
    /// whether commandline.txt overrides the Rockstar Launcher's own
    /// BattlEye setting are mixed; this covers Steam for certain and gives
    /// the Rockstar Launcher its best automatic shot without touching
    /// settings storage SUITE has no located, verified path to.
    let ensureNoBattleye (gameFolder: string) : Result<unit, string> =
        try
            if String.IsNullOrWhiteSpace(gameFolder) || not (Directory.Exists(gameFolder)) then
                Error "The game folder was not found."
            else
                let path = Path.Combine(gameFolder, "commandline.txt")
                let existing = if File.Exists(path) then File.ReadAllText(path) else ""
                if existing.IndexOf("-nobattleye", StringComparison.OrdinalIgnoreCase) >= 0 then
                    Ok()
                else
                    let updated = (existing.TrimEnd() + " -nobattleye").TrimStart()
                    File.WriteAllText(path, updated)
                    Ok()
        with ex ->
            Error ex.Message

    let tryDescribe (title: string) (api: string) =
        if not (isGta title) then None
        elif contains "fivem" title then
            Some "FiveM profile: use the verified Plugins/ReShade route and follow the server's rules. Keep third-party FPS counters disabled, use FiveM's built-in counter, use Window Capture for streaming, and never load render hooks into Rockstar GTA Online. The CitizenFX ReShade acknowledgement must be completed in FiveM's own configuration." 
        elif api = "dx11" || api = "" then
            Some "GTA V Legacy single-player profile: use the DX11 ReShade + DLSS5-Feeder route. Steam and Rockstar Launcher layouts differ; SUITE will keep the game folder clean where possible and show the launcher-specific file guidance. Do not use ENB beside this route, and test in single-player before changing anything else." 
        else
            Some ("GTA V profile: the detected renderer is " + api + ". Use the single-player ReShade/Feeder route only after confirming the game is offline; Rockstar GTA Online is excluded from this workflow.")
