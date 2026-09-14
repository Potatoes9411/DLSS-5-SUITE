using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Dlss5Suite.Setup.Services;

/// <summary>
/// Finding the application while it is running, and closing it.
///
/// This is the difference between an upgrade that works and one that half
/// writes itself: Windows holds a lock on a running executable, so replacing
/// the files underneath it fails partway through and leaves a broken install.
/// </summary>
public static class RunningApp
{
    /// <summary>
    /// Instances of the app that would block writing to <paramref name="installDir"/>.
    ///
    /// Matching on the process name alone would be wrong in both directions:
    /// it would miss nothing, but it would also catch a copy the user is
    /// running from somewhere else entirely and offer to close that. So the
    /// module path decides, and the name is only the cheap first filter.
    /// </summary>
    public static List<Process> FindBlocking(string installDir)
    {
        var found = new List<Process>();

        string target;
        try { target = Path.GetFullPath(installDir).TrimEnd('\\'); }
        catch { return found; }

        foreach (var p in ProcessNames.SelectMany(Process.GetProcessesByName))
        {
            try
            {
                var path = p.MainModule?.FileName;

                if (path is not null &&
                    Path.GetFullPath(path).StartsWith(target + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(p);
                    continue;
                }
            }
            catch
            {
                // A process we cannot read (elevated, or exiting) still holds
                // the same name. Treat it as blocking rather than pretending
                // the folder is free and failing on the first file.
                found.Add(p);
                continue;
            }

            p.Dispose();
        }

        return found;
    }

    /// <summary>
    /// The app, and the screen engine it runs as a separate process from
    /// engine\ - that executable is locked in the same way while it runs.
    /// </summary>
    static readonly string[] ProcessNames =
    {
        Path.GetFileNameWithoutExtension(Model.InstallPlan.ExeName),
        "FullScreenWrapperForDLSS5",
    };

    public static bool IsRunning(string installDir)
    {
        var found = FindBlocking(installDir);
        foreach (var p in found) p.Dispose();
        return found.Count > 0;
    }

    /// <summary>
    /// Asks each instance to close, then insists. The polite request goes
    /// first so the app can save what it is holding; only a window that never
    /// answers is killed.
    /// </summary>
    public static async Task<bool> CloseAsync(string installDir, CancellationToken ct = default)
    {
        var processes = FindBlocking(installDir);
        if (processes.Count == 0) return true;

        try
        {
            foreach (var p in processes)
            {
                try { if (!p.HasExited) p.CloseMainWindow(); } catch { }
            }

            var deadline = DateTime.UtcNow.AddSeconds(6);
            while (DateTime.UtcNow < deadline && processes.Any(NotExited))
            {
                await Task.Delay(150, ct).ConfigureAwait(false);
            }

            foreach (var p in processes.Where(NotExited))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
            }

            deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && processes.Any(NotExited))
            {
                await Task.Delay(150, ct).ConfigureAwait(false);
            }

            // The handle can outlive the process by a moment; give the file
            // system a beat before the caller starts writing.
            await Task.Delay(400, ct).ConfigureAwait(false);

            return !IsRunning(installDir);
        }
        finally
        {
            foreach (var p in processes) p.Dispose();
        }

        static bool NotExited(Process p)
        {
            try { return !p.HasExited; } catch { return false; }
        }
    }
}
