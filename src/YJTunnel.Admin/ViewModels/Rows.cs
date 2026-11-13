using System.Collections.ObjectModel;
using Avalonia.Media;

namespace YJTunnel.Admin.ViewModels;

/// <summary>界面行模型（每次刷新重建，携带预计算的显示文本与颜色）。</summary>
public sealed class SessionRow
{
    public string Initial { get; init; } = "";
    public string Name { get; init; } = "";
    public string ClientId { get; init; } = "";
    public string Machine { get; init; } = "";
    public string Ip { get; init; } = "";
    public string Version { get; init; } = "";
    public string Connections { get; init; } = "";
    public string Uptime { get; init; } = "";
    public IBrush AvatarBg { get; init; } = Palette.AccentSoft;
    public IBrush AvatarFg { get; init; } = Palette.Accent;
}

public sealed class PortRow
{
    public string CustomerId { get; init; } = "";
    public string Key { get; init; } = "";
    public string Title { get; init; } = "";
    public string LocalText { get; init; } = "";
    public string PublicText { get; init; } = "";
    public int RemotePort { get; init; }
    public bool Online { get; init; }
    public bool Failed { get; init; }
    public IBrush ChipBg { get; init; } = Palette.Panel2;
    public IBrush ChipFg { get; init; } = Palette.Ink2;
    public IBrush PortFg { get; init; } = Palette.Ink;
    /// <summary>端口胶囊显示文本：备注 本地→公网。</summary>
    public string ChipText => string.IsNullOrWhiteSpace(Title)
        ? $"{LocalText}→{PublicText}"
        : $"{Title} {LocalText}→{PublicText}";
    public string ToolTip { get; init; } = "";
}

public sealed class CustomerRow
{
    public string ClientId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Initial { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public bool Online { get; init; }
    public bool Disabled { get; init; }
    public bool Banned { get; init; }
    public string BadgeText { get; init; } = "";
    public IBrush BadgeBg { get; init; } = Palette.Panel2;
    public IBrush BadgeFg { get; init; } = Palette.Ink2;
    public ObservableCollection<PortRow> Ports { get; } = new();
    public IBrush AvatarBg { get; init; } = Palette.AccentSoft;
    public IBrush AvatarFg { get; init; } = Palette.Accent;
    /// <summary>是否显示「解封」按钮（禁用或封禁中）。</summary>
    public bool CanUnban => Disabled || Banned;
    /// <summary>是否显示「踢下线」（在线且未禁用）。</summary>
    public bool CanKick => Online && !Disabled;
}

public sealed class PendingRow
{
    public string ClientId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Initial { get; init; } = "";
    public string Machine { get; init; } = "";
    public string Time { get; init; } = "";
    public IBrush AvatarBg { get; init; } = Palette.OrangeSoft;
    public IBrush AvatarFg { get; init; } = Palette.Orange;
}

public sealed class EventRow
{
    public string Time { get; init; } = "";
    public string TypeLabel { get; init; } = "";
    public string Message { get; init; } = "";
    public IBrush BadgeBg { get; init; } = Palette.Panel2;
    public IBrush BadgeFg { get; init; } = Palette.Ink2;

    /// <summary>事件类型 → 中文标签 + 语义色（与服务端 events 的 type 对应）。</summary>
    public static EventRow From(string time, string type, string msg)
    {
        var (label, bg, fg) = type switch
        {
            "login_ok" or "client_login" => ("登录", Palette.GreenSoft, Palette.Green),
            "admin_login_ok" => ("管理", Palette.AccentSoft, Palette.Accent),
            "admin_login_fail" or "admin_locked" => ("安全", Palette.RedSoft, Palette.Red),
            "login_banned" or "admin_kick" => ("封禁", Palette.RedSoft, Palette.Red),
            "login_denied" or "login_pending" => ("拒绝", Palette.OrangeSoft, Palette.Orange),
            "logout" => ("断线", Palette.Panel2, Palette.Ink3),
            "admin_save_customer" => ("配置", Palette.AccentSoft, Palette.Accent),
            "port_occupied" or "bind_failed" => ("端口", Palette.RedSoft, Palette.Red),
            "auto_registered" or "port_adopted" => ("分配", Palette.GreenSoft, Palette.Green),
            "server_start" => ("启动", Palette.AccentSoft, Palette.Accent),
            _ => ("事件", Palette.Panel2, Palette.Ink2),
        };
        return new EventRow
        {
            Time = time.Length >= 19 ? time[11..19] : time,
            TypeLabel = label,
            Message = msg,
            BadgeBg = bg,
            BadgeFg = fg,
        };
    }
}

/// <summary>Designer Cut 调色板（与 XAML 资源保持一致）。</summary>
public static class Palette
{
    public static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#3B63F3"));
    public static readonly IBrush AccentSoft = new SolidColorBrush(Color.Parse("#EAEFFF"));
    public static readonly IBrush Green = new SolidColorBrush(Color.Parse("#17995A"));
    public static readonly IBrush GreenSoft = new SolidColorBrush(Color.Parse("#E4F5EC"));
    public static readonly IBrush Orange = new SolidColorBrush(Color.Parse("#D97C14"));
    public static readonly IBrush OrangeSoft = new SolidColorBrush(Color.Parse("#FDF1E0"));
    public static readonly IBrush Red = new SolidColorBrush(Color.Parse("#DE4B4B"));
    public static readonly IBrush RedSoft = new SolidColorBrush(Color.Parse("#FDEAEA"));
    public static readonly IBrush Ink = new SolidColorBrush(Color.Parse("#16181D"));
    public static readonly IBrush Ink2 = new SolidColorBrush(Color.Parse("#6A7280"));
    public static readonly IBrush Ink3 = new SolidColorBrush(Color.Parse("#9AA1AC"));
    public static readonly IBrush Panel2 = new SolidColorBrush(Color.Parse("#F6F7F9"));
}
