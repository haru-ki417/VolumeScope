using VolumeScope.Core.Geometry;
using VolumeScope.Core.Volumes;

namespace VolumeScope.Core.Mpr;

/// <summary>断面の種類（患者の体を基準にした向き）</summary>
public enum MprPlane
{
    /// <summary>横断面（体を輪切り。足側から見る）</summary>
    Axial,

    /// <summary>冠状断（正面から見る）</summary>
    Coronal,

    /// <summary>矢状断（横から見る）</summary>
    Sagittal,
}

/// <summary>厚みのある断面のまとめ方</summary>
public enum SlabMode
{
    /// <summary>1 枚の断面</summary>
    Thin,

    /// <summary>厚みの中で最大の値（造影血管や結節が見やすい）</summary>
    Mip,

    /// <summary>厚みの中の平均（雑音が減る）</summary>
    Average,
}

/// <summary>
/// 3D の画像から切り出した 1 枚の断面。値は HU。
/// 画面の右・下の向きは、放射線科の慣例に合わせる（横断面は患者の右が画面の左、前が上）。
/// </summary>
public sealed class MprSlice
{
    internal MprSlice(MprPlane plane, int width, int height, float[] values, double pixelSize, Vec3 origin, Vec3 right, Vec3 down)
    {
        Plane = plane;
        Width = width;
        Height = height;
        Values = values;
        PixelSize = pixelSize;
        Origin = origin;
        Right = right;
        Down = down;
    }

    public MprPlane Plane { get; }

    public int Width { get; }

    public int Height { get; }

    public float[] Values { get; }

    /// <summary>1 画素の大きさ（mm）。縦横同じ</summary>
    public double PixelSize { get; }

    /// <summary>画素 (0,0) の中心の患者座標</summary>
    public Vec3 Origin { get; }

    public Vec3 Right { get; }

    public Vec3 Down { get; }

    public Vec3 Normal => Vec3.Cross(Right, Down);

    public float this[int x, int y] => (uint)x < (uint)Width && (uint)y < (uint)Height ? Values[y * Width + x] : Volume.Outside;

    public Vec3 PixelToPatient(double x, double y) => Origin + Right * (x * PixelSize) + Down * (y * PixelSize);

    public (double X, double Y) PatientToPixel(Vec3 p)
    {
        var d = p - Origin;
        return (Vec3.Dot(d, Right) / PixelSize, Vec3.Dot(d, Down) / PixelSize);
    }

    /// <summary>表示用の画素（B, G, R, A の順）を作る</summary>
    public void RenderBgra(WindowLevel window, Span<byte> target)
    {
        if (target.Length < Width * Height * 4) throw new ArgumentException("書き込み先が小さすぎます。", nameof(target));
        for (int i = 0; i < Values.Length; i++)
        {
            byte g = window.ToGray(Values[i]);
            int o = i * 4;
            target[o] = g;
            target[o + 1] = g;
            target[o + 2] = g;
            target[o + 3] = 255;
        }
    }
}

/// <summary>断面を切り出す</summary>
public static class Mpr
{
    /// <summary>画面の右・下に対応する患者座標の向き（LPS: x = 左, y = 後ろ, z = 頭）</summary>
    public static (Vec3 Right, Vec3 Down) Axes(MprPlane plane) => plane switch
    {
        MprPlane.Axial => (Vec3.UnitX, Vec3.UnitY),     // 右 = 患者の左、下 = 背中側
        MprPlane.Coronal => (Vec3.UnitX, -Vec3.UnitZ),  // 右 = 患者の左、下 = 足側
        _ => (Vec3.UnitY, -Vec3.UnitZ),                 // 右 = 背中側（前が左）、下 = 足側
    };

    public static Vec3 Normal(MprPlane plane)
    {
        var (r, d) = Axes(plane);
        return Vec3.Cross(r, d);
    }

    /// <summary>断面を動かせる範囲（法線方向の位置 mm）と、1 枚ぶんの移動量</summary>
    public static (double Min, double Max, double Step) Range(Volume volume, MprPlane plane)
    {
        ArgumentNullException.ThrowIfNull(volume);
        var n = Normal(plane);
        var proj = volume.Corners().Select(c => Vec3.Dot(c, n)).ToList();
        // 法線方向に進んだとき、番地のどれかがちょうど 1 変わる距離（ガントリー傾斜でも正しい）
        var d = volume.Geometry.PatientDirectionToIndex(n);
        double maxComponent = Math.Max(Math.Abs(d.X), Math.Max(Math.Abs(d.Y), Math.Abs(d.Z)));
        double step = maxComponent > 1e-9 ? 1 / maxComponent : volume.Geometry.MinSpacing;
        return (proj.Min(), proj.Max(), step);
    }

    /// <param name="through">断面が通る点（患者座標）。3 つの断面の交点（十字の位置）</param>
    /// <param name="slabThickness">厚み（mm）。0 なら 1 枚の断面</param>
    public static MprSlice Reslice(Volume volume, MprPlane plane, Vec3 through, SlabMode mode = SlabMode.Thin, double slabThickness = 0, double? pixelSize = null)
    {
        ArgumentNullException.ThrowIfNull(volume);
        var (right, down) = Axes(plane);
        var normal = Vec3.Cross(right, down);
        var g = volume.Geometry;
        double px = pixelSize ?? Math.Min(g.SpacingX, g.SpacingY);
        px = Math.Max(px, 0.05);

        var corners = volume.Corners().ToList();
        double rMin = corners.Min(c => Vec3.Dot(c, right)), rMax = corners.Max(c => Vec3.Dot(c, right));
        double dMin = corners.Min(c => Vec3.Dot(c, down)), dMax = corners.Max(c => Vec3.Dot(c, down));
        int width = Math.Clamp((int)Math.Ceiling((rMax - rMin) / px) + 1, 1, 4096);
        int height = Math.Clamp((int)Math.Ceiling((dMax - dMin) / px) + 1, 1, 4096);
        double n0 = Vec3.Dot(through, normal);
        var origin = right * rMin + down * dMin + normal * n0;

        // 厚みの中の層（法線方向に、ボクセルより細かい間隔で）
        double layerStep = Math.Max(Range(volume, plane).Step * 0.5, 0.1);
        int half = mode == SlabMode.Thin || slabThickness <= 0 ? 0 : (int)Math.Floor(slabThickness / 2 / layerStep);
        var layers = Enumerable.Range(-half, 2 * half + 1).Select(k => k * layerStep).ToArray();

        var values = new float[width * height];
        var idxOrigin = g.PatientToIndex(origin);
        var di = g.PatientDirectionToIndex(right * px);
        var dj = g.PatientDirectionToIndex(down * px);
        var dn = g.PatientDirectionToIndex(normal);
        Parallel.For(0, height, j =>
        {
            for (int i = 0; i < width; i++)
            {
                var b = idxOrigin + di * i + dj * j;
                if (layers.Length == 1)
                {
                    values[j * width + i] = volume.Sample(b.X, b.Y, b.Z);
                    continue;
                }
                float max = float.MinValue, sum = 0;
                foreach (double t in layers)
                {
                    var q = b + dn * t;
                    float v = volume.Sample(q.X, q.Y, q.Z);
                    if (v > max) max = v;
                    sum += v;
                }
                values[j * width + i] = mode == SlabMode.Mip ? max : sum / layers.Length;
            }
        });
        return new MprSlice(plane, width, height, values, px, origin, right, down);
    }

    /// <summary>向きを表す文字（R/L, A/P, S/I）。画面の端に表示する</summary>
    public static string OrientationLetter(Vec3 direction)
    {
        double ax = Math.Abs(direction.X), ay = Math.Abs(direction.Y), az = Math.Abs(direction.Z);
        if (ax >= ay && ax >= az) return direction.X > 0 ? "L" : "R";
        if (ay >= az) return direction.Y > 0 ? "P" : "A";
        return direction.Z > 0 ? "S" : "I";
    }
}
