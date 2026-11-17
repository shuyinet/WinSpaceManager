using System.Globalization;
using System.Windows.Data;

namespace SysMgr.App.Converters;

/// <summary>字节数 → 可读字符串（B/KB/MB/GB/TB）。</summary>
public sealed class BytesFormatter : IValueConverter
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Format(value);

    /// <summary>供 C# 代码直接调用；未知（null）返回 null，便于 XAML 用 TargetNullValue 兜底。</summary>
    public static string? Format(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is not long bytes)
        {
            return value.ToString();
        }

        if (bytes <= 0)
        {
            return "0 B";
        }

        var unit = 0;
        double size = bytes;
        while (size >= 1024 && unit < Units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{size:0.##} {Units[unit]}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
