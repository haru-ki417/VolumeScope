using System.Buffers.Binary;
using VolumeScope.Core.Geometry;
using VolumeScope.Core.Surface;
using VolumeScope.Core.Volumes;

namespace VolumeScope.Tests;

public class MeshTests
{
    private static Mesh Sphere(double r = 15, int n = 44) =>
        MarchingCubes.Extract(SurfaceTests.Field(n, 1.0, p => 500 + (r - Vec3.Distance(p, new Vec3(n / 2.0, n / 2.0, n / 2.0))) * 100), 500);

    [Fact]
    public void 小さな破片だけを取り除く()
    {
        // 大きな球 + 離れた小さな点（雑音）
        var v = SurfaceTests.Field(50, 1.0, p =>
            Vec3.Distance(p, new Vec3(20, 20, 20)) < 14 ? 1000 :
            Vec3.Distance(p, new Vec3(42, 42, 42)) < 1.2 ? 1000 : -1000);
        var mesh = MarchingCubes.Extract(v, 0);
        Assert.Equal(2, MeshTools.CountComponents(mesh));
        var cleaned = MeshTools.RemoveSmallComponents(mesh);
        Assert.Equal(1, MeshTools.CountComponents(cleaned));
        SurfaceTests.AssertWatertight(cleaned);
        Assert.True(cleaned.Bounds().Max.X < 36);
    }

    [Fact]
    public void なめらかにしても_体積はほとんど変わらず_閉じたまま()
    {
        var mesh = Sphere();
        var smooth = MeshTools.TaubinSmooth(mesh, 10);
        Assert.Equal(mesh.SignedVolume(), smooth.SignedVolume(), mesh.SignedVolume() * 0.02);
        SurfaceTests.AssertWatertight(smooth);
    }

    [Fact]
    public void STL_は_決まった形式で書き出される()
    {
        var mesh = Sphere(8, 24);
        using var ms = new MemoryStream();
        mesh.WriteStl(ms);
        var bytes = ms.ToArray();
        Assert.Equal(84 + 50 * mesh.TriangleCount, bytes.Length);
        Assert.Equal((uint)mesh.TriangleCount, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(80)));
        // 1 つ目の三角形の頂点 a が、面の頂点と一致する
        float ax = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(84 + 12));
        Assert.Equal(mesh.Positions[mesh.Indices[0] * 3], ax);
    }

    [Fact]
    public void OBJ_は_頂点と面の数が一致する()
    {
        var mesh = Sphere(6, 18);
        using var sw = new StringWriter();
        mesh.WriteObj(sw);
        var lines = sw.ToString().Split('\n');
        Assert.Equal(mesh.VertexCount, lines.Count(l => l.StartsWith("v ", StringComparison.Ordinal)));
        Assert.Equal(mesh.TriangleCount, lines.Count(l => l.StartsWith("f ", StringComparison.Ordinal)));
        Assert.DoesNotContain(lines.Where(l => l.StartsWith('v')), l => l.Contains(',', StringComparison.Ordinal)); // 小数点はカンマにならない
    }

    [Fact]
    public void 見本の模型から骨の面を作ると_背骨と肋骨が別々の部分になる()
    {
        var v = DemoPhantom.Create(spacing: 2);
        var result = SurfaceBuilder.Build(v, new SurfaceOptions(TissuePresets.Bone.ThresholdHu));
        Assert.True(result.Mesh.TriangleCount > 10_000);
        Assert.True(result.Components >= 5, $"部分の数 {result.Components}");
        Assert.True(result.VolumeMl > 10);
        SurfaceTests.AssertWatertight(result.Mesh);
    }

    [Fact]
    public void 三角形が多すぎるときは_自動で粗くする()
    {
        var v = DemoPhantom.Create(spacing: 2);
        var result = SurfaceBuilder.Build(v, new SurfaceOptions(TissuePresets.Skin.ThresholdHu, RemoveFragments: false, SmoothIterations: 0) { TriangleBudget = 5_000 });
        Assert.True(result.DownsampleFactor > 1);
    }
}
