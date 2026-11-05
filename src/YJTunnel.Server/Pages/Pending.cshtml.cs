using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YJTunnel.Server;

namespace YJTunnel.Server.Pages;

/// <summary>待审核：机器名/appVersion/首次上线时间/申请映射；通过=建档+分配；拒绝=标记。</summary>
[Authorize(Policy = "TunnelAdmin")]
public class PendingModel : PageModel
{
    private readonly AdminService _admin;

    public PendingModel(AdminService admin) => _admin = admin;

    public IReadOnlyList<PendingRecord> Pending { get; private set; } = Array.Empty<PendingRecord>();
    public string? Message { get; private set; }
    public bool IsError { get; private set; }

    // PRG（v1.16）：POST 结果经 TempData 传递后重定向，刷新页面不会重复提交（重复审批/拒绝）
    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool StatusIsError { get; set; }

    public void OnGet()
    {
        Message = StatusMessage;
        IsError = StatusIsError;
        Pending = _admin.GetPending();
    }

    public async Task<IActionResult> OnPostApproveAsync(string clientId)
    {
        var error = await _admin.ApprovePendingAsync(clientId, RemoteIp);
        StatusMessage = error ?? $"已通过 {clientId}，端口分配完成，客户端稍后自动上线";
        StatusIsError = error != null;
        return RedirectToPage();
    }

    public IActionResult OnPostReject(string clientId)
    {
        _admin.RejectPending(clientId, RemoteIp);
        StatusMessage = $"已拒绝 {clientId}";
        return RedirectToPage();
    }

    private string RemoteIp => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "-";
}
