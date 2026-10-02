using System.IO;
using System.Windows;
using System.Windows.Threading;
using FellowOakDicom;
using FellowOakDicom.Imaging.NativeCodec;
using VolumeScope.App.Services;
using VolumeScope.App.ViewModels;

namespace VolumeScope.App;

public partial class App : Application
{
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VolumeScope");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        RegisterCodecs();

        var vm = new WorkspaceViewModel();
        var window = new MainWindow { DataContext = vm };
        MainWindow = window;

        // 見本のデータで画面を画像に保存して終わる: VolumeScope.exe --snapshots フォルダー
        int at = Array.IndexOf(e.Args, "--snapshots");
        if (at >= 0)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            string dir = at + 1 < e.Args.Length ? e.Args[at + 1] : Path.Combine(Environment.CurrentDirectory, "snapshots");
            try
            {
                await SnapshotRunner.RunAsync(window, vm, Path.GetFullPath(dir));
            }
            finally
            {
                Shutdown(0);
            }
            return;
        }

        window.Show();
        // フォルダーやファイルを指定して起動（エクスプローラーの「送る」など）
        var paths = e.Args.Where(a => Directory.Exists(a) || File.Exists(a)).ToList();
        if (paths.Count > 0) await vm.OpenPathsAsync(paths);
    }

    /// <summary>圧縮された DICOM（JPEG・JPEG 2000・JPEG-LS など）を展開できるようにする</summary>
    private static void RegisterCodecs()
    {
        try
        {
            new DicomSetupBuilder()
                .RegisterServices(s => s.AddFellowOakDicom().AddTranscoderManager<NativeTranscoderManager>())
                .SkipValidation()
                .Build();
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or InvalidOperationException or TypeInitializationException)
        {
            Log(ex); // 圧縮されていない DICOM はそのまま読める
        }
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception);
        MessageBox.Show("予期しないエラーが起きました。作業は続けられます。\n\n" + e.Exception.Message, "VolumeScope", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    internal static void Log(Exception ex)
    {
        try
        {
            string dir = Path.Combine(DataDirectory, "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"), $"[{DateTime.Now:O}] {ex}\n\n");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
