using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using VolumeScope.App.ViewModels;

namespace VolumeScope.App.Controls;

/// <summary>
/// CT 値の分布（対数の高さ）と、閾値の線。線をドラッグして閾値を変えられる。
/// 分布の山（空気・脂肪・軟部・骨）を見ながら、境目に閾値を合わせられる。
/// </summary>
public sealed class HistogramView : FrameworkElement
{
    public static readonly DependencyProperty HistogramProperty = DependencyProperty.Register(
        nameof(Histogram), typeof(long[]), typeof(HistogramView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThresholdProperty = DependencyProperty.Register(
        nameof(Threshold), typeof(double), typeof(HistogramView),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(HistogramView), new FrameworkPropertyMetadata(Brushes.Orange, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush Fill = PlaneColors.Brush(Color.FromRgb(0x4A, 0x58, 0x6B));
    private static readonly Brush Axis = PlaneColors.Brush(Color.FromRgb(0x6E, 0x7B, 0x8C));
    private static readonly Typeface Face = new("Bahnschrift");

    public HistogramView()
    {
        Height = 92;
        Cursor = Cursors.SizeWE;
        Focusable = true;
    }

    public long[]? Histogram
    {
        get => (long[]?)GetValue(HistogramProperty);
        set => SetValue(HistogramProperty, value);
    }

    public double Threshold
    {
        get => (double)GetValue(ThresholdProperty);
        set => SetValue(ThresholdProperty, value);
    }

    public Brush Accent
    {
        get => (Brush)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    private const double Bottom = 16;

    private double ToX(double hu) => (hu - WorkspaceViewModel.HistogramMin) / (WorkspaceViewModel.HistogramMax - WorkspaceViewModel.HistogramMin) * ActualWidth;

    private double ToHu(double x) => WorkspaceViewModel.HistogramMin + x / Math.Max(ActualWidth, 1) * (WorkspaceViewModel.HistogramMax - WorkspaceViewModel.HistogramMin);

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        var dc = drawingContext;
        double w = ActualWidth, h = ActualHeight - Bottom;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, ActualHeight));
        if (Histogram is { Length: > 0 } hist)
        {
            double max = Math.Log10(hist.Max() + 1);
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(0, h), true, true);
                for (int i = 0; i < hist.Length; i++)
                {
                    double x = (double)i / hist.Length * w;
                    double y = h - Math.Log10(hist[i] + 1) / Math.Max(max, 1e-9) * (h - 4);
                    ctx.LineTo(new Point(x, y), true, false);
                    ctx.LineTo(new Point((double)(i + 1) / hist.Length * w, y), true, false);
                }
                ctx.LineTo(new Point(w, h), true, false);
            }
            geo.Freeze();
            dc.DrawGeometry(Fill, null, geo);
        }

        // 目盛り
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (int hu in new[] { -1000, 0, 1000, 2000 })
        {
            double x = ToX(hu);
            dc.DrawLine(new Pen(Axis, 1), new Point(x, h), new Point(x, h + 3));
            var t = new FormattedText(hu.ToString(System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Face, 10, Axis, dpi);
            dc.DrawText(t, new Point(Math.Clamp(x - t.Width / 2, 0, w - t.Width), h + 3));
        }

        // 閾値
        double tx = ToX(Threshold);
        var pen = new Pen(Accent, 2);
        dc.DrawLine(pen, new Point(tx, 0), new Point(tx, h));
        dc.DrawEllipse(Accent, null, new Point(tx, 4), 4, 4);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        CaptureMouse();
        Focus();
        SetFrom(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (IsMouseCaptured) SetFrom(e.GetPosition(this).X);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        base.OnMouseLeftButtonUp(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Key is Key.Left or Key.Right)
        {
            Threshold = Math.Round(Threshold + (e.Key == Key.Right ? 10 : -10));
            e.Handled = true;
        }
    }

    private void SetFrom(double x) => Threshold = Math.Round(Math.Clamp(ToHu(x), -1000, 2000) / 5) * 5;
}
