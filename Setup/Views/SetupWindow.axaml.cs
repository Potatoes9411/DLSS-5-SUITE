using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Dlss5Suite.Setup.Model;
using Dlss5Suite.Setup.Services;

namespace Dlss5Suite.Setup.Views;

public partial class SetupWindow : Window
{
    private enum Page { Welcome = 0, Location = 1, Options = 2, Progress = 3, Finish = 4 }

    private readonly InstallPlan _plan;
    private readonly InstallEngine _engine = new();
    private readonly bool _isUpgrade;
    private CancellationTokenSource? _cts;

    private Page _page = Page.Welcome;

    // -----------------------------------------------------------------
    // CONTROLS
    // -----------------------------------------------------------------
    // Resolved by name rather than through generated fields. Avalonia 12's
    // name generator needs Roslyn 4.14 and the SDK in use here ships 4.11, so
    // it is silently skipped and those fields never appear - the application
    // this installs reaches for its own controls the same way, for the same
    // reason. Named lookups keep the project building on the SDK that is
    // actually installed instead of quietly requiring a newer one.
    private Grid TitleBar = null!;
    private Button CloseButton = null!, BrowseButton = null!, BackButton = null!, NextButton = null!;
    private Button CloseAndRetryButton = null!, RetryButton = null!, CancelInstallButton = null!;
    private Button ErrorRetryButton = null!, ErrorCloseButton = null!;
    private StackPanel PageWelcome = null!, PageLocation = null!, PageOptions = null!;
    private StackPanel PageProgress = null!, PageFinish = null!;
    private TextBlock WelcomeHeading = null!, WelcomeSub = null!, SpaceText = null!, StepText = null!;
    private TextBlock ProgressHeading = null!, ProgressStatus = null!, ErrorText = null!;
    private TextBlock FinishHeading = null!, FinishSub = null!;
    private TextBlock ElevationNoticeText = null!, SpaceNoticeText = null!, RunningText = null!;
    private RadioButton ScopeCurrentUser = null!, ScopeAllUsers = null!;
    private TextBox PathBox = null!;
    private Border ElevationNotice = null!, SpaceNotice = null!, RunningNotice = null!;
    private Border ErrorNotice = null!, PinWarning = null!;
    private TextBlock PinWarningText = null!;
    private CheckBox OptDesktop = null!, OptStartMenu = null!, OptTaskbar = null!, OptStartup = null!;
    private CheckBox FinishRun = null!, FinishWebsite = null!;
    private ProgressBar Bar = null!;

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        T Find<T>(string name) where T : Control =>
            this.FindControl<T>(name)
            ?? throw new InvalidOperationException($"SetupWindow.axaml has no control named '{name}'.");

        TitleBar = Find<Grid>("TitleBar");
        CloseButton = Find<Button>("CloseButton");
        BrowseButton = Find<Button>("BrowseButton");
        BackButton = Find<Button>("BackButton");
        NextButton = Find<Button>("NextButton");
        CloseAndRetryButton = Find<Button>("CloseAndRetryButton");
        RetryButton = Find<Button>("RetryButton");
        CancelInstallButton = Find<Button>("CancelInstallButton");
        ErrorRetryButton = Find<Button>("ErrorRetryButton");
        ErrorCloseButton = Find<Button>("ErrorCloseButton");

        PageWelcome = Find<StackPanel>("PageWelcome");
        PageLocation = Find<StackPanel>("PageLocation");
        PageOptions = Find<StackPanel>("PageOptions");
        PageProgress = Find<StackPanel>("PageProgress");
        PageFinish = Find<StackPanel>("PageFinish");

        WelcomeHeading = Find<TextBlock>("WelcomeHeading");
        WelcomeSub = Find<TextBlock>("WelcomeSub");
        SpaceText = Find<TextBlock>("SpaceText");
        StepText = Find<TextBlock>("StepText");
        ProgressHeading = Find<TextBlock>("ProgressHeading");
        ProgressStatus = Find<TextBlock>("ProgressStatus");
        ErrorText = Find<TextBlock>("ErrorText");
        FinishHeading = Find<TextBlock>("FinishHeading");
        FinishSub = Find<TextBlock>("FinishSub");
        ElevationNoticeText = Find<TextBlock>("ElevationNoticeText");
        SpaceNoticeText = Find<TextBlock>("SpaceNoticeText");
        RunningText = Find<TextBlock>("RunningText");

        ScopeCurrentUser = Find<RadioButton>("ScopeCurrentUser");
        ScopeAllUsers = Find<RadioButton>("ScopeAllUsers");
        PathBox = Find<TextBox>("PathBox");

        ElevationNotice = Find<Border>("ElevationNotice");
        SpaceNotice = Find<Border>("SpaceNotice");
        RunningNotice = Find<Border>("RunningNotice");
        ErrorNotice = Find<Border>("ErrorNotice");
        PinWarning = Find<Border>("PinWarning");
        PinWarningText = Find<TextBlock>("PinWarningText");

        OptDesktop = Find<CheckBox>("OptDesktop");
        OptStartMenu = Find<CheckBox>("OptStartMenu");
        OptTaskbar = Find<CheckBox>("OptTaskbar");
        OptStartup = Find<CheckBox>("OptStartup");
        FinishRun = Find<CheckBox>("FinishRun");
        FinishWebsite = Find<CheckBox>("FinishWebsite");

        Bar = Find<ProgressBar>("Bar");
    }

    public SetupWindow() : this(null) { }

    public SetupWindow(InstallPlan? resumed)
    {
        InitializeComponent();

        // Three ways in: a fresh run, a run over an existing install, and the
        // elevated relaunch carrying the answers already given.
        var existing = InstallEngine.FindExisting();
        _isUpgrade = existing is not null;

        _plan = resumed
                ?? existing
                ?? new InstallPlan { InstallDir = InstallPlan.DefaultDir(InstallScope.CurrentUser) };

        if (resumed is null && existing is not null)
        {
            // Keep the folder, take the defaults for everything else.
            _plan.DesktopShortcut = true;
            _plan.StartMenuShortcut = true;
        }

        WireUp();
        LoadPlanIntoControls();

        if (resumed is not null)
        {
            // The user answered everything before the UAC prompt. Carrying
            // them back through the wizard would be asking twice, so this
            // copy goes straight to work.
            GoTo(Page.Progress);
            _ = StartInstallAsync();
        }
        else
        {
            ApplyUpgradeWording();
            GoTo(Page.Welcome);
        }
    }

    // -----------------------------------------------------------------
    // WIRING
    // -----------------------------------------------------------------
    private void WireUp()
    {
        TitleBar.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        };

        CloseButton.Click += (_, _) => Close();
        NextButton.Click += async (_, _) => await OnNextAsync();
        BackButton.Click += (_, _) => OnBack();
        BrowseButton.Click += async (_, _) => await BrowseAsync();

        ScopeAllUsers.IsCheckedChanged += (_, _) => OnScopeChanged();
        ScopeCurrentUser.IsCheckedChanged += (_, _) => OnScopeChanged();

        PathBox.TextChanged += (_, _) => RefreshLocationNotices();

        CloseAndRetryButton.Click += async (_, _) =>
        {
            RunningNotice.IsVisible = false;
            ProgressStatus.Text = "Closing DLSS 5 SUITE…";
            await RunningApp.CloseAsync(_plan.InstallDir);
            await StartInstallAsync();
        };

        RetryButton.Click += async (_, _) =>
        {
            RunningNotice.IsVisible = false;
            await StartInstallAsync();
        };

        CancelInstallButton.Click += (_, _) => Close();
        ErrorCloseButton.Click += (_, _) => Close();

        ErrorRetryButton.Click += async (_, _) =>
        {
            ErrorNotice.IsVisible = false;
            await StartInstallAsync();
        };
    }

    private void LoadPlanIntoControls()
    {
        ScopeAllUsers.IsChecked = _plan.Scope == InstallScope.AllUsers;
        ScopeCurrentUser.IsChecked = _plan.Scope == InstallScope.CurrentUser;
        PathBox.Text = _plan.InstallDir;

        OptDesktop.IsChecked = _plan.DesktopShortcut;
        OptStartMenu.IsChecked = _plan.StartMenuShortcut;
        OptTaskbar.IsChecked = _plan.PinToTaskbar;
        OptStartup.IsChecked = _plan.StartWithWindows;

        FinishRun.IsChecked = _plan.LaunchWhenDone;
        FinishWebsite.IsChecked = _plan.OpenWebsiteWhenDone;
    }

    private void ReadControlsIntoPlan()
    {
        _plan.Scope = ScopeAllUsers.IsChecked == true ? InstallScope.AllUsers : InstallScope.CurrentUser;
        _plan.InstallDir = (PathBox.Text ?? "").Trim();

        _plan.DesktopShortcut = OptDesktop.IsChecked == true;
        _plan.StartMenuShortcut = OptStartMenu.IsChecked == true;
        _plan.PinToTaskbar = OptTaskbar.IsChecked == true;
        _plan.StartWithWindows = OptStartup.IsChecked == true;
    }

    private void ApplyUpgradeWording()
    {
        if (!_isUpgrade) return;

        WelcomeHeading.Text = $"Update {InstallPlan.ProductName}";
        WelcomeSub.Text =
            $"Version {InstallPlan.Version} will replace the copy already installed in {_plan.InstallDir}.";
    }

    // -----------------------------------------------------------------
    // NAVIGATION
    // -----------------------------------------------------------------
    private void GoTo(Page page)
    {
        _page = page;

        PageWelcome.IsVisible = page == Page.Welcome;
        PageLocation.IsVisible = page == Page.Location;
        PageOptions.IsVisible = page == Page.Options;
        PageProgress.IsVisible = page == Page.Progress;
        PageFinish.IsVisible = page == Page.Finish;

        BackButton.IsVisible = page is Page.Location or Page.Options;
        NextButton.IsVisible = page != Page.Progress;

        NextButton.Content = page switch
        {
            Page.Options => _isUpgrade ? "Update" : "Install",
            Page.Finish => "Finish",
            _ => "Next"
        };

        StepText.Text = page switch
        {
            Page.Welcome => "Step 1 of 3",
            Page.Location => "Step 2 of 3",
            Page.Options => "Step 3 of 3",
            _ => ""
        };

        if (page == Page.Location) RefreshLocationNotices();
    }

    private void OnBack() => GoTo(_page == Page.Options ? Page.Location : Page.Welcome);

    private async Task OnNextAsync()
    {
        switch (_page)
        {
            case Page.Welcome:
                ReadControlsIntoPlan();
                GoTo(Page.Location);
                break;

            case Page.Location:
                ReadControlsIntoPlan();
                if (!ValidateLocation()) return;
                GoTo(Page.Options);
                break;

            case Page.Options:
                ReadControlsIntoPlan();
                await BeginInstallAsync();
                break;

            case Page.Finish:
                FinishUp();
                break;
        }
    }

    private void OnScopeChanged()
    {
        var scope = ScopeAllUsers.IsChecked == true ? InstallScope.AllUsers : InstallScope.CurrentUser;

        // Only move the folder when the user has not typed one of their own,
        // so switching scope to compare the two never discards a custom path.
        var current = (PathBox.Text ?? "").Trim();

        if (string.IsNullOrEmpty(current)
            || current.Equals(InstallPlan.DefaultDir(InstallScope.AllUsers), StringComparison.OrdinalIgnoreCase)
            || current.Equals(InstallPlan.DefaultDir(InstallScope.CurrentUser), StringComparison.OrdinalIgnoreCase))
        {
            PathBox.Text = InstallPlan.DefaultDir(scope);
        }

        RefreshLocationNotices();
    }

    // -----------------------------------------------------------------
    // LOCATION
    // -----------------------------------------------------------------
    private async Task BrowseAsync()
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose an install folder",
            AllowMultiple = false
        });

        if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } local)
        {
            PathBox.Text = Path.Combine(local, InstallPlan.ProductName);
        }
    }

    private void RefreshLocationNotices()
    {
        var dir = (PathBox.Text ?? "").Trim();
        if (string.IsNullOrEmpty(dir)) return;

        var probe = new InstallPlan
        {
            Scope = ScopeAllUsers.IsChecked == true ? InstallScope.AllUsers : InstallScope.CurrentUser,
            InstallDir = dir
        };

        ElevationNotice.IsVisible = Elevation.IsRequiredFor(probe);

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(dir));

            if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
            {
                var free = new DriveInfo(root).AvailableFreeSpace;
                SpaceText.Text = $"{free / 1024d / 1024d / 1024d:0.0} GB free on {root.TrimEnd('\\')}";
            }
        }
        catch
        {
            SpaceText.Text = "";
        }
    }

    private bool ValidateLocation()
    {
        SpaceNotice.IsVisible = false;

        if (string.IsNullOrWhiteSpace(_plan.InstallDir))
        {
            ShowLocationProblem("Choose a folder to install into.");
            return false;
        }

        try
        {
            var full = Path.GetFullPath(_plan.InstallDir);

            if (!Path.IsPathRooted(full))
            {
                ShowLocationProblem("Enter a full path, including the drive.");
                return false;
            }

            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                ShowLocationProblem("That drive does not exist.");
                return false;
            }

            // Refuse a folder that is a drive root or a system folder: the
            // uninstaller deletes the install folder whole, and pointing it
            // at C:\ or C:\Windows would be catastrophic.
            if (full.TrimEnd('\\').Equals(root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                ShowLocationProblem("Pick a folder rather than the root of a drive.");
                return false;
            }

            foreach (var forbidden in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                         Environment.GetFolderPath(Environment.SpecialFolder.System),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                     })
            {
                if (!string.IsNullOrEmpty(forbidden) &&
                    full.TrimEnd('\\').Equals(forbidden.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    ShowLocationProblem($"Install into a folder inside {forbidden}, not {forbidden} itself.");
                    return false;
                }
            }

            _plan.InstallDir = full;
            return true;
        }
        catch
        {
            ShowLocationProblem("That path is not valid.");
            return false;
        }
    }

    private void ShowLocationProblem(string message)
    {
        SpaceNoticeText.Text = message;
        SpaceNotice.IsVisible = true;
    }

    // -----------------------------------------------------------------
    // INSTALL
    // -----------------------------------------------------------------
    private async Task BeginInstallAsync()
    {
        if (!InstallEngine.HasPayload)
        {
            GoTo(Page.Progress);
            ShowError("This setup was built without an application payload, so there is nothing to install.");
            return;
        }

        // Ask for rights now, before anything is written, and hand over what
        // the user has already answered so the elevated copy resumes here.
        if (Elevation.IsRequiredFor(_plan))
        {
            if (Elevation.TryRelaunchElevated(_plan, out _))
            {
                Close();
                return;
            }

            GoTo(Page.Progress);
            ShowError(
                "Administrator rights are needed for this location, and the request was declined. "
                + "Go back and choose \"Just me\", or a folder outside Program Files.");
            return;
        }

        GoTo(Page.Progress);
        await StartInstallAsync();
    }

    private async Task StartInstallAsync()
    {
        RunningNotice.IsVisible = false;
        ErrorNotice.IsVisible = false;
        ProgressHeading.Text = _isUpgrade ? "Updating…" : "Installing…";
        Bar.Value = 0;

        // The check that makes an update work. Windows locks a running
        // executable, so this has to be settled before the first file.
        if (RunningApp.IsRunning(_plan.InstallDir))
        {
            RunningText.Text =
                $"{InstallPlan.ProductName} is running, and Windows will not let its files be replaced "
                + "while it is open. Close it and try again, or let the setup close it for you.";
            RunningNotice.IsVisible = true;
            ProgressStatus.Text = "Waiting for the application to close.";
            return;
        }

        _cts = new CancellationTokenSource();

        var report = new Progress<Services.Progress>(p => Dispatcher.UIThread.Post(() =>
        {
            Bar.Value = p.Fraction;
            ProgressStatus.Text = p.Message;
        }));

        try
        {
            await _engine.RunAsync(_plan, report, _cts.Token);
            Dispatcher.UIThread.Post(() => ShowFinish());
        }
        catch (OperationCanceledException)
        {
            Dispatcher.UIThread.Post(() => Close());
        }
        catch (UnauthorizedAccessException)
        {
            Dispatcher.UIThread.Post(() => ShowError(
                "Windows refused access to that folder. Choose another location, or run the setup as "
                + "administrator."));
        }
        catch (IOException ex) when (RunningApp.IsRunning(_plan.InstallDir))
        {
            Dispatcher.UIThread.Post(() =>
            {
                // It started while the install was running.
                RunningText.Text = $"{InstallPlan.ProductName} started while the install was running: {ex.Message}";
                RunningNotice.IsVisible = true;
            });
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => ShowError(ex.Message));
        }
    }

    private void ShowError(string message)
    {
        ProgressHeading.Text = "That did not work";
        ProgressStatus.Text = "";
        ErrorText.Text = message;
        ErrorNotice.IsVisible = true;
        ErrorRetryButton.IsVisible = InstallEngine.HasPayload;
    }

    private void ShowFinish()
    {
        FinishHeading.Text = _isUpgrade ? "Updated" : "Installed";
        FinishSub.Text = $"{InstallPlan.ProductName} {InstallPlan.Version} is in {_plan.InstallDir}.";
        // Windows owns this decision, so the page reports its answer rather
        // than assuming one.
        if (_plan.PinToTaskbar && _engine.TaskbarOutcome != PinOutcome.Pinned
                               && _engine.TaskbarOutcome != PinOutcome.AlreadyPinned)
        {
            PinWarningText.Text = _engine.TaskbarOutcome switch
            {
                PinOutcome.Declined =>
                    "The taskbar pin was declined. You can still pin it later: right-click the app "
                    + "on the taskbar while it is running and choose “Pin to taskbar”.",
                _ =>
                    "Windows would not allow the taskbar pin on this system. Right-click the app on "
                    + "the taskbar while it is running and choose “Pin to taskbar”."
            };

            PinWarning.IsVisible = true;
        }
        else
        {
            PinWarning.IsVisible = false;
        }
        GoTo(Page.Finish);
    }

    private void FinishUp()
    {
        _plan.LaunchWhenDone = FinishRun.IsChecked == true;
        _plan.OpenWebsiteWhenDone = FinishWebsite.IsChecked == true;

        if (_plan.OpenWebsiteWhenDone) Open(InstallPlan.WebsiteUrl);

        if (_plan.LaunchWhenDone && File.Exists(_plan.ExePath))
        {
            // Started without elevation even when the setup is elevated: the
            // application asks for nothing, and a child of an elevated setup
            // would otherwise inherit administrator rights it never wanted.
            LaunchUnelevated(_plan.ExePath);
        }

        Close();
    }

    private static void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch { }
    }

    /// <summary>
    /// Runs the application as the logged-on user rather than as a child of
    /// this process. Explorer is already running unelevated, so asking it to
    /// do the launch drops the administrator token cleanly.
    /// </summary>
    private static void LaunchUnelevated(string exePath)
    {
        if (!Elevation.IsElevated)
        {
            Open(exePath);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                Arguments = $"\"{exePath}\"",
                UseShellExecute = true
            });
        }
        catch
        {
            Open(exePath);
        }
    }
}
