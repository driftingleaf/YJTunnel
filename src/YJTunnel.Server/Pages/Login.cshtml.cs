using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace YJTunnel.Server.Pages;

/// <summary>登录页：配置文件账号密码；5 次失败锁 15 分钟；Cookie 8h。</summary>
public class LoginModel : PageModel
{
    private readonly AdminService _admin;
    private readonly ILogger<LoginModel> _logger;

    public LoginModel(AdminService admin, ILogger<LoginModel> logger)
    {
        _admin = admin;
        _logger = logger;
    }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public string? Error { get; private set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(string username, string password)
    {
        var remote = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "-";
        var error = _admin.Login(username ?? "", password ?? "", remote);
        if (error != null)
        {
            Error = error;
            return Page();
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, username ?? "admin"),
            new("role", "admin"),
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity));
        _logger.LogInformation("管理台登录成功：{User} from {Ip}", username, remote);

        return LocalRedirect(string.IsNullOrEmpty(ReturnUrl) ? "/Index" : ReturnUrl);
    }

    public async Task<IActionResult> OnPostLogoutAsync()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToPage("/Login");
    }
}
