using System;
using System.IO;
using Avalonia;
using Dlss5Suite.Setup.Model;
using Dlss5Suite.Setup.Services;

namespace Dlss5Suite.Setup;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var silentIndex = Array.FindIndex(args, a => a.Equals("--silent", StringComparison.OrdinalIgnoreCase));
        if (silentIndex >= 0)
        {
            var dirIndex = Array.FindIndex(args, a => a.Equals("--dir", StringComparison.OrdinalIgnoreCase));
            var dir = dirIndex >= 0 && dirIndex + 1 < args.Length ? args[dirIndex + 1] : null;
            if (!string.IsNullOrWhiteSpace(dir))
            {
                var scope = dir.StartsWith("C:\\Program Files", StringComparison.OrdinalIgnoreCase) 
                    ? InstallScope.AllUsers : InstallScope.CurrentUser;
                    
                var plan = new InstallPlan 
                { 
                    InstallDir = dir,
                    Scope = scope,
                    IsSilentUpdate = true
                };

                var exe = Path.Combine(plan.InstallDir, "DLSS 5 SUITE.exe");

                // Get every copy of the app out of the install folder first.
                //
                // This used to wait five seconds and then let RunAsync throw
                // "DLSS 5 SUITE is still running" - an unhandled exception, so
                // the update died with no window and no message. That happened
                // whenever a second copy of the app was open, or the one that
                // started the update took longer than five seconds to finish
                // exiting. The Windows event log shows exactly that failure on
                // a .5 -> .6 update.
                //
                // So: give the app a fair chance to close on its own, then close
                // whatever is left - politely first, then forcibly. The user
                // asked for this update, so closing the old copy is the point.
                var waitUntil = DateTime.UtcNow.AddSeconds(20);
                while (RunningApp.IsRunning(plan.InstallDir) && DateTime.UtcNow < waitUntil)
                {
                    System.Threading.Thread.Sleep(200);
                }

                if (RunningApp.IsRunning(plan.InstallDir))
                {
                    RunningApp.CloseAsync(plan.InstallDir).GetAwaiter().GetResult();
                }

                try
                {
                    var engine = new InstallEngine();
                    engine.RunAsync(plan, new Progress<Progress>(p => {}), System.Threading.CancellationToken.None).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    // Never fail silently again. Record why, and put the user
                    // back in the app they had rather than leaving them with
                    // nothing open and no idea the update did not happen.
                    try
                    {
                        var logDir = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "DLSS5Suite");
                        Directory.CreateDirectory(logDir);
                        File.WriteAllText(
                            Path.Combine(logDir, "update_error.txt"),
                            $"{DateTime.Now:u}  update to {InstallPlan.Version} failed{Environment.NewLine}{ex}");
                    }
                    catch
                    {
                    }

                    if (File.Exists(exe))
                    {
                        try
                        {
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });
                        }
                        catch
                        {
                        }
                    }

                    return;
                }


                // Tell the app it was just updated, through a file as well as
                // the command line.
                //
                // The argument alone was not enough. The app that reads it is
                // the NEW version, and whether a given build handled --updated,
                // and whether it wired it to the flag the popup actually binds
                // to, varied from release to release - so the popup silently
                // never appeared. A marker in the user's data folder does not
                // depend on any of that: the app finds it, shows the popup, and
                // deletes it, so it fires exactly once.
                //
                // LocalApplicationData, not the install folder: an unelevated
                // app can read Program Files but cannot delete from it, and a
                // marker it cannot remove would show the popup on every launch.
                try
                {
                    var dataDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "DLSS5Suite");
                    Directory.CreateDirectory(dataDir);
                    File.WriteAllText(Path.Combine(dataDir, "update_completed.txt"), InstallPlan.Version);
                }
                catch
                {
                    // Losing the popup is not worth failing an update over.
                }

                if (File.Exists(exe))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
                    {
                        Arguments = "--updated " + InstallPlan.Version,
                        UseShellExecute = true
                    });
                }
                return;
            }
        }

        var resumeIndex = Array.FindIndex(args, a =>
            a.Equals("--resume", StringComparison.OrdinalIgnoreCase));

        if (resumeIndex >= 0 && resumeIndex + 1 < args.Length)
        {
            var statePath = args[resumeIndex + 1];
            App.Resumed = InstallPlan.TryReadFromFile(statePath);

            try { File.Delete(statePath); } catch { }
        }

        ShellLink.TagCurrentProcess();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

