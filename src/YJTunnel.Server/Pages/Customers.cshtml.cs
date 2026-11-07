using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YJTunnel.Server;

namespace YJTunnel.Server.Pages;

/// <summary>客户管理：列表（在线/离线/禁用标记）、新增、编辑名称备注、禁用/启用、删除、踢下线。</summary>
[Authorize(Policy = "TunnelAdmin")]
public class CustomersModel : PageModel
{
    private readonly AdminService _admin;

    public CustomersModel(AdminService admin) => _admin = admin;

    public IReadOnlyList<CustomerRecord> Customers { get; private set; } = Array.Empty<CustomerRecord>();
    public HashSet<string> OnlineIds { get; private set; } = new();
    public CustomerRecord? Editing { get; private set; }
    public string? Message { get; private set; }
    public bool IsError { get; private set; }

    // PRG（v1.16）：POST 结果经 TempData 传递后重定向，刷新页面不会重复提交（反复保存/踢客户端）
    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool StatusIsError { get; set; }

    public void OnGet(string? edit)
    {
        Message = StatusMessage;
        IsError = StatusIsError;
        Load();
        if (edit != null)
            Editing = Customers.FirstOrDefault(c => c.ClientId == edit);
    }

    private void Load()
    {
        Customers = _admin.GetCustomers();
        OnlineIds = _admin.GetOverview().Sessions.Select(s => s.ClientId).ToHashSet();
    }

    public async Task<IActionResult> OnPostSaveAsync(string clientId, string name, string note, bool enabled, bool isNew)
    {
        var record = _admin.GetCustomer(clientId);
        if (record == null)
        {
            if (!isNew)
                return NotFound();
            record = new CustomerRecord { ClientId = clientId, Mappings = new List<MappingRecord>() };
        }
        record.Name = name?.Trim() ?? "";
        record.Note = note?.Trim() ?? "";
        record.Enabled = enabled;

        var error = await _admin.SaveCustomerAsync(record, RemoteIp, notifyClient: !isNew);
        StatusMessage = error ?? $"客户「{record.Name}」已保存";
        StatusIsError = error != null;
        return RedirectToPage();
    }

    /// <summary>踢下线（v1.19）：可选临时封禁（分钟）或永久封禁（禁用客户）。仅断开则不传封禁参数。</summary>
    public async Task<IActionResult> OnPostKickAsync(string clientId, string banMode, int banMinutes)
    {
        int? minutes = null;
        var permanent = false;
        switch (banMode)
        {
            case "temp":
                if (banMinutes <= 0) { StatusMessage = "封禁时长无效"; StatusIsError = true; return RedirectToPage(); }
                minutes = banMinutes;
                break;
            case "permanent":
                permanent = true;
                break;
        }

        await _admin.KickAsync(clientId, RemoteIp, banMinutes: minutes, banPermanent: permanent);
        StatusMessage = permanent ? "已永久封禁（禁用客户），客户端登录将被拒绝"
            : minutes != null ? $"已踢下线并封禁 {minutes} 分钟，到期后客户端自动恢复"
            : "已踢下线，客户端将自动重连";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(string clientId)
    {
        var ok = await _admin.DeleteCustomerAsync(clientId, RemoteIp);
        StatusMessage = ok ? "客户已删除" : "客户不存在";
        StatusIsError = !ok;
        return RedirectToPage();
    }

    private string RemoteIp => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "-";

    /// <summary>按字符串稳定取头像渐变配色（客户头像用，跨页面共用）。</summary>
    public static string AvatarColor(string seed)
    {
        string[] palettes =
        {
            "linear-gradient(135deg,#3b82f6,#2563eb)", "linear-gradient(135deg,#10b981,#059669)",
            "linear-gradient(135deg,#f59e0b,#d97706)", "linear-gradient(135deg,#8b5cf6,#7c3aed)",
            "linear-gradient(135deg,#ec4899,#db2777)", "linear-gradient(135deg,#06b6d4,#0891b2)",
        };
        var n = 0;
        foreach (var ch in seed ?? "") n = n * 31 + ch;
        return palettes[Math.Abs(n) % palettes.Length];
    }
}
