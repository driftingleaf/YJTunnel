using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace YJTunnel.Server.Pages;

[Authorize(Policy = "TunnelAdmin")]
public class IndexModel : PageModel
{
    private readonly AdminService _admin;

    public IndexModel(AdminService admin) => _admin = admin;

    public OverviewData Data { get; private set; } = new();

    public void OnGet() => Data = _admin.GetOverview();

    /// <summary>总览轮询 JSON 接口（5s）。</summary>
    public JsonResult OnGetStats() => new(_admin.GetOverview());
}
