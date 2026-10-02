using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using VolumeScope.App.ViewModels;
using VolumeScope.Core.Geometry;
using VolumeScope.Core.Mpr;

namespace VolumeScope.App.Controls;

/// <summary>
/// 1 つの断面の画面。
///   左ボタン: 十字を動かす / 距離・円を置く（道具による）　ホイール: 断面を送る　Ctrl+ホイール: 拡大
///   右ボタンでドラッグ: 濃淡（左右 = 幅、上下 = レベル）　中ボタン・Shift+左: 移動　ダブルクリック: 大きく表示
/// </summary>
public sealed class MprView : Border
{
    public static readonly DependencyProperty PlaneProperty = DependencyProperty.Register(
        nameof(Plane), typeof(MprPlane), typeof(MprView), new PropertyMetadata(MprPlane.Axial, (d, _) => ((MprView)d).OnPlaneChanged()));

    private static readonly FontFamily NumberFont = new("Bahnschrift, Segoe UI");
    private readonly Grid root = new() { ClipToBounds = true, Background = Brushes.Black };
    private readonly Image image = new() { Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly Canvas overlay = new() { IsHitTestVisible = false };
    private readonly TextBlock topLeft = Corner(HorizontalAlignment.Left, VerticalAlignment.Top);
    private readonly TextBlock topRight = Corner(HorizontalAlignment.Right, VerticalAlignment.Top);
    private readonly TextBlock bottomLeft = Corner(HorizontalAlignment.Left, VerticalAlignment.Bottom);
    private readonly TextBlock bottomRight = Corner(HorizontalAlignment.Right, VerticalAlignment.Bottom);
    private readonly Border strip = new() { Height = 3, VerticalAlignment = VerticalAlignment.Top };

    private WorkspaceViewModel? vm;
    private MprSlice? slice;
    private WriteableBitmap? bitmap;
    private byte[] pixels = [];
    private double renderedPosition = double.NaN;
    private bool rendering, dirty;
    private double zoom = 1;
    private Vector pan;

    private enum Drag { None, Crosshair, Window, Pan, Distance, Circle }

    private Drag drag;
    private Point dragStart, lastMouse;
    private Vec3 measureStart, measureEnd;
    private WindowLevel windowAtStart;
    private string cursorText = "";

    public MprView()
    {
        Background = Brushes.Black;
        Focusable = true;
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var imageHost = new Canvas();
        imageHost.Children.Add(image);
        root.Children.Add(imageHost);
        root.Children.Add(overlay);
        foreach (var t in new[] { topLeft, topRight, bottomLeft, bottomRight }) root.Children.Add(t);
        root.Children.Add(strip);
        Child = root;
        OnPlaneChanged();

        DataContextChanged += (_, _) => Attach(DataContext as WorkspaceViewModel);
        SizeChanged += (_, _) => { UpdateTransform(); UpdateOverlay(); };
        MouseLeftButtonDown += OnLeftDown;
        MouseRightButtonDown += OnRightDown;
        MouseDown += OnMiddleDown;
        MouseMove += OnMove;
        MouseUp += OnUp;
        MouseWheel += OnWheel;
        MouseLeave += (_, _) => { cursorText = ""; UpdateText(); };
    }

    public MprPlane Plane
    {
        get => (MprPlane)GetValue(PlaneProperty);
        set => SetValue(PlaneProperty, value);
    }

    private Vec3 Normal => Mpr.Normal(Plane);

    private void OnPlaneChanged()
    {
        strip.Background = PlaneColors.Brush(PlaneColors.Of(Plane));
        renderedPosition = double.NaN;
        RequestSlice();
    }

    private static TextBlock Corner(HorizontalAlignment h, VerticalAlignment v) => new()
    {
        HorizontalAlignment = h,
        VerticalAlignment = v,
        Margin = new Thickness(10, 9, 10, 8),
        Foreground = new SolidColorBrush(Color.FromRgb(0xC9, 0xD2, 0xDE)),
        FontFamily = NumberFont,
        FontSize = 12.5,
        TextAlignment = h == HorizontalAlignment.Right ? TextAlignment.Right : TextAlignment.Left,
        IsHitTestVisible = false,
        Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 3, ShadowDepth = 0, Opacity = 0.9, Color = Colors.Black },
    };

    private void Attach(WorkspaceViewModel? next)
    {
        if (vm is not null)
        {
            vm.PropertyChanged -= OnVmChanged;
            vm.Measurements.CollectionChanged -= OnMeasurementsChanged;
        }
        vm = next;
        if (vm is null) return;
        vm.PropertyChanged += OnVmChanged;
        vm.Measurements.CollectionChanged += OnMeasurementsChanged;
        renderedPosition = double.NaN;
        RequestSlice();
    }

    private void OnMeasurementsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => UpdateOverlay();

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(WorkspaceViewModel.Volume):
                zoom = 1;
                pan = default;
                renderedPosition = double.NaN;
                slice = null;
                image.Source = null;
                RequestSlice();
                break;
            case nameof(WorkspaceViewModel.Crosshair):
                if (vm?.Volume is not null && (double.IsNaN(renderedPosition) || Math.Abs(Vec3.Dot(vm.Crosshair, Normal) - renderedPosition) > 1e-6)) RequestSlice();
                else UpdateOverlay();
                break;
            case nameof(WorkspaceViewModel.Slab):
            case nameof(WorkspaceViewModel.SlabThickness):
                renderedPosition = double.NaN;
                RequestSlice();
                break;
            case nameof(WorkspaceViewModel.Window):
                Repaint();
                break;
            case nameof(WorkspaceViewModel.Tool):
                Cursor = vm?.Tool == ToolMode.Crosshair ? Cursors.Cross : Cursors.Pen;
                break;
        }
    }

    // ---- 描画

    /// <summary>断面を作り直す（重い処理は別のスレッド。作っている間に頼まれたら、終わってから最新の状態でもう一度）</summary>
    private async void RequestSlice()
    {
        if (vm?.Volume is not { } volume)
        {
            UpdateText();
            overlay.Children.Clear();
            return;
        }
        if (rendering)
        {
            dirty = true;
            return;
        }
        rendering = true;
        try
        {
            var plane = Plane;
            var through = vm.Crosshair;
            var mode = vm.Slab;
            double thickness = vm.SlabThickness;
            var result = await Task.Run(() => Mpr.Reslice(volume, plane, through, mode, thickness));
            if (vm?.Volume == volume)
            {
                slice = result;
                renderedPosition = Vec3.Dot(through, Normal);
                Repaint();
                // 作っている間に状態が変わっていたら、もう一度
                if (Math.Abs(Vec3.Dot(vm.Crosshair, Normal) - renderedPosition) > 1e-6 || vm.Slab != mode || vm.SlabThickness != thickness) dirty = true;
            }
            else
            {
                dirty = true;
            }
        }
        finally
        {
            rendering = false;
        }
        if (dirty)
        {
            dirty = false;
            RequestSlice();
        }
    }

    private void Repaint()
    {
        if (slice is null || vm is null) return;
        if (bitmap is null || bitmap.PixelWidth != slice.Width || bitmap.PixelHeight != slice.Height)
        {
            bitmap = new WriteableBitmap(slice.Width, slice.Height, 96, 96, PixelFormats.Bgra32, null);
            pixels = new byte[slice.Width * slice.Height * 4];
            image.Source = bitmap;
            image.Width = slice.Width;
            image.Height = slice.Height;
        }
        slice.RenderBgra(vm.Window, pixels);
        bitmap.WritePixels(new Int32Rect(0, 0, slice.Width, slice.Height), pixels, slice.Width * 4, 0);
        UpdateTransform();
        UpdateOverlay();
    }

    private double Scale => slice is null ? 1 : Math.Min(ActualWidth / slice.Width, ActualHeight / slice.Height) * zoom;

    private Point Offset => slice is null ? default
        : new Point(ActualWidth / 2 + pan.X - slice.Width * Scale / 2, ActualHeight / 2 + pan.Y - slice.Height * Scale / 2);

    private void UpdateTransform()
    {
        if (slice is null) return;
        var o = Offset;
        image.RenderTransform = new MatrixTransform(Scale, 0, 0, Scale, o.X, o.Y);
    }

    private Point ToScreen(Vec3 p)
    {
        var (x, y) = slice!.PatientToPixel(p);
        var o = Offset;
        return new Point(o.X + (x + 0.5) * Scale, o.Y + (y + 0.5) * Scale);
    }

    private Vec3 ToPatient(Point s)
    {
        var o = Offset;
        return slice!.PixelToPatient((s.X - o.X) / Scale - 0.5, (s.Y - o.Y) / Scale - 0.5);
    }

    private void UpdateOverlay()
    {
        overlay.Children.Clear();
        UpdateText();
        if (slice is null || vm?.Volume is null) return;

        // 十字: ほかの 2 つの断面の位置を、その断面の色の線で
        var c = ToScreen(vm.Crosshair);
        foreach (var other in Enum.GetValues<MprPlane>().Where(p => p != Plane))
        {
            var n = Mpr.Normal(other);
            bool vertical = Math.Abs(Vec3.Dot(n, slice.Right)) > Math.Abs(Vec3.Dot(n, slice.Down));
            var brush = PlaneColors.Brush(PlaneColors.Of(other));
            const double gap = 14;
            if (vertical)
            {
                AddLine(c.X, 0, c.X, c.Y - gap, brush, 1.2);
                AddLine(c.X, c.Y + gap, c.X, ActualHeight, brush, 1.2);
            }
            else
            {
                AddLine(0, c.Y, c.X - gap, c.Y, brush, 1.2);
                AddLine(c.X + gap, c.Y, ActualWidth, c.Y, brush, 1.2);
            }
        }

        // 向きの文字（R/L, A/P, S/I）
        AddLetter(Mpr.OrientationLetter(-slice.Right), 8, ActualHeight / 2 - 9, false);
        AddLetter(Mpr.OrientationLetter(slice.Right), ActualWidth - 20, ActualHeight / 2 - 9, false);
        AddLetter(Mpr.OrientationLetter(-slice.Down), ActualWidth / 2 - 5, 24, false);
        AddLetter(Mpr.OrientationLetter(slice.Down), ActualWidth / 2 - 5, ActualHeight - 26, false);

        // 目盛り（1 cm または 5 cm）
        double mmPerDip = slice.PixelSize / Scale;
        double barMm = 100 / mmPerDip > 160 ? 50 : 10;
        double barLen = barMm / mmPerDip;
        double bx = ActualWidth - 16 - barLen, by = ActualHeight - 34;
        var bar = PlaneColors.Brush(Color.FromArgb(200, 0xC9, 0xD2, 0xDE));
        AddLine(bx, by, bx + barLen, by, bar, 1.5);
        AddLine(bx, by - 4, bx, by + 4, bar, 1.5);
        AddLine(bx + barLen, by - 4, bx + barLen, by + 4, bar, 1.5);
        AddText($"{barMm / 10:0} cm", bx + barLen / 2 - 14, by - 20, bar, 11.5);

        // この断面に置いた計測
        double pos = Vec3.Dot(vm.Crosshair, Normal);
        double tolerance = Mpr.Range(vm.Volume, Plane).Step / 2 + 1e-6;
        var yellow = PlaneColors.Brush(PlaneColors.Measure);
        foreach (var m in vm.Measurements.Where(m => m.Plane == Plane && Math.Abs(m.PlanePosition - pos) <= tolerance))
            DrawMeasurement(m, yellow);

        // 置いている途中の計測
        if (drag == Drag.Distance) DrawDistance(measureStart, measureEnd, yellow, null);
        if (drag == Drag.Circle) DrawCircle(measureStart, Vec3.Distance(measureStart, measureEnd), yellow, null);
    }

    private void DrawMeasurement(MeasurementItem m, Brush brush)
    {
        switch (m)
        {
            case DistanceMeasurement d:
                DrawDistance(d.A, d.B, brush, $"#{d.Number}  {d.Value}");
                break;
            case CircleMeasurement c:
                DrawCircle(c.Center, c.RadiusMm, brush, $"#{c.Number}  {c.Value}  SD {c.Stats.StandardDeviation:0}");
                break;
        }
    }

    private void DrawDistance(Vec3 a, Vec3 b, Brush brush, string? label)
    {
        var pa = ToScreen(a);
        var pb = ToScreen(b);
        AddLine(pa.X, pa.Y, pb.X, pb.Y, brush, 1.5);
        foreach (var p in new[] { pa, pb }) AddDot(p, brush);
        label ??= string.Create(CultureInfo.InvariantCulture, $"{Vec3.Distance(a, b):0.0} mm");
        AddText(label, Math.Max(pa.X, pb.X) + 8, (pa.Y + pb.Y) / 2 - 9, brush, 13);
    }

    private void DrawCircle(Vec3 center, double radiusMm, Brush brush, string? label)
    {
        var pc = ToScreen(center);
        double r = radiusMm / slice!.PixelSize * Scale;
        var e = new Ellipse { Width = 2 * r, Height = 2 * r, Stroke = brush, StrokeThickness = 1.5 };
        Canvas.SetLeft(e, pc.X - r);
        Canvas.SetTop(e, pc.Y - r);
        overlay.Children.Add(e);
        label ??= string.Create(CultureInfo.InvariantCulture, $"半径 {radiusMm:0.0} mm");
        AddText(label, pc.X + r + 6, pc.Y - 9, brush, 13);
    }

    private void AddLine(double x1, double y1, double x2, double y2, Brush brush, double thickness) =>
        overlay.Children.Add(new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = brush, StrokeThickness = thickness, SnapsToDevicePixels = true });

    private void AddDot(Point p, Brush brush)
    {
        var e = new Ellipse { Width = 6, Height = 6, Fill = brush };
        Canvas.SetLeft(e, p.X - 3);
        Canvas.SetTop(e, p.Y - 3);
        overlay.Children.Add(e);
    }

    private void AddText(string text, double x, double y, Brush brush, double size)
    {
        var t = new TextBlock
        {
            Text = text,
            Foreground = brush,
            FontFamily = NumberFont,
            FontSize = size,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 3, ShadowDepth = 0, Opacity = 1, Color = Colors.Black },
        };
        Canvas.SetLeft(t, x);
        Canvas.SetTop(t, y);
        overlay.Children.Add(t);
    }

    private void AddLetter(string letter, double x, double y, bool _) =>
        AddText(letter, x, y, PlaneColors.Brush(Color.FromRgb(0xA9, 0xB4, 0xC2)), 15);

    private void UpdateText()
    {
        if (vm?.Volume is null || slice is null)
        {
            topLeft.Text = topRight.Text = bottomLeft.Text = bottomRight.Text = "";
            return;
        }
        var color = PlaneColors.Brush(PlaneColors.Of(Plane));
        topLeft.Inlines.Clear();
        topLeft.Inlines.Add(new System.Windows.Documents.Run(Names.Plane(Plane)) { Foreground = color, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("BIZ UDPGothic, Yu Gothic UI") });
        double pos = Vec3.Dot(vm.Crosshair, Normal);
        string axisName = Plane switch { MprPlane.Axial => "頭尾", MprPlane.Coronal => "前後", _ => "左右" };
        topLeft.Inlines.Add(new System.Windows.Documents.Run(string.Create(CultureInfo.InvariantCulture, $"\n{axisName} {pos:0.0} mm")));
        string slab = vm.Slab == SlabMode.Thin ? "" : string.Create(CultureInfo.InvariantCulture, $"\n{(vm.Slab == SlabMode.Mip ? "MIP" : "平均")} {vm.SlabThickness:0} mm");
        topRight.Text = string.Create(CultureInfo.InvariantCulture, $"W {vm.Window.Width:0}  L {vm.Window.Level:0}{slab}");
        bottomLeft.Text = cursorText;
        bottomRight.Text = string.Create(CultureInfo.InvariantCulture, $"{zoom * 100:0}%");
        bottomRight.Margin = new Thickness(10, 0, 10, 48);
    }

    // ---- 操作

    private void OnLeftDown(object sender, MouseButtonEventArgs e)
    {
        if (slice is null || vm is null) return;
        Focus();
        if (e.ClickCount == 2)
        {
            vm.ToggleMaximizeCommand.Execute(Plane.ToString());
            return;
        }
        var p = e.GetPosition(this);
        dragStart = lastMouse = p;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            drag = Drag.Pan;
        else
            switch (vm.Tool)
            {
                case ToolMode.Distance:
                    drag = Drag.Distance;
                    measureStart = measureEnd = ToPatient(p);
                    break;
                case ToolMode.Circle:
                    drag = Drag.Circle;
                    measureStart = measureEnd = ToPatient(p);
                    break;
                default:
                    drag = Drag.Crosshair;
                    MoveCrosshair(p);
                    break;
            }
        CaptureMouse();
        e.Handled = true;
    }

    private void OnRightDown(object sender, MouseButtonEventArgs e)
    {
        if (vm is null) return;
        drag = Drag.Window;
        dragStart = lastMouse = e.GetPosition(this);
        windowAtStart = vm.Window;
        CaptureMouse();
        e.Handled = true;
    }

    private void OnMiddleDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        drag = Drag.Pan;
        lastMouse = e.GetPosition(this);
        CaptureMouse();
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (slice is null || vm is null) return;
        var p = e.GetPosition(this);
        switch (drag)
        {
            case Drag.Crosshair:
                MoveCrosshair(p);
                break;
            case Drag.Window:
                var d = p - dragStart;
                vm.Window = new WindowLevel(windowAtStart.Level - d.Y * 2, Math.Max(1, windowAtStart.Width + d.X * 4));
                break;
            case Drag.Pan:
                pan += p - lastMouse;
                UpdateTransform();
                UpdateOverlay();
                break;
            case Drag.Distance:
            case Drag.Circle:
                measureEnd = ToPatient(p);
                UpdateOverlay();
                break;
        }
        lastMouse = p;

        // 指している位置の値
        var q = ToPatient(p);
        var (x, y) = slice.PatientToPixel(q);
        int ix = (int)Math.Round(x), iy = (int)Math.Round(y);
        cursorText = ix >= 0 && iy >= 0 && ix < slice.Width && iy < slice.Height
            ? string.Create(CultureInfo.InvariantCulture, $"{slice[ix, iy]:0} HU\n{q.X:0.0}, {q.Y:0.0}, {q.Z:0.0} mm")
            : "";
        bottomLeft.Text = cursorText;
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (vm is not null && slice is not null)
        {
            double planePos = Vec3.Dot(vm.Crosshair, Normal);
            if (drag == Drag.Distance && Vec3.Distance(measureStart, measureEnd) > slice.PixelSize * 2)
                vm.AddDistance(Plane, planePos, measureStart, measureEnd);
            if (drag == Drag.Circle && Vec3.Distance(measureStart, measureEnd) > slice.PixelSize * 2)
                vm.AddCircle(slice, planePos, measureStart, Vec3.Distance(measureStart, measureEnd));
        }
        drag = Drag.None;
        ReleaseMouseCapture();
        UpdateOverlay();
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (slice is null || vm?.Volume is null) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            // カーソルの位置を中心に拡大
            var p = e.GetPosition(this);
            var before = ToPatient(p);
            zoom = Math.Clamp(zoom * (e.Delta > 0 ? 1.15 : 1 / 1.15), 0.25, 16);
            var after = ToScreen(before);
            pan += p - after;
            UpdateTransform();
            UpdateOverlay();
        }
        else
        {
            var (min, max, step) = Mpr.Range(vm.Volume, Plane);
            double pos = Vec3.Dot(vm.Crosshair, Normal);
            double next = Math.Clamp(pos + Math.Sign(e.Delta) * step, min, max);
            vm.Crosshair += Normal * (next - pos);
        }
        e.Handled = true;
    }

    private void MoveCrosshair(Point p)
    {
        // 断面の中の位置だけ変え、この断面の位置（法線方向）はそのまま
        var q = ToPatient(p);
        var n = Normal;
        vm!.Crosshair = q + n * (Vec3.Dot(vm.Crosshair, n) - Vec3.Dot(q, n));
    }

    /// <summary>今の表示を画像にする（保存用）</summary>
    public RenderTargetBitmap Snapshot()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var bmp = new RenderTargetBitmap((int)(ActualWidth * dpi.DpiScaleX), (int)(ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bmp.Render(this);
        return bmp;
    }
}
