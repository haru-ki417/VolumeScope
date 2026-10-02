using VolumeScope.Core.Geometry;

namespace VolumeScope.Core.Volumes;

/// <summary>検査の情報（画面の表示用）</summary>
public sealed record VolumeInfo
{
    public string PatientName { get; init; } = "";
    public string PatientId { get; init; } = "";
    public string StudyDate { get; init; } = "";
    public string Modality { get; init; } = "";
    public string SeriesDescription { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public string ConvolutionKernel { get; init; } = "";
    public double? SliceThickness { get; init; }
    public string SeriesInstanceUid { get; init; } = "";

    /// <summary>読み込みのときに気づいた注意点（スライス間隔が一定でない など）</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// 3 次元の画像。値は CT 値（HU）を short で持つ。
/// 番地は data[z * Width * Height + y * Width + x]（x: 列, y: 行, z: スライス）。
/// </summary>
public sealed class Volume
{
    /// <summary>範囲外を読んだときの値（空気）</summary>
    public const short Outside = -1024;

    public Volume(int width, int height, int depth, short[] data, VolumeGeometry geometry, VolumeInfo? info = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(depth);
        ArgumentNullException.ThrowIfNull(data);
        if ((long)width * height * depth != data.Length) throw new ArgumentException("データの大きさが合いません。", nameof(data));
        Width = width;
        Height = height;
        Depth = depth;
        Data = data;
        Geometry = geometry ?? throw new ArgumentNullException(nameof(geometry));
        Info = info ?? new VolumeInfo();
        (MinValue, MaxValue) = MinMax(data);
    }

    public int Width { get; }

    public int Height { get; }

    public int Depth { get; }

    public short[] Data { get; }

    public VolumeGeometry Geometry { get; }

    public VolumeInfo Info { get; }

    public short MinValue { get; }

    public short MaxValue { get; }

    public int SliceSize => Width * Height;

    public long VoxelCount => (long)Width * Height * Depth;

    public short this[int x, int y, int z] =>
        (uint)x < (uint)Width && (uint)y < (uint)Height && (uint)z < (uint)Depth ? Data[z * SliceSize + y * Width + x] : Outside;

    /// <summary>番地（小数）での値を、周りの 8 点から求める（三線形補間）。範囲外は空気</summary>
    public float Sample(double x, double y, double z)
    {
        if (x < -0.5 || y < -0.5 || z < -0.5 || x > Width - 0.5 || y > Height - 0.5 || z > Depth - 0.5) return Outside;
        // 端のボクセルの外側半分は、端の値をそのまま使う（空気と混ぜて暗くしない）
        x = Math.Clamp(x, 0, Width - 1);
        y = Math.Clamp(y, 0, Height - 1);
        z = Math.Clamp(z, 0, Depth - 1);
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y), z0 = (int)Math.Floor(z);
        float fx = (float)(x - x0), fy = (float)(y - y0), fz = (float)(z - z0);
        float c000 = this[x0, y0, z0], c100 = this[x0 + 1, y0, z0], c010 = this[x0, y0 + 1, z0], c110 = this[x0 + 1, y0 + 1, z0];
        float c001 = this[x0, y0, z0 + 1], c101 = this[x0 + 1, y0, z0 + 1], c011 = this[x0, y0 + 1, z0 + 1], c111 = this[x0 + 1, y0 + 1, z0 + 1];
        float c00 = c000 + (c100 - c000) * fx, c10 = c010 + (c110 - c010) * fx;
        float c01 = c001 + (c101 - c001) * fx, c11 = c011 + (c111 - c011) * fx;
        float c0 = c00 + (c10 - c00) * fy, c1 = c01 + (c11 - c01) * fy;
        return c0 + (c1 - c0) * fz;
    }

    /// <summary>患者座標（mm）での値</summary>
    public float SampleAt(Vec3 patient)
    {
        var i = Geometry.PatientToIndex(patient);
        return Sample(i.X, i.Y, i.Z);
    }

    /// <summary>画像全体の中心（患者座標）</summary>
    public Vec3 Center => Geometry.IndexToPatient(new Vec3((Width - 1) / 2.0, (Height - 1) / 2.0, (Depth - 1) / 2.0));

    /// <summary>8 つの角の患者座標</summary>
    public IEnumerable<Vec3> Corners()
    {
        foreach (int z in new[] { 0, Depth - 1 })
            foreach (int y in new[] { 0, Height - 1 })
                foreach (int x in new[] { 0, Width - 1 })
                    yield return Geometry.IndexToPatient(new Vec3(x, y, z));
    }

    /// <summary>値の分布（binWidth HU ごとの数）。閾値の目安の表示に使う</summary>
    public long[] Histogram(int minHu, int maxHu, int binWidth)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(binWidth);
        int bins = (maxHu - minHu) / binWidth + 1;
        var counts = new long[bins];
        foreach (short v in Data)
        {
            int b = (v - minHu) / binWidth;
            if (v >= minHu && b < bins) counts[b]++;
        }
        return counts;
    }

    /// <summary>
    /// factor 個ずつまとめて小さくした画像（平均）。大きな検査の面の作成や、操作中の表示を速くするため
    /// </summary>
    public Volume Downsample(int factor, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(factor, 1);
        if (factor == 1) return this;
        int w = (Width + factor - 1) / factor, h = (Height + factor - 1) / factor, d = (Depth + factor - 1) / factor;
        var data = new short[(long)w * h * d];
        Parallel.For(0, d, new ParallelOptions { CancellationToken = cancellationToken }, z =>
        {
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int sum = 0, n = 0;
                    for (int dz = 0; dz < factor; dz++)
                        for (int dy = 0; dy < factor; dy++)
                            for (int dx = 0; dx < factor; dx++)
                            {
                                int sx = x * factor + dx, sy = y * factor + dy, sz = z * factor + dz;
                                if (sx >= Width || sy >= Height || sz >= Depth) continue;
                                sum += Data[sz * SliceSize + sy * Width + sx];
                                n++;
                            }
                    data[(long)z * w * h + y * w + x] = (short)(sum / Math.Max(n, 1));
                }
        });
        // 新しい番地 0 の中心は、元の番地 (factor-1)/2 の位置
        double off = (factor - 1) / 2.0;
        var g = Geometry;
        var geometry = new VolumeGeometry(g.IndexToPatient(new Vec3(off, off, off)), g.RowDirection, g.ColumnDirection, g.SliceDirection,
            g.SpacingX * factor, g.SpacingY * factor, g.SpacingZ * factor);
        return new Volume(w, h, d, data, geometry, Info);
    }

    private static (short Min, short Max) MinMax(short[] data)
    {
        if (data.Length == 0) return (0, 0);
        short min = short.MaxValue, max = short.MinValue;
        foreach (short v in data)
        {
            if (v < min) min = v;
            if (v > max) max = v;
        }
        return (min, max);
    }
}
