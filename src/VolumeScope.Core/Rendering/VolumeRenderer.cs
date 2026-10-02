using VolumeScope.Core.Geometry;
using VolumeScope.Core.Mpr;
using VolumeScope.Core.Volumes;

namespace VolumeScope.Core.Rendering;

public enum RenderMode
{
    /// <summary>ボリュームレンダリング（色と半透明で立体を描く）</summary>
    Dvr,

    /// <summary>最大値投影（視線上で最も大きい値。造影血管や骨）</summary>
    Mip,
}

/// <summary>表示する範囲（各方向 0〜1 の割合）。前半分を切り取って中を見る などに使う</summary>
public sealed record CropBox(double MinX = 0, double MaxX = 1, double MinY = 0, double MaxY = 1, double MinZ = 0, double MaxZ = 1)
{
    public static CropBox Full { get; } = new();
}

public sealed record RenderSettings
{
    public RenderMode Mode { get; init; } = RenderMode.Dvr;
    public required TransferFunction Transfer { get; init; }
    public WindowLevel MipWindow { get; init; } = new(300, 1200);
    public CropBox Crop { get; init; } = CropBox.Full;
    /// <summary>サンプリングの間隔（最小のボクセル間隔に対する倍率）。大きいほど速く粗い</summary>
    public double StepFactor { get; init; } = 0.5;
    public bool Shading { get; init; } = true;
}

/// <summary>
/// CPU で行うレイキャスティング（GPU を持たない PC や仮想環境でも動く）。
///   ・視線ごとに、画像の箱に入ってから出るまで一定の間隔で値を読み、手前から順に重ねる
///   ・ほぼ不透明になったら打ち切る（早期打ち切り）
///   ・8×8×8 ボクセルの塊ごとの最大値を持ち、何も見えない塊は飛ばす（空の領域の飛ばし）
///   ・値の傾き（勾配）を面の向きとして陰影をつける
/// </summary>
public sealed class VolumeRenderer
{
    private const int Block = 8;
    private readonly Volume volume;
    private readonly short[] blockMax;
    private readonly int bx, by, bz;

    public VolumeRenderer(Volume volume)
    {
        this.volume = volume ?? throw new ArgumentNullException(nameof(volume));
        bx = (volume.Width + Block - 1) / Block;
        by = (volume.Height + Block - 1) / Block;
        bz = (volume.Depth + Block - 1) / Block;
        blockMax = new short[bx * by * bz];
        Array.Fill(blockMax, short.MinValue);
        // 塊の最大値（補間で隣の塊の値も混ざるので、境界の 1 ボクセルも含める）
        Parallel.For(0, bz, k =>
        {
            for (int j = 0; j < by; j++)
                for (int i = 0; i < bx; i++)
                {
                    short m = short.MinValue;
                    for (int z = Math.Max(k * Block - 1, 0); z < Math.Min((k + 1) * Block + 1, volume.Depth); z++)
                        for (int y = Math.Max(j * Block - 1, 0); y < Math.Min((j + 1) * Block + 1, volume.Height); y++)
                        {
                            int row = z * volume.SliceSize + y * volume.Width;
                            for (int x = Math.Max(i * Block - 1, 0); x < Math.Min((i + 1) * Block + 1, volume.Width); x++)
                                if (volume.Data[row + x] > m) m = volume.Data[row + x];
                        }
                    blockMax[(k * by + j) * bx + i] = m;
                }
        });
    }

    public Volume Volume => volume;

    /// <summary>画像全体が入る球の半径（mm）</summary>
    public double Radius => volume.Corners().Max(c => Vec3.Distance(c, volume.Center));

    /// <summary>B, G, R, A の順で width × height の画素を書き込む</summary>
    public void Render(Camera camera, RenderSettings settings, int width, int height, Span<byte> target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(settings);
        if (target.Length < width * height * 4) throw new ArgumentException("書き込み先が小さすぎます。", nameof(target));
        var buffer = new byte[width * height * 4];
        RenderInto(camera, settings, width, height, buffer, cancellationToken);
        buffer.CopyTo(target);
    }

    public byte[] Render(Camera camera, RenderSettings settings, int width, int height, CancellationToken cancellationToken = default)
    {
        var buffer = new byte[width * height * 4];
        RenderInto(camera, settings, width, height, buffer, cancellationToken);
        return buffer;
    }

    private void RenderInto(Camera camera, RenderSettings s, int width, int height, byte[] buffer, CancellationToken ct)
    {
        var g = volume.Geometry;
        var (forward, right, up) = camera.Basis();
        double radius = Radius;
        double pixel = 2 * radius / (Math.Min(width, height) * Math.Max(camera.Zoom, 0.05));
        var center = volume.Center + right * camera.PanX + up * camera.PanY;
        double stepMm = Math.Max(g.MinSpacing * s.StepFactor, 0.05);

        // 番地の空間での向き
        var dir = g.PatientDirectionToIndex(forward * stepMm); // 1 歩ぶん
        var dRight = g.PatientDirectionToIndex(right * pixel);
        var dUp = g.PatientDirectionToIndex(up * pixel);
        var start0 = g.PatientToIndex(center - forward * (radius * 1.05));

        // 表示する範囲（番地）
        var c = s.Crop;
        double x0 = Math.Max(-0.5, c.MinX * volume.Width - 0.5), x1 = Math.Min(volume.Width - 0.5, c.MaxX * volume.Width - 0.5);
        double y0 = Math.Max(-0.5, c.MinY * volume.Height - 0.5), y1 = Math.Min(volume.Height - 0.5, c.MaxY * volume.Height - 0.5);
        double z0 = Math.Max(-0.5, c.MinZ * volume.Depth - 0.5), z1 = Math.Min(volume.Depth - 0.5, c.MaxZ * volume.Depth - 0.5);
        // 補間で範囲外を読まないよう、少し内側に
        const double eps = 1e-3;
        var boxMin = new Vec3(Math.Max(x0, 0), Math.Max(y0, 0), Math.Max(z0, 0));
        var boxMax = new Vec3(Math.Min(x1, volume.Width - 1 - eps), Math.Min(y1, volume.Height - 1 - eps), Math.Min(z1, volume.Depth - 1 - eps));

        var light = (-forward).Normalized(); // カメラの位置からの光
        var tf = s.Transfer;
        double alphaScale = stepMm;
        float[] table = tf.Table;
        var sampler = new Sampler(volume);
        // 勾配（番地の空間）を患者座標の向きに直すための係数
        var gx = g.ColumnStep / (g.SpacingX * g.SpacingX);
        var gy = g.RowStep / (g.SpacingY * g.SpacingY);
        var gz = g.SliceStep / (g.SpacingZ * g.SpacingZ);

        Parallel.For(0, height, new ParallelOptions { CancellationToken = ct }, py =>
        {
            for (int px = 0; px < width; px++)
            {
                double u = px - width / 2.0 + 0.5, v = height / 2.0 - py - 0.5;
                var origin = start0 + dRight * u + dUp * v;
                int o = (py * width + px) * 4;
                if (!Intersect(origin, dir, boxMin, boxMax, out double tEnter, out double tExit))
                {
                    Background(buffer, o, py, height);
                    continue;
                }

                if (s.Mode == RenderMode.Mip)
                {
                    float max = float.MinValue;
                    for (double t = Math.Ceiling(tEnter); t <= tExit; t += 1)
                    {
                        var q = origin + dir * t;
                        if (BlockMaxAt(q) <= max)
                        {
                            t = SkipBlock(q, origin, dir, t) - 1;
                            continue;
                        }
                        float val = sampler.Sample(q.X, q.Y, q.Z);
                        if (val > max) max = val;
                    }
                    if (max == float.MinValue)
                    {
                        Background(buffer, o, py, height);
                        continue;
                    }
                    byte gray = s.MipWindow.ToGray(max);
                    buffer[o] = gray;
                    buffer[o + 1] = gray;
                    buffer[o + 2] = gray;
                    buffer[o + 3] = 255;
                    continue;
                }

                float r = 0, gg = 0, b = 0, a = 0;
                // 視線ごとに出発点を少しずらす（一定の間隔で読むことによる年輪のような模様を消す）
                double jitter = Hash(px, py);
                for (double t = Math.Floor(tEnter) + jitter; t <= tExit && a < 0.98f; t += 1)
                {
                    var q = origin + dir * t;
                    if (t < tEnter) continue;
                    if (BlockMaxAt(q) < tf.MinVisibleHu)
                    {
                        t = SkipBlock(q, origin, dir, t) - 1 + jitter;
                        continue;
                    }
                    float val = sampler.Sample(q.X, q.Y, q.Z);
                    int hu = Math.Clamp((int)val, TransferFunction.MinHu, TransferFunction.MaxHu) - TransferFunction.MinHu;
                    float opacity = table[hu * 4 + 3];
                    if (opacity <= 0) continue;
                    float alpha = 1 - MathF.Exp(-opacity * (float)alphaScale);
                    float cr = table[hu * 4], cg = table[hu * 4 + 1], cb = table[hu * 4 + 2];
                    if (s.Shading && alpha > 0.01f)
                    {
                        // 勾配: 値が増える向き。面の外向きは値が減る向き（-勾配）
                        float dx = sampler.Sample(q.X + 1, q.Y, q.Z) - sampler.Sample(q.X - 1, q.Y, q.Z);
                        float dy = sampler.Sample(q.X, q.Y + 1, q.Z) - sampler.Sample(q.X, q.Y - 1, q.Z);
                        float dz = sampler.Sample(q.X, q.Y, q.Z + 1) - sampler.Sample(q.X, q.Y, q.Z - 1);
                        var normal = -(gx * dx + gy * dy + gz * dz);
                        double len = normal.Length;
                        if (len > 1e-3)
                        {
                            normal /= len;
                            double diffuse = Math.Abs(Vec3.Dot(normal, light));
                            double spec = Math.Pow(diffuse, 24) * 0.25;
                            float shade = (float)(0.25 + 0.75 * diffuse);
                            cr = Math.Min(1, cr * shade + (float)spec);
                            cg = Math.Min(1, cg * shade + (float)spec);
                            cb = Math.Min(1, cb * shade + (float)spec);
                        }
                    }
                    float w = (1 - a) * alpha;
                    r += cr * w;
                    gg += cg * w;
                    b += cb * w;
                    a += w;
                }
                // 背景と重ねる
                var (bgB, bgG, bgR) = BackgroundColor(py, height);
                buffer[o] = (byte)Math.Clamp(b * 255 + bgB * (1 - a), 0, 255);
                buffer[o + 1] = (byte)Math.Clamp(gg * 255 + bgG * (1 - a), 0, 255);
                buffer[o + 2] = (byte)Math.Clamp(r * 255 + bgR * (1 - a), 0, 255);
                buffer[o + 3] = 255;
            }
        });
    }

    /// <summary>画素ごとに決まった 0〜1 の値（毎回同じ画像になるように乱数は使わない）</summary>
    private static double Hash(int x, int y)
    {
        uint h = (uint)(x * 73856093) ^ (uint)(y * 19349663);
        h ^= h >> 13;
        h *= 0x5bd1e995;
        h ^= h >> 15;
        return (h & 0xFFFF) / 65536.0;
    }

    private short BlockMaxAt(Vec3 q)
    {
        int i = Math.Clamp((int)(q.X + 0.5) / Block, 0, bx - 1);
        int j = Math.Clamp((int)(q.Y + 0.5) / Block, 0, by - 1);
        int k = Math.Clamp((int)(q.Z + 0.5) / Block, 0, bz - 1);
        return blockMax[(k * by + j) * bx + i];
    }

    /// <summary>今いる塊を抜ける位置（歩数）</summary>
    private static double SkipBlock(Vec3 q, Vec3 origin, Vec3 dir, double t)
    {
        int i = (int)(q.X + 0.5) / Block, j = (int)(q.Y + 0.5) / Block, k = (int)(q.Z + 0.5) / Block;
        double exit = Math.Min(AxisExit(origin.X, dir.X, i * Block - 0.5),
                      Math.Min(AxisExit(origin.Y, dir.Y, j * Block - 0.5), AxisExit(origin.Z, dir.Z, k * Block - 0.5)));
        return Math.Max(Math.Floor(exit) + 1, t + 1);
    }

    private static double AxisExit(double o, double d, double lo)
    {
        if (Math.Abs(d) < 1e-12) return double.MaxValue;
        return ((d > 0 ? lo + Block : lo) - o) / d;
    }

    /// <summary>直線と箱の交わり（歩数で表す）</summary>
    private static bool Intersect(Vec3 o, Vec3 d, Vec3 min, Vec3 max, out double tEnter, out double tExit)
    {
        tEnter = 0;
        tExit = double.MaxValue;
        return Slab(o.X, d.X, min.X, max.X, ref tEnter, ref tExit)
            && Slab(o.Y, d.Y, min.Y, max.Y, ref tEnter, ref tExit)
            && Slab(o.Z, d.Z, min.Z, max.Z, ref tEnter, ref tExit)
            && tExit >= tEnter;
    }

    private static bool Slab(double o, double d, double lo, double hi, ref double tEnter, ref double tExit)
    {
        if (Math.Abs(d) < 1e-12) return o >= lo && o <= hi;
        double t1 = (lo - o) / d, t2 = (hi - o) / d;
        if (t1 > t2) (t1, t2) = (t2, t1);
        tEnter = Math.Max(tEnter, t1);
        tExit = Math.Min(tExit, t2);
        return true;
    }

    private static (byte B, byte G, byte R) BackgroundColor(int y, int height)
    {
        // 上が少し明るい、落ち着いた濃紺のグラデーション
        double t = (double)y / Math.Max(height - 1, 1);
        return ((byte)(46 - 22 * t), (byte)(32 - 16 * t), (byte)(24 - 12 * t));
    }

    private static void Background(byte[] buffer, int o, int y, int height)
    {
        var (b, g, r) = BackgroundColor(y, height);
        buffer[o] = b;
        buffer[o + 1] = g;
        buffer[o + 2] = r;
        buffer[o + 3] = 255;
    }

    /// <summary>範囲の確認を省いた、速い三線形補間（呼ぶ側が範囲内を保証する。外れたら端の値）</summary>
    private readonly struct Sampler(Volume v)
    {
        private readonly short[] data = v.Data;
        private readonly int w = v.Width, h = v.Height, d = v.Depth, slice = v.SliceSize;

        public float Sample(double x, double y, double z)
        {
            x = Math.Clamp(x, 0, w - 1.0001);
            y = Math.Clamp(y, 0, h - 1.0001);
            z = Math.Clamp(z, 0, d - 1.0001);
            int x0 = (int)x, y0 = (int)y, z0 = (int)z;
            float fx = (float)(x - x0), fy = (float)(y - y0), fz = (float)(z - z0);
            int i = z0 * slice + y0 * w + x0;
            int dx = x0 + 1 < w ? 1 : 0, dy = y0 + 1 < h ? w : 0, dz = z0 + 1 < d ? slice : 0;
            float c00 = data[i] + (data[i + dx] - data[i]) * fx;
            float c10 = data[i + dy] + (data[i + dy + dx] - data[i + dy]) * fx;
            float c01 = data[i + dz] + (data[i + dz + dx] - data[i + dz]) * fx;
            float c11 = data[i + dz + dy] + (data[i + dz + dy + dx] - data[i + dz + dy]) * fx;
            float c0 = c00 + (c10 - c00) * fy, c1 = c01 + (c11 - c01) * fy;
            return c0 + (c1 - c0) * fz;
        }
    }
}
