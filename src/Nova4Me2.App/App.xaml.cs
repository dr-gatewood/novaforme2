using System.Windows;
using System.Windows.Threading;
using Nova4Me2.App.Services;
using Nova4Me2.Core.Util;

namespace Nova4Me2.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Error("Unhandled: " + a.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, a) => { Log.Error("Unobserved: " + a.Exception.GetBaseException().Message); a.SetObserved(); };
        var settings = Settings.Load();
        ThemeManager.Apply(settings.Theme);
        try { Log.ToFile(Settings.LogFile); } catch { }
        var w = new MainWindow();
        MainWindow = w;
        w.Show();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("UI exception: " + e.Exception);
        e.Handled = true;
        try { (MainWindow as MainWindow)?.Toast("Unexpected error", e.Exception.GetBaseException().Message, true); }
        catch { MessageBox.Show(e.Exception.GetBaseException().Message, "Nova4Me2", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { AppState.Current.Close(); } catch { }
        base.OnExit(e);
    }
}
