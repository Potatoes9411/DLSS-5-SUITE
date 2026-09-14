using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using Dlss5Suite.Setup.Model;

namespace Dlss5Suite.Setup.Services;

/// <summary>
/// Deciding when administrator rights are actually needed, and handing the
/// wizard's answers to the elevated copy so the user is not asked twice.
/// </summary>
public static class Elevation
{
    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// True when this plan cannot be carried out as the current user.
    ///
    /// An all-users install always needs it. Beyond that the test is not the
    /// folder's name but whether we can actually write there: people install
    /// to all sorts of places, and a custom folder under Program Files needs
    /// elevation just as much as the default one does, while a folder on a
    /// data drive needs none even if it looks unusual.
    /// </summary>
    public static bool IsRequiredFor(InstallPlan plan)
    {
        if (IsElevated) return false;
        if (plan.Scope == InstallScope.AllUsers) return true;
        return !CanWriteTo(plan.InstallDir);
    }

    /// <summary>
    /// Walks up to the nearest folder that exists and tries to create a file
    /// in it. Nothing else is reliable: the ACL on a folder can permit or deny
    /// writing for reasons no amount of path inspection would reveal.
    /// </summary>
    public static bool CanWriteTo(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return false;

        try
        {
            var probe = new DirectoryInfo(Path.GetFullPath(dir));
            while (probe is not null && !probe.Exists) probe = probe.Parent;
            if (probe is null) return false;

            var test = Path.Combine(probe.FullName, $".dlss5-write-test-{Guid.NewGuid():N}");
            using (File.Create(test, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Starts this same executable elevated, pointed at the plan on disk.
    /// Returns false when the user dismissed the UAC prompt, which is a
    /// normal answer and leaves the wizard exactly where it was.
    /// </summary>
    public static bool TryRelaunchElevated(InstallPlan plan, out string statePath)
    {
        statePath = plan.WriteToTempFile();

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            TryDelete(statePath);
            statePath = "";
            return false;
        }

        var info = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = true,
            Verb = "runas",
            Arguments = $"--resume \"{statePath}\""
        };

        try
        {
            var proc = Process.Start(info);
            if (proc is null)
            {
                TryDelete(statePath);
                statePath = "";
                return false;
            }

            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // ERROR_CANCELLED - the user said no at the prompt.
            TryDelete(statePath);
            statePath = "";
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
