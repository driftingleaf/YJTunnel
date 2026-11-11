using System.IO;
using Microsoft.Win32;

namespace YJTunnel.Client.Services;

/// <summary>开机自启（HKCU\Software\Microsoft\Windows\CurrentVersion\Run）。Linux 下无注册表，操作为空实现。</summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "YJTunnel";

    public static bool IsEnabled()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string path && SameFile(path);
    }

    public static void Set(bool enabled)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            // 引号包裹防路径空格；--minimized 表示自启时直接进托盘
            key.SetValue(ValueName, $"\"{exe}\" --minimized");
        }
        else
        {
            if (key.GetValue(ValueName) is not null) key.DeleteValue(ValueName);
        }
    }

    private static bool SameFile(string registeredPath)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;
        var clean = registeredPath.Trim().Trim('"');
        // Run 键可能带 --minimized 参数，取第一段比对
        var first = clean.Split(' ')[0];
        return string.Equals(
            Path.GetFullPath(first),
            Path.GetFullPath(exe),
            StringComparison.OrdinalIgnoreCase);
    }
}
