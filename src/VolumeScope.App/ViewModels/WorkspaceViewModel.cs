using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using VolumeScope.Core.Dicom;
using VolumeScope.Core.Geometry;
using VolumeScope.Core.Measurement;
using VolumeScope.Core.Mpr;
using VolumeScope.Core.Rendering;
using VolumeScope.Core.Surface;
using VolumeScope.Core.Volumes;

namespace VolumeScope.App.ViewModels;

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

/// <summary>画面全体の状態（読み込んだ画像・十字の位置・表示の設定・計測）</summary>
public sealed partial class WorkspaceViewModel : ObservableObject
{
    private CancellationTokenSource? surfaceCts;
    private double surfaceThreshold = double.NaN;
    private CancellationTokenSource? loadCts;
    private int measurementNumber;

    public WorkspaceViewModel()
    {
        SelectedTissue = TissuePresets.Bone;
        Threshold = TissuePresets.Bone.ThresholdHu;
        Window = Core.Mpr.WindowPresets.All[0].Window;
    }

    // ---- 読み込んだ画像

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVolume), nameof(PatientLine), nameof(SeriesLine), nameof(GeometryLine), nameof(Warnings), nameof(HasWarnings), nameof(IsCt))]
    public partial Volume? Volume { get; private set; }

    public VolumeRenderer? Renderer { get; private set; }

    public bool HasVolume => Volume is not null;

    public bool IsCt => Volume is null || string.Equals(Volume.Info.Modality, "CT", StringComparison.OrdinalIgnoreCase);

    public string PatientLine => Volume is null ? "" : Join(Volume.Info.PatientName, Volume.Info.PatientId, FormatDate(Volume.Info.StudyDate));

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

    public bool HasWarnings => Warnings.Count > 0;

    /// <summary>値の分布（-1024〜3071 HU を 16 HU ごと）</summary>
    [ObservableProperty]
    public partial long[]? Histogram { get; private set; }

    public const int HistogramMin = -1024;
    public const int HistogramMax = 2048;
    public const int HistogramBin = 16;

    // ---- 断面

    /// <summary>3 つの断面の交点（患者座標 mm）</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CrosshairValue))]
    public partial Vec3 Crosshair { get; set; }

    public string CrosshairValue => Volume is null ? "" : string.Create(CultureInfo.InvariantCulture, $"{Volume.SampleAt(Crosshair):0} HU");

    [ObservableProperty]
    public partial WindowLevel Window { get; set; }

    public IReadOnlyList<WindowPreset> WindowPresets => Core.Mpr.WindowPresets.All;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSlab))]
    public partial SlabMode Slab { get; set; } = SlabMode.Thin;

    public bool IsSlab => Slab != SlabMode.Thin;

    [ObservableProperty]
    public partial double SlabThickness { get; set; } = 10;

    [ObservableProperty]
    public partial ToolMode Tool { get; set; } = ToolMode.Crosshair;

    /// <summary>大きく表示している画面（null なら 4 分割）。"Axial" "Coronal" "Sagittal" "3D"</summary>
    [ObservableProperty]
    public partial string? Maximized { get; set; }

    // ---- 組織と 3D

    public IReadOnlyList<TissuePreset> Tissues => TissuePresets.All;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Transfer), nameof(SurfaceColor))]
    public partial TissuePreset SelectedTissue { get; set; }

    /// <summary>面を作る閾値（HU）。ボリュームレンダリングの見え方も同じだけずらす</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Transfer), nameof(ThresholdText))]
    public partial double Threshold { get; set; }

    public string ThresholdText => string.Create(CultureInfo.InvariantCulture, $"{Threshold:0} HU");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVolumeMode), nameof(IsSurfaceMode), nameof(IsMipMode))]
    public partial ThreeDMode Mode3D { get; set; } = ThreeDMode.Volume;

    public bool IsVolumeMode
    {
        get => Mode3D == ThreeDMode.Volume;
        set { if (value) Mode3D = ThreeDMode.Volume; }
    }

    public bool IsSurfaceMode
    {
        get => Mode3D == ThreeDMode.Surface;
        set { if (value) Mode3D = ThreeDMode.Surface; }
    }

    public bool IsMipMode
    {
        get => Mode3D == ThreeDMode.Mip;
        set { if (value) Mode3D = ThreeDMode.Mip; }
    }

    [ObservableProperty]
    public partial Camera Camera3D { get; set; } = new(25, 12, 1.25);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Crop), nameof(HasCrop))]
    public partial CropAxis CropAxis { get; set; } = CropAxis.None;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Crop))]
    public partial double CropPosition { get; set; } = 0.5;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Crop))]
    public partial bool CropFlip { get; set; }

    public bool HasCrop => CropAxis != CropAxis.None;

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
        // 患者の軸に最も近い番地の軸と、その向き
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

    public uint SurfaceColor => SelectedTissue.Color;

    partial void OnSelectedTissueChanged(TissuePreset value)
    {
        Threshold = value.ThresholdHu;
        if (HasVolume) Window = value.SuggestedWindow;
    }

    partial void OnThresholdChanged(double value) => ScheduleSurface();

    partial void OnMode3DChanged(ThreeDMode value)
    {
        if (value == ThreeDMode.Surface && Surface is null) ScheduleSurface();
    }

    // ---- 面

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSurface), nameof(SurfaceStats))]
    public partial SurfaceResult? Surface { get; private set; }

    public bool HasSurface => Surface is not null && Surface.Mesh.TriangleCount > 0;

    public string SurfaceStats => Surface is null ? "" : string.Create(CultureInfo.InvariantCulture,
        $"三角形 {Surface.Mesh.TriangleCount:N0}、部分 {Surface.Components}、表面積 {Surface.SurfaceAreaMm2 / 100:N0} cm²、体積 {Surface.VolumeMl:N1} mL");

    [ObservableProperty]
    public partial bool IsBuildingSurface { get; private set; }

    [ObservableProperty]
    public partial bool ExportFullQuality { get; set; }

    private async void ScheduleSurface()
    {
        surfaceCts?.Cancel();
        if (Volume is null) return;
        if (Mode3D != ThreeDMode.Surface)
        {
            // 面を表示していないときは作らない（古い面は捨て、書き出すときや「面」に切り替えたときに作る）
            if (Surface is not null && Math.Abs(surfaceThreshold - Threshold) > 1e-9) Surface = null;
            IsBuildingSurface = false;
            return;
        }
        var cts = surfaceCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(350, cts.Token); // スライダーを動かしている間は待つ
            IsBuildingSurface = true;
            var volume = Volume;
            var options = new SurfaceOptions(Threshold);
            var progress = new Progress<string>(s => Status = s);
            var result = await Task.Run(() => SurfaceBuilder.Build(volume, options, progress, cts.Token), cts.Token);
            if (cts.IsCancellationRequested || volume != Volume) return;
            surfaceThreshold = options.ThresholdHu;
            Surface = result;
            Status = string.Create(CultureInfo.InvariantCulture, $"面を作りました（{result.Elapsed.TotalSeconds:0.0} 秒）");
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
            if (cts == surfaceCts) IsBuildingSurface = false;
        }
    }

    // ---- 計測

    public ObservableCollection<MeasurementItem> Measurements { get; } = [];

    public bool HasMeasurements => Measurements.Count > 0;

    public void AddDistance(MprPlane plane, double planePosition, Vec3 a, Vec3 b)
    {
        Measurements.Add(new DistanceMeasurement(plane, planePosition, ++measurementNumber, a, b));
        OnPropertyChanged(nameof(HasMeasurements));
    }

    public void AddCircle(MprSlice slice, double planePosition, Vec3 center, double radiusMm)
    {
        ArgumentNullException.ThrowIfNull(slice);
        var (cx, cy) = slice.PatientToPixel(center);
        var stats = Core.Measurement.Measurements.CircleRoi(slice, cx, cy, radiusMm / slice.PixelSize);
        Measurements.Add(new CircleMeasurement(slice.Plane, planePosition, ++measurementNumber, center, radiusMm, stats));
        OnPropertyChanged(nameof(HasMeasurements));
    }

    [RelayCommand]
    private void RemoveMeasurement(MeasurementItem? item)
    {
        if (item is null) return;
        Measurements.Remove(item);
        OnPropertyChanged(nameof(HasMeasurements));
    }

    [RelayCommand]
    private void ClearMeasurements()
    {
        Measurements.Clear();
        OnPropertyChanged(nameof(HasMeasurements));
    }

    /// <summary>計測をした断面へ移動する</summary>
    [RelayCommand]
    private void GoToMeasurement(MeasurementItem? item)
    {
        if (item is null) return;
        var n = Mpr.Normal(item.Plane);
        var anchor = item switch
        {
            DistanceMeasurement d => (d.A + d.B) / 2,
            CircleMeasurement c => c.Center,
            _ => Crosshair,
        };
        Crosshair = anchor + n * (item.PlanePosition - Vec3.Dot(anchor, n));
    }

    // ---- 状態の表示

    [ObservableProperty]
    public partial string Status { get; set; } = "フォルダーを開くか、見本の模型で操作を試してください。";

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial double Progress { get; private set; }

    // ---- シリーズの選択

    public ObservableCollection<SeriesInfo> SeriesChoices { get; } = [];

    [ObservableProperty]
    public partial bool IsPickingSeries { get; set; }

    // ---- 読み込み

    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        var dialog = new OpenFolderDialog { Title = "DICOM の入ったフォルダーを選んでください（下のフォルダーも探します）" };
        if (dialog.ShowDialog() != true) return;
        await ScanAsync([dialog.FolderName]);
    }

    [RelayCommand]
    private async Task OpenFilesAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "DICOM のファイルを選んでください（Ctrl+A ですべて選べます）",
            Filter = "すべてのファイル (*.*)|*.*|DICOM (*.dcm)|*.dcm",
            Multiselect = true,
        };
        if (dialog.ShowDialog() != true) return;
        await ScanAsync(dialog.FileNames);
    }

    /// <summary>ファイルやフォルダーをウィンドウに落としたとき</summary>
    public Task OpenPathsAsync(IReadOnlyList<string> paths) => ScanAsync(paths);

    private async Task ScanAsync(IReadOnlyList<string> paths)
    {
        loadCts?.Cancel();
        var cts = loadCts = new CancellationTokenSource();
        IsBusy = true;
        Status = "DICOM を探しています…";
        try
        {
            var progress = new Progress<double>(p => Progress = p);
            var series = await Task.Run(() => SeriesScanner.Scan(paths, progress, cts.Token), cts.Token);
            var usable = series.Where(s => s.CanBuildVolume).ToList();
            if (usable.Count == 0)
            {
                Status = series.Count == 0 ? "DICOM の画像が見つかりませんでした。" : "3D にできるシリーズ（同じ向きの断面像が 2 枚以上）がありませんでした。";
                return;
            }
            if (usable.Count == 1)
            {
                await LoadAsync(usable[0]);
                return;
            }
            SeriesChoices.Clear();
            foreach (var s in usable) SeriesChoices.Add(s);
            IsPickingSeries = true;
            Status = $"{usable.Count} 個のシリーズが見つかりました。3D にするシリーズを選んでください。";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Status = "読み込めませんでした: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
        }
    }

    [RelayCommand]
    private async Task PickSeriesAsync(SeriesInfo? series)
    {
        if (series is null) return;
        IsPickingSeries = false;
        await LoadAsync(series);
    }

    [RelayCommand]
    private void CancelPick() => IsPickingSeries = false;

    private async Task LoadAsync(SeriesInfo series)
    {
        var cts = loadCts ??= new CancellationTokenSource();
        IsBusy = true;
        Status = $"「{series.Title}」の {series.ImageCount} 枚を読み込んでいます…";
        try
        {
            var progress = new Progress<double>(p => Progress = p);
            var volume = await Task.Run(() => VolumeLoader.Load(series, progress, cts.Token), cts.Token);
            await SetVolumeAsync(volume);
        }
        catch (VolumeLoadException ex)
        {
            Status = ex.Message;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Status = "読み込めませんでした: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
        }
    }

    [RelayCommand]
    private async Task OpenDemoAsync()
    {
        IsBusy = true;
        Status = "見本の模型を作っています…";
        try
        {
            var volume = await Task.Run(() => DemoPhantom.Create(1.0));
            await SetVolumeAsync(volume);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SetVolumeAsync(Volume volume)
    {
        ArgumentNullException.ThrowIfNull(volume);
        surfaceCts?.Cancel();
        bool isCt = string.Equals(volume.Info.Modality, "CT", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(volume.Info.Modality);
        var (renderer, histogram, auto) = await Task.Run(() =>
            (new VolumeRenderer(volume), volume.Histogram(HistogramMin, HistogramMax, HistogramBin), isCt ? default : AutoWindow(volume)));
        Renderer = renderer;
        Surface = null;
        surfaceThreshold = double.NaN;
        Measurements.Clear();
        measurementNumber = 0;
        OnPropertyChanged(nameof(HasMeasurements));
        Crosshair = volume.Center; // 先に十字を決めてから画像を替える（断面が新しい位置で作られるように）
        Volume = volume;
        Histogram = histogram;
        Camera3D = new Camera(25, 12, 1.25);
        CropAxis = CropAxis.None;
        Window = IsCt ? SelectedTissue.SuggestedWindow : auto;
        Status = HasWarnings ? "読み込みました（注意があります。右の「検査」を見てください）。" : "読み込みました。";
        if (Mode3D == ThreeDMode.Surface) ScheduleSurface();
    }

    /// <summary>CT 以外（MR など）は、値の分布から濃淡を決める</summary>
    private static WindowLevel AutoWindow(Volume v)
    {
        var sample = new short[(v.Data.Length + 96) / 97];
        for (int i = 0, k = 0; i < v.Data.Length && k < sample.Length; i += 97, k++) sample[k] = v.Data[i];
        Array.Sort(sample);
        var sorted = sample;
        if (sorted.Length == 0) return new WindowLevel(0, 1000);
        double lo = sorted[(int)(sorted.Length * 0.01)], hi = sorted[(int)(sorted.Length * 0.995)];
        return new WindowLevel((lo + hi) / 2, Math.Max(hi - lo, 1));
    }

    // ---- 3D の向き

    [RelayCommand]
    private void SetView(string? name) => Camera3D = name switch
    {
        "posterior" => Camera.Posterior with { Zoom = Camera3D.Zoom },
        "left" => Camera.Left with { Zoom = Camera3D.Zoom },
        "right" => Camera.Right with { Zoom = Camera3D.Zoom },
        "superior" => Camera.Superior with { Zoom = Camera3D.Zoom },
        _ => Camera.Anterior with { Zoom = Camera3D.Zoom },
    };

    [RelayCommand]
    private void ToggleMaximize(string? view) => Maximized = Maximized == view ? null : view;

    [RelayCommand]
    private void ApplyWindow(WindowPreset? preset)
    {
        if (preset is not null) Window = preset.Window;
    }

    // ---- 書き出し

    [RelayCommand]
    private async Task ExportSurfaceAsync(string? format)
    {
        if (Volume is null) return;
        bool stl = !string.Equals(format, "obj", StringComparison.OrdinalIgnoreCase);
        var dialog = new SaveFileDialog
        {
            Title = stl ? "STL で保存（3D プリント用）" : "OBJ で保存",
            Filter = stl ? "STL (*.stl)|*.stl" : "OBJ (*.obj)|*.obj",
            FileName = $"{SelectedTissue.Key}_{Threshold:0}HU.{(stl ? "stl" : "obj")}",
        };
        if (dialog.ShowDialog() != true) return;

        IsBusy = true;
        try
        {
            // 表示中の面が今の閾値のもので、作り直し中でなければ、それをそのまま使う
            bool current = Surface is not null && !IsBuildingSurface && Math.Abs(surfaceThreshold - Threshold) < 1e-9;
            var mesh = current ? Surface!.Mesh : null;
            if (mesh is null || ExportFullQuality && Surface!.DownsampleFactor != 1)
            {
                var volume = Volume;
                var options = new SurfaceOptions(Threshold, ExportFullQuality ? SurfaceQuality.Full : SurfaceQuality.Standard);
                var progress = new Progress<string>(s => Status = s);
                mesh = (await Task.Run(() => SurfaceBuilder.Build(volume, options, progress))).Mesh;
            }
            Status = "書き出しています…";
            var path = dialog.FileName;
            await Task.Run(() =>
            {
                if (stl)
                {
                    using var fs = File.Create(path);
                    mesh.WriteStl(fs, $"VolumeScope {SelectedTissue.Name} {Threshold:0}HU mm");
                }
                else
                {
                    using var sw = new StreamWriter(path);
                    mesh.WriteObj(sw, SelectedTissue.Key);
                }
            });
            Status = string.Create(CultureInfo.InvariantCulture, $"{Path.GetFileName(path)} に保存しました（三角形 {mesh.TriangleCount:N0}、単位 mm）。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "保存できませんでした: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string Join(params string?[] parts) => string.Join("　", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    private static string FormatDate(string yyyymmdd) =>
        yyyymmdd.Length == 8 ? $"{yyyymmdd[..4]}/{yyyymmdd[4..6]}/{yyyymmdd[6..]}" : yyyymmdd;
}
