using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.Json.Serialization;

namespace YJTunnel.Server;

/// <summary>
/// 客户档案中的单条映射分配记录。Key = localPort 字符串（映射稳定标识）。
/// </summary>
public sealed class MappingRecord
{
    public string Key { get; set; } = "";
    public int LocalPort { get; set; }
    public int RemotePort { get; set; }
    public string Note { get; set; } = "";
}

/// <summary>
/// 客户档案（customers.json 单条记录）。
/// </summary>
public sealed class CustomerRecord
{
    public string ClientId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Note { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>最近一次登录上报的机器名/版本（诊断用）。</summary>
    public string? MachineName { get; set; }
    public string? AppVersion { get; set; }
    public string? LastLoginAt { get; set; }

    /// <summary>
    /// 临时封禁截止时间（null=未封禁；到期后首次登录自动清除）。永久封禁用 Enabled=false 表达。
    /// </summary>
    public DateTimeOffset? BannedUntil { get; set; }

    public List<MappingRecord> Mappings { get; set; } = new();
}

/// <summary>待审核申请（pending.json 单条记录）。</summary>
public sealed class PendingRecord
{
    public string ClientId { get; set; } = "";
    public string? CompanyName { get; set; }
    public string? MachineName { get; set; }
    public string? AppVersion { get; set; }
    public string? FirstSeenAt { get; set; }
    public string? LastTryAt { get; set; }

    /// <summary>rejected=true 表示已被拒绝（客户端再登录不再入待审核列表，直接拒绝）。</summary>
    public bool Rejected { get; set; }

    public List<MappingRecord> Mappings { get; set; } = new();
}

/// <summary>
/// customers.json / pending.json 持久化存储。
/// ReaderWriterLockSlim 保护；写入 temp+rename 原子替换；每次启动将上一版备份为 .bak（§5.3）。
/// </summary>
public sealed class CustomerStore : IDisposable
{
    /// <summary>源生成类型元数据（AOT 安全）：按 T 分派。</summary>
    private static JsonTypeInfo<List<T>> GetListTypeInfo<T>() => typeof(T) == typeof(CustomerRecord)
        ? (JsonTypeInfo<List<T>>)(object)ServerJson.Store.ListCustomerRecord
        : (JsonTypeInfo<List<T>>)(object)ServerJson.Store.ListPendingRecord;

    private readonly ReaderWriterLockSlim _lock = new();
    private readonly string _customersPath;
    private readonly string _pendingPath;
    private List<CustomerRecord> _customers = new();
    private List<PendingRecord> _pending = new();

    public CustomerStore(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _customersPath = Path.Combine(dataDir, "customers.json");
        _pendingPath = Path.Combine(dataDir, "pending.json");
        Load();
    }

    public string CustomersPath => _customersPath;

    private void Load()
    {
        _customers = LoadList<CustomerRecord>(_customersPath);
        _pending = LoadList<PendingRecord>(_pendingPath);
    }

    private static List<T> LoadList<T>(string path)
    {
        if (!File.Exists(path))
            return new List<T>();
        try
        {
            var list = JsonSerializer.Deserialize(File.ReadAllText(path, Encoding.UTF8), GetListTypeInfo<T>());
            // 仅在本次成功解析后才把当前版备份为 .bak（防止把损坏内容覆盖掉唯一好备份）
            File.Copy(path, path + ".bak", overwrite: true);
            return list ?? new List<T>();
        }
        catch
        {
            // 当前文件损坏：尝试 .bak 恢复（.bak 保持上次成功版本），再不行从空开始
            try
            {
                var bak = path + ".bak";
                if (File.Exists(bak))
                    return JsonSerializer.Deserialize(File.ReadAllText(bak, Encoding.UTF8), GetListTypeInfo<T>()) ?? new List<T>();
            }
            catch { /* 彻底放弃，空表启动 */ }
            return new List<T>();
        }
    }

    private static void SaveList<T>(string path, List<T> list)
    {
        var json = JsonSerializer.Serialize(list, GetListTypeInfo<T>());
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json, Encoding.UTF8);
        if (File.Exists(path))
        {
#if NET8_0_OR_GREATER
            File.Move(tmp, path, overwrite: true);
#else
            File.Copy(tmp, path, overwrite: true);
            File.Delete(tmp);
#endif
        }
        else
        {
            File.Move(tmp, path);
        }
    }

    // ---------------- customers ----------------

    public IReadOnlyList<CustomerRecord> GetAllCustomers()
    {
        _lock.EnterReadLock();
        try { return _customers.ToList(); }
        finally { _lock.ExitReadLock(); }
    }

    public CustomerRecord? FindCustomer(string clientId)
    {
        _lock.EnterReadLock();
        try { return _customers.FirstOrDefault(c => c.ClientId == clientId); }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>查询端口已被分配给哪个客户（未分配返回 null）。</summary>
    public CustomerRecord? FindOwnerOfPort(int remotePort)
    {
        _lock.EnterReadLock();
        try { return _customers.FirstOrDefault(c => c.Mappings.Any(m => m.RemotePort == remotePort)); }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>新增或整体替换一个客户档案（管理台保存路径）。</summary>
    public void UpsertCustomer(CustomerRecord record)
    {
        _lock.EnterWriteLock();
        try
        {
            var idx = _customers.FindIndex(c => c.ClientId == record.ClientId);
            if (idx >= 0) _customers[idx] = record;
            else _customers.Add(record);
            SaveList(_customersPath, _customers);
        }
        finally { _lock.ExitWriteLock(); }
    }

    public bool DeleteCustomer(string clientId)
    {
        _lock.EnterWriteLock();
        try
        {
            var removed = _customers.RemoveAll(c => c.ClientId == clientId) > 0;
            if (removed) SaveList(_customersPath, _customers);
            return removed;
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>所有已分配远端端口集合（分配器可用性检查源 1）。</summary>
    public HashSet<int> AllAssignedPorts()
    {
        _lock.EnterReadLock();
        try { return _customers.SelectMany(c => c.Mappings).Select(m => m.RemotePort).ToHashSet(); }
        finally { _lock.ExitReadLock(); }
    }

    // ---------------- pending ----------------

    public IReadOnlyList<PendingRecord> GetAllPending()
    {
        _lock.EnterReadLock();
        try { return _pending.ToList(); }
        finally { _lock.ExitReadLock(); }
    }

    public PendingRecord? FindPending(string clientId)
    {
        _lock.EnterReadLock();
        try { return _pending.FirstOrDefault(p => p.ClientId == clientId); }
        finally { _lock.ExitReadLock(); }
    }

    public void UpsertPending(PendingRecord record)
    {
        _lock.EnterWriteLock();
        try
        {
            var idx = _pending.FindIndex(p => p.ClientId == record.ClientId);
            if (idx >= 0) _pending[idx] = record;
            else _pending.Add(record);
            SaveList(_pendingPath, _pending);
        }
        finally { _lock.ExitWriteLock(); }
    }

    public bool RemovePending(string clientId)
    {
        _lock.EnterWriteLock();
        try
        {
            var removed = _pending.RemoveAll(p => p.ClientId == clientId) > 0;
            if (removed) SaveList(_pendingPath, _pending);
            return removed;
        }
        finally { _lock.ExitWriteLock(); }
    }

    public void Dispose() => _lock.Dispose();
}
