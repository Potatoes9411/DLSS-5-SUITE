namespace DLSS_5_MANAGER.Services

open System

/// Series-specific route notes. These describe the renderer bridge required
/// by the Feeder path; they never claim that a game shipped with native DLSS.
module NeedForSpeedProfiles =

    let private contains (needle: string) (value: string) =
        not (String.IsNullOrWhiteSpace(value))
        && value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0

    let private isNeedForSpeed (title: string) =
        contains "need for speed" title || contains "needforspeed" title || contains "nfs " title

    let private isFortnite (title: string) =
        contains "fortnite" title

    /// A supported in-app route to select automatically. `None` means the
    /// title needs the separate DXVK-to-Vulkan classic bridge and must not be
    /// misrepresented as an ordinary DX9 install.
    let tryRecommendedRoute (title: string) =
        if isFortnite title then None
        elif not (isNeedForSpeed title) then None
        elif contains "unbound" title then Some "dx12"
        elif contains "heat" title || contains "payback" title || contains "rivals" title
             || contains "need for speed 2016" title || contains "most wanted 2012" title
             || contains "the run" title then Some "dx11"
        elif contains "underground" title || contains "carbon" title || contains "undercover" title
             || contains "world" title || contains "hot pursuit" title || contains "most wanted" title
             || contains "prostreet" title || contains "shift" title then None
        else None

    let tryDescribe (title: string) (api: string) (arch: string) =
        if isFortnite title then
            Some "Recommended: Lossless Scaling is the only safe method for Fortnite. Online competitive games with anti-cheat like Fortnite will ban you for using direct render hooks (like ReShade or OptiScaler). Lossless Scaling operates safely on the window level."
        elif not (isNeedForSpeed title) then None
        elif contains "underground 2" title || contains "underground ii" title
             || contains "most wanted" title && contains "2005" title
             || contains "prostreet" title || contains "nfs shift" title
             || title.Trim().Equals("shift", StringComparison.OrdinalIgnoreCase) then
            Some "Recommended: the verified classic Feeder bridge, not OptiScaler or the normal DX9 button. Use DXVK 3.0.2 x86 to translate D3D9 to Vulkan, then the Vulkan ReShade layer with DLSS5-Feeder addon32, host64 and Lumenite. Open the Feeder overlay with Home and confirm its successful-frame counter grows."
        elif contains "underground" title || contains "carbon" title || contains "undercover" title
             || contains "world" title || contains "hot pursuit 2" title
             || contains "most wanted" title then
            Some "Recommended: the classic Feeder bridge, not OptiScaler. This is normally a 32-bit Direct3D title: for D3D9 use DXVK x86, then the Vulkan ReShade layer with addon32 + host64. Test offline and verify the Feeder counter before keeping it."
        elif contains "unbound" title then
            Some "Recommended: ReShade (DX12) + DLSS5-Feeder. Need for Speed Unbound is a 64-bit DirectX 12 / Frostbite title and did not ship with a native DLSS entry point. Test in single-player first; do not load third-party render hooks into an online session."
        elif contains "heat" title || contains "payback" title || contains "rivals" title
             || contains "need for speed 2016" title || contains "most wanted 2012" title then
            Some "Recommended: ReShade (DX11) + DLSS5-Feeder — OptiScaler is not compatible with this game. This 64-bit Frostbite title did not ship with native DLSS. Start with single-player and verify the Feeder successful-frame counter; keep render hooks out of online play."
        elif contains "the run" title then
            Some "Recommended: ReShade (DX11) + the 32-bit DLSS5-Feeder addon and host64. Need for Speed The Run is a 32-bit DirectX 11 / Frostbite title; test offline because its original online service is shut down."
        elif api = "dx9" || arch = "32" then
            Some "Need for Speed legacy profile: use the 32-bit Feeder route. For D3D9, translate through DXVK x86 to the Vulkan ReShade layer, then use addon32 + host64. The standard native-DLSS/64-bit injection route cannot load directly."
        elif api = "dx11" then
            Some "Need for Speed DirectX 11 profile: use the ReShade Feed route. Treat results as experimental unless the game exposes a verified upscaler path."
        elif api = "dx12" then
            Some "Need for Speed DirectX 12 profile: use the DX12/ReShade Feed route and verify in single-player before any online session."
        else
            Some "Need for Speed profile: renderer not confirmed yet. Scan the executable before installing; the app will choose the safe route from the detected API and architecture."
