using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.IO.Buffer;
using VolumeScope.Core.Geometry;
using VolumeScope.Core.Volumes;

namespace VolumeScope.Tests;

/// <summary>テスト用に、Volume を 1 スライス 1 ファイルの CT DICOM として書き出す</summary>
internal static class TestDicom
{
    public sealed record Options
    {
        public bool Shuffle { get; init; }
        public bool ReverseInstanceNumbers { get; init; }
        public bool AddLocalizer { get; init; }
        public bool AddDuplicate { get; init; }
        public bool OmitPosition { get; init; }
        public bool UnsignedWithIntercept { get; init; }
        public int BitsStored { get; init; } = 16;
        public string SeriesUid { get; init; } = DicomUID.Generate().UID;
        public string Description { get; init; } = "TEST AXIAL";
    }

    public static IReadOnlyList<string> Write(Volume v, string dir, Options? options = null)
    {
        options ??= new Options();
        Directory.CreateDirectory(dir);
        var files = new List<string>();
        var g = v.Geometry;
        string study = DicomUID.Generate().UID;
        var order = Enumerable.Range(0, v.Depth).ToList();
        if (options.Shuffle) order = order.OrderBy(i => (i * 7919) % 101).ThenBy(i => i).ToList();

        foreach (int z in order)
        {
            var pos = g.IndexToPatient(new Vec3(0, 0, z));
            int instance = options.ReverseInstanceNumbers ? v.Depth - z : z + 1;
            var ds = Slice(v, z, pos, instance, study, options, "ORIGINAL\\PRIMARY\\AXIAL");
            string path = Path.Combine(dir, $"IMG{files.Count:0000}");
            new DicomFile(ds).Save(path);
            files.Add(path);
        }
        if (options.AddDuplicate)
        {
            var ds = Slice(v, 3, g.IndexToPatient(new Vec3(0, 0, 3)), 999, study, options, "ORIGINAL\\PRIMARY\\AXIAL");
            string path = Path.Combine(dir, "DUP");
            new DicomFile(ds).Save(path);
            files.Add(path);
        }
        if (options.AddLocalizer)
        {
            var ds = Slice(v, 0, g.IndexToPatient(Vec3.Zero) + new Vec3(0, 0, 500), 0, study, options, "ORIGINAL\\PRIMARY\\LOCALIZER");
            string path = Path.Combine(dir, "SCOUT");
            new DicomFile(ds).Save(path);
            files.Add(path);
        }
        File.WriteAllText(Path.Combine(dir, "readme.txt"), "not dicom");
        return files;
    }

    private static DicomDataset Slice(Volume v, int z, Vec3 pos, int instance, string study, Options o, string imageType)
    {
        var g = v.Geometry;
        var ds = new DicomDataset
        {
            { DicomTag.SOPClassUID, DicomUID.CTImageStorage },
            { DicomTag.SOPInstanceUID, DicomUID.Generate() },
            { DicomTag.StudyInstanceUID, study },
            { DicomTag.SeriesInstanceUID, o.SeriesUid },
            { DicomTag.PatientName, "TEST^PHANTOM" },
            { DicomTag.PatientID, "T-001" },
            { DicomTag.StudyDate, "20260101" },
            { DicomTag.Modality, "CT" },
            { DicomTag.SeriesDescription, o.Description },
            { DicomTag.SeriesNumber, 3 },
            { DicomTag.InstanceNumber, instance },
            { DicomTag.Rows, (ushort)v.Height },
            { DicomTag.Columns, (ushort)v.Width },
            { DicomTag.PixelSpacing, new[] { g.SpacingY, g.SpacingX } },
            { DicomTag.SliceThickness, g.SpacingZ },
            { DicomTag.SamplesPerPixel, (ushort)1 },
            { DicomTag.PhotometricInterpretation, PhotometricInterpretation.Monochrome2.Value },
            { DicomTag.BitsAllocated, (ushort)16 },
            { DicomTag.BitsStored, (ushort)o.BitsStored },
            { DicomTag.HighBit, (ushort)(o.BitsStored - 1) },
            { DicomTag.PixelRepresentation, (ushort)(o.UnsignedWithIntercept ? 0 : 1) },
            { DicomTag.RescaleIntercept, o.UnsignedWithIntercept ? -1024.0 : 0.0 },
            { DicomTag.RescaleSlope, 1.0 },
        };
        ds.Add(DicomTag.ImageType, imageType.Split('\\'));
        if (!o.OmitPosition)
        {
            ds.Add(DicomTag.ImagePositionPatient, new[] { pos.X, pos.Y, pos.Z });
            ds.Add(DicomTag.ImageOrientationPatient, new[] { g.RowDirection.X, g.RowDirection.Y, g.RowDirection.Z, g.ColumnDirection.X, g.ColumnDirection.Y, g.ColumnDirection.Z });
        }

        var bytes = new byte[v.Width * v.Height * 2];
        for (int i = 0; i < v.Width * v.Height; i++)
        {
            int hu = v.Data[z * v.SliceSize + i];
            int stored = o.UnsignedWithIntercept ? hu + 1024 : hu;
            if (o.BitsStored < 16) stored &= (1 << o.BitsStored) - 1; // 上位ビットを捨てる（12 ビットの符号つき など）
            bytes[i * 2] = (byte)(stored & 0xFF);
            bytes[i * 2 + 1] = (byte)((stored >> 8) & 0xFF);
        }
        var pixelData = DicomPixelData.Create(ds, true);
        pixelData.AddFrame(new MemoryByteBuffer(bytes));
        return ds;
    }

    /// <summary>小さな見本の画像（速いテスト用）</summary>
    public static Volume Small(VolumeGeometry? geometry = null, int w = 24, int h = 20, int d = 12, Func<int, int, int, short>? value = null)
    {
        geometry ??= new VolumeGeometry(new Vec3(-10, -20, 30), Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ, 0.8, 0.7, 2.5);
        value ??= (x, y, z) => (short)(x * 10 - y * 3 + z * 50 - 500);
        var data = new short[w * h * d];
        for (int z = 0; z < d; z++)
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    data[z * w * h + y * w + x] = value(x, y, z);
        return new Volume(w, h, d, data, geometry);
    }
}
