using System;
using System.Threading.Tasks;

namespace Dlss5Suite.Setup.Services;

public enum PinOutcome
{
    /// <summary>Windows pinned it.</summary>
    Pinned,

    /// <summary>The prompt appeared and the user said no.</summary>
    Declined,

    /// <summary>Already on the taskbar.</summary>
    AlreadyPinned,

    /// <summary>Windows will not allow a program to pin itself here.</summary>
    NotAllowed
}

/// <summary>
/// Asks Windows to pin the application to the taskbar.
///
/// There is no way to simply do this: the shell verb that used to work was
/// removed in Windows 10 1809, and writing into the pinned folder by hand
/// needs a registry blob whose format is undocumented and changes between
/// builds. What replaced it is Windows.UI.Shell.TaskbarManager, which does not
/// pin anything itself - it shows the system's own consent prompt ("... would
/// like to pin to the taskbar") and pins only if the user agrees. That is the
/// prompt Firefox and Chrome show, and this is the API behind it.
///
/// Two things have to line up first, both handled in ShellLink: the process
/// must carry an explicit AppUserModelID, and a Start menu shortcut must carry
/// the same one. Without them Windows has no application to pin.
///
/// It can still say no - the API is unavailable before Windows 10 1803, and
/// policy or the shell can refuse - so every path returns an outcome rather
/// than throwing, and the caller reports what actually happened.
/// </summary>
public static class TaskbarPin
{
    public static async Task<PinOutcome> RequestAsync()
    {
        try
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
                return PinOutcome.NotAllowed;

            var manager = Windows.UI.Shell.TaskbarManager.GetDefault();

            if (manager is null || !manager.IsSupported)
                return PinOutcome.NotAllowed;

            if (await manager.IsCurrentAppPinnedAsync())
                return PinOutcome.AlreadyPinned;

            if (!manager.IsPinningAllowed)
                return PinOutcome.NotAllowed;

            // Shows the system prompt. True only if the user accepted it.
            var pinned = await manager.RequestPinCurrentAppAsync();
            return pinned ? PinOutcome.Pinned : PinOutcome.Declined;
        }
        catch
        {
            // Unpackaged processes can be refused outright depending on the
            // Windows build, which surfaces as an exception rather than a
            // false. Nothing here is worth failing an install over.
            return PinOutcome.NotAllowed;
        }
    }
}
