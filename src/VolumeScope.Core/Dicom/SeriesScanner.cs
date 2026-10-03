using FellowOakDicom;

namespace VolumeScope.Core.Dicom;

/// <summary>
/// DICOM のファイル 1 つ（名前と、中身を読む方法）。PC のファイルでも、ブラウザーで選んだファイル（メモリの中）でも同じように扱う。
/// </summary>
public sealed record DicomInput(string Name, Func<Stream> Open)
{
    public static DicomInput FromPath(string path) => new(path, () => File.OpenRead(path));

    public static DicomInput FromBytes(string name, byte[] bytes) => new(name, () => new MemoryStream(bytes, writable: false));
}

/// <summary>フォルダーの中で見つかった 1 つのシリーズ</summary>
public sealed record SeriesInfo(
    string SeriesInstanceUid,
    string StudyInstanceUid,
    string PatientName,
    string PatientId,
    string StudyDate,
    string Modality,
    string SeriesDescription,
    int SeriesNumber,
    int Rows,
    int Columns,
    IReadOnlyList<DicomInput> Inputs)
{
    public IReadOnlyList<string> Files => Inputs.Select(i => i.Name).ToList();

    public int ImageCount => Inputs.Count;

    /// <summary>3D にできるか（CT / MR / PT などの断面像で、2 枚以上）</summary>
    public bool CanBuildVolume => ImageCount >= 2 && Rows > 0 && Columns > 0;

    public string Title => string.IsNullOrWhiteSpace(SeriesDescription) ? $"シリーズ {SeriesNumber}" : SeriesDescription;
}

/// <summary>フォルダー（または選んだファイル）から DICOM のシリーズを探す。画素は読まない（速い）</summary>
public static class SeriesScanner
{
    public static IReadOnlyList<SeriesInfo> Scan(IEnumerable<string> paths, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        // 開けないフォルダー（権限のないものなど）は飛ばして、ほかを探し続ける
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
        var files = paths.SelectMany(p => Directory.Exists(p) ? Directory.EnumerateFiles(p, "*", options) : [p])
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(DicomInput.FromPath);
        return Scan(files, progress, cancellationToken);
    }

    /// <summary>ファイルの一覧（メモリの中のものでもよい）からシリーズを探す</summary>
    public static IReadOnlyList<SeriesInfo> Scan(IEnumerable<DicomInput> inputs, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var files = inputs
            .Where(f => !string.Equals(Path.GetFileName(f.Name), "DICOMDIR", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var headers = new List<(DicomInput Path, DicomDataset Ds)>();
        int done = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ds = TryReadHeader(file);
            if (ds is not null && ds.Contains(DicomTag.SeriesInstanceUID)) headers.Add((file, ds));
            progress?.Report((double)++done / Math.Max(files.Count, 1));
        }

        return headers
            .GroupBy(h => h.Ds.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, ""))
            .Select(g =>
            {
                var ds = g.First().Ds;
                return new SeriesInfo(
                    g.Key,
                    ds.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, ""),
                    PersonName(ds.GetSingleValueOrDefault(DicomTag.PatientName, "")),
                    ds.GetSingleValueOrDefault(DicomTag.PatientID, ""),
                    ds.GetSingleValueOrDefault(DicomTag.StudyDate, ""),
                    ds.GetSingleValueOrDefault(DicomTag.Modality, ""),
                    ds.GetSingleValueOrDefault(DicomTag.SeriesDescription, ""),
                    ds.GetSingleValueOrDefault(DicomTag.SeriesNumber, 0),
                    ds.GetSingleValueOrDefault(DicomTag.Rows, 0),
                    ds.GetSingleValueOrDefault(DicomTag.Columns, 0),
                    g.Select(h => h.Path).ToList());
            })
            .OrderByDescending(s => s.CanBuildVolume)
            .ThenByDescending(s => s.ImageCount)
            .ToList();
    }

    /// <summary>DICOM でなければ null（例外にはしない）</summary>
    internal static DicomDataset? TryReadHeader(DicomInput input)
    {
        try
        {
            using var stream = input.Open();
            var file = DicomFile.Open(stream, FileReadOption.SkipLargeTags);
            return file.Dataset.Contains(DicomTag.SOPInstanceUID) ? file.Dataset : null;
        }
        catch (Exception ex) when (ex is DicomFileException or DicomDataException or IOException or UnauthorizedAccessException or EndOfStreamException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>"YAMADA^TARO" → "YAMADA TARO"</summary>
    internal static string PersonName(string raw) => string.Join(' ', raw.Split('^', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
