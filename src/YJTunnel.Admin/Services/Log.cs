using System.IO;

namespace YJTunnel.Admin.Services;

/// <summary>日志（%APPDATA%\YJTunnel\logs\admin-yyyyMMdd.log，保留 7 天）。供诊断与排障。</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly string Dir = Path.Combine(AdminConfigStore.DefaultDir, "logs");

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", ex is null ? message : $"{message} :: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                CleanOld();
                var path = Path.Combine(Dir, $"admin-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志失败不影响主流程
        }
    }

    private static void CleanOld()
    {
        foreach (var file in Directory.EnumerateFiles(Dir, "admin-*.log"))
        {
            if (File.GetLastWriteTime(file) < DateTime.Now.AddDays(-7))
                File.Delete(file);
        }
    }
}
