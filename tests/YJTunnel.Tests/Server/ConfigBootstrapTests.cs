using YJTunnel.Server;

namespace YJTunnel.Tests.Server;

/// <summary>
/// 首次部署配置自愈（v1.9）：程序目录缺配置时自动从 BaseDirectory\configs 模板生成、幂等不覆盖。
/// csproj 已配置 configs\tunnelserver.json 随发布拷贝，故模板在测试 BaseDirectory 下恒存在（与生产一致）。
/// </summary>
public class ConfigBootstrapTests
{
    private static string TemplatePath
        => Path.Combine(AppContext.BaseDirectory, "configs", "tunnelserver.json");

    [Fact]
    public void 模板随构建拷贝至BaseDirectory()
    {
        Assert.True(File.Exists(TemplatePath), "csproj 应把 configs/tunnelserver.json 拷贝到输出目录");
    }

    [Fact]
    public void 缺配置_自动从模板生成()
    {
        var dir = Path.Combine(Path.GetTempPath(), "yj-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "tunnelserver.json");

        Assert.False(File.Exists(target));
        Assert.True(TunnelServerHostConfig.EnsureConfigFile(target));
        Assert.True(File.Exists(target));

        Directory.Delete(dir, true);
    }

    [Fact]
    public void 已有配置_幂等不覆盖()
    {
        var dir = Path.Combine(Path.GetTempPath(), "yj-cfg2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "tunnelserver.json");
        File.WriteAllText(target, "{}"); // 模拟现场已改过 Token/白名单的配置

        Assert.True(TunnelServerHostConfig.EnsureConfigFile(target));
        Assert.Equal("{}", File.ReadAllText(target)); // 不被模板覆盖

        Directory.Delete(dir, true);
    }
}
