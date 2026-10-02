using System.Diagnostics;
using VolumeScope.Core.Volumes;

namespace VolumeScope.Core.Surface;

/// <summary>面を作るときの細かさ</summary>
public enum SurfaceQuality
{
    /// <summary>操作中のプレビュー（4 ボクセルずつまとめる）</summary>
    Preview,

    /// <summary>ふつう（三角形の数が上限を超えないように自動で調整）</summary>
    Standard,

    /// <summary>元の細かさのまま（3D プリント用）</summary>
    Full,
}

public sealed record SurfaceOptions(double ThresholdHu, SurfaceQuality Quality = SurfaceQuality.Standard, bool RemoveFragments = true, int SmoothIterations = 6)
{
    /// <summary>ふつうの細かさで許す三角形の数（WPF の表示が重くならない程度）</summary>
    public int TriangleBudget { get; init; } = 1_500_000;
}

public sealed record SurfaceResult(Mesh Mesh, int DownsampleFactor, double SurfaceAreaMm2, double VolumeMl, int Components, TimeSpan Elapsed);

/// <summary>閾値から面を作る一連の処理（縮小 → マーチングキューブス → 破片の除去 → なめらかに）</summary>
public static class SurfaceBuilder
{
    public static SurfaceResult Build(Volume volume, SurfaceOptions options, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(options);
        var sw = Stopwatch.StartNew();

        int factor = options.Quality switch
        {
            SurfaceQuality.Preview => 4,
            SurfaceQuality.Full => 1,
            // 大きな画像は、まず 2 ボクセルずつまとめる（512×512×数百枚で約 1/8 の計算量）
            _ => volume.VoxelCount > 60_000_000 ? 2 : 1,
        };

        Mesh mesh;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(factor == 1 ? "面を作っています…" : $"面を作っています（{factor} ボクセルずつまとめて）…");
            var source = volume.Downsample(factor, cancellationToken);
            mesh = MarchingCubes.Extract(source, options.ThresholdHu, cancellationToken);
            if (options.Quality != SurfaceQuality.Standard || mesh.TriangleCount <= options.TriangleBudget || factor >= 4) break;
            factor++;
        }

        if (options.RemoveFragments && mesh.TriangleCount > 0)
        {
            progress?.Report("小さな破片を取り除いています…");
            mesh = MeshTools.RemoveSmallComponents(mesh, cancellationToken: cancellationToken);
        }
        if (options.SmoothIterations > 0 && mesh.TriangleCount > 0)
        {
            progress?.Report("なめらかにしています…");
            mesh = MeshTools.TaubinSmooth(mesh, options.SmoothIterations, cancellationToken: cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new SurfaceResult(mesh, factor, mesh.SurfaceArea(), Math.Max(mesh.SignedVolume(), 0) / 1000, MeshTools.CountComponents(mesh), sw.Elapsed);
    }
}
