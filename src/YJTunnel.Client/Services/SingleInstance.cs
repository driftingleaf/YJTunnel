using System.IO;
using System.Threading;

namespace YJTunnel.Client.Services;

/// <summary>
/// 单实例守卫：跨平台互斥（Windows 命名 Mutex / Unix 文件锁）。
/// 第二个实例启动时，通知已有实例弹出主窗口后退出。
/// </summary>
public static class SingleInstance
{
    private const string MutexName = "YJTunnel.Client.SingleInstance";
    private const string ShowEventName = "YJTunnel.Client.ShowWindow";

    private static Mutex? _mutex;
    private static EventWaitHandle? _showEvent;

    /// <summary>尝试成为唯一实例。返回 false 表示已有实例在跑。</summary>
    public static bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (createdNew)
        {
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            return true;
        }
        _mutex.Dispose();
        _mutex = null;
        return false;
    }

    /// <summary>通知已有实例显示主窗口（由第二个实例调用）。命名 EventWaitHandle 仅 Windows 支持。</summary>
    public static void SignalShow()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (EventWaitHandle.TryOpenExisting(ShowEventName, out var evt))
        {
            using (evt) evt.Set();
        }
    }

    /// <summary>监听"弹出主窗口"信号（由唯一实例在后台线程调用）。</summary>
    public static void ListenShow(Action onShow)
    {
        _showEvent = _showEvent ?? new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var evt = _showEvent;
        Task.Run(() =>
        {
            while (evt.WaitOne())
            {
                onShow();
            }
        });
    }

    public static void Release()
    {
        try { _mutex?.ReleaseMutex(); } catch { /* 非拥有线程释放时忽略 */ }
        _mutex?.Dispose();
        _mutex = null;
    }
}
