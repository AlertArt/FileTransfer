namespace FileTransferApp.Core.Services.Impl;

/// <summary>
/// 简单令牌桶限速器：限制单位时间可发送的字节数。
/// <see cref="LimitBytesPerSecond"/> &lt;= 0 表示不限速（<see cref="AcquireAsync"/> 立即返回）。
/// 桶容量为 1 秒的额度（允许 1 秒的突发）。
/// </summary>
public sealed class BandwidthThrottle
{
    private readonly object _gate = new();
    private long _limit;          // 字节/秒
    private double _tokens;       // 当前令牌
    private long _lastTickMs;     // 上次补充时间

    public BandwidthThrottle(long limitBytesPerSecond = 0) => _limit = Math.Max(0, limitBytesPerSecond);

    /// <summary>每秒字节上限；&lt;=0 表示不限速。运行期可改（重置令牌桶）。</summary>
    public long LimitBytesPerSecond
    {
        get { lock (_gate) return _limit; }
        set { lock (_gate) { _limit = Math.Max(0, value); _tokens = 0; _lastTickMs = 0; } }
    }

    /// <summary>发送 <paramref name="bytes"/> 字节前调用；必要时等待以维持速率。</summary>
    public async Task AcquireAsync(int bytes, CancellationToken ct = default)
    {
        if (bytes <= 0) return;

        while (true)
        {
            TimeSpan wait;
            lock (_gate)
            {
                if (_limit <= 0) return; // 不限速

                var now = Environment.TickCount64;
                if (_lastTickMs == 0) _lastTickMs = now;

                // 按经过时间补充令牌，上限 = 1 秒额度
                _tokens = Math.Min(_limit, _tokens + (now - _lastTickMs) * (_limit / 1000.0));
                _lastTickMs = now;

                if (_tokens >= bytes)
                {
                    _tokens -= bytes;
                    return;
                }

                var need = bytes - _tokens;
                wait = TimeSpan.FromMilliseconds(Math.Max(1, need / (_limit / 1000.0)));
            }

            await Task.Delay(wait, ct).ConfigureAwait(false);
        }
    }
}
