using VolumeScope.Core.Geometry;
using VolumeScope.Core.Mpr;

namespace VolumeScope.Core.Measurement;

/// <summary>円形の範囲の CT 値の統計</summary>
public sealed record RoiStatistics(double Mean, double StandardDeviation, double Min, double Max, int PixelCount, double AreaMm2);

public static class Measurements
{
    /// <summary>2 点間の距離（mm）</summary>
    public static double Distance(Vec3 a, Vec3 b) => Vec3.Distance(a, b);

    /// <summary>断面上の円（中心と半径は画素単位）の中の CT 値</summary>
    public static RoiStatistics CircleRoi(MprSlice slice, double centerX, double centerY, double radiusPixels)
    {
        ArgumentNullException.ThrowIfNull(slice);
        var values = new List<double>();
        int r = (int)Math.Ceiling(radiusPixels);
        for (int y = (int)centerY - r; y <= (int)centerY + r + 1; y++)
            for (int x = (int)centerX - r; x <= (int)centerX + r + 1; x++)
            {
                if ((uint)x >= (uint)slice.Width || (uint)y >= (uint)slice.Height) continue;
                double dx = x - centerX, dy = y - centerY;
                if (dx * dx + dy * dy <= radiusPixels * radiusPixels) values.Add(slice[x, y]);
            }
        if (values.Count == 0) return new RoiStatistics(0, 0, 0, 0, 0, 0);
        double mean = values.Average();
        double sd = values.Count > 1 ? Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1)) : 0;
        return new RoiStatistics(mean, sd, values.Min(), values.Max(), values.Count, values.Count * slice.PixelSize * slice.PixelSize);
    }
}
