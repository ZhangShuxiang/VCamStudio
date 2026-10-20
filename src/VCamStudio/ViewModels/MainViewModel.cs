using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using VCamStudio.Models;
using VCamStudio.Services;
using VCamStudio.Shared;

namespace VCamStudio.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private static readonly Brush BadgeReadyBrush = CreateBrush(Color.FromRgb(16, 185, 129));
    private static readonly Brush BadgeWarningBrush = CreateBrush(Color.FromRgb(245, 158, 11));
    private static readonly Brush BadgeIdleBrush = CreateBrush(Color.FromRgb(100, 116, 139));

    private readonly ConfigService _configService;
    private readonly CameraComponentService _componentService;
    private readonly FramePump _framePump;
    private readonly SemaphoreSlim _installGate = new(1, 1);

    private CameraTabViewModel? _selectedTab;
    private string _configPath;
    private string _statusMessage = "就绪";
    private InstallState _componentState = InstallState.Unknown;
    private bool _disposed;
    private bool _initialized;

    public MainViewModel(ConfigService configService, CameraComponentService componentService)
    {
        _configService = configService;
        _componentService = componentService;
        _configPath = configService.DefaultPath;

        Tabs = new ObservableCollection<CameraTabViewModel>();
        _framePump = new FramePump();
        _framePump.Tick += OnFrameTick;

        AddTabCommand = new RelayCommand(AddTab, () => CanAddTab);
        CloseTabCommand = new AsyncRelayCommand<CameraTabViewModel>(
            CloseTabAsync,
            tab => tab != null && !tab.IsDefault);
        LoadConfigCommand = new AsyncRelayCommand(LoadConfigAsync);
        SaveConfigCommand = new AsyncRelayCommand(SaveConfigAsync);
        InstallComponentsCommand = new AsyncRelayCommand(InstallComponentsAsync);
        OpenConfigFolderCommand = new RelayCommand(OpenConfigFolder);
        OpenLogFolderCommand = new RelayCommand(OpenLogFolder);
    }

    public ObservableCollection<CameraTabViewModel> Tabs { get; }

    public CameraTabViewModel? SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (ReferenceEquals(_selectedTab, value))
                return;

            if (_selectedTab != null)
                _selectedTab.IsSelected = false;

            _selectedTab = value;

            if (_selectedTab != null)
                _selectedTab.IsSelected = true;

            OnPropertyChanged();
            RefreshFramePump();
        }
    }

    public string ConfigPath
    {
        get => _configPath;
        private set
        {
            if (SetProperty(ref _configPath, value))
                OnPropertyChanged(nameof(ConfigPathDisplay));
        }
    }

    public string ConfigPathDisplay
    {
        get
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home) &&
                (_configPath.Equals(home, StringComparison.OrdinalIgnoreCase) ||
                 _configPath.StartsWith(home + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            {
                return "~" + _configPath[home.Length..];
            }

            return _configPath;
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public InstallState ComponentState
    {
        get => _componentState;
        private set
        {
            if (SetProperty(ref _componentState, value))
            {
                OnPropertyChanged(nameof(ComponentBadgeText));
                OnPropertyChanged(nameof(ComponentBadgeBrush));
            }
        }
    }

    public string ComponentBadgeText => _componentState switch
    {
        InstallState.Ready => "组件已就绪",
        InstallState.NeedsUpdate => "组件需更新",
        InstallState.NotInstalled => "组件未安装",
        _ => "检查中"
    };

    public Brush ComponentBadgeBrush => _componentState switch
    {
        InstallState.Ready => BadgeReadyBrush,
        InstallState.NeedsUpdate => BadgeWarningBrush,
        _ => BadgeIdleBrush
    };

    public bool CanAddTab => Tabs.Count < CameraSlots.Count;

    public bool HasActiveCameras => Tabs.Any(tab => tab.IsCameraOn);

    private bool HasTabsToStop => Tabs.Any(tab => tab.IsCameraOn || tab.IsBusy);

    public RelayCommand AddTabCommand { get; }
    public AsyncRelayCommand<CameraTabViewModel> CloseTabCommand { get; }
    public AsyncRelayCommand LoadConfigCommand { get; }
    public AsyncRelayCommand SaveConfigCommand { get; }
    public AsyncRelayCommand InstallComponentsCommand { get; }
    public RelayCommand OpenConfigFolderCommand { get; }
    public RelayCommand OpenLogFolderCommand { get; }

    public async Task InitializeAsync()
    {
        if (_initialized)
            return;

        _initialized = true;
        AppConfig config;

        string? error = null;
        if (File.Exists(_configPath))
        {
            var loaded = _configService.Load(_configPath, out error);
            config = loaded ?? _configService.CreateDefault();
        }
        else
        {
            config = _configService.CreateDefault();
        }

        BuildTabs(config);
        await RefreshComponentStateAsync();
        RefreshFramePump();
        StatusMessage = "就绪 · 配置文件 " + ConfigPathDisplay;

        if (!File.Exists(_configPath) && !SaveConfigNow())
        {
            // Some managed / locked-down profiles do not allow creating a new
            // dot-file in the profile root. Keep the documented default as the
            // first choice and fall back to LocalAppData only when necessary.
            string fallback = _configService.FallbackPath;
            if (!string.Equals(fallback, _configPath, StringComparison.OrdinalIgnoreCase))
            {
                ConfigPath = fallback;
                if (SaveConfigNow())
                {
                    StatusMessage = "配置文件已保存到 " + ConfigPathDisplay;
                }
                else
                {
                    ConfigPath = _configService.PortablePath;
                    if (SaveConfigNow())
                        StatusMessage = "配置文件已保存到 " + ConfigPathDisplay;
                    else
                        ShowError("无法写入默认配置文件，请在“保存配置”时选择其他位置。\n" + AppLog.FilePath);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(error))
            ShowError("配置加载失败，已使用默认配置。\n" + error);
    }

    private void BuildTabs(AppConfig config)
    {
        SelectedTab = null;

        foreach (var tab in Tabs)
            tab.Dispose();

        Tabs.Clear();

        for (int i = 0; i < config.Tabs.Count; i++)
        {
            var camera = new CameraTabViewModel(this, config.Tabs[i], isDefault: i == 0);
            Tabs.Add(camera);
        }

        if (Tabs.Count == 0)
            Tabs.Add(new CameraTabViewModel(this, _configService.CreateDefault().Tabs[0], isDefault: true));

        SelectedTab = Tabs[Math.Clamp(config.SelectedIndex, 0, Tabs.Count - 1)];
        OnPropertyChanged(nameof(CanAddTab));
        AddTabCommand.RaiseCanExecuteChanged();
    }

    private void AddTab()
    {
        if (!CanAddTab)
            return;

        var used = Tabs.Select(tab => tab.Slot).ToHashSet();
        int slot = Enumerable.Range(0, CameraSlots.Count).First(index => !used.Contains(index));
        var config = new CameraConfig
        {
            Title = $"摄像头 {slot + 1}",
            Slot = slot,
            Width = 1280,
            Height = 720,
            Fps = 30
        };

        var tab = new CameraTabViewModel(this, config, isDefault: false);
        Tabs.Add(tab);
        SelectedTab = tab;
        OnPropertyChanged(nameof(CanAddTab));
        AddTabCommand.RaiseCanExecuteChanged();
        StatusMessage = $"已添加标签页“{tab.Title}”";
    }

    private async Task CloseTabAsync(CameraTabViewModel? tab)
    {
        if (tab == null || tab.IsDefault)
            return;

        if (tab.IsCameraOn || tab.IsBusy)
        {
            var result = MessageBox.Show(
                Application.Current.MainWindow,
                $"“{tab.Title}”的虚拟摄像头仍在运行或正在切换状态。\n\n关闭标签页将先停止该摄像头，是否继续？",
                "关闭标签页",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

            await tab.StopForShutdownAsync();
        }

        bool wasSelected = ReferenceEquals(SelectedTab, tab);
        int index = Tabs.IndexOf(tab);
        if (wasSelected)
            SelectedTab = null;

        Tabs.Remove(tab);
        tab.Dispose();

        if (Tabs.Count > 0 && (wasSelected || SelectedTab == null))
            SelectedTab = Tabs[Math.Clamp(index, 0, Tabs.Count - 1)];

        OnPropertyChanged(nameof(CanAddTab));
        AddTabCommand.RaiseCanExecuteChanged();
        CloseTabCommand.RaiseCanExecuteChanged();
        StatusMessage = "已关闭标签页";
    }

    private async Task LoadConfigAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "加载 VCam Studio 配置",
            Filter = "VCam Studio 配置|*.vcam;*.json|所有文件|*.*",
            InitialDirectory = _configService.DefaultDirectory,
            FileName = Path.GetFileName(_configPath),
            CheckFileExists = true
        };

        if (dialog.ShowDialog() != true)
            return;

        var config = _configService.Load(dialog.FileName, out string? error);
        if (config == null)
        {
            ShowError("无法加载配置文件。\n" + error);
            return;
        }

        // Validate the file before stopping cameras, so a cancelled or invalid
        // load does not interrupt a running session.
        if (!await ConfirmStopAllAsync(
                "加载配置",
                "加载配置会停止当前正在运行或正在切换的所有虚拟摄像头，是否继续？",
                MessageBoxImage.Warning))
        {
            return;
        }

        ConfigPath = dialog.FileName;
        BuildTabs(config);
        StatusMessage = "已加载配置 · " + ConfigPathDisplay;
    }

    private Task SaveConfigAsync()
    {
        if (SaveConfigNow())
            StatusMessage = "配置已保存 · " + ConfigPathDisplay;
        else
            ShowError("保存配置失败，详细信息见日志：\n" + AppLog.FilePath);

        return Task.CompletedTask;
    }

    private bool SaveConfigNow()
    {
        var config = new AppConfig
        {
            Version = 1,
            SelectedIndex = SelectedTab == null ? 0 : Tabs.IndexOf(SelectedTab),
            Tabs = Tabs.Select(tab => tab.ToConfig()).ToList()
        };

        if (_configService.Save(ConfigPath, config, out string? error))
            return true;

        AppLog.Write("ERROR", "保存配置失败: " + error);
        return false;
    }

    private void OpenConfigFolder()
    {
        try
        {
            if (File.Exists(ConfigPath))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{ConfigPath}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo(_configService.DefaultDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowError("无法打开配置目录：" + ex.Message);
        }
    }

    private void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(AppLog.DirectoryPath);
            Process.Start(new ProcessStartInfo(AppLog.DirectoryPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowError("无法打开日志目录：" + ex.Message);
        }
    }

    public async Task<bool> EnsureComponentsInstalledAsync()
    {
        if (_componentService.GetState(out _) == InstallState.Ready)
            return true;

        var result = MessageBox.Show(
            Application.Current.MainWindow,
            "开启虚拟摄像头前需要安装 COM 组件并注册到 Windows。\n\n" +
            "此操作需要管理员权限，是否现在安装/修复？",
            "安装虚拟摄像头组件",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
            return false;

        await InstallComponentsAsync();
        return _componentService.GetState(out _) == InstallState.Ready;
    }

    private async Task InstallComponentsAsync()
    {
        if (!await _installGate.WaitAsync(0))
            return;

        try
        {
            // A tab that is currently starting is not running yet. Stop only
            // cameras that are actually on; waiting for the tab that invoked
            // this install (via EnsureComponentsInstalledAsync) would self-block.
            if (HasActiveCameras)
            {
                var stop = MessageBox.Show(
                    Application.Current.MainWindow,
                    "安装/修复组件前需要停止所有正在运行的虚拟摄像头，是否继续？",
                    "安装组件",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (stop != MessageBoxResult.Yes)
                    return;

                await StopAllCamerasAsync(includeBusy: false);
                await Task.Delay(300);
            }

            StatusMessage = "正在安装虚拟摄像头组件…";
            var (success, message) = await _componentService.InstallAsync();
            await RefreshComponentStateAsync();

            if (success)
            {
                StatusMessage = "虚拟摄像头组件安装完成";
                MessageBox.Show(
                    Application.Current.MainWindow,
                    message + "\n\n现在可以开启虚拟摄像头，并在 Windows 相机、浏览器或会议软件中选择它。",
                    "VCam Studio",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else
            {
                StatusMessage = "组件安装失败";
                ShowError(message);
            }
        }
        finally
        {
            _installGate.Release();
        }
    }

    private async Task RefreshComponentStateAsync()
    {
        InstallState state = await Task.Run(() => _componentService.GetState(out _));
        ComponentState = state;
    }

    private async Task StopAllCamerasAsync(bool includeBusy = true)
    {
        foreach (var tab in Tabs.ToArray())
        {
            if (includeBusy || tab.IsCameraOn)
                await tab.StopForShutdownAsync();
        }

        StatusMessage = HasActiveCameras
            ? "部分虚拟摄像头未能停止，请查看日志"
            : "所有虚拟摄像头已停止";
    }

    /// <summary>
    /// Shows a confirmation when any tab is running or transitioning and stops
    /// all of them. Returns false when the user cancels.
    /// </summary>
    private async Task<bool> ConfirmStopAllAsync(string title, string message, MessageBoxImage image)
    {
        if (!HasTabsToStop)
            return true;

        var result = MessageBox.Show(
            Application.Current.MainWindow,
            message,
            title,
            MessageBoxButton.YesNo,
            image);

        if (result != MessageBoxResult.Yes)
            return false;

        await StopAllCamerasAsync();
        return true;
    }

    public async Task<bool> PrepareForShutdownAsync()
    {
        int busyOrRunning = Tabs.Count(tab => tab.IsCameraOn || tab.IsBusy);
        if (!await ConfirmStopAllAsync(
                "退出 VCam Studio",
                $"还有 {busyOrRunning} 个虚拟摄像头正在运行或正在切换状态。\n\n退出 VCam Studio 将停止它们，是否继续？",
                MessageBoxImage.Question))
        {
            return false;
        }

        SaveConfigNow();
        return true;
    }

    public void NotifyCameraStateChanged()
    {
        OnPropertyChanged(nameof(HasActiveCameras));
        StatusMessage = HasActiveCameras
            ? $"{Tabs.Count(tab => tab.IsCameraOn)} 个虚拟摄像头正在运行"
            : "所有虚拟摄像头已关闭";
        RefreshFramePump();
    }

    /// <summary>
    /// Called by a tab when its camera or preview state changes. The pump is
    /// stopped entirely when no tab needs a frame, so a closed preview with all
    /// cameras off costs no timer ticks or composition work.
    /// </summary>
    public void NotifyRenderDemandChanged() => RefreshFramePump();

    private void RefreshFramePump()
    {
        if (_disposed)
            return;

        if (Tabs.Any(tab => tab.NeedsFrames))
            _framePump.Start();
        else
            _framePump.Stop();
    }

    public void ShowError(string message)
    {
        AppLog.Write("ERROR", message);
        MessageBox.Show(
            Application.Current.MainWindow,
            message,
            "VCam Studio",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private void OnFrameTick(long now)
    {
        // This runs on the UI thread and RenderTick never mutates the tab
        // collection, so iterating the collection directly avoids an array
        // allocation on every pump tick.
        foreach (var tab in Tabs)
        {
            try
            {
                tab.RenderTick(now);
            }
            catch (Exception ex)
            {
                // One broken material/camera must not stop every other preview.
                AppLog.Write("ERROR", $"预览渲染失败 (slot {tab.Slot}): {ex}");
            }
        }
    }

    private static Brush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public void Shutdown()
    {
        if (_disposed)
            return;

        _disposed = true;
        _framePump.Tick -= OnFrameTick;
        _framePump.Dispose();

        foreach (var tab in Tabs)
            tab.Dispose();
    }

    public void Dispose()
    {
        Shutdown();
        _installGate.Dispose();
    }
}
