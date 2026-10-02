using VolumeScope.Core.Dicom;
using VolumeScope.Core.Geometry;
using VolumeScope.Core.Volumes;

namespace VolumeScope.Tests;

public sealed class LoaderTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "vs-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }

    private Volume RoundTrip(Volume v, TestDicom.Options? o = null)
    {
        TestDicom.Write(v, dir, o);
        var series = SeriesScanner.Scan([dir], cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(series);
        return VolumeLoader.Load(series[0], cancellationToken: TestContext.Current.CancellationToken);
    }

    private static void AssertSameVoxels(Volume expected, Volume actual)
    {
        Assert.Equal((expected.Width, expected.Height, expected.Depth), (actual.Width, actual.Height, actual.Depth));
        Assert.Equal(expected.Data, actual.Data);
    }

    [Fact]
    public void ファイルの順番がばらばらでも_位置の順に正しく積み重ねる()
    {
        var v = TestDicom.Small();
        var loaded = RoundTrip(v, new TestDicom.Options { Shuffle = true, ReverseInstanceNumbers = true });
        AssertSameVoxels(v, loaded);
        Assert.Equal(2.5, loaded.Geometry.SpacingZ, 1e-6);
        Assert.Equal(0.8, loaded.Geometry.SpacingX, 1e-9);
        Assert.Equal(0.7, loaded.Geometry.SpacingY, 1e-9);
        Assert.Equal(new Vec3(-10, -20, 30), loaded.Geometry.Origin);
    }

    [Fact]
    public void 位置決め画像と重複を除き_注意として知らせる()
    {
        var v = TestDicom.Small();
        var loaded = RoundTrip(v, new TestDicom.Options { AddLocalizer = true, AddDuplicate = true });
        AssertSameVoxels(v, loaded);
        Assert.Contains(loaded.Info.Warnings, w => w.Contains("LOCALIZER", StringComparison.Ordinal));
        Assert.Contains(loaded.Info.Warnings, w => w.Contains("同じ位置", StringComparison.Ordinal));
    }

    [Fact]
    public void 足から頭ではなく頭から足の順に撮った画像も_患者座標は正しい()
    {
        // スライスの向きが -z（頭から足へ）の画像
        var g = new VolumeGeometry(new Vec3(0, 0, 100), Vec3.UnitX, Vec3.UnitY, -Vec3.UnitZ, 1, 1, 3);
        var v = TestDicom.Small(g);
        var loaded = RoundTrip(v);
        // 読み込むと断面に垂直な向き（+z）の順に並び替わる。同じ患者座標の値が同じであればよい
        foreach (var p in new[] { new Vec3(5, 6, 97), new Vec3(10, 3, 76), new Vec3(1, 15, 100) })
            Assert.Equal(v.SampleAt(p), loaded.SampleAt(p), 0.01);
        Assert.Equal(Vec3.UnitZ, loaded.Geometry.SliceDirection);
    }

    [Fact]
    public void 斜めの断面とガントリー傾斜にも対応する()
    {
        double a = 20 * Math.PI / 180;
        var row = new Vec3(Math.Cos(a), Math.Sin(a), 0);
        var col = new Vec3(-Math.Sin(a), Math.Cos(a), 0);
        // スライスの並ぶ向きが断面に垂直でない（y 方向に傾く）
        var slice = new Vec3(0, Math.Sin(15 * Math.PI / 180), Math.Cos(15 * Math.PI / 180));
        var g = new VolumeGeometry(new Vec3(3, 4, 5), row, col, slice, 0.9, 0.9, 2);
        var v = TestDicom.Small(g);
        var loaded = RoundTrip(v);
        foreach (var idx in new[] { new Vec3(3, 4, 5), new Vec3(20, 1, 9), new Vec3(0, 19, 0) })
        {
            var p = g.IndexToPatient(idx);
            Assert.Equal(v.SampleAt(p), loaded.SampleAt(p), 0.01);
        }
        Assert.Contains(loaded.Info.Warnings, w => w.Contains("ガントリー傾斜", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, 16)]
    [InlineData(false, 12)]
    [InlineData(true, 16)]
    public void 画素の形式によらず_CT値に直して読む(bool unsignedPixels, int bitsStored)
    {
        var v = TestDicom.Small(value: (x, y, z) => (short)(-1000 + x * 60 + z * 7)); // -1000〜約 450 HU（負の値を含む）
        var loaded = RoundTrip(v, new TestDicom.Options { UnsignedWithIntercept = unsignedPixels, BitsStored = bitsStored });
        AssertSameVoxels(v, loaded);
    }

    [Fact]
    public void 位置の情報がない古い形式は_画像番号の順に並べて注意を出す()
    {
        var v = TestDicom.Small();
        var loaded = RoundTrip(v, new TestDicom.Options { OmitPosition = true, Shuffle = true });
        AssertSameVoxels(v, loaded);
        Assert.Contains(loaded.Info.Warnings, w => w.Contains("画像番号の順", StringComparison.Ordinal));
    }

    [Fact]
    public void 複数のシリーズを見分ける()
    {
        var v = TestDicom.Small();
        TestDicom.Write(v, Path.Combine(dir, "a"), new TestDicom.Options { Description = "A" });
        TestDicom.Write(TestDicom.Small(d: 5), Path.Combine(dir, "b"), new TestDicom.Options { Description = "B" });
        var series = SeriesScanner.Scan([dir], cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(["A", "B"], series.Select(s => s.SeriesDescription));
        Assert.Equal([12, 5], series.Select(s => s.ImageCount));
        Assert.Equal("TEST PHANTOM", series[0].PatientName);
    }

    [Fact]
    public void DICOM_でないファイルだけなら_空の一覧()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.txt"), "hello");
        File.WriteAllBytes(Path.Combine(dir, "b.dcm"), new byte[300]);
        Assert.Empty(SeriesScanner.Scan([dir], cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void 見本の模型は_想定した組織の値を持つ()
    {
        var v = DemoPhantom.Create(spacing: 4);
        Assert.InRange(v.SampleAt(new Vec3(-105, -85, -95)), -1001, -999); // 体の外（空気）
        Assert.InRange(v.SampleAt(new Vec3(-45, 0, 15)), -900, -800);    // 右肺
        Assert.InRange(v.SampleAt(new Vec3(12, 22, 0)), 200, 300);       // 大動脈
        Assert.InRange(v.SampleAt(DemoPhantom.NoduleCenter), 20, 100);   // 結節
        Assert.Contains("人工", v.Info.Warnings[0], StringComparison.Ordinal);
    }

    [Fact]
    public void 縮小すると_大きさと位置が対応する()
    {
        var v = TestDicom.Small(w: 24, h: 20, d: 12);
        var s = v.Downsample(2);
        Assert.Equal((12, 10, 6), (s.Width, s.Height, s.Depth));
        Assert.Equal(1.6, s.Geometry.SpacingX, 1e-9);
        // 縮小後の番地 0 の中心は、元の番地 0 と 1 の中間
        Assert.Equal(v.Geometry.IndexToPatient(new Vec3(0.5, 0.5, 0.5)), s.Geometry.Origin);
        // 線形に変わる値の平均は、中心の値と同じ
        Assert.Equal(v.Sample(2.5, 4.5, 6.5), s[1, 2, 3], 1.0);
    }
}
