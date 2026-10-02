using System.Windows.Media;
using VolumeScope.Core.Mpr;

namespace VolumeScope.App.Controls;

/// <summary>断面ごとの色（枠・見出し・ほかの画面の十字の線に使う）</summary>
public static class PlaneColors
{
    public static readonly Color Axial = Color.FromRgb(0xE3, 0xA5, 0x48);
    public static readonly Color Coronal = Color.FromRgb(0x3F, 0xB8, 0xAF);
    public static readonly Color Sagittal = Color.FromRgb(0xE0, 0x6C, 0x8A);
    public static readonly Color Measure = Color.FromRgb(0xF4, 0xE0, 0x7A);

    public static Color Of(MprPlane plane) => plane switch
    {
        MprPlane.Axial => Axial,
        MprPlane.Coronal => Coronal,
        _ => Sagittal,
    };

    public static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
