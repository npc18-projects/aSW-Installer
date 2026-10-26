using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace AStoryWealth.Installer;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;
            // A mistake inside a button handler must not close the app half way through something (a removal, an install).
            Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                try { window.ShowUnexpectedError(e.Exception); } catch { }
                e.Handled = true;
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}