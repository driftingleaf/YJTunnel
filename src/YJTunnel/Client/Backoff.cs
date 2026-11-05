namespace YJTunnel.Client;

/// <summary>
/// 重连退避（D5）：1→2→4→8→16→30s 上限 + 抖动（±20%）。连接存活超过 60s 调用 Reset 重置。
/// </summary>
public sealed class Backoff
{
    private static readonly int[] Steps = { 1, 2, 4, 8, 16, 30 };

    private readonly Random _jitter;
    private int _index;

    public Backoff() : this(new Random()) { }

    /// <summary>测试注入确定性随机源。</summary>
    public Backoff(Random jitterSource)
    {
        _jitter = jitterSource;
    }

    /// <summary>已连续失败次数（从 1 开始计）。</summary>
    public int Attempt { get; private set; }

    /// <summary>取下一次退避延迟并步进：基准秒数 × [0.8, 1.2) 抖动。</summary>
    public TimeSpan Next()
    {
        var baseSeconds = Steps[Math.Min(_index, Steps.Length - 1)];
        if (_index < Steps.Length - 1) _index++;
        Attempt++;
        var seconds = baseSeconds * (0.8 + _jitter.NextDouble() * 0.4);
        return TimeSpan.FromSeconds(Math.Max(0.1, seconds));
    }

    /// <summary>会话稳定运行后重置到第一步。</summary>
    public void Reset()
    {
        _index = 0;
        Attempt = 0;
    }

    /// <summary>当前基准步长（不含抖动，诊断/测试用）。</summary>
    public int CurrentStepSeconds => Steps[Math.Min(_index, Steps.Length - 1)];
}
