using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace YJTunnel.Server.Pages;

/// <summary>操作日志：events.jsonl 时间线，按客户/类型筛选。</summary>
[Authorize(Policy = "TunnelAdmin")]
public class LogsModel : PageModel
{
    private readonly AdminService _admin;

    public LogsModel(AdminService admin) => _admin = admin;

    public List<Dictionary<string, object?>> Events { get; private set; } = new();
    public string? ClientIdFilter { get; private set; }
    public string? TypeFilter { get; private set; }

    public void OnGet(string? clientId, string? type)
    {
        ClientIdFilter = clientId;
        TypeFilter = type;
        Events = _admin.QueryEvents(clientId, type, 500);
    }
}
