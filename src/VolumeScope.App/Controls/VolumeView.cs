using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using HelixToolkit.Wpf;
using VolumeScope.App.ViewModels;
using VolumeScope.Core.Geometry;
using VolumeScope.Core.Rendering;
using VolumeScope.Core.Surface;
using Camera = VolumeScope.Core.Rendering.Camera;

namespace VolumeScope.App.Controls;

/// <summary>
/// 3D の画面。ボリュームレンダリングと MIP は CPU で描いた画像、面は WPF の 3D で表示する。
///   左ボタンでドラッグ: 回す　ホイール: 拡大　中ボタン・Shift+左: 移動　ダブルクリック: 大きく表示
///   回している間は粗く速く描き、止めると細かく描き直す。
/// </summary>
public sealed class VolumeView : Border
{
    private readonly Grid root = new() { ClipToBounds = true, Background = Brushes.Black };
    private readonly Image image = new() { Stretch = Stretch.Fill };
    private readonly HelixViewport3D helix;
    private readonly ModelVisual3D model = new();
    private readonly TextBlock info = new()
    {
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Bottom,
        Margin = new Thickness(10, 0, 10, 8),
        Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x9A, 0xAA)),
        FontFamily = new FontFamily("Bahnschrift, Segoe UI"),
        FontSize = 11.5,
        IsHitTestVisible = false,
    };
    private readonly DispatcherTimer refine = new() { Interval = TimeSpan.FromMilliseconds(220) };

    private WorkspaceViewModel? vm;
    private WriteableBitmap? bitmap;
    private bool rendering;
    private (bool Pending, bool Preview) next;
    private bool interacting;
    private Point last;
    private bool panning;
    private Volume3DState? lastState;

    private sealed record Volume3DState(object? Volume, Camera Camera, TransferFunction Transfer, ThreeDMode Mode, CropBox Crop, int W, int H, bool Preview);

    public VolumeView()
    {
        Background = Brushes.Black;
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.Linear);
        helix = new HelixViewport3D
        {
            ModelUpDirection = new Vector3D(0, 0, 1),
            ShowViewCube = true,
            ViewCubeFrontText = "A",
            ViewCubeBackText = "P",
            ViewCubeLeftText = "R",
            ViewCubeRightText = "L",
            ViewCubeTopText = "S",
            ViewCubeBottomText = "I",
            IsHeadLightEnabled = true,
            Background = new LinearGradientBrush(Color.FromRgb(0x18, 0x20, 0x2E), Color.FromRgb(0x0C, 0x10, 0x18), 90),
            Visibility = Visibility.Collapsed,
            ZoomExtentsWhenLoaded = false,
        };
        helix.Children.Add(new SunLight());
        helix.Children.Add(model);
        root.Children.Add(image);
        root.Children.Add(helix);
        root.Children.Add(info);
        Child = root;

        refine.Tick += (_, _) =>
        {
            refine.Stop();
            interacting = false;
            Request(preview: false);
        };
        DataContextChanged += (_, _) => Attach(DataContext as WorkspaceViewModel);
        SizeChanged += (_, _) => Request(preview: true, settle: true);
        MouseLeftButtonDown += OnDown;
        MouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle) Begin(e, pan: true);
        };
        MouseMove += OnMove;
        MouseUp += (_, _) => End();
        MouseWheel += OnWheel;
    }

    private void Attach(WorkspaceViewModel? nextVm)
    {
        if (vm is not null) vm.PropertyChanged -= OnVmChanged;
        vm = nextVm;
        if (vm is null) return;
        vm.PropertyChanged += OnVmChanged;
        UpdateMode();
        Request(preview: false);
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(WorkspaceViewModel.Volume):
                model.Content = null;
                Request(preview: false);
                break;
            case nameof(WorkspaceViewModel.Camera3D):
                if (vm!.Mode3D == ThreeDMode.Surface && !interacting) AimHelix();
                else Request(preview: interacting, settle: true);
                break;
            case nameof(WorkspaceViewModel.Transfer):
            case nameof(WorkspaceViewModel.Crop):
                Request(preview: true, settle: true);
                break;
            case nameof(WorkspaceViewModel.Mode3D):
                UpdateMode();
                Request(preview: false);
                break;
            case nameof(WorkspaceViewModel.Surface):
            case nameof(WorkspaceViewModel.SurfaceColor):
                ShowSurface();
                break;
            case nameof(WorkspaceViewModel.IsBuildingSurface):
                UpdateInfo(null);
                break;
        }
    }

    private void UpdateMode()
    {
        bool surface = vm?.Mode3D == ThreeDMode.Surface;
        helix.Visibility = surface ? Visibility.Visible : Visibility.Collapsed;
        image.Visibility = surface ? Visibility.Collapsed : Visibility.Visible;
        if (surface) ShowSurface();
        UpdateInfo(null);
    }

    // ---- ボリュームレンダリング / MIP

    /// <param name="settle">粗く描いたあと、少し待って細かく描き直す</param>
    private void Request(bool preview, bool settle = false)
    {
        if (settle)
        {
            refine.Stop();
            refine.Start();
        }
        if (vm?.Renderer is null || vm.Mode3D == ThreeDMode.Surface || ActualWidth < 4 || ActualHeight < 4)
        {
            if (vm?.Renderer is null) image.Source = null;
            return;
        }
        if (rendering)
        {
            next = (true, next.Pending ? next.Preview && preview : preview);
            return;
        }
        RenderAsync(preview);
    }

    private async void RenderAsync(bool preview)
    {
        var renderer = vm!.Renderer!;
        var dpi = VisualTreeHelper.GetDpi(this);
        double scale = preview ? 0.4 : Math.Min(dpi.DpiScaleX, 1.5);
        int w = Math.Clamp((int)(ActualWidth * scale), 16, 1400), h = Math.Clamp((int)(ActualHeight * scale), 16, 1400);
        var state = new Volume3DState(vm.Volume, vm.Camera3D, vm.Transfer, vm.Mode3D, vm.Crop, w, h, preview);
        if (state == lastState) return;
        rendering = true;
        var settings = new RenderSettings
        {
            Mode = state.Mode == ThreeDMode.Mip ? RenderMode.Mip : RenderMode.Dvr,
            Transfer = state.Transfer,
            Crop = state.Crop,
            StepFactor = preview ? 1.4 : 0.5,
            Shading = true,
            MipWindow = vm.Window.Width > 600 ? vm.Window : new Core.Mpr.WindowLevel(300, 1200),
        };
        var sw = Stopwatch.StartNew();
        try
        {
            var pixels = await Task.Run(() => renderer.Render(state.Camera, settings, w, h));
            if (bitmap is null || bitmap.PixelWidth != w || bitmap.PixelHeight != h)
            {
                bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
                image.Source = bitmap;
            }
            bitmap.WritePixels(new Int32Rect(0, 0, w, h), pixels, w * 4, 0);
            lastState = state;
            UpdateInfo(string.Create(CultureInfo.InvariantCulture, $"{(preview ? "プレビュー" : $"{w}×{h}")}  {sw.ElapsedMilliseconds} ms"));
        }
        finally
        {
            rendering = false;
        }
        if (next.Pending)
        {
            bool p = next.Preview;
            next = default;
            Request(p);
        }
    }

    private void UpdateInfo(string? timing)
    {
        if (vm is null) return;
        string mode = vm.Mode3D switch
        {
            ThreeDMode.Surface => vm.IsBuildingSurface ? "面を作っています…" : "面",
            ThreeDMode.Mip => "MIP",
            _ => $"ボリューム（{vm.Transfer.Name}）",
        };
        info.Text = timing is null ? mode : $"{mode}\n{timing}";
    }

    // ---- 面

    private void ShowSurface()
    {
        if (vm?.Surface is not { } result || result.Mesh.TriangleCount == 0)
        {
            model.Content = null;
            return;
        }
        var mesh = ToWpf(result.Mesh);
        var c = vm.SurfaceColor;
        var color = Color.FromRgb((byte)(c >> 16), (byte)(c >> 8), (byte)c);
        var front = new MaterialGroup();
        front.Children.Add(new DiffuseMaterial(new SolidColorBrush(color)));
        front.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromRgb(70, 70, 70)), 40));
        front.Freeze();
        var back = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb((byte)(color.R / 2), (byte)(color.G / 3), (byte)(color.B / 3))));
        back.Freeze();
        bool first = model.Content is null;
        model.Content = new GeometryModel3D(mesh, front) { BackMaterial = back };
        if (first) AimHelix();
        UpdateInfo(null);
    }

    private static MeshGeometry3D ToWpf(Mesh m)
    {
        var positions = new Point3DCollection(m.VertexCount);
        var normals = new Vector3DCollection(m.VertexCount);
        for (int i = 0; i < m.Positions.Length; i += 3)
        {
            positions.Add(new Point3D(m.Positions[i], m.Positions[i + 1], m.Positions[i + 2]));
            normals.Add(new Vector3D(m.Normals[i], m.Normals[i + 1], m.Normals[i + 2]));
        }
        var indices = new Int32Collection(m.Indices);
        var mesh = new MeshGeometry3D { Positions = positions, Normals = normals, TriangleIndices = indices };
        mesh.Freeze();
        return mesh;
    }

    /// <summary>面の表示のカメラを、ボリュームレンダリングと同じ向きにする</summary>
    private void AimHelix()
    {
        if (vm?.Volume is null) return;
        var (forward, _, up) = vm.Camera3D.Basis();
        var center = vm.Volume.Center;
        double radius = vm.Volume.Corners().Max(cn => Vec3.Distance(cn, center));
        double distance = radius * 2.6 / Math.Max(vm.Camera3D.Zoom, 0.2);
        var pos = center - forward * distance;
        helix.Camera = new PerspectiveCamera(
            new Point3D(pos.X, pos.Y, pos.Z),
            new Vector3D(forward.X * distance, forward.Y * distance, forward.Z * distance),
            new Vector3D(up.X, up.Y, up.Z),
            40);
    }

    // ---- 操作

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (vm is null) return;
        if (e.ClickCount == 2)
        {
            vm.ToggleMaximizeCommand.Execute("3D");
            return;
        }
        if (vm.Mode3D == ThreeDMode.Surface) return; // 面は HelixToolkit の操作に任せる
        Begin(e, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
    }

    private void Begin(MouseButtonEventArgs e, bool pan)
    {
        if (vm?.Mode3D == ThreeDMode.Surface) return;
        panning = pan;
        interacting = true;
        last = e.GetPosition(this);
        CaptureMouse();
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!interacting || vm is null || !IsMouseCaptured) return;
        var p = e.GetPosition(this);
        var d = p - last;
        last = p;
        if (panning)
        {
            double mmPerDip = 2 * (vm.Renderer?.Radius ?? 100) / (Math.Min(ActualWidth, ActualHeight) * vm.Camera3D.Zoom);
            vm.Camera3D = vm.Camera3D with { PanX = vm.Camera3D.PanX - d.X * mmPerDip, PanY = vm.Camera3D.PanY + d.Y * mmPerDip };
        }
        else
        {
            vm.Camera3D = vm.Camera3D.Orbit(d.X * 0.45, d.Y * 0.45);
        }
        Request(preview: true, settle: true);
    }

    private void End()
    {
        if (!IsMouseCaptured) return;
        ReleaseMouseCapture();
        refine.Stop();
        refine.Start();
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (vm is null || vm.Mode3D == ThreeDMode.Surface) return;
        interacting = true;
        vm.Camera3D = vm.Camera3D with { Zoom = Math.Clamp(vm.Camera3D.Zoom * (e.Delta > 0 ? 1.12 : 1 / 1.12), 0.3, 12) };
        Request(preview: true, settle: true);
        e.Handled = true;
    }

    /// <summary>今の表示を画像にする（保存用）</summary>
    public BitmapSource Snapshot()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var bmp = new RenderTargetBitmap((int)(ActualWidth * dpi.DpiScaleX), (int)(ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bmp.Render(this);
        return bmp;
    }
}
