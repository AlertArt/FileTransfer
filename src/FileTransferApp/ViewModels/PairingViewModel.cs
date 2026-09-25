using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileTransferApp.Core.Security;
using FileTransferApp.Core.Services.Interfaces;
using FileTransferApp.Services;

namespace FileTransferApp.ViewModels;

/// <summary>配对管理视图模型：集中展示已配对设备，支持逐个解除 / 全部解除。</summary>
public partial class PairingViewModel : ObservableObject
{
    private readonly IPairingService _pairing;
    private readonly ILocalizationService _loc;

    public ObservableCollection<PairingItemViewModel> Items { get; } = new();

    [ObservableProperty] public partial bool HasItems { get; set; }

    public PairingViewModel(IPairingService pairing, ILocalizationService localization)
    {
        _pairing = pairing;
        _loc = localization;
        Load();
    }

    public void Load()
    {
        Items.Clear();
        foreach (var r in _pairing.GetAllPairRecords().OrderByDescending(r => r.LastUsedUtc))
            Items.Add(new PairingItemViewModel(r, _loc));
        HasItems = Items.Count > 0;
    }

    [RelayCommand]
    private void Unpair(PairingItemViewModel? item)
    {
        if (item is null) return;
        _pairing.Unpair(item.DeviceId);
        Load();
    }

    [RelayCommand]
    private void ClearAll()
    {
        foreach (var r in _pairing.GetAllPairRecords())
            _pairing.Unpair(r.PeerDeviceId);
        Load();
    }
}

/// <summary>单条配对记录的展示。</summary>
public sealed class PairingItemViewModel
{
    public string DeviceId { get; }
    public string Name { get; }
    public string Meta { get; }

    public PairingItemViewModel(PairRecord record, ILocalizationService loc)
    {
        DeviceId = record.PeerDeviceId;
        Name = string.IsNullOrEmpty(record.PeerDeviceName) ? record.PeerDeviceId : record.PeerDeviceName;
        var type = string.IsNullOrEmpty(record.PeerDeviceType) ? "?" : record.PeerDeviceType;
        var when = record.PairedUtc.ToLocalTime().ToString("yyyy-MM-dd");
        Meta = $"{type} · {loc.Format("PairingPairedAt", when)}";
    }
}
