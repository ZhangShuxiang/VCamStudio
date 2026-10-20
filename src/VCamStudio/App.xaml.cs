using System.Threading;
using System.Windows;
using System.Windows.Threading;
using DirectN;
using VCamStudio.Services;
using VCamStudio.ViewModels;
using MessageBox = System.Windows.MessageBox;

namespace VCamStudio;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private MainViewModel? _viewModel;
    private bool _mfStarted;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppLog.Write("INFO", "VCam Studio 启动。");

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.Write("FATAL", "未处理异常: " + args.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Write("ERROR", "未观察的异步异常: " + args.Exception);
            args.SetObserved();
        };

        if (e.Args.Any(arg => string.Equals(arg, "--install", StringComparison.OrdinalIgnoreCase)))
        {
            int exitCode = Installer.RunInstall();
            Shutdown(exitCode);
            return;
        }

        if (e.Args.Any(arg => string.Equals(arg, "--uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            int exitCode = Installer.RunUninstall();
            Shutdown(exitCode);
            return;
        }

        bool createdNew;
        _instanceMutex = new Mutex(true, @"Local\VCamStudio.SingleInstance", out createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "VCam Studio 已经在运行。",
                "VCam Studio",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        try
        {
            MFFunctions.MFStartup();
            _mfStarted = true;
        }
        catch (Exception ex)
        {
            AppLog.Write("ERROR", "Media Foundation 初始化失败: " + ex);
        }

        _viewModel = new MainViewModel(new ConfigService(), new CameraComponentService());

        var window = new MainWindow(_viewModel);
        MainWindow = window;
        window.Show();

        _ = InitializeViewModelAsync(_viewModel);
    }

    private static async Task InitializeViewModelAsync(MainViewModel viewModel)
    {
        try
        {
            await viewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            AppLog.Write("ERROR", "初始化失败: " + ex);
            MessageBox.Show(
                "VCam Studio 初始化失败：\n\n" + ex.Message + "\n\n详细信息已写入日志：\n" + AppLog.FilePath,
                "VCam Studio",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _viewModel?.Shutdown();
            if (_mfStarted)
                MFFunctions.MFShutdown();
        }
        catch (Exception ex)
        {
            AppLog.Write("WARN", "退出清理失败: " + ex.Message);
        }

        _instanceMutex?.Dispose();
        _instanceMutex = null;
        AppLog.Write("INFO", "VCam Studio 退出。");
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Write("ERROR", "UI 线程未处理异常: " + e.Exception);
        MessageBox.Show(
            "发生了一个未处理的错误：\n\n" + e.Exception.Message + "\n\n详细信息已写入日志：\n" + AppLog.FilePath,
            "VCam Studio",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
