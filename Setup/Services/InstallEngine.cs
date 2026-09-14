using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Dlss5Suite.Setup.Model;

namespace Dlss5Suite.Setup.Services;

public sealed record Progress(double Fraction, string Message);

/// <summary>
/// Does the install: unpacks the payload, writes the shortcuts, and registers
/// the entry Windows shows in Apps &amp; features.
/// </summary>
public sealed class InstallEngine
{
    public const string PayloadResource = "payload.zip";

    /// <summary>
    /// False for a build made without PayloadDir. The wizard checks this up
    /// front rather than letting the user answer five questions and then
    /// discover there is nothing to install.
    /// </summary>
    public static bool HasPayload =>
        Assembly.GetExecutingAssembly().GetManifestResourceNames().Contains(PayloadResource)
#if WEB_SETUP
        || true
#endif
        ;

    private static Stream OpenPayload() =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResource)
        ?? throw new InvalidOperationException(
            "This setup was built without a payload, so there is nothing to install. "
            + "Rebuild it with -p:PayloadDir=<published application folder>.");

    public async Task RunAsync(InstallPlan plan, IProgress<Progress> report, CancellationToken ct)
    {
        report.Report(new Progress(0.02, "Preparing…"));

        // An upgrade over a running copy is the case that breaks installers:
        // Windows locks a running executable, so the copy fails partway and
        // leaves a folder that is half one version and half the other. The
        // wizard has already offered to close it; this is the last check
        // before anything is written.
        if (RunningApp.IsRunning(plan.InstallDir))
            throw new IOException($"{InstallPlan.ProductName} is still running.");

        Directory.CreateDirectory(plan.InstallDir);

        report.Report(new Progress(0.06, "Removing the previous version…"));
        RemovePreviousFiles(plan.InstallDir, ct);

        var downloadedPayload = await OpenPayloadAsync(report, ct).ConfigureAwait(false);
        try
        {
            await Task.Run(() => Extract(plan, report, ct, downloadedPayload.Stream), ct).ConfigureAwait(false);
        }
        finally
        {
            downloadedPayload.Stream.Dispose();
            if (downloadedPayload.TempPath is not null)
                TryDo(() => File.Delete(downloadedPayload.TempPath));
        }

        report.Report(new Progress(0.90, "Creating shortcuts…"));
        ApplyShortcuts(plan);

        report.Report(new Progress(0.96, "Registering…"));
        WriteUninstallEntry(plan);
        WriteUninstaller(plan);

        // Last, and only if it was asked for. Windows shows its own consent
        // prompt here, so it has to come after the shortcut it pins exists -
        // and it must not sit in the middle of the file copy, because the
        // user is answering a dialog while it waits.
        if (plan.PinToTaskbar)
        {
            report.Report(new Progress(0.98, "Asking Windows about the taskbar…"));
            TaskbarOutcome = await TaskbarPin.RequestAsync().ConfigureAwait(false);
        }

        report.Report(new Progress(1.0, "Done."));
    }

    // -----------------------------------------------------------------
    // FILES
    // -----------------------------------------------------------------
    /// <summary>
    /// Clears the previous version's files, but leaves anything the user put
    /// in the folder themselves - a replacement payload under "mod files" is
    /// a supported thing to do, and an upgrade must not throw it away.
    /// </summary>
    private static void RemovePreviousFiles(string dir, CancellationToken ct)
    {
        if (!Directory.Exists(dir)) return;

        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            try { File.Delete(file); } catch { }
        }

        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(sub);

            if (name.Equals("mod files", StringComparison.OrdinalIgnoreCase)) continue;

            try { Directory.Delete(sub, recursive: true); } catch { }
        }
    }

    private sealed record PayloadHandle(Stream Stream, string? TempPath);

    private static async Task<PayloadHandle> OpenPayloadAsync(IProgress<Progress> report, CancellationToken ct)
    {
        await Task.CompletedTask;
        var embedded = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResource);
        if (embedded is not null) return new PayloadHandle(embedded, null);

#if WEB_SETUP
        var currentVersion = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        
        var api = string.IsNullOrWhiteSpace(currentVersion)
            ? "https://api.github.com/repos/Potatoes9411/DLSS-5-SUITE/releases/latest"
            : $"https://api.github.com/repos/Potatoes9411/DLSS-5-SUITE/releases/tags/v{currentVersion}";

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"DLSS5-SUITE-WebSetup/{currentVersion}");
        report.Report(new Progress(0.02, "Checking the verified release…"));
        using var metadataResponse = await http.GetAsync(api, ct).ConfigureAwait(false);
        metadataResponse.EnsureSuccessStatusCode();
        await using var metadataStream = await metadataResponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(metadataStream, cancellationToken: ct).ConfigureAwait(false);
        var tag = (document.RootElement.GetProperty("tag_name").GetString() ?? "").TrimStart('v', 'V');
        var expectedName = $"DLSS 5 SUITE Portable v{tag}.zip";
        var githubNormalizedName = expectedName.Replace(' ', '.');
        var assets = document.RootElement.GetProperty("assets").EnumerateArray();
        JsonElement? selected = null;
        foreach (var asset in assets)
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (name.Equals(expectedName, StringComparison.OrdinalIgnoreCase)
                || name.Equals(githubNormalizedName, StringComparison.OrdinalIgnoreCase))
            {
                selected = asset;
                break;
            }
        }
        if (selected is null) throw new InvalidOperationException("The latest GitHub release does not contain the expected portable ZIP.");
        var item = selected.Value;
        var nameValue = item.GetProperty("name").GetString()!;
        var url = item.GetProperty("browser_download_url").GetString()!;
        if (!item.TryGetProperty("digest", out var digestElement))
            throw new InvalidOperationException("GitHub did not publish a SHA-256 digest for the portable ZIP.");
        var expected = (digestElement.GetString() ?? "").Replace("sha256:", "", StringComparison.OrdinalIgnoreCase);
        if (expected.Length != 64) throw new InvalidOperationException("The portable ZIP has no valid SHA-256 digest.");

        var temp = Path.Combine(Path.GetTempPath(), $"dlss5-suite-{Guid.NewGuid():N}.zip");
        using var downloadResponse = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        downloadResponse.EnsureSuccessStatusCode();
        var total = downloadResponse.Content.Headers.ContentLength ?? 0L;
        await using (var input = await downloadResponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
        {
            var buffer = new byte[131072];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                var fraction = total > 0 ? done / (double)total : 0;
                report.Report(new Progress(0.03 + 0.35 * fraction, $"Downloading {nameValue}…"));
            }
        }
        await using (var verify = File.OpenRead(temp))
        {
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(verify, ct).ConfigureAwait(false));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(temp);
                throw new InvalidOperationException("The portable ZIP failed SHA-256 verification.");
            }
        }
        report.Report(new Progress(0.39, "Download verified. Preparing installation…"));
        return new PayloadHandle(File.OpenRead(temp), temp);
#else
        throw new InvalidOperationException("This setup was built without an application payload.");
#endif
    }

    private static void Extract(InstallPlan plan, IProgress<Progress> report, CancellationToken ct, Stream payload)
    {
        using var zip = new ZipArchive(payload, ZipArchiveMode.Read, leaveOpen: true);

        var entries = zip.Entries.Where(e => e.Length > 0 || !e.FullName.EndsWith('/')).ToList();
        var total = Math.Max(1L, entries.Sum(e => e.Length));
        long done = 0;

        var root = Path.GetFullPath(plan.InstallDir);

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name)) continue;

            var target = Path.GetFullPath(Path.Combine(root, entry.FullName));

            // Refuse anything that resolves outside the install folder. The
            // payload is ours, but an archive that can write anywhere is not
            // a thing to leave lying around in an elevated process.
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                continue;

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);

            done += entry.Length;

            report.Report(new Progress(
                0.08 + 0.80 * (done / (double)total),
                $"Installing {entry.Name}…"));
        }
    }

    // -----------------------------------------------------------------
    // SHORTCUTS
    // -----------------------------------------------------------------
    private void ApplyShortcuts(InstallPlan plan)
    {
        if (plan.IsSilentUpdate) return;
        if (plan.DesktopShortcut)
            TryDo(() => Shortcuts.CreateLink(Shortcuts.DesktopLinkPath(plan.Scope), plan.ExePath));

        if (plan.StartMenuShortcut)
            TryDo(() => Shortcuts.CreateLink(Shortcuts.StartMenuLinkPath(plan.Scope), plan.ExePath));

        TryDo(() => Shortcuts.SetStartWithWindows(plan, plan.StartWithWindows));
    }

    /// <summary>How the taskbar request went. Read by the finish page.</summary>
    public PinOutcome TaskbarOutcome { get; private set; } = PinOutcome.NotAllowed;

    private static void TryDo(Action action)
    {
        try { action(); } catch { }
    }

    // -----------------------------------------------------------------
    // APPS & FEATURES
    // -----------------------------------------------------------------
    private const string UninstallRoot =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    private static void WriteUninstallEntry(InstallPlan plan)
    {
        var root = plan.Scope == InstallScope.AllUsers ? Registry.LocalMachine : Registry.CurrentUser;
        using var key = root.CreateSubKey($@"{UninstallRoot}\{InstallPlan.UninstallKey}", true);
        if (key is null) return;

        var uninstaller = Path.Combine(plan.InstallDir, "uninstall.exe");

        key.SetValue("DisplayName", InstallPlan.ProductName);
        key.SetValue("DisplayVersion", InstallPlan.Version);
        key.SetValue("Publisher", InstallPlan.Publisher);
        key.SetValue("DisplayIcon", plan.ExePath);
        key.SetValue("InstallLocation", plan.InstallDir);
        key.SetValue("URLInfoAbout", InstallPlan.WebsiteUrl);
        key.SetValue("UninstallString", $"\"{uninstaller}\"");
        key.SetValue("QuietUninstallString", $"\"{uninstaller}\" --silent");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));

        key.SetValue("EstimatedSize", DirectorySizeKb(plan.InstallDir), RegistryValueKind.DWord);
    }

    private static int DirectorySizeKb(string dir)
    {
        try
        {
            var bytes = new DirectoryInfo(dir)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);

            return (int)Math.Clamp(bytes / 1024, 1, int.MaxValue);
        }
        catch
        {
            return 1;
        }
    }

    /// <summary>
    /// Drops a copy of this setup next to the application to act as its
    /// uninstaller, so removing it never depends on the download still being
    /// in the user's Downloads folder.
    /// </summary>
    private static void WriteUninstaller(InstallPlan plan)
    {
        try
        {
            var self = Environment.ProcessPath;
            if (string.IsNullOrEmpty(self)) return;

            File.Copy(self, Path.Combine(plan.InstallDir, "uninstall.exe"), overwrite: true);
        }
        catch { }
    }

    // -----------------------------------------------------------------
    // UNINSTALL
    // -----------------------------------------------------------------
    public static async Task UninstallAsync(InstallPlan plan, IProgress<Progress> report, CancellationToken ct)
    {
        report.Report(new Progress(0.05, "Closing the application…"));
        await RunningApp.CloseAsync(plan.InstallDir, ct).ConfigureAwait(false);

        report.Report(new Progress(0.25, "Removing shortcuts…"));
        TryDo(() => File.Delete(Shortcuts.DesktopLinkPath(plan.Scope)));
        TryDo(() => File.Delete(Shortcuts.StartMenuLinkPath(plan.Scope)));
        TryDo(() => Shortcuts.SetStartWithWindows(plan, false));

        report.Report(new Progress(0.45, "Removing files…"));
        await Task.Run(() =>
        {
            try { Directory.Delete(plan.InstallDir, recursive: true); } catch { }
        }, ct).ConfigureAwait(false);

        report.Report(new Progress(0.85, "Cleaning up…"));
        var root = plan.Scope == InstallScope.AllUsers ? Registry.LocalMachine : Registry.CurrentUser;
        TryDo(() => root.DeleteSubKeyTree($@"{UninstallRoot}\{InstallPlan.UninstallKey}", false));

        report.Report(new Progress(1.0, "Removed."));
    }

    /// <summary>
    /// Finds an existing install, so the wizard can say "update" rather than
    /// "install" and default to the folder already in use.
    /// </summary>
    public static InstallPlan? FindExisting()
    {
        foreach (var (root, scope) in new[]
                 {
                     (Registry.LocalMachine, InstallScope.AllUsers),
                     (Registry.CurrentUser, InstallScope.CurrentUser)
                 })
        {
            try
            {
                using var key = root.OpenSubKey($@"{UninstallRoot}\{InstallPlan.UninstallKey}");
                var dir = key?.GetValue("InstallLocation") as string;

                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                    return new InstallPlan { Scope = scope, InstallDir = dir };
            }
            catch { }
        }

        return null;
    }
}

