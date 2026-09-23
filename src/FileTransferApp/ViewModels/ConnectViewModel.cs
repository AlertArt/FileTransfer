using System.Net;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileTransferApp.Core.Models;
using FileTransferApp.Core.Protocols;
using FileTransferApp.Services;

namespace FileTransferApp.ViewModels;

/// <summary>
/// 连接码（二维码配对）视图模型：
///  - 展示本机连接码二维码（供对方扫码 / 复制）；
///  - 支持粘贴对方连接码导入并选中设备（跨平台，桌面也能用）。
/// </summary>
public partial class ConnectViewModel : ObservableObject
{
    private readonly DeviceListViewModel _devices;

    [ObservableProperty] public partial Bitmap? QrImage { get; set; }
    [ObservableProperty] public partial string SelfCode { get; set; } = string.Empty;
    [ObservableProperty] public partial string ImportCode { get; set; } = string.Empty;
    [ObservableProperty] public partial string StatusHint { get; set; } = string.Empty;
    [ObservableProperty] public partial bool ImportOk { get; set; }

    public ConnectViewModel(DeviceListViewModel devices, DeviceNode self, string selfIp)
    {
        _devices = devices;

        // 本机节点默认没有 IpAddress（发现层不填），用外部传入的 LAN IP 组装连接码
        var codeNode = new DeviceNode
        {
            DeviceId = self.DeviceId,
            DeviceName = self.DeviceName,
            DeviceType = self.DeviceType,
            IpAddress = IPAddress.TryParse(selfIp, out var ip) ? ip : null,
            Port = self.Port,
            ProtocolVersion = self.ProtocolVersion,
        };
        SelfCode = ConnectionCode.Build(codeNode);
        QrImage = QrCodeRenderer.Render(SelfCode);
    }

    [RelayCommand]
    private void Import()
    {
        if (!ConnectionCode.TryParse(ImportCode, out var node) || node is null)
        {
            ImportOk = false;
            StatusHint = LocalizationService.Instance.GetString("Connect.ImportInvalid");
            return;
        }

        _devices.AddOrSelectManual(node);
        ImportOk = true;
        StatusHint = LocalizationService.Instance.Format("Connect.ImportOk", node.DeviceName);
        ImportCode = string.Empty;
    }
}
