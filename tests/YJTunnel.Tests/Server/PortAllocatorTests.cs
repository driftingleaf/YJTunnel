using System.Net;
using System.Net.Sockets;
using YJTunnel.Server;

namespace YJTunnel.Tests.Server;

public class PortAllocatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yj-pa-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Allowed_WithinRange_AndNotDenied()
    {
        using var store = new CustomerStore(_dir);
        var options = new TunnelServerOptions
        {
            AllowPorts = new List<PortRange> { new(21000, 21009) },
            DenyPorts = new List<int> { 21003 },
        };
        var allocator = new PortAllocator(store, options, _ => false);

        Assert.True(allocator.IsAllowed(21000));
        Assert.True(allocator.IsAllowed(21009));
        Assert.False(allocator.IsAllowed(21003), "黑名单端口不可用");
        Assert.False(allocator.IsAllowed(20999), "段外不可用");
        Assert.False(allocator.IsAllowed(21010), "段外不可用");
    }

    [Fact]
    public void AllocateFree_SkipsAssignedRecords()
    {
        var store = new CustomerStore(_dir);
        store.UpsertCustomer(new CustomerRecord
        {
            ClientId = "a",
            Mappings =
            {
                new MappingRecord { Key = "1", RemotePort = 21000 },
                new MappingRecord { Key = "2", RemotePort = 21001 },
            },
        });
        var allocator = new PortAllocator(store, Options(), _ => false);

        var port = allocator.AllocateFree();
        Assert.Equal(21002, port); // 跳过已分配记录，取段内最靠前空闲
    }

    /// <summary>P2 验收项：模拟 FastTunnel（外部进程）占用端口时分配器能避开（三源检查源②）。</summary>
    [Fact]
    public void AllocateFree_SkipsForeignActiveListener()
    {
        var store = new CustomerStore(_dir);
        var allocator = new PortAllocator(store, Options(), _ => false);

        // 模拟 FastTunnel 占住段首端口
        var blocker = new TcpListener(IPAddress.Any, 21000);
        blocker.Start();
        try
        {
            var port = allocator.AllocateFree();
            Assert.NotNull(port);
            Assert.NotEqual(21000, port.Value);
            Assert.False(allocator.IsUsable(21000));
            Assert.True(allocator.IsUsable(21001));
        }
        finally
        {
            blocker.Stop();
        }
    }

    [Fact]
    public void SelfOwned_Port_TreatedAsTakeoverable_NotForeign()
    {
        var store = new CustomerStore(_dir);
        // 记录不持有 21000（模拟死会话残留的注册表条目）→ 自持回调=true 视为可接管
        var allocator = new PortAllocator(store, Options(), p => p == 21000);

        var blocker = new TcpListener(IPAddress.Any, 21000);
        blocker.Start();
        try
        {
            Assert.True(allocator.IsUsable(21000), "自持端口不算外部占用（走接管路径）");
            Assert.Equal(21000, allocator.AllocateFree()); // 自持端口可被重新分配给新会话
        }
        finally
        {
            blocker.Stop();
        }
    }

    [Fact]
    public void AllocateFree_Exhausted_ReturnsNull()
    {
        var store = new CustomerStore(_dir);
        for (var i = 0; i < 10; i++)
        {
            store.UpsertCustomer(new CustomerRecord
            {
                ClientId = $"c{i}",
                Mappings = { new MappingRecord { Key = "k", RemotePort = 21000 + i } },
            });
        }
        var allocator = new PortAllocator(store, Options(), _ => false);
        Assert.Null(allocator.AllocateFree());
    }

    private TunnelServerOptions Options() => new()
    {
        AllowPorts = new List<PortRange> { new(21000, 21009) },
        Tokens = new List<string> { "t" },
        DataDir = _dir,
    };

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
