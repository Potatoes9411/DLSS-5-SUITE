using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Dlss5Suite.Setup.Model;
using Dlss5Suite.Setup.Views;

namespace Dlss5Suite.Setup;

public partial class App : Application
{
    /// <summary>
    /// The plan handed over by the unelevated copy, when this process is the
    /// elevated half of an install. Null on a normal run.
    /// </summary>
    public static InstallPlan? Resumed { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new SetupWindow(Resumed);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
