using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace VolumeScope.App.Controls;

/// <summary>列挙の値が ConverterParameter と同じなら true（ラジオボタン用。true になったらその値に戻す）</summary>
public sealed class EnumEquals : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && parameter is string s && string.Equals(value.ToString(), s, StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is string s ? Enum.Parse(targetType, s) : Binding.DoNothing;
}

/// <summary>true / 空でない文字列 / null でない → 表示（ConverterParameter=Invert で反対）</summary>
public sealed class Visible : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool b = value switch
        {
            bool x => x,
            string s => !string.IsNullOrWhiteSpace(s),
            int i => i > 0,
            null => false,
            _ => true,
        };
        if (parameter as string == "Invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>0xAARRGGBB → ブラシ</summary>
public sealed class ArgbToBrush : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        uint c = value is uint u ? u : 0xFF888888;
        var b = new SolidColorBrush(Color.FromArgb((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c));
        b.Freeze();
        return b;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
