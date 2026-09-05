namespace FileTransferApp.Core.Services.Interfaces;

/// <summary>
/// HTTP/TCP 传输服务接口（基于 System.IO.Pipelines 的轻量 HTTP/1.1 服务端）。
/// 监听 53318 端口，响应 /prepare、/chunk、/control 三个 RESTful 接口。
/// </summary>
public interface ITransferServer
{
    bool IsRunning { get; }

    Task StartAsync(System.Threading.CancellationToken ct = default);
    Task StopAsync();
}
