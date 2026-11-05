using System.Text.Json;
using YJTunnel.Server;

namespace YJTunnel.Tests.Server;

public class CustomerStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yj-cs-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Upsert_Find_Delete_RoundTrip()
    {
        var store = new CustomerStore(_dir);
        var record = new CustomerRecord
        {
            ClientId = "client-a",
            Name = "客户A",
            Mappings = { new MappingRecord { Key = "1433", LocalPort = 1433, RemotePort = 2001 } },
        };
        store.UpsertCustomer(record);

        var found = store.FindCustomer("client-a");
        Assert.NotNull(found);
        Assert.Equal("客户A", found.Name);
        Assert.Equal(2001, found.Mappings[0].RemotePort);

        record.Name = "客户A-改名";
        store.UpsertCustomer(record);
        Assert.Equal("客户A-改名", store.FindCustomer("client-a").Name);
        Assert.Single(store.GetAllCustomers());

        Assert.True(store.DeleteCustomer("client-a"));
        Assert.Null(store.FindCustomer("client-a"));
    }

    [Fact]
    public void Persistence_AcrossInstances_And_BakBackup()
    {
        var (store1, path) = NewStore();
        store1.UpsertCustomer(new CustomerRecord { ClientId = "c1", Name = "一" });
        store1.Dispose();

        // 第二次打开：启动即备份 .bak，且数据可读
        var store2 = new CustomerStore(_dir);
        Assert.Equal("一", store2.FindCustomer("c1").Name);
        store2.Dispose();

        Assert.True(File.Exists(path + ".bak"), "启动应生成 customers.json.bak");
    }

    [Fact]
    public void CorruptedFile_FallsBackToBak()
    {
        var (store1, path) = NewStore();
        store1.UpsertCustomer(new CustomerRecord { ClientId = "c1", Name = "备份内容" });
        store1.Dispose();

        // 再次打开生成 .bak 后写坏主文件
        using (var s = new CustomerStore(_dir)) { }
        File.WriteAllText(path, "{{{ broken json");

        var store3 = new CustomerStore(_dir);
        var recovered = store3.FindCustomer("c1");
        Assert.NotNull(recovered);
        Assert.Equal("备份内容", recovered.Name);
    }

    [Fact]
    public void AtomicWrite_TempRename_NoLeftovers()
    {
        var (store, path) = NewStore();
        store.UpsertCustomer(new CustomerRecord { ClientId = "c1", Name = "原子写" });
        Assert.False(File.Exists(path + ".tmp"), "temp 文件应已 rename 清理");
        var json = File.ReadAllText(path);
        Assert.Contains("原子写", json);
    }

    [Fact]
    public void Pending_Upsert_Remove()
    {
        var store = new CustomerStore(_dir);
        store.UpsertPending(new PendingRecord { ClientId = "p1", MachineName = "PC-01", Rejected = false });
        Assert.NotNull(store.FindPending("p1"));
        Assert.Single(store.GetAllPending());

        store.UpsertPending(new PendingRecord { ClientId = "p2", Rejected = true });
        Assert.Equal(2, store.GetAllPending().Count);

        Assert.True(store.RemovePending("p1"));
        Assert.Null(store.FindPending("p1"));
    }

    private (CustomerStore store, string path) NewStore()
    {
        var store = new CustomerStore(_dir);
        return (store, Path.Combine(_dir, "customers.json"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}

public class EventLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yj-el-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void SessionEvents_AlwaysLogged()
    {
        var log = new EventLog(_dir);
        for (var i = 0; i < 100; i++)
            log.Session(TunnelEventLevel.Info, "login_ok", $"事件{i}", "cid");
        log.Dispose();
        var lines = File.ReadAllLines(log.FilePath);
        Assert.Equal(100, lines.Count(l => l.Length > 0));
    }

    [Fact]
    public void ConnectionFail_RateLimited_10PerIpPerMinute()
    {
        var log = new EventLog(_dir);
        // 1 万次短连接失败（§十三：事件日志 1 万次短连接不超限）
        for (var i = 0; i < 10_000; i++)
            log.ConnectionFail("attach_timeout", $"失败{i}", remoteIp: "1.2.3.4");
        // 不同 IP 不受牵连（各 10 条）
        for (var i = 0; i < 50; i++)
            log.ConnectionFail("attach_timeout", $"失败{i}", remoteIp: "5.6.7.8");
        log.Dispose();

        var lines = File.ReadAllLines(log.FilePath).Where(l => l.Length > 0).ToList();
        Assert.True(lines.Count <= 25, $"连接级失败被限流（实际 {lines.Count} 条）");
        Assert.True(lines.Count >= 20, $"限流前应有初始记录（实际 {lines.Count} 条）");
    }

    [Fact]
    public void PortStats_Aggregated_NotPerConnection()
    {
        var log = new EventLog(_dir);
        // 500 次"成功连接"不逐条记录，只计聚合
        for (var i = 0; i < 500; i++)
            log.CountConnection(2001);
        log.CountBytes(2001, 100, 200);
        log.Dispose();

        var lines = File.ReadAllLines(log.FilePath).Where(l => l.Length > 0).ToList();
        // Dispose 时 flush 一条聚合统计（连接数 500），绝无 500 条明细
        Assert.True(lines.Count <= 2, $"成功连接只聚合计数（实际 {lines.Count} 行）");
        Assert.Contains(lines, l => l.Contains("port_stats") && l.Contains("500"));
    }

    [Fact]
    public void Query_FilterByClientAndType()
    {
        var log = new EventLog(_dir);
        log.Session(TunnelEventLevel.Info, "login_ok", "A登录", "client-a");
        log.Session(TunnelEventLevel.Warn, "login_denied", "B被拒", "client-b");
        log.Session(TunnelEventLevel.Info, "login_ok", "B登录", "client-b");

        var byClient = log.Query("client-b", null);
        Assert.Equal(2, byClient.Count);
        var byType = log.Query(null, "login_denied");
        Assert.Single(byType);
        var all = log.Query(null, null);
        Assert.Equal(3, all.Count);
        log.Dispose();
    }

    /// <summary>回归：服务重启（新建 EventLog 实例）不得清空既有日志（启动清理曾因时间解析错位误删全文件）。</summary>
    [Fact]
    public void Restart_PreservesExistingLog()
    {
        var log1 = new EventLog(_dir);
        log1.Session(TunnelEventLevel.Info, "login_ok", "重启前的事件", "client-a");
        log1.Dispose();

        using var log2 = new EventLog(_dir); // 模拟重启：重新打开
        log2.Session(TunnelEventLevel.Info, "server_start", "重启后的事件");
        var all = log2.Query(null, null);
        Assert.Equal(2, all.Count);
        Assert.Contains(all, e => e["msg"]?.ToString() == "重启前的事件");
        Assert.Contains(all, e => e["msg"]?.ToString() == "重启后的事件");
    }

    /// <summary>回归：清理只删超期条目；无法解析的行必须保留。</summary>
    [Fact]
    public void Retention_RemovesOnlyExpired_KeepsUnparsable()
    {
        var log1 = new EventLog(_dir);
        log1.Session(TunnelEventLevel.Info, "login_ok", "近期事件", "client-a");
        log1.Dispose();

        var path = Path.Combine(_dir, "logs", "events.jsonl");
        // 手工追加一条超期条目 + 一条无法解析的坏行
        File.AppendAllText(path,
            "{\"time\":\"2020-01-01 00:00:00.000\",\"level\":\"info\",\"type\":\"old\",\"msg\":\"超期事件\"}\n" +
            "not-a-json-line\n");

        using var log2 = new EventLog(_dir, retentionDays: 1); // 启动触发清理
        var all = log2.Query(null, null);
        Assert.Contains(all, e => e["msg"]?.ToString() == "近期事件");
        Assert.DoesNotContain(all, e => e["msg"]?.ToString() == "超期事件");
        Assert.Contains(File.ReadAllLines(path), l => l == "not-a-json-line"); // 坏行不误删
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
