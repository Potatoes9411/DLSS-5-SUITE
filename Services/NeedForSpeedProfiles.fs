namespace DLSS_5_MANAGER.Services

open System

/// Series-specific route notes. These describe the renderer bridge required
/// by the Feeder path; they never claim that a game shipped with native DLSS.
module NeedForSpeedProfiles =

    let private contains (needle: string) (value: string) =
        not (String.IsNullOrWhiteSpace(value))
        && value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0

    let tryDescribe (title: string) (api: string) (arch: string) =
        if not (contains "need for speed" title || contains "nfs " title) then None
        elif contains "underground 2" title || contains "underground ii" title
             || contains "most wanted" title && contains "2005" title
             || contains "prostreet" title || contains "nfs shift" title
             || title.Trim().Equals("shift", StringComparison.OrdinalIgnoreCase) then
            Some "Need for Speed classic profile (verified community route): this is a 32-bit D3D9 title. Use DXVK 3.0.2 x86 to translate D3D9 to Vulkan, then the Vulkan ReShade layer with DLSS5-Feeder addon32, host64 and Lumenite. Do not use the ordinary D3D9/dgVoodoo route for this profile. Open the Feeder overlay with Home and confirm its successful-frame counter grows."
        elif contains "underground" title || contains "carbon" title || contains "undercover" title
             || contains "world" title || contains "hot pursuit 2" title
             || contains "most wanted" title then
            Some "Need for Speed legacy profile: this is normally a 32-bit Direct3D title. The compatible DLSS 5 approach is Feeder through a renderer bridge, not native DLSS injection. For D3D9, use DXVK x86 then the Vulkan ReShade layer with addon32 + host64; test this game-specific route offline and verify the Feeder counter before keeping it."
        elif contains "unbound" title then
            Some "Need for Speed Unbound: 64-bit DirectX 12 / Frostbite profile. It did not ship with a native DLSS entry point, so use the DX12 ReShade/Feeder route. Test in single-player first; do not load third-party render hooks into an online session."
        elif contains "heat" title || contains "payback" title || contains "rivals" title
             || contains "need for speed 2016" title || contains "most wanted 2012" title then
            Some "Need for Speed Frostbite profile: 64-bit DirectX 11. It did not ship with native DLSS, so use the D3D11 ReShade/Feeder route rather than the native-DLSS/OptiScaler route. Start with single-player and verify the Feeder successful-frame counter; keep render hooks out of online play."
        elif contains "the run" title then
            Some "Need for Speed The Run: 32-bit DirectX 11 / Frostbite profile. Use the 32-bit ReShade + DLSS5-Feeder addon32 and host64 route; test offline because the original online service is shut down."
        elif api = "dx9" || arch = "32" then
            Some "Need for Speed legacy profile: use the 32-bit Feeder route. For D3D9, translate through DXVK x86 to the Vulkan ReShade layer, then use addon32 + host64. The standard native-DLSS/64-bit injection route cannot load directly."
        elif api = "dx11" then
            Some "Need for Speed DirectX 11 profile: use the ReShade Feed route. Treat results as experimental unless the game exposes a verified upscaler path."
        elif api = "dx12" then
            Some "Need for Speed DirectX 12 profile: use the DX12/ReShade Feed route and verify in single-player before any online session."
        else
            Some "Need for Speed profile: renderer not confirmed yet. Scan the executable before installing; the app will choose the safe route from the detected API and architecture."
