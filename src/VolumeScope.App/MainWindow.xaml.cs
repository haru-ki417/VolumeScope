using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using VolumeScope.App.ViewModels;
using VolumeScope.Core.Dicom;

namespace VolumeScope.App;

public partial class MainWindow : Window
{
    private WorkspaceViewModel? vm;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (vm is not null) vm.PropertyChanged -= OnVmChanged;
            vm = DataContext as WorkspaceViewModel;
            if (vm is not null) vm.PropertyChanged += OnVmChanged;
        };
        Drop += OnDrop;
        KeyDown += OnKey;
    }

    /// <summary>
    /// ふつうに起動したとき：最大化して開く。最大化を戻したときも、画面（作業領域）からはみ出さない大きさにする。
    /// （画面の拡大率が大きい PC では、決めておいた大きさより画面のほうが小さいことがある）
    /// </summary>
    public void StartMaximized()
    {
        var area = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, area.Width);
        MinHeight = Math.Min(MinHeight, area.Height);
        Width = Math.Max(MinWidth, Math.Min(Width, area.Width * 0.92));
        Height = Math.Max(MinHeight, Math.Min(Height, area.Height * 0.92));
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowState = WindowState.Maximized;
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceViewModel.Maximized)) ApplyLayout(vm!.Maximized);
    }

    /// <summary>1 つの画面を大きく表示する / 4 分割に戻す</summary>
    private void ApplyLayout(string? maximized)
    {
        var views = new (FrameworkElement View, string Key, int Row, int Col)[]
        {
            (AxialView, "Axial", 0, 0), (CoronalView, "Coronal", 0, 1), (SagittalView, "Sagittal", 1, 0), (ThreeDView, "3D", 1, 1),
        };
        foreach (var (view, key, row, col) in views)
        {
            bool show = maximized is null || maximized == key;
            view.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            Grid.SetRow(view, maximized == key ? 0 : row);
            Grid.SetColumn(view, maximized == key ? 0 : col);
            Grid.SetRowSpan(view, maximized == key ? 2 : 1);
            Grid.SetColumnSpan(view, maximized == key ? 2 : 1);
            view.Margin = maximized == key ? new Thickness(0) : new Thickness(col == 0 ? 0 : 1, row == 0 ? 0 : 1, col == 0 ? 1 : 0, row == 0 ? 1 : 0);
        }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && vm is not null)
        {
            if (vm.IsPickingSeries) vm.IsPickingSeries = false;
            else vm.Maximized = null;
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (vm is null || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0) await vm.OpenPathsAsync(paths);
    }

    private void OnSeriesDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SeriesList.SelectedItem is SeriesInfo s) vm?.PickSeriesCommand.Execute(s);
    }

    /// <summary>4 つの画面（または大きく表示している画面）を PNG で保存</summary>
    private void OnSaveScreen(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "画面を保存",
            Filter = "PNG 画像 (*.png)|*.png",
            FileName = $"volumescope_{DateTime.Now:yyyyMMdd_HHmmss}.png",
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            SavePng(Viewports, dialog.FileName);
            if (vm is not null) vm.Status = $"{Path.GetFileName(dialog.FileName)} に保存しました。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (vm is not null) vm.Status = "保存できませんでした: " + ex.Message;
        }
    }

    internal static void SavePng(FrameworkElement element, string path)
    {
        var dpi = VisualTreeHelper.GetDpi(element);
        var bmp = new RenderTargetBitmap((int)(element.ActualWidth * dpi.DpiScaleX), (int)(element.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bmp.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }
}
