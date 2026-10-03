namespace VolumeScope.Core.Rendering;

/// <summary>CT 値ごとの色と不透明度の点（不透明度は 1 mm あたり）</summary>
public readonly record struct TransferPoint(double Hu, float R, float G, float B, float OpacityPerMm);

/// <summary>
/// 伝達関数: CT 値 → 色と不透明度。点のあいだは直線でつなぐ。
/// 描画のときは、1 HU ごとの表にして速く引けるようにする。
/// </summary>
public sealed class TransferFunction
{
    public const int MinHu = -1024;
    public const int MaxHu = 3071;

    public TransferFunction(string name, IReadOnlyList<TransferPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 2) throw new ArgumentException("点が 2 つ以上必要です。", nameof(points));
        Name = name;
        Points = points.OrderBy(p => p.Hu).ToList();
        Table = new float[(MaxHu - MinHu + 1) * 4];
        for (int hu = MinHu; hu <= MaxHu; hu++)
        {
            var (r, g, b, a) = Evaluate(hu);
            int o = (hu - MinHu) * 4;
            Table[o] = r;
            Table[o + 1] = g;
            Table[o + 2] = b;
            Table[o + 3] = a;
        }
        MinVisibleHu = Enumerable.Range(MinHu, MaxHu - MinHu + 1).FirstOrDefault(hu => Table[(hu - MinHu) * 4 + 3] > 0, MaxHu + 1);
    }

    public string Name { get; }

    public IReadOnlyList<TransferPoint> Points { get; }

    /// <summary>R, G, B, 1mm あたりの不透明度 を 1 HU ごとに並べた表</summary>
    internal float[] Table { get; }

    /// <summary>表の写し（-1024 HU から 1 HU ごとに R, G, B, 1mm あたりの不透明度）。ブラウザー版の GPU の描画に使う</summary>
    public float[] CopyTable() => (float[])Table.Clone();

    /// <summary>これより小さい値は見えない（空の領域を飛ばすのに使う）</summary>
    public int MinVisibleHu { get; }

    public (float R, float G, float B, float Opacity) Evaluate(double hu)
    {
        if (hu <= Points[0].Hu) return (Points[0].R, Points[0].G, Points[0].B, Points[0].OpacityPerMm);
        if (hu >= Points[^1].Hu) return (Points[^1].R, Points[^1].G, Points[^1].B, Points[^1].OpacityPerMm);
        for (int i = 1; i < Points.Count; i++)
        {
            if (hu > Points[i].Hu) continue;
            var a = Points[i - 1];
            var b = Points[i];
            float t = (float)((hu - a.Hu) / Math.Max(b.Hu - a.Hu, 1e-9));
            return (a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, a.OpacityPerMm + (b.OpacityPerMm - a.OpacityPerMm) * t);
        }
        return (0, 0, 0, 0);
    }

    /// <summary>全体を shift HU だけずらした伝達関数（画面のスライダーで見え方を調整する）</summary>
    public TransferFunction Shifted(double shift) =>
        new(Name, Points.Select(p => p with { Hu = p.Hu + shift }).ToList());
}

public static class TransferPresets
{
    public static TransferFunction Bone { get; } = new("骨", [
        new(-1024, 0, 0, 0, 0),
        new(140, 0.75f, 0.55f, 0.45f, 0),
        new(250, 0.92f, 0.82f, 0.68f, 0.25f),
        new(600, 1.0f, 0.97f, 0.9f, 0.9f),
        new(3071, 1.0f, 1.0f, 1.0f, 1.0f),
    ]);

    public static TransferFunction Skin { get; } = new("皮膚", [
        new(-1024, 0, 0, 0, 0),
        new(-600, 0.85f, 0.6f, 0.5f, 0),
        new(-350, 0.93f, 0.72f, 0.6f, 0.6f),
        new(-100, 0.95f, 0.75f, 0.62f, 0.9f),
        new(3071, 1, 0.95f, 0.9f, 1.0f),
    ]);

    public static TransferFunction Vessels { get; } = new("造影血管", [
        new(-1024, 0, 0, 0, 0),
        new(100, 0.7f, 0.1f, 0.1f, 0),
        new(180, 0.9f, 0.25f, 0.2f, 0.35f),
        new(320, 0.98f, 0.6f, 0.45f, 0.6f),
        new(500, 1.0f, 0.95f, 0.88f, 0.9f),
        new(3071, 1, 1, 1, 1),
    ]);

    public static TransferFunction SoftTissue { get; } = new("軟部と骨", [
        new(-1024, 0, 0, 0, 0),
        new(-150, 0.75f, 0.45f, 0.35f, 0),
        new(-50, 0.85f, 0.5f, 0.4f, 0.015f),
        new(120, 0.9f, 0.55f, 0.45f, 0.02f),
        new(200, 0.95f, 0.85f, 0.75f, 0.3f),
        new(700, 1.0f, 0.97f, 0.92f, 0.95f),
        new(3071, 1, 1, 1, 1),
    ]);

    public static TransferFunction Lung { get; } = new("肺", [
        new(-1024, 0, 0, 0, 0),
        new(-990, 0.5f, 0.7f, 0.9f, 0),
        new(-900, 0.55f, 0.75f, 0.95f, 0.006f),
        new(-600, 0.7f, 0.85f, 1.0f, 0.03f),
        new(-450, 0.7f, 0.85f, 1.0f, 0),
        new(200, 0.95f, 0.9f, 0.85f, 0),
        new(400, 1.0f, 0.97f, 0.9f, 0.6f),
        new(3071, 1, 1, 1, 1),
    ]);

    public static IReadOnlyList<TransferFunction> All { get; } = [Bone, Skin, Vessels, SoftTissue, Lung];
}
