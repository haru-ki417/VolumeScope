using VolumeScope.Core.Geometry;
using VolumeScope.Core.Rendering;
using VolumeScope.Core.Volumes;

namespace VolumeScope.Tests;

public class RenderTests
{
    private static readonly Volume Phantom = DemoPhantom.Create(spacing: 3);
    private static readonly VolumeRenderer Renderer = new(Phantom);

    private static (byte B, byte G, byte R) Pixel(byte[] img, int w, int x, int y) => (img[(y * w + x) * 4], img[(y * w + x) * 4 + 1], img[(y * w + x) * 4 + 2]);

    [Fact]
    public void 正面から見ると_患者の右が画面の左になる()
    {
        var (forward, right, up) = Camera.Anterior.Basis();
        Assert.Equal(Vec3.UnitY, forward);
        Assert.Equal(Vec3.UnitX, right); // +x は患者の左
        Assert.Equal(Vec3.UnitZ, up);
        var (f2, r2, u2) = new Camera(37, 21).Basis();
        Assert.Equal(0, Vec3.Dot(f2, r2), 1e-9);
        Assert.Equal(0, Vec3.Dot(f2, u2), 1e-9);
        Assert.Equal(1, r2.Length, 1e-9);
    }

    [Fact]
    public void MIP_では_背骨が明るく_外側は背景()
    {
        const int n = 96;
        var img = Renderer.Render(Camera.Anterior, new RenderSettings { Transfer = TransferPresets.Bone, Mode = RenderMode.Mip }, n, n);
        var corner = Pixel(img, n, 1, 1);
        var center = Pixel(img, n, n / 2, n / 2);
        Assert.True(center.R > corner.R + 80, $"中央 {center} 角 {corner}");
        Assert.True(corner.R < 60 && corner.B > corner.R, $"角 {corner}"); // 背景の濃紺
    }

    [Fact]
    public void 何も見えない設定なら_背景だけになる()
    {
        var none = new TransferFunction("none", [new(-1024, 0, 0, 0, 0), new(3071, 0, 0, 0, 0)]);
        Assert.Equal(TransferFunction.MaxHu + 1, none.MinVisibleHu);
        const int n = 48;
        var img = Renderer.Render(Camera.Anterior, new RenderSettings { Transfer = none }, n, n);
        var bg = Renderer.Render(Camera.Anterior, new RenderSettings { Transfer = none, Crop = new CropBox(0.4, 0.4, 0, 1, 0, 1) }, n, n);
        Assert.Equal(bg, img);
    }

    [Fact]
    public void 骨の表示は_毎回同じ画像になり_体の中に色がつく()
    {
        const int n = 64;
        var s = new RenderSettings { Transfer = TransferPresets.Bone };
        var a = Renderer.Render(new Camera(30, 10), s, n, n);
        var b = Renderer.Render(new Camera(30, 10), s, n, n);
        Assert.Equal(a, b);
        int bright = Enumerable.Range(0, n * n).Count(i => a[i * 4 + 2] > 150);
        Assert.InRange(bright, n * n / 20, n * n / 2);
    }

    [Fact]
    public void 前半分を切り取ると_見える骨が変わる()
    {
        const int n = 64;
        var full = Renderer.Render(Camera.Anterior, new RenderSettings { Transfer = TransferPresets.Bone }, n, n);
        var cut = Renderer.Render(Camera.Anterior, new RenderSettings { Transfer = TransferPresets.Bone, Crop = new CropBox(MinY: 0.75) }, n, n);
        Assert.NotEqual(full, cut);
    }

    [Fact]
    public void 伝達関数は点のあいだを直線でつなぐ()
    {
        var tf = new TransferFunction("t", [new(0, 0, 0, 0, 0), new(100, 1, 0.5f, 0, 1)]);
        var (r, g, _, a) = tf.Evaluate(25);
        Assert.Equal(0.25f, r, 1e-6f);
        Assert.Equal(0.125f, g, 1e-6f);
        Assert.Equal(0.25f, a, 1e-6f);
        Assert.Equal(1, tf.MinVisibleHu);
        Assert.Equal(51, tf.Shifted(50).MinVisibleHu);
    }
}

public class RegressionTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(33, 17)]
    [InlineData(-70, 40)]
    public void 空の領域を飛ばしても_描かれる画像は同じ(double azimuth, double elevation)
    {
        var v = DemoPhantom.Create(spacing: 3);
        var fast = new VolumeRenderer(v);
        var exact = new VolumeRenderer(v) { SkipEmptySpace = false };
        foreach (var mode in new[] { RenderMode.Dvr, RenderMode.Mip })
        {
            var s = new RenderSettings { Transfer = TransferPresets.Bone, Mode = mode };
            Assert.Equal(exact.Render(new Camera(azimuth, elevation), s, 72, 72), fast.Render(new Camera(azimuth, elevation), s, 72, 72));
        }
    }

    [Fact]
    public void ガントリー傾斜があっても_断面の送り幅は_1スライスぶん()
    {
        double tilt = 20 * Math.PI / 180;
        var slice = new Vec3(0, Math.Sin(tilt), Math.Cos(tilt));
        var g = new VolumeGeometry(Vec3.Zero, Vec3.UnitX, Vec3.UnitY, slice, 0.7, 0.7, 1.25);
        var v = TestDicom.Small(g, w: 30, h: 30, d: 30);
        var (_, _, step) = Core.Mpr.Mpr.Range(v, Core.Mpr.MprPlane.Axial);
        // 頭尾方向に step 進むと、スライスの番地がちょうど 1 変わる
        Assert.Equal(1.0, Math.Abs(g.PatientDirectionToIndex(Vec3.UnitZ * step).Z), 1e-9);
        Assert.Equal(1.25 * Math.Cos(tilt), step, 1e-9);
    }

    [Fact]
    public void 画像の端の外側半ボクセルは_端の値のまま()
    {
        var v = TestDicom.Small(w: 10, h: 10, d: 4, value: (_, _, _) => 100);
        Assert.Equal(100, v.Sample(9.25, 5, 2), 1e-3);
        Assert.Equal(100, v.Sample(-0.4, 5, 2), 1e-3);
        Assert.Equal(Volume.Outside, v.Sample(10, 5, 2));
    }

    [Fact]
    public void 画像の端で切れる面にも_面積ゼロの三角形ができない()
    {
        var v = SurfaceTests.Field(20, 2.0, p => p.Z < 10 ? 1000 : -1000);
        var mesh = Core.Surface.MarchingCubes.Extract(v, 0);
        for (int t = 0; t < mesh.TriangleCount; t++)
        {
            var a = mesh.Vertex(mesh.Indices[t * 3]);
            var b = mesh.Vertex(mesh.Indices[t * 3 + 1]);
            var c = mesh.Vertex(mesh.Indices[t * 3 + 2]);
            Assert.True(Vec3.Cross(b - a, c - a).Length > 1e-6, $"三角形 {t} の面積が 0");
        }
    }

    [Fact]
    public void なめらかにする処理は取り消せる()
    {
        var v = SurfaceTests.Field(30, 1.0, p => 500 + (10 - Vec3.Distance(p, new Vec3(15, 15, 15))) * 100);
        var mesh = Core.Surface.MarchingCubes.Extract(v, 500);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => Core.Surface.MeshTools.TaubinSmooth(mesh, 5, cancellationToken: cts.Token));
    }
}
