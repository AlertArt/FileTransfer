using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using FileTransferApp.Core.Models;
using FileTransferApp.Core.Services.Impl;
using FileTransferApp.Core.Services.Interfaces;
using Xunit;

namespace FileTransferApp.Core.Tests;

/// <summary>
/// 同名文件残留任务清理逻辑验证：发送端在接收端未完成时重发同名文件（新 FileId），
/// 旧任务持有的 .tmp 写入流句柄应被自动释放，不会导致新 prepare IO_SharingViolation。
/// </summary>
public class StaleReceiveCleanupTests
{
    private sealed class FakeStorageService : IStorageService
    {
        public List<string> CancelledPaths { get; } = new();
        public bool OpenWriteThrowsSharingViolation { get; set; }

        public Task<(Stream Stream, string FinalPath)> OpenWriteStreamAsync(string fileName, long totalSize)
        {
            if (OpenWriteThrowsSharingViolation)
                throw new IOException("IO_SharingViolation_File, /fake/dir/" + fileName + ".tmp");

            var finalPath = Path.Combine("/fake/dir", fileName);
            return Task.FromResult<(Stream, string)>((new MemoryStream(), finalPath));
        }

        public Task CancelWriteAsync(string finalPath)
        {
            CancelledPaths.Add(finalPath);
            return Task.CompletedTask;
        }

        // 以下接口未在清理路径中使用，提供最小实现
        public Task<Stream> OpenReadStreamAsync(string fileIdentifier)
            => Task.FromResult<Stream>(new MemoryStream());
        public Task FinalizeWriteAsync(string finalPath) => Task.CompletedTask;
        public string GetDefaultReceiveDirectory() => "/fake/dir";
        public bool FileExists(string path) => false;
        public Task<string> ComputeSha256Async(string filePath) => Task.FromResult("aaa");
        public long GetFileSize(string filePath) => 0;
    }

    private sealed class FakeThumbnailService : IThumbnailService
    {
        public Task<byte[]?> GenerateThumbnailAsync(string filePath, int maxWidth = 128, int maxHeight = 128)
            => Task.FromResult<byte[]?>(null);
    }

    private sealed class FakeApprovalService : ITransferApprovalService
    {
        public bool Reject { get; set; }
        public Task<(bool Accepted, string? SavePath)> RequestApprovalAsync(FileMetadata metadata, string peerName)
            => Task.FromResult((!Reject, (string?)null));
    }

    private static PipelinesTransferEngine CreateEngine(FakeStorageService storage, bool reject = false)
    {
        var approval = new FakeApprovalService { Reject = reject };
        var messenger = WeakReferenceMessenger.Default;
        return new PipelinesTransferEngine(storage, new FakeThumbnailService(), approval, messenger);
    }

    private static PrepareRequest MakePrepare(string fileId, string fileName, long size = 1024)
        => new() { FileId = fileId, FileName = fileName, FileSize = size, ChunkSize = 1024 };

    private static DeviceNode MakePeer()
        => new() { DeviceId = "peer-1", DeviceName = "Peer", IpAddress = System.Net.IPAddress.Parse("192.168.1.2"), Port = 53318 };

    [Fact]
    public async Task SameNameNewFileId_CleansUpPreviousTask()
    {
        var storage = new FakeStorageService();
        var engine = CreateEngine(storage);

        // 注册第一个接收任务（等待审批/首个 chunk 到达前保持 WaitingApproval 状态）
        var prep1 = MakePrepare("file-A", "photo.bin");
        var resp1 = await engine.RegisterReceiveTaskAsync(prep1, MakePeer());
        Assert.True(resp1.Accepted);

        // 注册第二个：新 FileId，同名文件
        var prep2 = MakePrepare("file-B", "photo.bin");
        var resp2 = await engine.RegisterReceiveTaskAsync(prep2, MakePeer());
        Assert.True(resp2.Accepted);

        // 第一个任务应被清理
        var tasks = engine.GetTasks();
        Assert.Single(tasks); // 只剩第二个
        Assert.Equal("file-B", tasks[0].FileId);

        // CancelWriteAsync 应被调用一次（清理第一个的 .tmp）
        Assert.Single(storage.CancelledPaths);
        Assert.Contains("photo.bin", storage.CancelledPaths[0]);
    }

    [Fact]
    public async Task SameNameWithCompletedTask_DoesNotCancel()
    {
        var storage = new FakeStorageService();
        var engine = CreateEngine(storage);

        // 注册第一个接收任务
        var prep1 = MakePrepare("file-A", "doc.pdf");
        var resp1 = await engine.RegisterReceiveTaskAsync(prep1, MakePeer());
        Assert.True(resp1.Accepted);

        // 模拟第一个任务已终态完成：此时它不持有 .tmp 写入流
        var taskA = engine.GetTask("file-A")!;
        taskA.State = FileTransferApp.Core.Models.TransferState.Completed;
        taskA.EndedUtc = DateTime.UtcNow;

        // 注册第二个：新 FileId，同名文件
        var prep2 = MakePrepare("file-B", "doc.pdf");
        var resp2 = await engine.RegisterReceiveTaskAsync(prep2, MakePeer());
        Assert.True(resp2.Accepted);

        // 已完成的任务不应被清理：两个任务都应在列表
        var tasks = engine.GetTasks();
        Assert.Equal(2, tasks.Count);

        // CancelWriteAsync 不应被调用（终态任务不持有 .tmp）
        Assert.Empty(storage.CancelledPaths);
    }

    [Fact]
    public async Task DifferentNames_DoNotInterfere()
    {
        var storage = new FakeStorageService();
        var engine = CreateEngine(storage);

        var resp1 = await engine.RegisterReceiveTaskAsync(MakePrepare("file-A", "a.bin"), MakePeer());
        var resp2 = await engine.RegisterReceiveTaskAsync(MakePrepare("file-B", "b.bin"), MakePeer());
        Assert.True(resp1.Accepted);
        Assert.True(resp2.Accepted);

        var tasks = engine.GetTasks();
        Assert.Equal(2, tasks.Count);
        Assert.Empty(storage.CancelledPaths);
    }

    [Fact]
    public async Task RejectedApproval_DoesNotCleanupOld()
    {
        var storage = new FakeStorageService();
        var engine = CreateEngine(storage, reject: false); // 先同意

        await engine.RegisterReceiveTaskAsync(MakePrepare("file-A", "photo.bin"), MakePeer());

        // 再次创建引擎，但使用拒绝审批的 approval
        var storage2 = new FakeStorageService();
        var engine2 = CreateEngine(storage2, reject: true);
        var resp = await engine2.RegisterReceiveTaskAsync(MakePrepare("file-B", "photo.bin"), MakePeer());

        Assert.False(resp.Accepted);
        Assert.Empty(storage2.CancelledPaths);
    }
}
