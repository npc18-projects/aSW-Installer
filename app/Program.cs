using Avalonia;
using System;
using AStoryWealth.Core;

namespace AStoryWealth.Installer;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Update helper: the new exe started from the staging folder. It waits for the old app to exit,
        // copies the new files over the install folder and starts the app again. No window.
        if (args.Length >= 4 && args[0] == UpdateApplier.Flag)
        {
            var logPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AStoryWealth", "update.log");
            void Log(string s)
            {
                try
                {
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(logPath)!);
                    System.IO.File.AppendAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {s}{Environment.NewLine}");
                }
                catch { /* logging is best effort */ }
            }
            var ok = UpdateApplier.Apply(args[1], args[2], int.TryParse(args[3], out var pid) ? pid : 0, restart: true, log: Log);
            Log(ok ? "update applied" : "update NOT applied; the old version is still in place");
            return;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
