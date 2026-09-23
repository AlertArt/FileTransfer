using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileTransferApp.Core.Models;
using FileTransferApp.Core.Services.Impl;
using FileTransferApp.Core.Services.Interfaces;
using FileTransferApp.Services;

namespace FileTransferApp.ViewModels;

/// <summary>传输历史视图模型：展示终态任务快照（持久化，重启后仍在），支持清空。</summary>
public partial class HistoryViewModel : ObservableObject
{
    private readonly ITransferHistoryStore _store;

    public ObservableCollection<HistoryItemViewModel> Entries { get; } = new();

    [ObservableProperty] public partial bool HasEntries { get; set; }

    public HistoryViewModel(ITransferHistoryStore store)
    {
        _store = store;
        Load();
    }

    public void Load()
    {
        Entries.Clear();
        foreach (var e in _store.GetAll()) Entries.Add(new HistoryItemViewModel(e));
        HasEntries = Entries.Count > 0;
    }

    [RelayCommand]
    private void Clear()
    {
        _store.Clear();
        Load();
    }
}

/// <summary>单条历史的展示（预格式化为本地化文案，便于直接绑定）。</summary>
public sealed class HistoryItemViewModel
{
    public string FileName { get; }
    public string Meta { get; }
    public string? Error { get; }
    public bool HasError { get; }

    public HistoryItemViewModel(TransferHistoryEntry e)
    {
        FileName = e.FileName;

        var dirKey = e.Direction == TransferDirection.Send ? "Direction.Send" : "Direction.Receive";
        var stateKey = e.State switch
        {
            TransferState.Failed => "State.Failed",
            TransferState.Cancelled => "State.Cancelled",
            TransferState.Disconnected => "State.Disconnected",
            _ => "State.Completed",
        };
        var dir = LocalizationService.Instance.GetString(dirKey);
        var state = LocalizationService.Instance.GetString(stateKey);
        var size = SpeedFormatter.FormatSize(e.TotalBytes);
        var when = (e.EndedUtc == default ? e.StartedUtc : e.EndedUtc).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        var peer = string.IsNullOrEmpty(e.PeerName) ? string.Empty : $" · {e.PeerName}";
        Meta = $"[{dir}] {size} · {state} · {when}{peer}";

        Error = string.IsNullOrWhiteSpace(e.ErrorMessage) ? null : e.ErrorMessage;
        HasError = Error is not null;
    }
}
