using System.Text;
using System.Text.Json;

namespace YJTunnel.Server;

/// <summary>事件级别。</summary>
public enum TunnelEventLevel
{
    Info,
    Warn,
    Error,
}

/// <summary>
/// 事件日志（logs/events.jsonl，§5.4）。
/// 会话级事件全记；连接级失败每 IP 每分钟 ≤10 条；连接级成功不逐条记录，每 60s 按端口聚合一条件统计。
/// </summary>
public sealed class EventLog : IDisposable
{
    private readonly string _path;
    private readonly int _retentionDays;
    private readonly object _ioLock = new();

    // 每 IP 每分钟失败限流
    private readonly Dictionary<string, Queue<DateTime>> _failWindows = new();
    private const int MaxFailLinesPerMinute = 10;

    // 每 60s 端口聚合统计
    private readonly Dictionary<int, PortStats> _portStats = new();
    private readonly TimeSpan _statsFlushInterval = TimeSpan.FromSeconds(60);
    private DateTime _lastStatsFlush = DateTime.Now;
    private sealed class PortStats
    {
        public long Connections;
        public long BytesUp;
        public long BytesDown;
        public DateTime WindowStart = DateTime.Now;
    }

    private Timer? _retentionTimer;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public EventLog(string dataDir, int retentionDays = 90)
    {
        var dir = Path.Combine(dataDir, "logs");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "events.jsonl");
        _retentionDays = retentionDays;
        CleanupRetention();
        // 每天清理一次过期事件
        _retentionTimer = new Timer(_ => CleanupRetention(), null, TimeSpan.FromDays(1), TimeSpan.FromDays(1));
    }

    public string FilePath => _path;

    /// <summary>会话级事件：登录/注销/绑定结果/配置变更/管理操作/PortOccupied/心跳收割——全记。</summary>
    public void Session(TunnelEventLevel level, string type, string message,
        string? clientId = null, string? remoteIp = null, object? data = null)
        => Write(level, type, message, clientId, remoteIp, data);

    /// <summary>连接级失败：attach 超时、数据连接异常——每 IP 每分钟 ≤10 条。</summary>
    public void ConnectionFail(string type, string message, string? clientId = null, string? remoteIp = null)
    {
        var ip = remoteIp ?? "-";
        lock (_failWindows)
        {
            var cutoff = DateTime.Now.AddMinutes(-1);

            // 防御性修剪（v1.18）：字典随来源 IP 数量增长（每条很小，但攻击者可换 IP 洪水），超阈值时清理无近期失败的 IP
            if (_failWindows.Count > 4096)
            {
                var stale = _failWindows
                    .Where(kv => kv.Value.Count == 0 || kv.Value.Peek() < cutoff)
                    .Select(kv => kv.Key)
                    .ToList();
                foreach (var k in stale) _failWindows.Remove(k);
            }

            if (!_failWindows.TryGetValue(ip, out var q))
                _failWindows[ip] = q = new Queue<DateTime>();
            while (q.Count > 0 && q.Peek() < cutoff) q.Dequeue();
            if (q.Count >= MaxFailLinesPerMinute)
                return; // 限流丢弃
            q.Enqueue(DateTime.Now);
        }
        Write(TunnelEventLevel.Warn, type, message, clientId, remoteIp, null);
    }

    /// <summary>连接级成功：不逐条记录，计入端口聚合统计。</summary>
    public void CountConnection(int remotePort)
    {
        lock (_portStats)
        {
            FlushStatsIfNeeded();
            if (!_portStats.TryGetValue(remotePort, out var s))
                _portStats[remotePort] = s = new PortStats();
            s.Connections++;
        }
    }

    /// <summary>数据连接字节数计入端口聚合统计。</summary>
    public void CountBytes(int remotePort, long up, long down)
    {
        lock (_portStats)
        {
            if (!_portStats.TryGetValue(remotePort, out var s))
                _portStats[remotePort] = s = new PortStats();
            s.BytesUp += up;
            s.BytesDown += down;
        }
    }

    private void FlushStatsIfNeeded(bool force = false)
    {
        if (!force && DateTime.Now - _lastStatsFlush < _statsFlushInterval)
            return;
        _lastStatsFlush = DateTime.Now;
        foreach (var (port, s) in _portStats.ToList())
        {
            if (s.Connections == 0 && s.BytesUp == 0 && s.BytesDown == 0) continue;
            Write(TunnelEventLevel.Info, "port_stats",
                $"端口 {port} 近1分钟：连接 {s.Connections} 次，上行 {s.BytesUp} B，下行 {s.BytesDown} B",
                clientId: null, remoteIp: null,
                data: new { port, connections = s.Connections, bytesUp = s.BytesUp, bytesDown = s.BytesDown });
            s.Connections = 0;
            s.BytesUp = 0;
            s.BytesDown = 0;
            s.WindowStart = DateTime.Now;
        }
    }

    /// <summary>管理台查询事件（倒序，最多 limit 条）。</summary>
    public List<Dictionary<string, object?>> Query(string? clientIdFilter, string? typeFilter, int limit = 500)
    {
        var result = new List<Dictionary<string, object?>>();
        lock (_ioLock)
        {
            if (!File.Exists(_path))
                return result;
            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            string? line;
            var skipped = 0;
            while ((line = reader.ReadLine()) != null)
            {
                if (limit > 0 && result.Count >= limit)
                {
                    skipped++;
                    continue;
                }
                Dictionary<string, object?>? entry = null;
                try { entry = JsonSerializer.Deserialize<Dictionary<string, object?>>(line); }
                catch { continue; }
                if (entry == null) continue;
                if (!string.IsNullOrEmpty(clientIdFilter)
                    && !string.Equals(entry.TryGetValue("clientId", out var c) ? c?.ToString() : null, clientIdFilter, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.IsNullOrEmpty(typeFilter)
                    && !string.Equals(entry.TryGetValue("type", out var t) ? t?.ToString() : null, typeFilter, StringComparison.OrdinalIgnoreCase))
                    continue;
                result.Add(entry);
            }
            _ = skipped;
        }
        // 文件是追加序（时间正序），查询返回倒序（最新在前）
        result.Reverse();
        return result;
    }

    private void Write(TunnelEventLevel level, string type, string message, string? clientId, string? remoteIp, object? data)
    {
        var entry = new Dictionary<string, object?>
        {
            ["time"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
            ["level"] = level.ToString().ToLowerInvariant(),
            ["type"] = type,
            ["msg"] = message,
        };
        if (clientId != null) entry["clientId"] = clientId;
        if (remoteIp != null) entry["remoteIp"] = remoteIp;
        if (data != null) entry["data"] = data;
        try
        {
            // 中文消息不转义，保证 events.jsonl 可直接人工排查
            var line = JsonSerializer.Serialize(entry, JsonOptions);
            lock (_ioLock)
                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
            // 日志失败不能影响隧道本身
        }
    }

    /// <summary>按保留天数清理事件文件：只删"能解析出时间且已超期"的行；解析失败的行一律保留（绝不误删）。</summary>
    private void CleanupRetention()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var cutoff = DateTime.Now.AddDays(-_retentionDays);
            List<string> keep;
            lock (_ioLock)
            {
                var all = File.ReadAllLines(_path, Encoding.UTF8);
                keep = new List<string>(all.Length);
                foreach (var line in all)
                {
                    if (line.Length == 0) continue;
                    if (TryGetEntryTime(line, out var time) && time < cutoff)
                        continue; // 仅超期条目被清理
                    keep.Add(line);
                }
                if (keep.Count != all.Length)
                    File.WriteAllText(_path, string.Join(Environment.NewLine, keep) + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // 清理失败不影响服务，也绝不能因清理损坏日志
        }
    }

    /// <summary>从事件行 JSON 中解析 time 字段。解析失败返回 false（调用方应保留该行）。</summary>
    private static bool TryGetEntryTime(string line, out DateTime time)
    {
        time = default;
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("time", out var t)
                && DateTime.TryParse(t.GetString(), out time))
                return true;
        }
        catch
        {
            // 非 JSON 行（历史遗留/半行）按无法解析处理
        }
        return false;
    }

    public void Dispose()
    {
        // 停机时无条件冲刷未满窗口的聚合统计（否则短生命周期进程会丢最后一段统计）
        lock (_portStats) FlushStatsIfNeeded(force: true);
        _retentionTimer?.Dispose();
    }
}
