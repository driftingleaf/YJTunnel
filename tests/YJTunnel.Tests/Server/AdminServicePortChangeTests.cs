using YJTunnel;
using YJTunnel.Server;
using YJTunnel.Server;
using TunnelHostServer = YJTunnel.Server.TunnelServer;

namespace YJTunnel.Tests.Server;

/// <summary>
/// 改端口前置校验（v1.14，审核补充）：白名单/黑名单、跨客户唯一性、同客户映射唯一性、本机占用——
/// 把"保存成功但绑定失败（被占）"的事后状态提前为明确报错。
/// </summary>
public class AdminServicePortChangeTests
{
    private static (TestTunnelServer Wrapper, AdminService Admin, TunnelHostServer Host) Create()
    {
        var wrapper = TestTunnelServer.Start(o =>
        {
            o.DenyPorts = new List<int> { 22500 };
            o.AllowPorts = new List<PortRange> { new(22000, 22999) };
        });
        return (wrapper, new AdminService(wrapper.Server, new AdminOptions()), wrapper.Server);
    }

    private static CustomerRecord AddCustomer(TunnelHostServer server, string clientId, string name, params int[] remotePorts)
    {
        var record = new CustomerRecord
        {
            ClientId = clientId,
            Name = name,
            Mappings = remotePorts.Select((p, i) => new MappingRecord
            { Key = (20000 + i).ToString(), LocalPort = 10000 + i, RemotePort = p }).ToList(),
        };
        server.Customers.UpsertCustomer(record);
        return record;
    }

    [Fact]
    public void 改到其他客户的端口_拒绝并指明持有者()
    {
        var (wrapper, admin, host) = Create();
        try
        {
            AddCustomer(host, "c1", "甲公司", 22001);
            AddCustomer(host, "c2", "乙公司", 22002);
            var c1 = host.Customers.FindCustomer("c1")!;

            var error = admin.ValidatePortChange(c1, "20000", 22002);
            Assert.NotNull(error);
            Assert.Contains("乙公司", error);
        }
        finally { wrapper.Dispose(); }
    }

    [Fact]
    public void 改到白名单内空闲端口_通过()
    {
        var (wrapper, admin, host) = Create();
        try
        {
            var c1 = AddCustomer(host, "c1", "甲公司", 22001);
            Assert.Null(admin.ValidatePortChange(c1, "20000", 22005));
        }
        finally { wrapper.Dispose(); }
    }

    [Fact]
    public void 改到黑名单端口_拒绝()
    {
        var (wrapper, admin, host) = Create();
        try
        {
            var c1 = AddCustomer(host, "c1", "甲公司", 22001);
            var error = admin.ValidatePortChange(c1, "20000", 22500);
            Assert.NotNull(error);
            Assert.Contains("黑名单", error);
        }
        finally { wrapper.Dispose(); }
    }

    [Fact]
    public void 改到同客户另一映射的端口_拒绝()
    {
        var (wrapper, admin, host) = Create();
        try
        {
            var c1 = AddCustomer(host, "c1", "甲公司", 22001, 22002);
            var error = admin.ValidatePortChange(c1, "20000", 22002);
            Assert.NotNull(error);
            Assert.Contains("另一映射", error);
        }
        finally { wrapper.Dispose(); }
    }

    [Fact]
    public void 改到被本机其他程序监听的端口_拒绝()
    {
        var (wrapper, admin, host) = Create();
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 22999);
        try
        {
            listener.Start();
            var c1 = AddCustomer(host, "c1", "甲公司", 22001);
            var error = admin.ValidatePortChange(c1, "20000", 22999);
            Assert.NotNull(error);
            Assert.Contains("占用", error);
        }
        finally
        {
            listener.Stop();
            wrapper.Dispose();
        }
    }

    [Fact]
    public void 改到自己当前端口_不视为占用_由调用方拦截新旧相同()
    {
        var (wrapper, admin, host) = Create();
        try
        {
            var c1 = AddCustomer(host, "c1", "甲公司", 22001);
            Assert.Null(admin.ValidatePortChange(c1, "20000", 22001));
        }
        finally { wrapper.Dispose(); }
    }
}
