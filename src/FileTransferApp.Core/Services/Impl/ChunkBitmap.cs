namespace FileTransferApp.Core.Services.Impl;

/// <summary>
/// 断点续传切片 Bitmap。
/// 接收端维护已接收的切片索引集合；重新建立连接后回传该 Bitmap，
/// 发送端据此仅推送缺失切片，实现无损续传。
/// MarkReceived 是线程安全的，允许多请求通道并发写入（HTTP chunk endpoint）。
/// </summary>
public sealed class ChunkBitmap
{
    private readonly HashSet<int> _received = new();
    private readonly int _totalChunks;
    private readonly object _lock = new();

    public ChunkBitmap(int totalChunks)
    {
        _totalChunks = totalChunks;
    }

    public bool IsComplete
    {
        get
        {
            lock (_lock)
                return _totalChunks > 0 && _received.Count >= _totalChunks;
        }
    }

    /// <summary>标记某切片已接收（线程安全）</summary>
    public void MarkReceived(int index)
    {
        if (index < 0 || index >= _totalChunks) return;
        lock (_lock)
            _received.Add(index);
    }

    /// <summary>已接收切片索引（有序快照）</summary>
    public int[] Received
    {
        get
        {
            lock (_lock)
                return GetOrdered(_received);
        }
    }

    /// <summary>缺失切片索引（有序快照）</summary>
    public int[] Missing
    {
        get
        {
            lock (_lock)
            {
                var missing = new List<int>(_totalChunks - _received.Count);
                for (int i = 0; i < _totalChunks; i++)
                    if (!_received.Contains(i)) missing.Add(i);
                return missing.ToArray();
            }
        }
    }

    /// <summary>根据对端回传的已接收列表，计算本端仍需发送的缺失切片</summary>
    public static int[] ComputeMissing(int totalChunks, IEnumerable<int> peerReceived)
    {
        var recv = new HashSet<int>(peerReceived);
        var missing = new List<int>(Math.Max(0, totalChunks - recv.Count));
        for (int i = 0; i < totalChunks; i++)
            if (!recv.Contains(i)) missing.Add(i);
        return missing.ToArray();
    }

    private static int[] GetOrdered(HashSet<int> set)
    {
        var arr = new int[set.Count];
        set.CopyTo(arr);
        Array.Sort(arr);
        return arr;
    }
}
