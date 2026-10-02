using System.Globalization;

namespace VolumeScope.Core.Geometry;

/// <summary>
/// 3 次元のベクトル（倍精度）。患者座標（DICOM の LPS: x = 患者の左, y = 後ろ, z = 頭側, 単位 mm）と、
/// ボクセルの番地（列・行・スライス）の両方に使う。
/// </summary>
public readonly record struct Vec3(double X, double Y, double Z)
{
    public static readonly Vec3 Zero = new(0, 0, 0);
    public static readonly Vec3 UnitX = new(1, 0, 0);
    public static readonly Vec3 UnitY = new(0, 1, 0);
    public static readonly Vec3 UnitZ = new(0, 0, 1);

    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static Vec3 operator -(Vec3 a) => new(-a.X, -a.Y, -a.Z);

    public static Vec3 operator *(Vec3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);

    public static Vec3 operator *(double s, Vec3 a) => a * s;

    public static Vec3 operator /(Vec3 a, double s) => new(a.X / s, a.Y / s, a.Z / s);

    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

    public static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    public static Vec3 Cross(Vec3 a, Vec3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    public Vec3 Normalized()
    {
        double l = Length;
        return l > 0 ? this / l : this;
    }

    public static double Distance(Vec3 a, Vec3 b) => (a - b).Length;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"({X:0.###}, {Y:0.###}, {Z:0.###})");
}

/// <summary>
/// ボクセルの番地 ⇔ 患者座標 の変換（アフィン変換）。
/// 患者座標 = Origin + i·ColumnStep + j·RowStep + k·SliceStep
///   i: 列（画像の右方向）, j: 行（画像の下方向）, k: スライス
/// </summary>
public sealed class VolumeGeometry
{
    private readonly double[,] inverse;

    public VolumeGeometry(Vec3 origin, Vec3 rowDirection, Vec3 columnDirection, Vec3 sliceDirection, double spacingX, double spacingY, double spacingZ)
    {
        if (spacingX <= 0 || spacingY <= 0 || spacingZ <= 0) throw new ArgumentException("ボクセルの間隔は正の値である必要があります。");
        Origin = origin;
        RowDirection = rowDirection.Normalized();
        ColumnDirection = columnDirection.Normalized();
        SliceDirection = sliceDirection.Normalized();
        SpacingX = spacingX;
        SpacingY = spacingY;
        SpacingZ = spacingZ;
        inverse = Invert(ColumnStep, RowStep, SliceStep);
    }

    /// <summary>番地 (0,0,0) の中心の患者座標</summary>
    public Vec3 Origin { get; }

    /// <summary>列が増える向き（DICOM の Image Orientation の前半）</summary>
    public Vec3 RowDirection { get; }

    /// <summary>行が増える向き（Image Orientation の後半）</summary>
    public Vec3 ColumnDirection { get; }

    /// <summary>スライスが増える向き</summary>
    public Vec3 SliceDirection { get; }

    public double SpacingX { get; }

    public double SpacingY { get; }

    public double SpacingZ { get; }

    public Vec3 ColumnStep => RowDirection * SpacingX;

    public Vec3 RowStep => ColumnDirection * SpacingY;

    public Vec3 SliceStep => SliceDirection * SpacingZ;

    public double MinSpacing => Math.Min(SpacingX, Math.Min(SpacingY, SpacingZ));

    public double VoxelVolumeMm3 => SpacingX * SpacingY * SpacingZ * Math.Abs(Vec3.Dot(Vec3.Cross(RowDirection, ColumnDirection), SliceDirection));

    public Vec3 IndexToPatient(Vec3 index) => Origin + ColumnStep * index.X + RowStep * index.Y + SliceStep * index.Z;

    public Vec3 PatientToIndex(Vec3 p)
    {
        var d = p - Origin;
        return new Vec3(
            inverse[0, 0] * d.X + inverse[0, 1] * d.Y + inverse[0, 2] * d.Z,
            inverse[1, 0] * d.X + inverse[1, 1] * d.Y + inverse[1, 2] * d.Z,
            inverse[2, 0] * d.X + inverse[2, 1] * d.Y + inverse[2, 2] * d.Z);
    }

    /// <summary>患者座標での向きを、番地での向き（1 mm あたり）に直す</summary>
    public Vec3 PatientDirectionToIndex(Vec3 v) => new(
        inverse[0, 0] * v.X + inverse[0, 1] * v.Y + inverse[0, 2] * v.Z,
        inverse[1, 0] * v.X + inverse[1, 1] * v.Y + inverse[1, 2] * v.Z,
        inverse[2, 0] * v.X + inverse[2, 1] * v.Y + inverse[2, 2] * v.Z);

    /// <summary>列ベクトル a, b, c を並べた行列の逆行列</summary>
    private static double[,] Invert(Vec3 a, Vec3 b, Vec3 c)
    {
        double det = Vec3.Dot(a, Vec3.Cross(b, c));
        if (Math.Abs(det) < 1e-12) throw new ArgumentException("ボクセルの向きが正しくありません（3 つの向きが同じ平面上にあります）。");
        var r0 = Vec3.Cross(b, c) / det;
        var r1 = Vec3.Cross(c, a) / det;
        var r2 = Vec3.Cross(a, b) / det;
        return new[,] { { r0.X, r0.Y, r0.Z }, { r1.X, r1.Y, r1.Z }, { r2.X, r2.Y, r2.Z } };
    }
}
