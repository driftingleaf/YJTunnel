using System.Net.NetworkInformation;

namespace YJTunnel.Server;

/// <summary>
/// 端口分配器（§5.2）。候选端口可用性检查三源合一：
/// ① customers.json 分配记录；② 本机活动 TCP 监听枚举（跨进程，感知 FastTunnel 及任何第三方进程占位）；
/// ③ 黑白名单。自身注册表已持有（可接管）的端口不视为"被外部占用"。
/// </summary>
public sealed class PortAllocator
{
    private readonly CustomerStore _store;
    private readonly TunnelServerOptions _options;

    /// <summary>判断端口是否被本服务自己的监听注册表持有（可走接管路径，不算外部占用）。</summary>
    private readonly Func<int, bool> _isSelfOwned;

    public PortAllocator(CustomerStore store, TunnelServerOptions options, Func<int, bool> isSelfOwned)
    {
        _store = store;
        _options = options;
        _isSelfOwned = isSelfOwned;
    }

    /// <summary>端口在白名单段内且不在黑名单。</summary>
    public bool IsAllowed(int port)
        => _options.AllowPorts.Any(r => r.Contains(port)) && !_options.DenyPorts.Contains(port);

    /// <summary>端口未被 customers.json 分配，且本机无外部进程监听（自持端口除外）。</summary>
    public bool IsFree(int port)
    {
        if (_store.AllAssignedPorts().Contains(port))
            return false;
        if (_isSelfOwned(port))
            return true; // 自持端口走接管，不算占用
        return !GetActiveListenPorts().Contains(port);
    }

    /// <summary>端口可用于分配（三源全过）。</summary>
    public bool IsUsable(int port) => IsAllowed(port) && IsFree(port);

    /// <summary>
    /// 从白名单段内自动分配最靠前的可用端口；无可用返回 null。
    /// 调用方负责把分配结果写入 customers.json（分配即入库）。
    /// </summary>
    public int? AllocateFree()
    {
        // 每次分配快照一次监听端口集合（避免段内数千次枚举）
        var active = GetActiveListenPorts();
        var assigned = _store.AllAssignedPorts();
        foreach (var range in _options.AllowPorts.OrderBy(r => r.Start))
        {
            for (var port = range.Start; port <= range.End; port++)
            {
                if (_options.DenyPorts.Contains(port)) continue;
                if (assigned.Contains(port)) continue;
                if (_isSelfOwned(port)) return port; // 自持（死会话残留）可接管
                if (active.Contains(port)) continue;
                return port;
            }
        }
        return null;
    }

    /// <summary>本机活动 TCP 监听端口集合（跨进程）。</summary>
    public static HashSet<int> GetActiveListenPorts()
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Select(ep => ep.Port)
                .ToHashSet();
        }
        catch
        {
            // 枚举失败（极老系统）时仅依赖记录源与黑白名单
            return new HashSet<int>();
        }
    }
}
