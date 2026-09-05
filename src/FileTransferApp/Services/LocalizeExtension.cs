using System;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace FileTransferApp.Services;

/// <summary>
/// XAML 本地化标记扩展。用法：{local:Localize Key=AppTitle} 或 {local:Localize Key=OnlineCount, Args={Binding Devices.Devices.Count}}
/// 内部绑定到 LocalizationService.Instance[key]，语言切换时自动刷新。
/// </summary>
public class LocalizeExtension : MarkupExtension
{
    /// <summary>语言文件中的 key</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>可选：格式化参数（支持绑定）。如 Args={Binding Count}</summary>
    public object? Args { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (string.IsNullOrEmpty(Key)) return string.Empty;

        // 无格式化参数：直接绑定到 Instance[Key]
        if (Args is null)
        {
            return new Binding
            {
                Source = LocalizationService.Instance,
                Path = $"[{Key}]",
                Mode = BindingMode.OneWay,
            };
        }

        // 有格式化参数：先取格式串，再用 StringFormat 格式化
        // Avalonia 的 Binding.StringFormat 会把绑定值作为 {0}，但我们需要把 Args 作为参数
        // 这里用一个技巧：绑定到 Args，StringFormat 设为 Instance.GetFormatString(Key)
        var format = LocalizationService.Instance.GetString(Key);
        return new Binding
        {
            Source = Args,
            Mode = BindingMode.OneWay,
            StringFormat = format,
        };
    }
}
