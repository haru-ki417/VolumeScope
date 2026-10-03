using System.Globalization;
using VolumeScope.Core.Geometry;
using VolumeScope.Core.Measurement;
using VolumeScope.Core.Mpr;

namespace VolumeScope.Web.State;

public enum ToolMode
{
    /// <summary>十字を動かす（3 つの断面の交点を決める）</summary>
    Crosshair,

    /// <summary>2 点間の距離</summary>
    Distance,

    /// <summary>円の範囲の CT 値</summary>
    Circle,
}

/// <summary>断面の上に置いた計測。置いた断面（向きと位置）でだけ表示する</summary>
public abstract class MeasurementItem(MprPlane plane, double planePosition, int number)
{
    public MprPlane Plane { get; } = plane;

    /// <summary>断面の法線方向の位置（mm）</summary>
    public double PlanePosition { get; } = planePosition;

    public int Number { get; } = number;

    public string PlaneName => Names.Plane(Plane);

    public abstract string Kind { get; }

    public abstract string Value { get; }

    public abstract string Detail { get; }

    public string Where => string.Create(CultureInfo.InvariantCulture, $"{PlaneName} {PlanePosition:0.0} mm");
}

public sealed class DistanceMeasurement(MprPlane plane, double planePosition, int number, Vec3 a, Vec3 b) : MeasurementItem(plane, planePosition, number)
{
    public Vec3 A { get; } = a;

    public Vec3 B { get; } = b;

    public double Millimeters => Measurements.Distance(A, B);

    public override string Kind => "距離";

    public override string Value => string.Create(CultureInfo.InvariantCulture, $"{Millimeters:0.0} mm");

    public override string Detail => Where;
}

public sealed class CircleMeasurement(MprPlane plane, double planePosition, int number, Vec3 center, double radiusMm, RoiStatistics stats) : MeasurementItem(plane, planePosition, number)
{
    public Vec3 Center { get; } = center;

    public double RadiusMm { get; } = radiusMm;

    public RoiStatistics Stats { get; } = stats;

    public override string Kind => "円";

    public override string Value => string.Create(CultureInfo.InvariantCulture, $"{Stats.Mean:0} HU");

    public override string Detail => string.Create(CultureInfo.InvariantCulture,
        $"SD {Stats.StandardDeviation:0}、最小 {Stats.Min:0}、最大 {Stats.Max:0}、{Stats.AreaMm2 / 100:0.00} cm²（{Where}）");
}

public static class Names
{
    public static string Plane(MprPlane p) => p switch
    {
        MprPlane.Axial => "横断",
        MprPlane.Coronal => "冠状断",
        _ => "矢状断",
    };

    public static string Color(MprPlane p) => p switch
    {
        MprPlane.Axial => "#E3A548",
        MprPlane.Coronal => "#3FB8AF",
        _ => "#E06C8A",
    };

    /// <summary>断面を送る向きの名前（スライダーの見出し）</summary>
    public static string Axis(MprPlane p) => p switch
    {
        MprPlane.Axial => "頭尾",
        MprPlane.Coronal => "前後",
        _ => "左右",
    };
}
