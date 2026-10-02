using VolumeScope.Core.Geometry;
using VolumeScope.Core.Surface;
using VolumeScope.Core.Volumes;

namespace VolumeScope.Tests;

public class SurfaceTests
{
    internal static Volume Field(int n, double spacing, Func<Vec3, double> f, Vec3? origin = null)
    {
        var g = new VolumeGeometry(origin ?? Vec3.Zero, Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ, spacing, spacing, spacing);
        var data = new short[n * n * n];
        for (int z = 0; z < n; z++)
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                    data[z * n * n + y * n + x] = (short)Math.Round(f(g.IndexToPatient(new Vec3(x, y, z))));
        return new Volume(n, n, n, data, g);
    }

    /// <summary>
    /// 閉じた面かどうか: 向きつきの辺 (a→b) がちょうど 1 回ずつ現れ、逆向き (b→a) もちょうど 1 回ある
    /// （= すべての辺がちょうど 2 つの三角形に共有され、表裏の向きがそろっている）
    /// </summary>
    internal static void AssertWatertight(Mesh m)
    {
        var directed = new Dictionary<(int, int), int>();
        for (int t = 0; t < m.Indices.Length; t += 3)
            for (int k = 0; k < 3; k++)
            {
                int a = m.Indices[t + k], b = m.Indices[t + (k + 1) % 3];
                Assert.NotEqual(a, b);
                directed[(a, b)] = directed.GetValueOrDefault((a, b)) + 1;
            }
        foreach (var ((a, b), count) in directed)
        {
            Assert.True(count == 1, $"辺 {a}->{b} が {count} 回あります");
            Assert.True(directed.ContainsKey((b, a)), $"辺 {a}-{b} に隣の三角形がありません（穴）");
        }
    }

    [Fact]
    public void 球は_閉じた面になり_体積と表面積が理論値に近い()
    {
        double r = 20;
        var v = Field(56, 1.0, p => Vec3.Distance(p, new Vec3(27.3, 27.6, 27.1)) < r ? 1000 : 0);
        // 0/1000 の境界は 500 で切る
        var smooth = Field(56, 1.0, p => 500 + (r - Vec3.Distance(p, new Vec3(27.3, 27.6, 27.1))) * 100);
        var mesh = MarchingCubes.Extract(smooth, 500);
        AssertWatertight(mesh);

        double expectedVolume = 4.0 / 3 * Math.PI * r * r * r, expectedArea = 4 * Math.PI * r * r;
        Assert.Equal(expectedVolume, mesh.SignedVolume(), expectedVolume * 0.01);
        Assert.Equal(expectedArea, mesh.SurfaceArea(), expectedArea * 0.02);
        // オイラー標数 V - E + F = 2（穴のない 1 つの閉じた面）
        int edges = mesh.TriangleCount * 3 / 2;
        Assert.Equal(2, mesh.VertexCount - edges + mesh.TriangleCount);

        // 二値の画像でも閉じた面
        AssertWatertight(MarchingCubes.Extract(v, 500));
    }

    [Fact]
    public void 雑音の多い画像でも_穴のない面になる()
    {
        var random = new Random(3);
        var v = Field(30, 1.0, _ => random.Next(-200, 200));
        var mesh = MarchingCubes.Extract(v, 0);
        Assert.True(mesh.TriangleCount > 1000);
        AssertWatertight(mesh);
    }

    [Fact]
    public void 画像の端に接する物体も_閉じた面になる()
    {
        // 端まで続く板（画像の外は「閾値未満」として閉じる）
        var v = Field(20, 2.0, p => p.Z < 10 ? 1000 : -1000);
        var mesh = MarchingCubes.Extract(v, 0);
        AssertWatertight(mesh);
        Assert.True(mesh.SignedVolume() > 0);
    }

    [Fact]
    public void ボクセルの大きさと位置が_患者座標の面に反映される()
    {
        var g = new VolumeGeometry(new Vec3(-50, 10, 200), Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ, 0.5, 0.5, 2.0);
        int n = 40;
        var data = new short[n * n * n];
        var center = new Vec3(-40, 20, 240);
        for (int z = 0; z < n; z++)
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                    data[z * n * n + y * n + x] = (short)(500 + (8 - Vec3.Distance(g.IndexToPatient(new Vec3(x, y, z)), center)) * 100);
        var mesh = MarchingCubes.Extract(new Volume(n, n, n, data, g), 500);
        var (min, max) = mesh.Bounds();
        Assert.Equal(center.X - 8, min.X, 0.3);
        Assert.Equal(center.Z + 8, max.Z, 0.3);
    }

    [Fact]
    public void 何もなければ_空の面()
    {
        var v = Field(10, 1.0, _ => -1000);
        Assert.Equal(0, MarchingCubes.Extract(v, 0).TriangleCount);
    }

    [Fact]
    public void 辺の表は_三角形の表と食い違わない()
    {
        // 元の試作では辺の表が誤っていた。三角形の表から作るので、使う辺が必ず含まれる
        for (int c = 0; c < 256; c++)
            for (int i = 0; i < 16 && MarchingCubes.TriTable[c, i] >= 0; i++)
                Assert.NotEqual(0, MarchingCubes.EdgeMask[c] & (1 << MarchingCubes.TriTable[c, i]));
        Assert.Equal(0, MarchingCubes.EdgeMask[0]);
        Assert.Equal(0, MarchingCubes.EdgeMask[255]);
    }
}
