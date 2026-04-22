using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Animation;
using Serilog;
using VCDV.Logging;

namespace VCDV;

public partial class App : Application
{
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint uPeriod);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint uPeriod);

    private void App_Startup(object sender, StartupEventArgs e)
    {
        // Raise Windows timer resolution from default 15.6ms to 1ms.
        // Without this, WinRT frame callbacks can fire up to 15ms late, causing frames
        // to arrive just after a VBlank fires and wait an extra full refresh cycle (stutter).
        timeBeginPeriod(1);

        AppLogger.Initialize();

        DispatcherUnhandledException += (_, ex) =>
        {
            WriteCrash(ex.Exception, "DispatcherUnhandledException");
            ex.Handled = true;
            Shutdown(1);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            WriteCrash(ex.ExceptionObject as Exception, "UnhandledException");
        };
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Log.Error(ex.Exception, "Unobserved task exception");
            ex.SetObserved();
        };

        try
        {
            var window = new MainWindow();
            window.Show();
        }
        catch (Exception ex)
        {
            WriteCrash(ex, "MainWindow startup");
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        timeEndPeriod(1);
        AppLogger.Shutdown();
        base.OnExit(e);
    }

    private static void WriteCrash(Exception? ex, string context)
    {
        var message = $"[{context}] {ex}";
        try { Log.Fatal(ex, context); Log.CloseAndFlush(); } catch { /* ignore */ }

        // Guaranteed fallback: write next to the exe even if Serilog is broken
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "crash.txt");
            File.WriteAllText(path, message);
        }
        catch { /* ignore */ }

        MessageBox.Show(message, "Game View — Startup Error",
            MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
