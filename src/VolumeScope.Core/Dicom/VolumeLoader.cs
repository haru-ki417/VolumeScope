using System.Globalization;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.Imaging.Codec;
using FellowOakDicom.Imaging.Render;
using VolumeScope.Core.Geometry;
using VolumeScope.Core.Volumes;

namespace VolumeScope.Core.Dicom;

/// <summary>読み込めなかった理由（画面に出す）</summary>
public sealed class VolumeLoadException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// 1 つのシリーズの断面像を積み重ねて、3 次元の画像（Volume）にする。
///
///   1. 位置決め用の画像（LOCALIZER）や、大きさ・向きの違う画像を除く
///   2. 各画像の位置（Image Position）を、断面に垂直な向きに投影して並べる（Instance Number には頼らない）
///   3. 同じ位置の重複を除き、スライスの間隔と向きを位置から求める（ガントリー傾斜にも対応）
///   4. 画素値を CT 値（HU）に直す（Rescale Slope / Intercept）。圧縮されていれば展開する
/// </summary>
public static class VolumeLoader
{
    /// <summary>扱える最大のボクセル数（約 20 億。short で約 4GB）</summary>
    public const long MaxVoxels = int.MaxValue;

    private sealed record Header(
        DicomInput Path, int Rows, int Columns, Vec3? Position, Vec3? RowDir, Vec3? ColDir,
        double SpacingRow, double SpacingCol, int InstanceNumber, bool IsLocalizer, int Frames, string Photometric);

    public static Volume Load(SeriesInfo series, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(series);
        var warnings = new List<string>();

        var headers = series.Inputs
            .Select(f => (Path: f, Ds: SeriesScanner.TryReadHeader(f)))
            .Where(h => h.Ds is not null)
            .Select(h => ReadHeader(h.Path, h.Ds!))
            .ToList();
        if (headers.Count == 0) throw new VolumeLoadException("DICOM の画像が見つかりません。");

        if (headers.Any(h => h.Frames > 1))
            throw new VolumeLoadException("1 つのファイルに複数の断面が入った形式（マルチフレーム / Enhanced CT）には、まだ対応していません。スライスごとのファイルで書き出してください。");
        if (headers.Any(h => !h.Photometric.StartsWith("MONOCHROME", StringComparison.Ordinal)))
            throw new VolumeLoadException("カラーの画像は 3D にできません（CT・MR などのグレースケールの断面像が必要です）。");

        int localizers = headers.Count(h => h.IsLocalizer);
        if (localizers > 0) warnings.Add($"位置決め用の画像（LOCALIZER）{localizers} 枚を除きました。");
        var candidates = headers.Where(h => !h.IsLocalizer).ToList();

        // 大きさ・向きがそろった、いちばん多いまとまりを使う
        var group = candidates
            .GroupBy(h => (h.Rows, h.Columns, Orientation: OrientationKey(h)))
            .OrderByDescending(g => g.Count())
            .FirstOrDefault()?.ToList() ?? [];
        int otherShapes = candidates.Count - group.Count;
        if (otherShapes > 0) warnings.Add($"大きさや向きの違う画像 {otherShapes} 枚を除きました。");
        if (group.Count < 2) throw new VolumeLoadException("3D にするには、同じ向きの断面像が 2 枚以上必要です。");

        var first = group[0];
        int width = first.Columns, height = first.Rows;
        double spacingX = first.SpacingCol, spacingY = first.SpacingRow;
        if (spacingX <= 0 || spacingY <= 0)
        {
            spacingX = spacingY = 1;
            warnings.Add("画素の大きさ（Pixel Spacing）が書かれていないため、1 mm として扱いました。計測の値は正しくありません。");
        }

        Vec3 rowDir, colDir, sliceDir, origin;
        double spacingZ;
        List<Header> ordered;
        if (group.All(h => h.Position is not null && h.RowDir is not null && h.ColDir is not null))
        {
            rowDir = first.RowDir!.Value.Normalized();
            colDir = first.ColDir!.Value.Normalized();
            var normal = Vec3.Cross(rowDir, colDir).Normalized();

            // 断面に垂直な向きの位置で並べ、同じ位置の重複を除く
            var sorted = group.OrderBy(h => Vec3.Dot(h.Position!.Value, normal)).ThenBy(h => h.InstanceNumber).ToList();
            ordered = [];
            foreach (var h in sorted)
            {
                if (ordered.Count > 0 && Math.Abs(Vec3.Dot(h.Position!.Value - ordered[^1].Position!.Value, normal)) < 0.01) continue;
                ordered.Add(h);
            }
            if (ordered.Count < sorted.Count) warnings.Add($"同じ位置の画像 {sorted.Count - ordered.Count} 枚を除きました（複数回の撮影が混ざっている可能性があります）。");
            if (ordered.Count < 2) throw new VolumeLoadException("すべての画像が同じ位置にあるため、3D にできません。");

            var span = ordered[^1].Position!.Value - ordered[0].Position!.Value;
            spacingZ = span.Length / (ordered.Count - 1);
            sliceDir = span.Normalized();
            origin = ordered[0].Position!.Value;

            // 間隔がそろっているか
            var gaps = ordered.Zip(ordered.Skip(1), (a, b) => Vec3.Dot(b.Position!.Value - a.Position!.Value, normal)).ToList();
            double minGap = gaps.Min(), maxGap = gaps.Max();
            if (maxGap - minGap > Math.Max(0.05 * spacingZ, 0.05))
                warnings.Add(string.Create(CultureInfo.InvariantCulture,
                    $"スライスの間隔が一定ではありません（{minGap:0.##}〜{maxGap:0.##} mm）。平均の間隔で 3D にしたため、形や計測に誤差が出ることがあります。"));

            // ガントリー傾斜（スライスの並ぶ向きが断面に垂直でない）
            double tilt = Math.Acos(Math.Clamp(Math.Abs(Vec3.Dot(sliceDir, normal)), 0, 1)) * 180 / Math.PI;
            if (tilt > 0.5)
                warnings.Add(string.Create(CultureInfo.InvariantCulture, $"ガントリー傾斜（約 {tilt:0.#}°）があります。傾きを考慮して 3D にしています。"));
        }
        else
        {
            // 位置の情報がない古い形式: 画像番号の順に、スライス厚の間隔で並べる
            ordered = group.OrderBy(h => h.InstanceNumber).ToList();
            var ds = SeriesScanner.TryReadHeader(first.Path)!;
            spacingZ = ds.GetSingleValueOrDefault(DicomTag.SpacingBetweenSlices, ds.GetSingleValueOrDefault(DicomTag.SliceThickness, 1.0));
            if (spacingZ <= 0) spacingZ = 1;
            rowDir = Vec3.UnitX;
            colDir = Vec3.UnitY;
            sliceDir = Vec3.UnitZ;
            origin = Vec3.Zero;
            warnings.Add("画像の位置と向きが書かれていないため、画像番号の順に並べました。向きの表示（左右・前後）や計測は正しくない可能性があります。");
        }

        int depth = ordered.Count;
        long voxels = (long)width * height * depth;
        if (voxels > MaxVoxels) throw new VolumeLoadException($"画像が大きすぎます（{width}×{height}×{depth}）。");

        var data = new short[voxels];
        int sliceSize = width * height;
        int done = 0;
        var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
        Parallel.For(0, depth, new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Environment.ProcessorCount }, z =>
        {
            try
            {
                DecodeSlice(ordered[z].Path, width, height, data.AsSpan(z * sliceSize, sliceSize));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Enqueue($"{System.IO.Path.GetFileName(ordered[z].Path.Name)}: {ex.Message}");
            }
            progress?.Report((double)Interlocked.Increment(ref done) / depth);
        });
        if (!errors.IsEmpty)
            throw new VolumeLoadException($"{errors.Count} 枚の画像を展開できませんでした（圧縮形式に対応していない可能性があります）。\n{errors.First()}");

        var info = ReadInfo(SeriesScanner.TryReadHeader(first.Path)!, warnings);
        var geometry = new VolumeGeometry(origin, rowDir, colDir, sliceDir, spacingX, spacingY, spacingZ);
        return new Volume(width, height, depth, data, geometry, info);
    }

    private static Header ReadHeader(DicomInput path, DicomDataset ds)
    {
        Vec3? pos = null, row = null, col = null;
        if (ds.TryGetValues<double>(DicomTag.ImagePositionPatient, out var p) && p.Length >= 3) pos = new Vec3(p[0], p[1], p[2]);
        if (ds.TryGetValues<double>(DicomTag.ImageOrientationPatient, out var o) && o.Length >= 6)
        {
            row = new Vec3(o[0], o[1], o[2]);
            col = new Vec3(o[3], o[4], o[5]);
        }
        double sr = 0, sc = 0;
        if (ds.TryGetValues<double>(DicomTag.PixelSpacing, out var s) && s.Length >= 2)
        {
            sr = s[0]; // 行と行の間隔（縦）
            sc = s[1]; // 列と列の間隔（横）
        }
        bool localizer = ds.TryGetValues<string>(DicomTag.ImageType, out var types) && types.Any(t => t.Equals("LOCALIZER", StringComparison.OrdinalIgnoreCase));
        return new Header(path,
            ds.GetSingleValueOrDefault(DicomTag.Rows, 0),
            ds.GetSingleValueOrDefault(DicomTag.Columns, 0),
            pos, row, col, sr, sc,
            ds.GetSingleValueOrDefault(DicomTag.InstanceNumber, 0),
            localizer,
            ds.GetSingleValueOrDefault(DicomTag.NumberOfFrames, 1),
            ds.GetSingleValueOrDefault(DicomTag.PhotometricInterpretation, "MONOCHROME2"));
    }

    private static string OrientationKey(Header h) =>
        h.RowDir is null || h.ColDir is null ? "" : string.Create(CultureInfo.InvariantCulture,
            $"{h.RowDir.Value.X:0.00},{h.RowDir.Value.Y:0.00},{h.RowDir.Value.Z:0.00},{h.ColDir.Value.X:0.00},{h.ColDir.Value.Y:0.00},{h.ColDir.Value.Z:0.00}");

    /// <summary>1 枚の画像を読み、CT 値（HU）にして書き込む</summary>
    internal static void DecodeSlice(DicomInput input, int width, int height, Span<short> target)
    {
        DicomFile file;
        using (var stream = input.Open())
        {
            file = DicomFile.Open(stream, FileReadOption.ReadAll);
        }
        if (file.Dataset.InternalTransferSyntax.IsEncapsulated)
            file = new DicomTranscoder(file.Dataset.InternalTransferSyntax, DicomTransferSyntax.ExplicitVRLittleEndian).Transcode(file);
        var ds = file.Dataset;
        if (ds.GetSingleValueOrDefault(DicomTag.Columns, 0) != width || ds.GetSingleValueOrDefault(DicomTag.Rows, 0) != height)
            throw new InvalidDataException("画像の大きさが他と違います。");

        double slope = ds.GetSingleValueOrDefault(DicomTag.RescaleSlope, 1.0);
        double intercept = ds.GetSingleValueOrDefault(DicomTag.RescaleIntercept, 0.0);
        if (slope == 0) slope = 1;

        var pixels = PixelDataFactory.Create(DicomPixelData.Create(ds), 0);
        if (pixels.Width != width || pixels.Height != height) throw new InvalidDataException("画素の数が合いません。");
        int n = width * height;
        switch (pixels)
        {
            case GrayscalePixelDataS16 s16:
                for (int i = 0; i < n; i++) target[i] = ToHu(s16.Data[i], slope, intercept);
                break;
            case GrayscalePixelDataU16 u16:
                for (int i = 0; i < n; i++) target[i] = ToHu(u16.Data[i], slope, intercept);
                break;
            case GrayscalePixelDataU8 u8:
                for (int i = 0; i < n; i++) target[i] = ToHu(u8.Data[i], slope, intercept);
                break;
            default:
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                        target[y * width + x] = ToHu(pixels.GetPixel(x, y), slope, intercept);
                break;
        }
    }

    private static short ToHu(double raw, double slope, double intercept) =>
        (short)Math.Clamp(Math.Round(raw * slope + intercept), short.MinValue, short.MaxValue);

    private static VolumeInfo ReadInfo(DicomDataset ds, List<string> warnings) => new()
    {
        PatientName = SeriesScanner.PersonName(ds.GetSingleValueOrDefault(DicomTag.PatientName, "")),
        PatientId = ds.GetSingleValueOrDefault(DicomTag.PatientID, ""),
        StudyDate = ds.GetSingleValueOrDefault(DicomTag.StudyDate, ""),
        Modality = ds.GetSingleValueOrDefault(DicomTag.Modality, ""),
        SeriesDescription = ds.GetSingleValueOrDefault(DicomTag.SeriesDescription, ""),
        Manufacturer = ds.GetSingleValueOrDefault(DicomTag.Manufacturer, ""),
        ConvolutionKernel = ds.TryGetValues<string>(DicomTag.ConvolutionKernel, out var k) ? string.Join(",", k) : "",
        SliceThickness = ds.TryGetSingleValue<double>(DicomTag.SliceThickness, out var t) ? t : null,
        SeriesInstanceUid = ds.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, ""),
        Warnings = warnings,
    };
}
