using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YJTunnel;
using YJTunnel.Server;
using TunnelHost = YJTunnel.Server.TunnelServer;

namespace YJTunnel.Server.Pages;

/// <summary>
/// 端口分配表：端口↔客户↔本地端口↔状态；CSV 导出；修改已分配端口。
/// PRG 模式（v1.16）：POST 结果经 TempData 传递后重定向——修复"刷新页面=重新提交表单"
/// 导致反复踢客户端、在线/离线抖动的问题。
/// </summary>
[Authorize(Policy = "TunnelAdmin")]
public class PortsModel : PageModel
{
    private readonly AdminService _admin;
    private readonly TunnelHost _tunnel;

    public PortsModel(AdminService admin, TunnelHost tunnel)
    {
        _admin = admin;
        _tunnel = tunnel;
    }

    public List<PortUsageRow> Ports { get; private set; } = new();
    public string? Message { get; private set; }
    public bool IsError { get; private set; }

    /// <summary>改端口保存成功后待确认绑定的目标（clientId+mappingKey），页面据此轮询自动刷新（v1.15）。</summary>
    public string? PendingBindClient { get; private set; }
    public string? PendingBindKey { get; private set; }

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool StatusIsError { get; set; }
    [TempData] public string? BindPollClientId { get; set; }
    [TempData] public string? BindPollKey { get; set; }

    public void OnGet()
    {
        Message = StatusMessage;
        IsError = StatusIsError;
        PendingBindClient = BindPollClientId;
        PendingBindKey = BindPollKey;
        Ports = _admin.GetOverview().Ports;
    }

    /// <summary>修改已分配端口：前置校验（v1.14，占用/唯一性/白名单）→ 保存 → 对在线客户端推 ConfigChanged（&lt;10s 生效）。</summary>
    public async Task<IActionResult> OnPostChangePortAsync(string clientId, string mappingKey, int newPort)
    {
        var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "-";
        var record = _admin.GetCustomer(clientId);
        if (record == null)
        {
            StatusMessage = "客户不存在";
            StatusIsError = true;
            return RedirectToPage();
        }

        var mapping = record.Mappings.FirstOrDefault(m => m.Key == mappingKey);
        if (mapping == null)
        {
            StatusMessage = "映射不存在（客户端可能已更换本地端口）";
            StatusIsError = true;
            return RedirectToPage();
        }

        // v1.14：保存前前置校验——"保存成功但绑定失败（被占）"的事后状态，提前为明确报错
        var validationError = _admin.ValidatePortChange(record, mappingKey, newPort);
        if (validationError != null)
        {
            StatusMessage = validationError;
            StatusIsError = true;
            return RedirectToPage();
        }

        var oldPort = mapping.RemotePort;
        mapping.RemotePort = newPort;
        var saveError = await _admin.SaveCustomerAsync(record, remoteIp);
        if (saveError != null)
        {
            mapping.RemotePort = oldPort; // 回滚内存态（保存失败未持久化）
            StatusMessage = saveError;
            StatusIsError = true;
            return RedirectToPage();
        }

        StatusMessage = $"端口 {oldPort} → {newPort} 已保存，等待客户端重连绑定（页面将自动刷新）";
        BindPollClientId = clientId;
        BindPollKey = mappingKey;
        return RedirectToPage();
    }
}
