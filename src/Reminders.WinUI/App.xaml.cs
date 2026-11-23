using Microsoft.UI.Xaml;
using Reminders.Core;

namespace Reminders.Windows;

public partial class App : Application
{
    private Window? _window;
    internal static bool IsDemoLaunch =>
        Environment.GetCommandLineArgs().Contains("--demo", StringComparer.OrdinalIgnoreCase)
        || Environment.GetEnvironmentVariable("REMINDERS_DEMO") == "1";

    public App()
    {
        if (!IsDemoLaunch) AppStorage.MigrateLegacyData();
        UnhandledException += (_, args) => Services.AppLog.Error("Unhandled UI exception", args.Exception);
        Services.AppLog.Info("Starting Apple Reminders on Windows " + typeof(App).Assembly.GetName().Version + " build=" +
            System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(App).Assembly)?.InformationalVersion);
        try { InitializeComponent(); }
        catch (Exception error)
        {
            Services.AppLog.Error("Application resources could not be initialized", error);
            throw;
        }
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
#if DEBUG
            if (Environment.GetCommandLineArgs().Contains("--preview-editor", StringComparer.OrdinalIgnoreCase))
                _window.Title = Services.L10n.Get("EditorPreviewWindowTitle");
            if (Environment.GetCommandLineArgs().Contains("--test-apple-sign-in", StringComparer.OrdinalIgnoreCase))
                _window.Title = Services.L10n.Get("SignInTestWindowTitle");
#endif
            _window.Activate();
            Services.AppLog.Info("Main window activated");
        }
        catch (Exception error)
        {
            Services.AppLog.Error("Main window could not be launched", error);
            throw;
        }
    }
}
