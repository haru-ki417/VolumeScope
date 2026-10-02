using VolumeScope.Core.Geometry;

namespace VolumeScope.Core.Rendering;

/// <summary>
/// 3D 表示のカメラ（平行投影）。方位角 0・仰角 0 で正面（患者の前）から見る。
/// 正面から見ると、患者の右が画面の左になる（放射線科の慣例と同じ）。
/// </summary>
public sealed record Camera(double AzimuthDeg = 0, double ElevationDeg = 0, double Zoom = 1, double PanX = 0, double PanY = 0)
{
    /// <summary>見ている向き・画面の右・画面の上（患者座標）</summary>
    public (Vec3 Forward, Vec3 Right, Vec3 Up) Basis()
    {
        double az = AzimuthDeg * Math.PI / 180, el = Math.Clamp(ElevationDeg, -89.9, 89.9) * Math.PI / 180;
        // 方位角 0 で前（-y）から後ろ（+y）を見る。z（頭）が上
        var forward = new Vec3(Math.Sin(az) * Math.Cos(el), Math.Cos(az) * Math.Cos(el), -Math.Sin(el)).Normalized();
        var right = Vec3.Cross(forward, Vec3.UnitZ).Normalized();
        var up = Vec3.Cross(right, forward).Normalized();
        return (forward, right, up);
    }

    public Camera Orbit(double dAzimuth, double dElevation) =>
        this with { AzimuthDeg = (AzimuthDeg + dAzimuth) % 360, ElevationDeg = Math.Clamp(ElevationDeg + dElevation, -89, 89) };

    public static Camera Anterior { get; } = new();

    public static Camera Posterior { get; } = new(180);

    public static Camera Left { get; } = new(-90);

    public static Camera Right { get; } = new(90);

    public static Camera Superior { get; } = new(0, 89);
}
