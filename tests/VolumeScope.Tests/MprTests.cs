using VolumeScope.Core.Geometry;
using VolumeScope.Core.Measurement;
using VolumeScope.Core.Mpr;
using VolumeScope.Core.Volumes;

namespace VolumeScope.Tests;

public class MprTests
{
    private static readonly Volume Phantom = DemoPhantom.Create(spacing: 2);

    [Fact]
    public void 横断面は_元のスライスと同じ値になる()
    {
        var v = TestDicom.Small(new VolumeGeometry(Vec3.Zero, Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ, 1, 1, 1));
        int z = 5;
        var slice = Mpr.Reslice(v, MprPlane.Axial, new Vec3(0, 0, z), pixelSize: 1);
        Assert.Equal((v.Width, v.Height), (slice.Width, slice.Height));
        for (int y = 0; y < v.Height; y += 3)
            for (int x = 0; x < v.Width; x += 4)
                Assert.Equal(v[x, y, z], slice[x, y], 0.01);
    }

    [Theory]
    [InlineData(MprPlane.Axial, "L", "P")]
    [InlineData(MprPlane.Coronal, "L", "I")]
    [InlineData(MprPlane.Sagittal, "P", "I")]
    public void 画面の右と下の向きは_放射線科の慣例どおり(MprPlane plane, string right, string down)
    {
        var (r, d) = Mpr.Axes(plane);
        Assert.Equal(right, Mpr.OrientationLetter(r));
        Assert.Equal(down, Mpr.OrientationLetter(d));
    }

    [Fact]
    public void 画素と患者座標は相互に変換できる()
    {
        var slice = Mpr.Reslice(Phantom, MprPlane.Coronal, new Vec3(0, 10, 0));
        var p = slice.PixelToPatient(33.5, 21.25);
        var (x, y) = slice.PatientToPixel(p);
        Assert.Equal(33.5, x, 1e-6);
        Assert.Equal(21.25, y, 1e-6);
        Assert.Equal(10, p.Y, 1e-6); // 冠状断は前後の位置（y）が一定
    }

    [Fact]
    public void 断面の値は_患者座標の値と一致する()
    {
        foreach (var plane in Enum.GetValues<MprPlane>())
        {
            var through = DemoPhantom.NoduleCenter;
            var slice = Mpr.Reslice(Phantom, plane, through);
            var (x, y) = slice.PatientToPixel(through);
            Assert.Equal(Phantom.SampleAt(through), slice[(int)Math.Round(x), (int)Math.Round(y)], 40.0);
            Assert.InRange(slice[(int)Math.Round(x), (int)Math.Round(y)], 0, 120); // 結節の中
        }
    }

    [Fact]
    public void 厚みのある_MIP_は_薄い断面より値が小さくならない()
    {
        var thin = Mpr.Reslice(Phantom, MprPlane.Axial, new Vec3(0, 0, 20));
        var mip = Mpr.Reslice(Phantom, MprPlane.Axial, new Vec3(0, 0, 20), SlabMode.Mip, 20);
        int greater = 0;
        for (int i = 0; i < thin.Values.Length; i++)
        {
            Assert.True(mip.Values[i] >= thin.Values[i] - 0.01f);
            if (mip.Values[i] > thin.Values[i] + 50) greater++;
        }
        Assert.True(greater > 100); // 肋骨などが重なって見える
    }

    [Fact]
    public void 動かせる範囲は_画像の端から端まで()
    {
        var (min, max, step) = Mpr.Range(Phantom, MprPlane.Axial);
        Assert.Equal(-99, min, 0.01);
        Assert.Equal(99, max, 0.01);
        Assert.Equal(2, step, 1e-6);
    }

    [Fact]
    public void 円の範囲の_CT値の平均と面積()
    {
        var v = TestDicom.Small(new VolumeGeometry(Vec3.Zero, Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ, 0.5, 0.5, 1), w: 40, h: 40, d: 3, value: (_, _, _) => 37);
        var slice = Mpr.Reslice(v, MprPlane.Axial, new Vec3(0, 0, 1));
        var roi = Measurements.CircleRoi(slice, 20, 20, 8);
        Assert.Equal(37, roi.Mean, 1e-6);
        Assert.Equal(0, roi.StandardDeviation, 1e-6);
        Assert.Equal(Math.PI * 4 * 4, roi.AreaMm2, Math.PI * 16 * 0.1); // 半径 8 画素 = 4 mm
    }

    [Fact]
    public void 濃淡の変換()
    {
        var wl = new WindowLevel(40, 400);
        Assert.Equal(0, wl.ToGray(-200));
        Assert.Equal(255, wl.ToGray(300));
        Assert.Equal(128, wl.ToGray(40));
    }
}
