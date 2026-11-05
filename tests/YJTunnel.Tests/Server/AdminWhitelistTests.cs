using System.Net;
using YJTunnel;
using YJTunnel.Server;

namespace YJTunnel.Tests.Server;

/// <summary>
/// 管理台来源白名单判定（计划 §六 / D8）：
/// 回环恒放行、精确 IP 匹配、IPv4-mapped IPv6 归一化、"*" 通配放行所有来源。
/// </summary>
public class AdminWhitelistTests
{
    private static AdminService Create(params string[] allowed)
        => new(null!, new AdminOptions { AllowedIps = allowed.ToList() });

    // ---- 空=仅回环 ----

    [Fact]
    public void 空白名单_仅回环放行()
    {
        var svc = Create();
        Assert.True(svc.IsIpAllowed(IPAddress.Loopback));
        Assert.False(svc.IsIpAllowed(IPAddress.Parse("8.8.8.8")));
        Assert.False(svc.IsIpAllowed(null));
    }

    // ---- 精确匹配 ----

    [Fact]
    public void 精确IP_命中放行_未命中拒绝()
    {
        var svc = Create("8.8.8.8");
        Assert.True(svc.IsIpAllowed(IPAddress.Parse("8.8.8.8")));
        Assert.False(svc.IsIpAllowed(IPAddress.Parse("9.9.9.9")));
        Assert.True(svc.IsIpAllowed(IPAddress.Loopback)); // 回环恒放行
    }

    [Fact]
    public void 条目两侧空白_大小写不敏感_仍命中()
    {
        var svc = Create(" 8.8.8.8 ");
        Assert.True(svc.IsIpAllowed(IPAddress.Parse("8.8.8.8")));
    }

    // ---- IPv4-mapped IPv6 归一化（双栈监听远端呈 ::ffff:a.b.c.d，旧实现静默失灵）----

    [Fact]
    public void IPv4映射地址_归一化后命中白名单()
    {
        var svc = Create("8.8.8.8");
        var mapped = IPAddress.Parse("::ffff:8.8.8.8");
        Assert.True(mapped.IsIPv4MappedToIPv6);
        Assert.True(svc.IsIpAllowed(mapped));
    }

    [Fact]
    public void IPv4映射回环_放行()
    {
        var svc = Create();
        Assert.True(svc.IsIpAllowed(IPAddress.Parse("::ffff:127.0.0.1")));
    }

    // ---- "*" 通配 ----

    [Fact]
    public void 通配星号_放行任意来源()
    {
        var svc = Create("*");
        Assert.True(svc.IsIpAllowed(IPAddress.Parse("8.8.8.8")));
        Assert.True(svc.IsIpAllowed(IPAddress.Parse("2001:db8::1")));
        Assert.True(svc.IsIpAllowed(IPAddress.Loopback));
    }

    [Fact]
    public void 通配星号_条目带空白_仍生效()
    {
        var svc = Create(" * ");
        Assert.True(svc.IsIpAllowed(IPAddress.Parse("1.2.3.4")));
    }

    [Fact]
    public void 非通配配置_星号不误伤精确匹配()
    {
        var svc = Create("8.8.8.8", "::1");
        Assert.True(svc.IsIpAllowed(IPAddress.Parse("8.8.8.8")));
        Assert.False(svc.IsIpAllowed(IPAddress.Parse("1.2.3.4")));
    }
}
