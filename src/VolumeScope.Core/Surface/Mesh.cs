using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using VolumeScope.Core.Geometry;

namespace VolumeScope.Core.Surface;

/// <summary>
/// 三角形の面の集まり。座標は患者座標（mm）。頂点は共有する（隣の三角形と同じ頂点を使う）。
/// </summary>
public sealed class Mesh
{
    public Mesh(float[] positions, int[] indices, float[]? normals = null)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(indices);
        if (positions.Length % 3 != 0 || indices.Length % 3 != 0) throw new ArgumentException("座標・番号の数が 3 の倍数ではありません。");
        Positions = positions;
        Indices = indices;
        Normals = normals ?? ComputeNormals(positions, indices);
    }

    public float[] Positions { get; }

    public int[] Indices { get; }

    public float[] Normals { get; }

    public int VertexCount => Positions.Length / 3;

    public int TriangleCount => Indices.Length / 3;

    public static Mesh Empty { get; } = new([], []);

    public Vec3 Vertex(int i) => new(Positions[i * 3], Positions[i * 3 + 1], Positions[i * 3 + 2]);

    /// <summary>表面積（mm²）</summary>
    public double SurfaceArea()
    {
        double sum = 0;
        for (int t = 0; t < TriangleCount; t++)
        {
            var (a, b, c) = Triangle(t);
            sum += Vec3.Cross(b - a, c - a).Length / 2;
        }
        return sum;
    }

    /// <summary>囲まれた体積（mm³）。閉じた面で、外向きの三角形なら正になる</summary>
    public double SignedVolume()
    {
        double sum = 0;
        for (int t = 0; t < TriangleCount; t++)
        {
            var (a, b, c) = Triangle(t);
            sum += Vec3.Dot(a, Vec3.Cross(b, c)) / 6;
        }
        return sum;
    }

    public (Vec3 Min, Vec3 Max) Bounds()
    {
        if (VertexCount == 0) return (Vec3.Zero, Vec3.Zero);
        double[] min = [double.MaxValue, double.MaxValue, double.MaxValue], max = [double.MinValue, double.MinValue, double.MinValue];
        for (int i = 0; i < Positions.Length; i++)
        {
            min[i % 3] = Math.Min(min[i % 3], Positions[i]);
            max[i % 3] = Math.Max(max[i % 3], Positions[i]);
        }
        return (new Vec3(min[0], min[1], min[2]), new Vec3(max[0], max[1], max[2]));
    }

    private (Vec3 A, Vec3 B, Vec3 C) Triangle(int t) => (Vertex(Indices[t * 3]), Vertex(Indices[t * 3 + 1]), Vertex(Indices[t * 3 + 2]));

    /// <summary>三角形の向き（表裏）を反対にする</summary>
    public Mesh Flipped()
    {
        var idx = (int[])Indices.Clone();
        for (int t = 0; t < idx.Length; t += 3) (idx[t + 1], idx[t + 2]) = (idx[t + 2], idx[t + 1]);
        var n = Normals.Select(v => -v).ToArray();
        return new Mesh(Positions, idx, n);
    }

    /// <summary>頂点の向き（面の向きを面積で重みづけした平均）</summary>
    internal static float[] ComputeNormals(float[] p, int[] idx)
    {
        var n = new float[p.Length];
        for (int t = 0; t < idx.Length; t += 3)
        {
            int a = idx[t] * 3, b = idx[t + 1] * 3, c = idx[t + 2] * 3;
            float ux = p[b] - p[a], uy = p[b + 1] - p[a + 1], uz = p[b + 2] - p[a + 2];
            float vx = p[c] - p[a], vy = p[c + 1] - p[a + 1], vz = p[c + 2] - p[a + 2];
            float nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            foreach (int k in new[] { a, b, c })
            {
                n[k] += nx;
                n[k + 1] += ny;
                n[k + 2] += nz;
            }
        }
        for (int i = 0; i < n.Length; i += 3)
        {
            float l = MathF.Sqrt(n[i] * n[i] + n[i + 1] * n[i + 1] + n[i + 2] * n[i + 2]);
            if (l > 0)
            {
                n[i] /= l;
                n[i + 1] /= l;
                n[i + 2] /= l;
            }
        }
        return n;
    }

    // ---- 書き出し

    /// <summary>STL（バイナリ）。単位は mm。3D プリンターや CAD で読める</summary>
    public void WriteStl(Stream stream, string header = "VolumeScope")
    {
        ArgumentNullException.ThrowIfNull(stream);
        var head = new byte[80];
        Encoding.ASCII.GetBytes(header.Length > 80 ? header[..80] : header).CopyTo(head, 0);
        stream.Write(head);
        Span<byte> buf = stackalloc byte[50];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, (uint)TriangleCount);
        stream.Write(buf[..4]);
        for (int t = 0; t < TriangleCount; t++)
        {
            var (a, b, c) = Triangle(t);
            var n = Vec3.Cross(b - a, c - a).Normalized();
            int o = 0;
            foreach (var v in new[] { n, a, b, c })
            {
                BinaryPrimitives.WriteSingleLittleEndian(buf[o..], (float)v.X);
                BinaryPrimitives.WriteSingleLittleEndian(buf[(o + 4)..], (float)v.Y);
                BinaryPrimitives.WriteSingleLittleEndian(buf[(o + 8)..], (float)v.Z);
                o += 12;
            }
            buf[48] = 0;
            buf[49] = 0;
            stream.Write(buf);
        }
    }

    /// <summary>OBJ（テキスト）。頂点の向きつき</summary>
    public void WriteObj(TextWriter writer, string name = "surface")
    {
        ArgumentNullException.ThrowIfNull(writer);
        var ci = CultureInfo.InvariantCulture;
        writer.WriteLine("# VolumeScope — units: mm (DICOM patient coordinates, LPS)");
        writer.WriteLine($"o {name}");
        for (int i = 0; i < Positions.Length; i += 3)
            writer.WriteLine(string.Create(ci, $"v {Positions[i]:0.###} {Positions[i + 1]:0.###} {Positions[i + 2]:0.###}"));
        for (int i = 0; i < Normals.Length; i += 3)
            writer.WriteLine(string.Create(ci, $"vn {Normals[i]:0.####} {Normals[i + 1]:0.####} {Normals[i + 2]:0.####}"));
        for (int t = 0; t < Indices.Length; t += 3)
        {
            int a = Indices[t] + 1, b = Indices[t + 1] + 1, c = Indices[t + 2] + 1;
            writer.WriteLine(string.Create(ci, $"f {a}//{a} {b}//{b} {c}//{c}"));
        }
    }
}
