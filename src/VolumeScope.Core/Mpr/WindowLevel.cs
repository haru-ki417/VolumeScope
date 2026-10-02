namespace VolumeScope.Core.Mpr;

/// <summary>表示の濃淡（ウィンドウ幅とレベル、単位 HU）</summary>
public readonly record struct WindowLevel(double Level, double Width)
{
    public double Low => Level - Width / 2;

    public double High => Level + Width / 2;

    /// <summary>HU を 0〜255 の明るさに直す</summary>
    public byte ToGray(float hu)
    {
        double t = (hu - Low) / Math.Max(Width, 1);
        return t <= 0 ? (byte)0 : t >= 1 ? (byte)255 : (byte)(t * 255 + 0.5);
    }
}

public sealed record WindowPreset(string Name, WindowLevel Window);

public static class WindowPresets
{
    public static IReadOnlyList<WindowPreset> All { get; } =
    [
        new("縦隔・軟部", new WindowLevel(40, 400)),
        new("肺野", new WindowLevel(-600, 1500)),
        new("骨", new WindowLevel(400, 1800)),
        new("腹部", new WindowLevel(50, 350)),
        new("脳", new WindowLevel(40, 80)),
        new("造影血管", new WindowLevel(150, 600)),
    ];
}
