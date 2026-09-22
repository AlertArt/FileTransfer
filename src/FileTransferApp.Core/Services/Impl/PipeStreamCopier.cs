using System.IO.Pipelines;
using FileTransferApp.Core.Protocols;

namespace FileTransferApp.Core.Services.Impl;

/// <summary>
/// System.IO.Pipelines 零拷贝内核（设计 5.4）。
/// 用 <see cref="Pipe"/> 把 source 流写入 dest 流，避免大缓冲区多次拷贝。
/// 从 <see cref="PipelinesTransferEngine"/> 抽出为独立、无状态的工具类，便于单独复用与测试。
/// </summary>
internal static class PipeStreamCopier
{
    /// <summary>使用 Pipe 将 source 流零拷贝写入 dest 文件流。</summary>
    public static async Task CopyAsync(Stream source, Stream dest, CancellationToken ct)
    {
        var pipe = new Pipe();
        Task writing = FillPipeAsync(source, pipe.Writer, ct);
        Task reading = ReadPipeAsync(dest, pipe.Reader, ct);
        await Task.WhenAll(writing, reading).ConfigureAwait(false);
    }

    private static async Task FillPipeAsync(Stream source, PipeWriter writer, CancellationToken ct)
    {
        const int minimumBufferSize = ProtocolConstants.ReceiveBufferSize; // 64KB
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var memory = writer.GetMemory(minimumBufferSize);
                var bytesRead = await source.ReadAsync(memory, ct).ConfigureAwait(false);
                if (bytesRead == 0) break;

                writer.Advance(bytesRead);
                var result = await writer.FlushAsync(ct).ConfigureAwait(false);
                if (result.IsCompleted) break;
            }
        }
        finally
        {
            await writer.CompleteAsync();
        }
    }

    private static async Task ReadPipeAsync(Stream dest, PipeReader reader, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await reader.ReadAsync(ct).ConfigureAwait(false);
                var buffer = result.Buffer;
                foreach (var segment in buffer)
                    await dest.WriteAsync(segment, ct).ConfigureAwait(false);
                reader.AdvanceTo(buffer.End);
                if (result.IsCompleted) break;
            }
            await dest.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            await reader.CompleteAsync();
        }
    }
}
