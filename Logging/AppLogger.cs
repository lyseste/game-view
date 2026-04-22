using System.IO;
using Serilog;
using Serilog.Events;

namespace VCDV.Logging;

public static class AppLogger
{
    public static void Initialize()
    {
        var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(logDir);
        PruneOldLogs(logDir, keepCount: 5);

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var logPath   = Path.Combine(logDir, $"game-view_{timestamp}.log");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .WriteTo.File(
                logPath,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss}  {Level:u4} {Message:lj}{NewLine}{Exception}",
                flushToDiskInterval: TimeSpan.Zero)
            .CreateLogger();

        Log.Information("====== Game View starting ======");
        Log.Information("version: 1.0.0");
        Log.Information("platform: windows");
    }

    public static void Shutdown()
    {
        Log.Information("====== Game View shutting down ======");
        Log.CloseAndFlush();
    }

    private static void PruneOldLogs(string logDir, int keepCount)
    {
        try
        {
            var logs = Directory.GetFiles(logDir, "game-view_*.log")
                                .OrderByDescending(f => f)
                                .Skip(keepCount)
                                .ToArray();
            foreach (var log in logs)
                File.Delete(log);
        }
        catch { /* non-fatal */ }
    }
}
