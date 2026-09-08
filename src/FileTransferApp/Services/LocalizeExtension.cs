using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace FileTransferApp.Services;

/// <summary>
/// XAML 本地化标记扩展。用法：{svc:Localize Key=AppTitle} 或 {svc:Localize Key=OnlineCount, Args={Binding Devices.Devices.Count}}
/// - 统一监听 LocalizationService.CurrentLanguage（标准属性通知，语言切换时必定触发刷新），
///   避免依赖索引器 this[key] 的 "Item[]" PropertyChanged（Avalonia 12 下不可靠，会导致切换语言后部分文本要重启才生效）。
/// - 无 Args：MultiBinding[CurrentLanguage] → LocalizeValueConverter 直接取文案
/// - 有 Args：MultiBinding[CurrentLanguage, 参数] → LocalizeValueConverter 执行 string.Format
///   注意：不可把 Args(绑定) 作为 Binding.Source 嵌套，Avalonia 12 编译绑定下会渲染为
///   绑定对象自身（表现为 "Data.CompiledBinding" 之类的字符串）。
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

        // 语言源：绑定标准属性而非索引器，切换语言时必然引发重新求值
        var langBinding = new Binding
        {
            Source = LocalizationService.Instance,
            Path = nameof(LocalizationService.CurrentLanguage),
            Mode = BindingMode.OneWay,
        };

        var multi = new MultiBinding
        {
            Converter = new LocalizeValueConverter(),
            ConverterParameter = Key,
        };
        multi.Bindings.Add(langBinding);
        if (Args is not null)
            multi.Bindings.Add(Args as BindingBase ?? new Binding { Source = Args });
        return multi;
    }
}

/// <summary>
/// [CurrentLanguage(, 参数)] → 按参数 Key 解析文案。
/// values.Count==1：返回 key 对应文案；values.Count==2：string.Format(文案, 参数)。
/// </summary>
internal sealed class LocalizeValueConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (parameter is not string key) return AvaloniaProperty.UnsetValue;
        if (values is null) return AvaloniaProperty.UnsetValue;

        if (values.Count == 1)
            return LocalizationService.Instance.GetString(key);

        if (values.Count == 2 && values[1] is { } arg)
        {
            var fmt = LocalizationService.Instance.GetString(key);
            try { return string.Format(fmt, arg); }
            catch { return fmt; }
        }
        return AvaloniaProperty.UnsetValue;
    }

    public IList<object?> ConvertBack(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}