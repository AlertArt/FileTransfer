using FileTransferApp.Core.Services.Impl;
using FileTransferApp.Core.Services.Interfaces;
using Xunit;

namespace FileTransferApp.Core.Tests;

/// <summary>
/// <see cref="NullPlatformKeepAliveService"/> no-op 契约单元测试。
/// 验证其作为桌面端默认占位实现不会抛异常、单例语义、多次调用安全。
/// </summary>
public class NullPlatformKeepAliveServiceTests
{
    [Fact]
    public void Instance_Is_Singleton()
    {
        Assert.Same(NullPlatformKeepAliveService.Instance, NullPlatformKeepAliveService.Instance);
    }

    [Fact]
    public void Implements_IPlatformKeepAliveService()
    {
        IPlatformKeepAliveService svc = NullPlatformKeepAliveService.Instance;
        Assert.NotNull(svc);
    }

    [Fact]
    public void StartKeepAlive_Does_Not_Throw()
    {
        var svc = NullPlatformKeepAliveService.Instance;
        var ex = Record.Exception(() => svc.StartKeepAlive("title", "content"));
        Assert.Null(ex);
    }

    [Fact]
    public void StopKeepAlive_Does_Not_Throw()
    {
        var svc = NullPlatformKeepAliveService.Instance;
        var ex = Record.Exception(svc.StopKeepAlive);
        Assert.Null(ex);
    }

    [Fact]
    public void Repeated_Calls_Do_Not_Throw()
    {
        var svc = NullPlatformKeepAliveService.Instance;

        for (int i = 0; i < 10; i++)
        {
            svc.StartKeepAlive($"title-{i}", $"content-{i}");
            svc.StopKeepAlive();
        }
    }

    [Fact]
    public void Start_And_Stop_Are_Symmetric_No_State_Coupling()
    {
        // 桌面端不需要保活，仅验证 null pattern：调用顺序无关紧要
        var svc = NullPlatformKeepAliveService.Instance;

        svc.StopKeepAlive();        // 先 Stop 再 Start 不抛
        svc.StartKeepAlive("", "");
        svc.StopKeepAlive();
        svc.StopKeepAlive();        // 重复 Stop 不抛
    }

    [Fact]
    public void Null_And_Empty_Arguments_Do_Not_Throw()
    {
        var svc = NullPlatformKeepAliveService.Instance;

        svc.StartKeepAlive(null!, null!);
        svc.StartKeepAlive(string.Empty, string.Empty);
        svc.StartKeepAlive("   ", "   ");
    }
}
