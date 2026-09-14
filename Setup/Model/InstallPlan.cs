using System;
using System.Linq;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dlss5Suite.Setup.Model;

public enum InstallScope
{
    /// <summary>Under the user's own profile. Never needs elevation.</summary>
    CurrentUser,

    /// <summary>Program Files and HKLM. Always needs elevation.</summary>
    AllUsers
}

/// <summary>
/// Everything the wizard collects, in one serialisable object.
///
/// This is also the elevation handoff. When the chosen scope or folder needs
/// rights the current process does not have, the plan is written to a file in
/// the temp folder and the elevated copy is started pointed at it, so the user
/// answers the questions once and the install carries on from the step it
/// stopped at rather than restarting the wizard.
/// </summary>
public sealed class InstallPlan
{
    public const string ProductName = "DLSS 5 SUITE";
    public const string Publisher = "Potatoes-dev";
    // Read from the build rather than typed in. It used to be a constant that
    // every release had to remember to edit, and one was packaged still naming
    // the previous version. Directory.Build.props is the only place a version
    // is set now.
    public static readonly string Version = "v" + (
        typeof(InstallPlan).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion ?? "0.0.0");
    public const string WebsiteUrl = "https://potatoes-dev.com";

    /// <summary>Executable inside the install folder, and the process name to watch for.</summary>
    public const string ExeName = "DLSS 5 SUITE.exe";

    /// <summary>Add/Remove Programs key. Stable across versions, so an upgrade replaces its own entry.</summary>
    public const string UninstallKey = "DLSS5Suite";

    public InstallScope Scope { get; set; } = InstallScope.CurrentUser;
    public string InstallDir { get; set; } = "";

    public bool DesktopShortcut { get; set; } = true;
    public bool StartMenuShortcut { get; set; } = true;
    public bool PinToTaskbar { get; set; }
    public bool StartWithWindows { get; set; }

    public bool LaunchWhenDone { get; set; } = true;
    public bool OpenWebsiteWhenDone { get; set; }

    /// <summary>Set when the plan came back from an elevated relaunch.</summary>
    [JsonIgnore]
    public bool Resumed { get; set; }
    public bool IsSilentUpdate { get; set; }

    public string ExePath => Path.Combine(InstallDir, ExeName);

    public static string DefaultDir(InstallScope scope) =>
        scope == InstallScope.AllUsers
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), ProductName)
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", ProductName);

    // ---------------------------------------------------------------------
    // ELEVATION HANDOFF
    // ---------------------------------------------------------------------
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string WriteToTempFile()
    {
        var path = Path.Combine(
            Path.GetTempPath(), $"dlss5suite-setup-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        return path;
    }

    public static InstallPlan? TryReadFromFile(string path)
    {
        try
        {
            var plan = JsonSerializer.Deserialize<InstallPlan>(File.ReadAllText(path));
            if (plan is not null) plan.Resumed = true;
            return plan;
        }
        catch
        {
            return null;
        }
    }
}















