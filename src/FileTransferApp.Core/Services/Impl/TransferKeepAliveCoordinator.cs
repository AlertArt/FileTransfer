using FileTransferApp.Core.Models;
using FileTransferApp.Core.Services.Interfaces;

namespace FileTransferApp.Core.Services.Impl;

/// <summary>
/// 传输任务保活协调器：跟踪所有任务的状态，在首个活动任务出现时启动保活，
/// 在最后一个活动任务终止时停止保活。幂等去重，避免重复调用平台 API。
///
/// 抽取自 MainViewModel，修复原 EnsureKeepAliveStarted 中"先加任务再查活动数"
/// 导致接收端首个任务保活不触发的 bug：本协调器自维护 fileId→state 映射，
/// 不依赖外部 Transfers 列表的实时性。
/// </summary>
public sealed class TransferKeepAliveCoordinator
{
    private readonly IPlatformKeepAliveService _keepAlive;
    private readonly Dictionary<string, TransferState> _taskStates = new();
    private bool _isStarted;

    /// <summary>保活当前是否已启动（诊断/测试用）</summary>
    public bool IsStarted => _isStarted;

    /// <summary>当前跟踪的任务数</summary>
    public int TrackedTaskCount => _taskStates.Count;

    /// <summary>当前是否有活动任务（非终态）</summary>
    public bool HasActiveTasks => _taskStates.Values.Any(s => !TransferStateMachine.IsTerminal(s));

    public TransferKeepAliveCoordinator(IPlatformKeepAliveService keepAlive)
        => _keepAlive = keepAlive;

    /// <summary>任务状态变化时调用：根据全局活动任务数同步保活开关</summary>
    public void OnTaskStateChanged(string fileId, TransferState newState, string title, string content)
    {
        _taskStates[fileId] = newState;
        Sync(title, content);
    }

    /// <summary>任务完成时调用：标记为终态并同步保活开关</summary>
    public void OnTaskCompleted(string fileId, bool success, string title, string content)
    {
        _taskStates[fileId] = success ? TransferState.Completed : TransferState.Failed;
        Sync(title, content);
    }

    /// <summary>任务从跟踪列表移除时调用（如 UI 清除历史）</summary>
    public void RemoveTask(string fileId, string title, string content)
    {
        _taskStates.Remove(fileId);
        Sync(title, content);
    }

    /// <summary>强制重置内部状态（测试与异常恢复用）</summary>
    public void Reset()
    {
        _taskStates.Clear();
        if (_isStarted)
        {
            _keepAlive.StopKeepAlive();
            _isStarted = false;
        }
    }

    private void Sync(string title, string content)
    {
        if (HasActiveTasks && !_isStarted)
        {
            _keepAlive.StartKeepAlive(title, content);
            _isStarted = true;
        }
        else if (!HasActiveTasks && _isStarted)
        {
            _keepAlive.StopKeepAlive();
            _isStarted = false;
        }
        // 已启动且有活动任务 → 不重复 Start（避免调用平台 API）
        // 已停止且无活动任务 → 不重复 Stop
    }
}
