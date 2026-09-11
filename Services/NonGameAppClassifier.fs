namespace DLSS_5_MANAGER.Services

open System
open DLSS_5_MANAGER.Models

/// Known utilities can appear in launcher manifests beside actual games.
/// They are visible only when requested and are never batch-install targets.
module NonGameAppClassifier =
    let private markers =
        [| "blender"; "obs studio"; "obs64"; "3dmark"; "clipchamp"; "clipbase"
           "lossless scaling"; "losslessscaling"; "steamworks common redistributables" |]

    let isUtility (game: GameItem) =
        let text = (game.Title + " " + game.TargetExecutablePath).ToLowerInvariant()
        markers |> Array.exists text.Contains
