using System;
using System.IO;
using Microsoft.Win32;
using Dlss5Suite.Setup.Model;

namespace Dlss5Suite.Setup.Services;

/// <summary>
/// Shortcuts, the run-at-login entry, and the taskbar pin.
/// </summary>
public static class Shortcuts
{
    /// <summary>
    /// Writes a .lnk. Delegates to ShellLink because the shortcut has to carry
    /// an AppUserModelID for the taskbar pin to have anything to identify the
    /// application by, and only IPropertyStore can set that.
    /// </summary>
    public static void CreateLink(string linkPath, string targetPath, string? description = null)
        => ShellLink.Create(linkPath, targetPath, description ?? InstallPlan.ProductName);

    public static string DesktopLinkPath(InstallScope scope) =>
        Path.Combine(
            Environment.GetFolderPath(
                scope == InstallScope.AllUsers
                    ? Environment.SpecialFolder.CommonDesktopDirectory
                    : Environment.SpecialFolder.DesktopDirectory),
            InstallPlan.ProductName + ".lnk");

    public static string StartMenuLinkPath(InstallScope scope) =>
        Path.Combine(
            Environment.GetFolderPath(
                scope == InstallScope.AllUsers
                    ? Environment.SpecialFolder.CommonPrograms
                    : Environment.SpecialFolder.Programs),
            InstallPlan.ProductName + ".lnk");

    // ---------------------------------------------------------------------
    // START WITH WINDOWS
    // ---------------------------------------------------------------------
    // The Run key rather than a Startup-folder shortcut, because Task Manager
    // lists Run entries under Startup with a name and lets the user switch
    // them off, which a loose .lnk does not get.
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void SetStartWithWindows(InstallPlan plan, bool enabled)
    {
        var root = plan.Scope == InstallScope.AllUsers ? Registry.LocalMachine : Registry.CurrentUser;
        using var key = root.CreateSubKey(RunKey, writable: true);
        if (key is null) return;

        if (enabled) key.SetValue(InstallPlan.UninstallKey, $"\"{plan.ExePath}\"");
        else key.DeleteValue(InstallPlan.UninstallKey, throwOnMissingValue: false);
    }
}
