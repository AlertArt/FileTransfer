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
/// XAML 本地化标记扩展。用法：{local:Localize Key=AppTitle} 或 {local:Localize Key=OnlineCount, Args={Binding Devices.Devices.Count}}
/// - 无 Args：绑定到 LocalizationService.Instance[key]，语言切换时自动刷新
/// - 有 Args：组合 [格式串, 参数] 为 MultiBinding，语言/参数任一变化均自动刷新
///   注意：不可把 Args(绑定) 作为 Binding.Source 嵌套，Avalonia 12 编译绑定下会渲染为
///   绑定对象自身（表现为 "Data.CompiledBinding" 之类的字符串），必须先修根因。
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
        var formatBinding = new Binding
        {
            Source = LocalizationService.Instance,
            Path = $"[{Key}]",
            Mode = BindingMode.OneWay,
        };
        if (Args is null)
            return formatBinding;

        // 有格式化参数：格式串(语言索引) + 参数 组合成 MultiBinding，由转换器执行 Format
        var argsBinding = Args as BindingBase ?? new Binding { Source = Args };
        var multi = new MultiBinding
        {
            Converter = new LocalizeFormatConverter(),
        };
        multi.Bindings.Add(formatBinding);
        multi.Bindings.Add(argsBinding);
        return multi;
    }
}

/// <summary>[格式串, 参数] → string.Format(格式串, 参数)。</summary>
internal sealed class LocalizeFormatConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is { Count: 2 } && values[0] is string format && values[1] is not null)
        {
            try { return string.Format(format, values[1]); }
            catch { return format; }
        }
        return AvaloniaProperty.UnsetValue;
    }

    public IList<object?> ConvertBack(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}