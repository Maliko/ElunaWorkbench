using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace ElunaWorkbench;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Never lose the user's work because of an unexpected exception in an editor feature.
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            LogError(e.Exception);
            e.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogError(e.Exception);
            e.SetObserved();
        };
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;
            if (desktop.Args is { Length: > 0 }) window.OpenFromCommandLine(desktop.Args[0]);
        }
        base.OnFrameworkInitializationCompleted();
    }

    public static void LogError(Exception ex)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ElunaWorkbench");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"), $"[{DateTime.Now:O}] {ex}\n\n");
        }
        catch
        {
            // ignore logging failures
        }
        Console.Error.WriteLine(ex);
    }
}
