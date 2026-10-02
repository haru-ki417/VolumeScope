using System.IO;
using System.Windows;
using System.Windows.Threading;
using VolumeScope.App.ViewModels;
using VolumeScope.Core.Geometry;
using VolumeScope.Core.Mpr;
using VolumeScope.Core.Surface;
using VolumeScope.Core.Volumes;

namespace VolumeScope.App.Services;

/// <summary>
/// 見本の模型で画面を一通り開き、画像に保存して終わる（README の画像づくりと、Windows での表示の確認）。
///   VolumeScope.exe --snapshots フォルダー
/// </summary>
public static class SnapshotRunner
{
    public static async Task RunAsync(MainWindow window, WorkspaceViewModel vm, string dir)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(vm);
        Directory.CreateDirectory(dir);
        var log = new List<string>();
        try
        {
            window.Width = 1600;
            window.Height = 960;
            window.Show();
            await Idle(800);
            Save(window, dir, "01-empty.png", log);

            await vm.SetVolumeAsync(DemoPhantom.Create(1.0));
            // 十字を結節に合わせ、距離と円を置く
            var n = DemoPhantom.NoduleCenter;
            vm.Crosshair = n;
            double r = DemoPhantom.NoduleDiameterMm / 2;
            vm.AddDistance(MprPlane.Axial, n.Z, n - new Vec3(r, 0, 0), n + new Vec3(r, 0, 0));
            vm.Window = WindowPresets.All[1].Window; // 肺野
            await Idle(2500);
            Save(window, dir, "02-bone-volume.png", log);

            vm.SelectedTissue = TissuePresets.Vessels;
            vm.Camera3D = vm.Camera3D with { AzimuthDeg = -35, ElevationDeg = 8 };
            await Idle(2500);
            Save(window, dir, "03-vessels.png", log);

            vm.SelectedTissue = TissuePresets.Bone;
            vm.Mode3D = ThreeDMode.Surface;
            await WaitUntil(() => vm.HasSurface && !vm.IsBuildingSurface, 60_000);
            await Idle(1500);
            Save(window, dir, "04-bone-surface.png", log);
            log.Add("surface: " + vm.SurfaceStats);

            vm.Mode3D = ThreeDMode.Volume;
            vm.SelectedTissue = TissuePresets.Skin;
            vm.CropAxis = CropAxis.AnteriorPosterior;
            vm.CropPosition = 0.45;
            vm.Maximized = "3D";
            await Idle(3000);
            Save(window, dir, "05-skin-cropped-3d.png", log);

            vm.Maximized = null;
            vm.CropAxis = CropAxis.None;
            vm.SelectedTissue = TissuePresets.Bone;
            vm.Mode3D = ThreeDMode.Mip;
            vm.Slab = SlabMode.Mip;
            vm.SlabThickness = 20;
            vm.Window = WindowPresets.All[2].Window;
            await Idle(3000);
            Save(window, dir, "06-mip-slab.png", log);
            log.Add("ok");
        }
        catch (Exception ex)
        {
            log.Add("ERROR: " + ex);
        }
        finally
        {
            try
            {
                await File.WriteAllLinesAsync(Path.Combine(dir, "snapshots.log"), log);
            }
            catch (IOException)
            {
            }
            window.Close();
        }
    }

    private static async Task Idle(int ms)
    {
        await Task.Delay(ms);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < end) await Task.Delay(200);
    }

    private static void Save(Window window, string dir, string name, List<string> log)
    {
        if (window.Content is not FrameworkElement root) return;
        MainWindow.SavePng(root, Path.Combine(dir, name));
        log.Add(name);
    }
}
