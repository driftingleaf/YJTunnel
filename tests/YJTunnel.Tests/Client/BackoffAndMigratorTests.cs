using YJTunnel.Client;

namespace YJTunnel.Tests.Client;

public class BackoffTests
{
    [Fact]
    public void Sequence_1_2_4_8_16_30_Cap()
    {
        var backoff = new Backoff(new Random(1));
        // 无抖动时基准序列；抖动范围 [0.8, 1.2) 内验证
        var expected = new[] { 1, 2, 4, 8, 16, 30, 30, 30 };
        for (var i = 0; i < expected.Length; i++)
        {
            var delay = backoff.Next();
            Assert.InRange(delay.TotalSeconds, expected[i] * 0.8 - 0.001, expected[i] * 1.2);
        }
        Assert.Equal(8, backoff.Attempt);
    }

    [Fact]
    public void Reset_BackToFirstStep()
    {
        var backoff = new Backoff(new Random(2));
        for (var i = 0; i < 5; i++) backoff.Next();
        backoff.Reset();
        Assert.Equal(0, backoff.Attempt);
        var delay = backoff.Next();
        Assert.InRange(delay.TotalSeconds, 1 * 0.8 - 0.001, 1 * 1.2);
    }

    [Fact]
    public void Jitter_Bounded_AllSeeds()
    {
        for (var seed = 0; seed < 100; seed++)
        {
            var backoff = new Backoff(new Random(seed));
            var delay = backoff.Next(); // 第一步基准 1s
            Assert.InRange(delay.TotalSeconds, 0.8 - 0.001, 1.2);
        }
    }

    [Fact]
    public void CurrentStep_NeverBelow1()
    {
        var backoff = new Backoff(new Random(3));
        Assert.Equal(1, backoff.CurrentStepSeconds);
        backoff.Next();
        Assert.Equal(2, backoff.CurrentStepSeconds);
        backoff.Reset();
        Assert.Equal(1, backoff.CurrentStepSeconds);
    }
}

public class LegacyConfigMigratorTests
{
    private const string LegacyJson = @"{
  ""Server"": { ""ServerAddr"": ""182.254.172.138"", ""ServerPort"": 5001 },
  ""Token"": ""OLD_PLACEHOLDER_TOKEN"",
  ""Webs"": [ { ""Host"": ""ignored"" } ],
  ""Forwards"": [
    { ""LocalIp"": ""127.0.0.1"", ""LocalPort"": 1433, ""RemotePort"": 2001, ""Protocol"": 0 },
    { ""LocalIp"": ""192.168.1.5"", ""LocalPort"": 9001, ""RemotePort"": 3001 }
  ]
}";

    [Fact]
    public void Migrate_PortRewritten_5001To5003()
    {
        var config = LegacyConfigMigrator.FromLegacyJson(LegacyJson, "NEW-TOKEN");
        Assert.NotNull(config);
        Assert.Equal(5003, config.Server.ServerPort);
        Assert.Equal("182.254.172.138", config.Server.ServerAddr);
    }

    [Fact]
    public void Migrate_TokenTakesNewValue_NotLegacy()
    {
        // D15：旧值是全员相同的占位符，继承会导致更新后全员 LoginDenied
        var config = LegacyConfigMigrator.FromLegacyJson(LegacyJson, "NEW-TOKEN");
        Assert.NotNull(config);
        Assert.Equal("NEW-TOKEN", config.Token);
        Assert.NotEqual("OLD_PLACEHOLDER_TOKEN", config.Token);
    }

    [Fact]
    public void Migrate_ForwardsPreserved_AsExplicitRequest()
    {
        var config = LegacyConfigMigrator.FromLegacyJson(LegacyJson, "NEW-TOKEN");
        Assert.NotNull(config);
        Assert.Equal(2, config.Forwards.Count);
        Assert.Equal(1433, config.Forwards[0].LocalPort);
        Assert.Equal(2001, config.Forwards[0].RemotePort); // 存量客户原远端端口沿用（D14）
        Assert.Equal("192.168.1.5", config.Forwards[1].LocalIp); // 客户局域网地址保留
        Assert.Equal(3001, config.Forwards[1].RemotePort);
    }

    [Fact]
    public void Migrate_CustomPort_NotRewritten()
    {
        var custom = @"{ ""Server"": { ""ServerAddr"": ""1.2.3.4"", ""ServerPort"": 6001 }, ""Token"": ""x"", ""Forwards"": [] }";
        var config = LegacyConfigMigrator.FromLegacyJson(custom, "NEW-TOKEN");
        Assert.NotNull(config);
        Assert.Equal(6001, config.Server.ServerPort); // 现场自定义端口不受影响
    }

    [Fact]
    public void Migrate_EmptyLocalIp_Defaults_127()
    {
        var noIp = @"{ ""Server"": { ""ServerAddr"": ""1.2.3.4"", ""ServerPort"": 5001 }, ""Token"": ""x"",
                      ""Forwards"": [ { ""LocalPort"": 1433, ""RemotePort"": 2001 } ] }";
        var config = LegacyConfigMigrator.FromLegacyJson(noIp, "NEW-TOKEN");
        Assert.NotNull(config);
        Assert.Equal("127.0.0.1", config.Forwards[0].LocalIp);
    }

    [Fact]
    public void Migrate_InvalidJson_ReturnsNull()
    {
        Assert.Null(LegacyConfigMigrator.FromLegacyJson("not a json", "NEW-TOKEN"));
    }

    [Fact]
    public void Migrate_BackupPortRewritten()
    {
        var config = LegacyConfigMigrator.FromLegacyJson(LegacyJson, "NEW-TOKEN");
        Assert.NotNull(config);
        Assert.Null(config.BackupServer); // 旧主配置无 BackupServer 字段

        var withBackup = @"{ ""Server"": { ""ServerAddr"": ""1.2.3.4"", ""ServerPort"": 5001 },
            ""BackupServer"": { ""ServerAddr"": ""5.6.7.8"", ""ServerPort"": 5001 }, ""Token"": ""x"", ""Forwards"": [] }";
        var migrated = LegacyConfigMigrator.FromLegacyJson(withBackup, "NEW-TOKEN");
        Assert.NotNull(migrated);
        Assert.NotNull(migrated.BackupServer);
        Assert.Equal(5003, migrated.BackupServer.ServerPort);
    }
}
