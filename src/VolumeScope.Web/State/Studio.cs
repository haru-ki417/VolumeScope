using System.Globalization;
using System.IO.Compression;
using VolumeScope.Core.Dicom;
using VolumeScope.Core.Geometry;
using VolumeScope.Core.Mpr;
using VolumeScope.Core.Rendering;
using VolumeScope.Core.Surface;
using VolumeScope.Core.Volumes;

namespace VolumeScope.Web.State;

public enum ThreeDMode
{
    /// <summary>ボリュームレンダリング</summary>
    Volume,

    /// <summary>面（閾値の境目の三角形）</summary>
    Surface,

    /// <summary>最大値投影</summary>
    Mip,
}

public enum CropAxis
{
    None,
    AnteriorPosterior,
    LeftRight,
    SuperiorInferior,
}

/// <summary>
/// 画面全体の状態（読み込んだ画像・十字の位置・表示の設定・計測）。Windows 版の WorkspaceViewModel と同じ考え方。
/// 変えたら <see cref="Changed"/> で画面に知らせる。
/// </summary>
public sealed class Studio(BrowserIo io)
{
    public const int HistogramMin = -1024;
    public const int HistogramMax = 2048;
    public const int HistogramBin = 16;

    private CancellationTokenSource? surfaceCts;
    private CancellationTokenSource? loadCts;
    private double surfaceThreshold = double.NaN;
    private int measurementNumber;

    public event Action? Changed;

    public void Notify() => Changed?.Invoke();

    // ---- 読み込んだ画像

    public Volume? Volume { get; private set; }

    /// <summary>画像を替えるたびに増える（表示の部品が作り直しを判断する）</summary>
    public int VolumeVersion { get; private set; }

    public bool HasVolume => Volume is not null;

    public bool IsCt => Volume is null || string.IsNullOrEmpty(Volume.Info.Modality) || string.Equals(Volume.Info.Modality, "CT", StringComparison.OrdinalIgnoreCase);

    public bool IsDemo { get; private set; }

    public string PatientLine => Volume is null ? "" : IsDemo ? "見本の模型（人工の胸部 CT）" : Join(Volume.Info.PatientName, Volume.Info.PatientId, FormatDate(Volume.Info.StudyDate));

    public string SeriesLine => Volume is null ? "" : Join(Volume.Info.Modality, Volume.Info.SeriesDescription, Volume.Info.ConvolutionKernel);

    public string GeometryLine
    {
        get
        {
            if (Volume is null) return "";
            var g = Volume.Geometry;
            return string.Create(CultureInfo.InvariantCulture,
                $"{Volume.Width}×{Volume.Height}×{Volume.Depth} ボクセル、{g.SpacingX:0.###}×{g.SpacingY:0.###}×{g.SpacingZ:0.###} mm");
        }
    }

    public IReadOnlyList<string> Warnings => Volume?.Info.Warnings ?? [];

    /// <summary>値の分布（-1024〜2048 HU を 16 HU ごと）</summary>
    public long[]? Histogram { get; private set; }

    // ---- 断面

    /// <summary>3 つの断面の交点（患者座標 mm）</summary>
    public Vec3 Crosshair { get; private set; }

    public string CrosshairValue => Volume is null ? "" : string.Create(CultureInfo.InvariantCulture, $"{Volume.SampleAt(Crosshair):0} HU");

    public WindowLevel Window { get; private set; } = WindowPresets.All[0].Window;

    public SlabMode Slab { get; private set; } = SlabMode.Thin;

    public double SlabThickness { get; private set; } = 10;

    public ToolMode Tool { get; private set; } = ToolMode.Crosshair;

    /// <summary>大きく表示している画面（null なら 4 分割）。"Axial" "Coronal" "Sagittal" "3D"</summary>
    public string? Maximized { get; private set; }

    public void SetCrosshair(Vec3 p)
    {
        Crosshair = p;
        Notify();
    }

    /// <summary>断面の中の位置だけ変え、その断面の位置（法線方向）はそのまま</summary>
    public void MoveCrosshairInPlane(MprPlane plane, Vec3 point)
    {
        var n = Mpr.Normal(plane);
        SetCrosshair(point + n * (Vec3.Dot(Crosshair, n) - Vec3.Dot(point, n)));
    }

    /// <summary>断面を steps 枚ぶん送る</summary>
    public void ScrollSlice(MprPlane plane, int steps)
    {
        if (Volume is null || steps == 0) return;
        var (min, max, step) = Mpr.Range(Volume, plane);
        var n = Mpr.Normal(plane);
        double pos = Vec3.Dot(Crosshair, n);
        double next = Math.Clamp(pos + steps * step, min, max);
        SetCrosshair(Crosshair + n * (next - pos));
    }

    /// <summary>断面の位置（法線方向 mm）を直接決める（スライダー）</summary>
    public void SetSlicePosition(MprPlane plane, double position)
    {
        if (Volume is null) return;
        var n = Mpr.Normal(plane);
        SetCrosshair(Crosshair + n * (position - Vec3.Dot(Crosshair, n)));
    }

    public void SetWindow(WindowLevel w)
    {
        Window = new WindowLevel(Math.Clamp(w.Level, -2000, 4000), Math.Clamp(w.Width, 1, 6000));
        Notify();
    }

    /// <summary>右ボタンのドラッグ（左右 = 幅、上下 = レベル）</summary>
    public void DragWindow(double dx, double dy) => SetWindow(new WindowLevel(Window.Level - dy * 2, Window.Width + dx * 4));

    public void SetSlab(SlabMode mode)
    {
        Slab = mode;
        Notify();
    }

    public void SetSlabThickness(double mm)
    {
        SlabThickness = Math.Clamp(mm, 2, 60);
        Notify();
    }

    public void SetTool(ToolMode tool)
    {
        Tool = tool;
        Notify();
    }

    public void ToggleMaximize(string? view)
    {
        Maximized = Maximized == view ? null : view;
        Notify();
    }

    public void SetMaximized(string? view)
    {
        Maximized = view;
        Notify();
    }

    // ---- 組織と 3D

    public TissuePreset SelectedTissue { get; private set; } = TissuePresets.Bone;

    /// <summary>面を作る閾値（HU）。ボリュームレンダリングの見え方も同じだけずらす</summary>
    public double Threshold { get; private set; } = TissuePresets.Bone.ThresholdHu;

    public string ThresholdText => string.Create(CultureInfo.InvariantCulture, $"{Threshold:0} HU");

    public ThreeDMode Mode3D { get; private set; } = ThreeDMode.Volume;

    public bool Shading { get; private set; } = true;

    public Camera Camera3D { get; private set; } = new(25, 12, 1.25);

    /// <summary>C# の側でカメラを決めたときに増える（3D の画面へ送り直す）</summary>
    public int CameraVersion { get; private set; }

    public CropAxis CropAxis { get; private set; } = CropAxis.None;

    public double CropPosition { get; private set; } = 0.5;

    public bool CropFlip { get; private set; }

    public void SetTissue(TissuePreset tissue)
    {
        SelectedTissue = tissue;
        Threshold = tissue.ThresholdHu;
        if (HasVolume && IsCt) Window = tissue.SuggestedWindow;
        ScheduleSurface();
        Notify();
    }

    public void SetThreshold(double hu)
    {
        Threshold = Math.Clamp(Math.Round(hu), -1000, 2000);
        ScheduleSurface();
        Notify();
    }

    public void SetMode(ThreeDMode mode)
    {
        Mode3D = mode;
        if (mode == ThreeDMode.Surface && (Surface is null || Math.Abs(surfaceThreshold - Threshold) > 1e-9)) ScheduleSurface();
        Notify();
    }

    public void SetShading(bool on)
    {
        Shading = on;
        Notify();
    }

    /// <summary>3D の画面で回した・動かしたとき（画面からの知らせなので送り返さない）</summary>
    public void CameraMovedByUser(Camera camera)
    {
        Camera3D = camera;
        Notify();
    }

    public void SetView(string name)
    {
        Camera3D = name switch
        {
            "posterior" => Camera.Posterior with { Zoom = Camera3D.Zoom },
            "left" => Camera.Left with { Zoom = Camera3D.Zoom },
            "right" => Camera.Right with { Zoom = Camera3D.Zoom },
            "superior" => Camera.Superior with { Zoom = Camera3D.Zoom },
            "reset" => new Camera(25, 12, 1.25),
            _ => Camera.Anterior with { Zoom = Camera3D.Zoom },
        };
        CameraVersion++;
        Notify();
    }

    public void SetCrop(CropAxis axis)
    {
        CropAxis = axis;
        Notify();
    }

    public void SetCropPosition(double p)
    {
        CropPosition = Math.Clamp(p, 0, 0.98);
        Notify();
    }

    public void SetCropFlip(bool flip)
    {
        CropFlip = flip;
        Notify();
    }

    /// <summary>切り取りの範囲。前後は「前から切る」、左右は「右から切る」、上下は「頭側から切る」が基本</summary>
    public CropBox Crop
    {
        get
        {
            double p = CropPosition;
            return (CropAxis, CropFlip) switch
            {
                (CropAxis.None, _) => CropBox.Full,
                (CropAxis.AnteriorPosterior, false) => IndexCrop(Vec3.UnitY, p, keepHigh: true),
                (CropAxis.AnteriorPosterior, true) => IndexCrop(Vec3.UnitY, p, keepHigh: false),
                (CropAxis.LeftRight, false) => IndexCrop(Vec3.UnitX, p, keepHigh: true),
                (CropAxis.LeftRight, true) => IndexCrop(Vec3.UnitX, p, keepHigh: false),
                (CropAxis.SuperiorInferior, false) => IndexCrop(Vec3.UnitZ, p, keepHigh: false),
                _ => IndexCrop(Vec3.UnitZ, p, keepHigh: true),
            };
        }
    }

    /// <summary>患者座標の向き（前後など）を、画像の番地の向きに対応させた切り取り</summary>
    private CropBox IndexCrop(Vec3 patientAxis, double p, bool keepHigh)
    {
        if (Volume is null) return CropBox.Full;
        var g = Volume.Geometry;
        var dirs = new[] { g.RowDirection, g.ColumnDirection, g.SliceDirection };
        int axis = Enumerable.Range(0, 3).OrderByDescending(i => Math.Abs(Vec3.Dot(dirs[i], patientAxis))).First();
        bool same = Vec3.Dot(dirs[axis], patientAxis) > 0;
        bool high = keepHigh == same;
        double lo = high ? p : 0, hi = high ? 1 : 1 - p;
        return axis switch
        {
            0 => new CropBox(MinX: lo, MaxX: hi),
            1 => new CropBox(MinY: lo, MaxY: hi),
            _ => new CropBox(MinZ: lo, MaxZ: hi),
        };
    }

    public TransferFunction Transfer
    {
        get
        {
            var tf = SelectedTissue.Key switch
            {
                "skin" => TransferPresets.Skin,
                "vessels" => TransferPresets.Vessels,
                "lung" => TransferPresets.Lung,
                _ => TransferPresets.Bone,
            };
            double shift = Threshold - SelectedTissue.ThresholdHu;
            return Math.Abs(shift) < 0.5 ? tf : tf.Shifted(shift);
        }
    }

    /// <summary>3D の MIP の濃淡（断面の濃淡が狭すぎるときは見やすい幅にする）</summary>
    public WindowLevel MipWindow => Window.Width > 600 ? Window : new WindowLevel(300, 1200);

    // ---- 面

    public SurfaceResult? Surface { get; private set; }

    public int SurfaceVersion { get; private set; }

    public bool HasSurface => Surface is not null && Surface.Mesh.TriangleCount > 0;

    public string SurfaceStats => Surface is null ? "" : string.Create(CultureInfo.InvariantCulture,
        $"三角形 {Surface.Mesh.TriangleCount:N0}、部分 {Surface.Components}、表面積 {Surface.SurfaceAreaMm2 / 100:N0} cm²、体積 {Surface.VolumeMl:N1} mL");

    public bool IsBuildingSurface { get; private set; }

    public bool ExportFullQuality { get; private set; }

    public void SetExportFullQuality(bool on)
    {
        ExportFullQuality = on;
        Notify();
    }

    private async void ScheduleSurface()
    {
        surfaceCts?.Cancel();
        if (Volume is null) return;
        if (Mode3D != ThreeDMode.Surface)
        {
            // 面を表示していないときは作らない（「面」に切り替えたときや書き出すときに作る）
            if (Surface is not null && Math.Abs(surfaceThreshold - Threshold) > 1e-9)
            {
                Surface = null;
                SurfaceVersion++;
            }
            IsBuildingSurface = false;
            return;
        }
        var cts = surfaceCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(350, cts.Token); // スライダーを動かしている間は待つ
            IsBuildingSurface = true;
            Status = "面を作っています…";
            Notify();
            await Task.Delay(40, cts.Token); // 「作っています」を先に画面に出す
            var volume = Volume;
            var options = new SurfaceOptions(Threshold);
            var result = SurfaceBuilder.Build(volume, options, null, cts.Token);
            if (cts.IsCancellationRequested || volume != Volume) return;
            surfaceThreshold = options.ThresholdHu;
            Surface = result;
            SurfaceVersion++;
            Status = string.Create(CultureInfo.InvariantCulture, $"面を作りました（{result.Elapsed.TotalSeconds:0.0} 秒）。");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Status = "面を作れませんでした: " + ex.Message;
        }
        finally
        {
            if (cts == surfaceCts)
            {
                IsBuildingSurface = false;
                Notify();
            }
        }
    }

    // ---- 計測

    public List<MeasurementItem> Measurements { get; } = [];

    public void AddDistance(MprPlane plane, double planePosition, Vec3 a, Vec3 b)
    {
        Measurements.Add(new DistanceMeasurement(plane, planePosition, ++measurementNumber, a, b));
        Notify();
    }

    public void AddCircle(MprSlice slice, double planePosition, Vec3 center, double radiusMm)
    {
        ArgumentNullException.ThrowIfNull(slice);
        var (cx, cy) = slice.PatientToPixel(center);
        var stats = Core.Measurement.Measurements.CircleRoi(slice, cx, cy, radiusMm / slice.PixelSize);
        Measurements.Add(new CircleMeasurement(slice.Plane, planePosition, ++measurementNumber, center, radiusMm, stats));
        Notify();
    }

    public void RemoveMeasurement(MeasurementItem item)
    {
        Measurements.Remove(item);
        Notify();
    }

    public void ClearMeasurements()
    {
        Measurements.Clear();
        Notify();
    }

    /// <summary>計測をした断面へ移動する</summary>
    public void GoToMeasurement(MeasurementItem item)
    {
        var n = Mpr.Normal(item.Plane);
        var anchor = item switch
        {
            DistanceMeasurement d => (d.A + d.B) / 2,
            CircleMeasurement c => c.Center,
            _ => Crosshair,
        };
        SetCrosshair(anchor + n * (item.PlanePosition - Vec3.Dot(anchor, n)));
    }

    // ---- 状態の表示

    public string Status { get; set; } = "DICOM のフォルダーかファイルを開くか、見本の模型で操作を試してください。";

    public bool IsBusy { get; private set; }

    /// <summary>進み具合（0〜1）。負なら、どれだけ進んだか分からない処理</summary>
    public double Progress { get; private set; } = -1;

    // ---- シリーズの選択

    public List<SeriesInfo> SeriesChoices { get; } = [];

    public bool IsPickingSeries { get; private set; }

    public void SetPickingSeries(bool on)
    {
        IsPickingSeries = on && SeriesChoices.Count > 0;
        Notify();
    }

    // ---- 読み込み

    private static readonly string[] SkipInZip = [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".txt", ".xml", ".htm", ".html", ".pdf", ".exe", ".dll", ".ini", ".json", ".csv", ".inf", ".js", ".css"];

    /// <summary>ブラウザーで選んだ・落としたファイルを読む（ZIP は中を展開する）</summary>
    public async Task LoadPickedAsync(int count, long totalBytes)
    {
        if (count <= 0)
        {
            Status = "読めるファイルがありませんでした。";
            Notify();
            return;
        }
        loadCts?.Cancel();
        var cts = loadCts = new CancellationTokenSource();
        IsBusy = true;
        IsPickingSeries = false;
        Progress = 0;
        Status = $"ファイルを読んでいます（{count} 個）…";
        Notify();
        try
        {
            var inputs = new List<DicomInput>();
            long done = 0;
            for (int i = 0; i < count; i++)
            {
                cts.Token.ThrowIfCancellationRequested();
                string name = await io.FileNameAsync(i);
                var bytes = await io.ReadFileAsync(i);
                if (bytes is null) continue;
                done += bytes.Length;
                if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) inputs.AddRange(ExpandZip(name, bytes));
                else inputs.Add(DicomInput.FromBytes(name, bytes));
                if (i % 8 == 7 || i == count - 1)
                {
                    Progress = totalBytes > 0 ? (double)done / totalBytes : (double)(i + 1) / count;
                    Status = $"ファイルを読んでいます（{i + 1} / {count}）…";
                    Notify();
                }
            }
            await io.ReleaseFilesAsync();
            await ScanAsync(inputs, cts);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            Status = "読み込めませんでした: " + ex.Message;
        }
        finally
        {
            if (cts == loadCts)
            {
                IsBusy = false;
                Progress = -1;
                Notify();
            }
        }
    }

    private static List<DicomInput> ExpandZip(string zipName, byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
        var list = new List<DicomInput>();
        foreach (var entry in zip.Entries)
        {
            if (entry.Length == 0 || entry.FullName.EndsWith('/')) continue;
            if (SkipInZip.Any(e => entry.Name.EndsWith(e, StringComparison.OrdinalIgnoreCase))) continue;
            if (entry.FullName.StartsWith("__MACOSX/", StringComparison.Ordinal)) continue;
            using var s = entry.Open();
            var data = new byte[entry.Length];
            s.ReadExactly(data);
            list.Add(DicomInput.FromBytes(zipName + "/" + entry.FullName, data));
        }
        return list;
    }

    private async Task ScanAsync(List<DicomInput> inputs, CancellationTokenSource cts)
    {
        Progress = -1;
        Status = $"DICOM を探しています（{inputs.Count} 個のファイル）…";
        Notify();
        await Task.Delay(40, cts.Token);
        var series = SeriesScanner.Scan(inputs, null, cts.Token);
        var usable = series.Where(s => s.CanBuildVolume).ToList();
        if (usable.Count == 0)
        {
            Status = series.Count == 0 ? "DICOM の画像が見つかりませんでした。" : "3D にできるシリーズ（同じ向きの断面像が 2 枚以上）がありませんでした。";
            return;
        }
        SeriesChoices.Clear();
        SeriesChoices.AddRange(usable);
        if (usable.Count == 1)
        {
            await LoadSeriesCoreAsync(usable[0], cts);
            return;
        }
        IsPickingSeries = true;
        Status = $"{usable.Count} 個のシリーズが見つかりました。3D にするシリーズを選んでください。";
    }

    public async Task PickSeriesAsync(SeriesInfo series)
    {
        loadCts?.Cancel();
        var cts = loadCts = new CancellationTokenSource();
        IsPickingSeries = false;
        IsBusy = true;
        Notify();
        try
        {
            await LoadSeriesCoreAsync(series, cts);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (cts == loadCts)
            {
                IsBusy = false;
                Progress = -1;
                Notify();
            }
        }
    }

    private async Task LoadSeriesCoreAsync(SeriesInfo series, CancellationTokenSource cts)
    {
        Progress = -1;
        Status = $"「{series.Title}」の {series.ImageCount} 枚を 3D にしています…";
        Notify();
        await Task.Delay(40, cts.Token);
        try
        {
            var volume = VolumeLoader.Load(series, null, cts.Token);
            await SetVolumeAsync(volume, demo: false);
        }
        catch (VolumeLoadException ex)
        {
            Status = ex.Message;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Status = ex.Message.Contains("codec", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("transcod", StringComparison.OrdinalIgnoreCase)
                ? "圧縮された DICOM（JPEG・JPEG 2000 など）は、ブラウザー版では開けません。Windows 版を使うか、圧縮していない形で書き出してください。"
                : "読み込めませんでした: " + ex.Message;
        }
    }

    public async Task OpenDemoAsync()
    {
        loadCts?.Cancel();
        var cts = loadCts = new CancellationTokenSource();
        IsBusy = true;
        IsPickingSeries = false;
        Progress = -1;
        Status = "見本の模型を作っています…";
        Notify();
        try
        {
            await Task.Delay(40, cts.Token);
            var volume = DemoPhantom.Create(1.0);
            SeriesChoices.Clear();
            await SetVolumeAsync(volume, demo: true);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (cts == loadCts)
            {
                IsBusy = false;
                Notify();
            }
        }
    }

    private async Task SetVolumeAsync(Volume volume, bool demo)
    {
        surfaceCts?.Cancel();
        Status = "値の分布を調べています…";
        Notify();
        await Task.Yield();
        bool isCt = string.IsNullOrEmpty(volume.Info.Modality) || string.Equals(volume.Info.Modality, "CT", StringComparison.OrdinalIgnoreCase);
        var histogram = volume.Histogram(HistogramMin, HistogramMax, HistogramBin);
        Surface = null;
        SurfaceVersion++;
        surfaceThreshold = double.NaN;
        Measurements.Clear();
        measurementNumber = 0;
        Crosshair = volume.Center;
        Volume = volume;
        VolumeVersion++;
        IsDemo = demo;
        Histogram = histogram;
        Camera3D = new Camera(25, 12, 1.25);
        CameraVersion++;
        CropAxis = CropAxis.None;
        Window = isCt ? SelectedTissue.SuggestedWindow : AutoWindow(volume);
        Status = Warnings.Count > 0 ? "読み込みました（注意があります。「検査」を見てください）。" : "読み込みました。";
        if (Mode3D == ThreeDMode.Surface) ScheduleSurface();
        Notify();
    }

    /// <summary>CT 以外（MR など）は、値の分布から濃淡を決める</summary>
    private static WindowLevel AutoWindow(Volume v)
    {
        var sample = new short[(v.Data.Length + 96) / 97];
        for (int i = 0, k = 0; i < v.Data.Length && k < sample.Length; i += 97, k++) sample[k] = v.Data[i];
        Array.Sort(sample);
        if (sample.Length == 0) return new WindowLevel(0, 1000);
        double lo = sample[(int)(sample.Length * 0.01)], hi = sample[(int)(sample.Length * 0.995)];
        return new WindowLevel((lo + hi) / 2, Math.Max(hi - lo, 1));
    }

    // ---- 書き出し

    public async Task ExportSurfaceAsync(bool stl)
    {
        if (Volume is null) return;
        IsBusy = true;
        Progress = -1;
        Status = "書き出す面を用意しています…";
        Notify();
        try
        {
            await Task.Delay(40);
            bool current = Surface is not null && !IsBuildingSurface && Math.Abs(surfaceThreshold - Threshold) < 1e-9;
            var mesh = current ? Surface!.Mesh : null;
            if (mesh is null || ExportFullQuality && Surface!.DownsampleFactor != 1)
            {
                var options = new SurfaceOptions(Threshold, ExportFullQuality ? SurfaceQuality.Full : SurfaceQuality.Standard);
                mesh = SurfaceBuilder.Build(Volume, options).Mesh;
            }
            if (mesh.TriangleCount == 0)
            {
                Status = "この閾値では面ができませんでした。閾値を下げてみてください。";
                return;
            }
            string name = string.Create(CultureInfo.InvariantCulture, $"{SelectedTissue.Key}_{Threshold:0}HU.{(stl ? "stl" : "obj")}");
            byte[] bytes;
            using (var ms = new MemoryStream())
            {
                if (stl)
                {
                    mesh.WriteStl(ms, string.Create(CultureInfo.InvariantCulture, $"VolumeScope {SelectedTissue.Key} {Threshold:0}HU mm"));
                }
                else
                {
                    using var sw = new StreamWriter(ms, leaveOpen: true);
                    mesh.WriteObj(sw, SelectedTissue.Key);
                }
                bytes = ms.ToArray();
            }
            await io.DownloadAsync(name, stl ? "model/stl" : "text/plain", bytes);
            Status = string.Create(CultureInfo.InvariantCulture, $"{name} を保存しました（三角形 {mesh.TriangleCount:N0}、単位 mm）。");
        }
        catch (Exception ex)
        {
            Status = "書き出せませんでした: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            Notify();
        }
    }

    // ---- 表示の文字

    public static string Fmt(double v, string format = "0.#") => v.ToString(format, CultureInfo.InvariantCulture);

    private static string Join(params string?[] parts) => string.Join("　", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    private static string FormatDate(string yyyymmdd) =>
        yyyymmdd.Length == 8 ? $"{yyyymmdd[..4]}/{yyyymmdd[4..6]}/{yyyymmdd[6..]}" : yyyymmdd;
}
