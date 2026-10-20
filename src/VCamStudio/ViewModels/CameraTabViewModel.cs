using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using VCamStudio.Models;
using VCamStudio.Rendering;
using VCamStudio.Services;
using VCamStudio.Shared;

namespace VCamStudio.ViewModels;

public sealed class CameraTabViewModel : ObservableObject, IDisposable
{
    private static readonly IReadOnlyList<FitModeOption> AllFitModes =
    [
        new(FitMode.Contain, "适应"),
        new(FitMode.Cover, "填充"),
        new(FitMode.Stretch, "拉伸")
    ];

    private static readonly Brush OutputBackgroundBrush =
        Freeze(new SolidColorBrush(Color.FromRgb(7, 12, 25)));

    private static readonly Brush StatusOff = Freeze(new SolidColorBrush(Color.FromRgb(100, 116, 139)));
    private static readonly Brush StatusStarting = Freeze(new SolidColorBrush(Color.FromRgb(245, 158, 11)));
    private static readonly Brush StatusLive = Freeze(new SolidColorBrush(Color.FromRgb(16, 185, 129)));
    private static readonly Brush StatusError = Freeze(new SolidColorBrush(Color.FromRgb(239, 68, 68)));

    private readonly MainViewModel _host;
    private readonly FrameComposer _composer;
    private readonly SharedFrameWriter _writer = new();

    private VirtualCameraController? _controller;
    private bool _isCameraOn;
    private bool _isBusy;
    private bool _isSelected;
    private bool _isPreviewVisible;
    private bool _disposed;
    private readonly string _title;
    private string? _mediaPath;
    private MediaKind _mediaKind;
    private string? _cameraError;
    private string _cameraStatus = "已关闭";
    private Brush _cameraStatusBrush = StatusOff;
    private string _mediaStatus = "测试画面";
    private ResolutionOption _selectedResolution;
    private FrameRateOption _selectedFrameRate;
    private FitModeOption _selectedFitMode;
    private bool _mirror;
    private int _width;
    private int _height;

    private RenderTargetBitmap? _renderBitmap;
    private WriteableBitmap? _previewBitmap;
    private byte[]? _pixelBuffer;
    private ImageSource? _previewSource;
    private long _frameNumber;
    private long _nextFrameTicks;
    private long _nextBridgeAttemptTicks;
    private CancellationTokenSource? _restartCts;

    public CameraTabViewModel(MainViewModel host, CameraConfig config, bool isDefault)
    {
        _host = host;
        _title = string.IsNullOrWhiteSpace(config.Title) ? $"摄像头 {config.Slot + 1}" : config.Title;
        Slot = config.Slot;
        IsDefault = isDefault;
        _width = config.Width;
        _height = config.Height;
        _mirror = config.Mirror;
        _isPreviewVisible = config.ShowPreview;

        _composer = new FrameComposer($"{_title} · 通道 {Slot + 1}");
        _composer.StateChanged += OnComposerStateChanged;

        Resolutions = new ObservableCollection<ResolutionOption>
        {
            new(1920, 1080),
            new(1280, 720),
            new(960, 540),
            new(640, 360)
        };
        EnsureResolution(_width, _height);
        _selectedResolution = Resolutions.First(option => option.Width == _width && option.Height == _height);

        FrameRates = new ObservableCollection<FrameRateOption>
        {
            new(30),
            new(25),
            new(24),
            new(15)
        };
        if (FrameRates.All(option => option.Value != config.Fps))
            FrameRates.Add(new FrameRateOption(config.Fps));
        _selectedFrameRate = FrameRates.First(option => option.Value == config.Fps);

        FitModes = new ObservableCollection<FitModeOption>(AllFitModes);
        _selectedFitMode = FitModes.First(option => option.Value == config.FitMode);

        ChooseMediaCommand = new RelayCommand(ChooseMedia);
        ClearMediaCommand = new RelayCommand(ClearMedia, () => HasMedia || !string.IsNullOrWhiteSpace(_mediaPath));
        TogglePreviewCommand = new RelayCommand(() => IsPreviewVisible = !IsPreviewVisible);

        if (!string.IsNullOrWhiteSpace(config.MediaPath))
            SetMediaFromPath(config.MediaPath, showError: false);
    }

    public int Slot { get; }
    public string Title => _title;
    public string SlotLabel => $"通道 {Slot + 1}";
    public string ConfigSourceId => CameraSlots.GetSourceId(Slot);
    public bool IsClosable => !IsDefault;
    public bool IsDefault { get; }

    public ObservableCollection<ResolutionOption> Resolutions { get; }
    public ObservableCollection<FrameRateOption> FrameRates { get; }
    public ObservableCollection<FitModeOption> FitModes { get; }

    public ResolutionOption SelectedResolution
    {
        get => _selectedResolution;
        set
        {
            if (value == null || !SetProperty(ref _selectedResolution, value))
                return;

            _width = value.Width;
            _height = value.Height;
            OnPropertyChanged(nameof(ResolutionText));
            OnPropertyChanged(nameof(PreviewInfo));
            ScheduleSettingsRestart();
        }
    }

    public FrameRateOption SelectedFrameRate
    {
        get => _selectedFrameRate;
        set
        {
            if (value == null || !SetProperty(ref _selectedFrameRate, value))
                return;

            OnPropertyChanged(nameof(FrameRateText));
            OnPropertyChanged(nameof(PreviewInfo));
            ScheduleSettingsRestart();
        }
    }

    public FitModeOption SelectedFitMode
    {
        get => _selectedFitMode;
        set
        {
            if (value != null && SetProperty(ref _selectedFitMode, value))
                OnPropertyChanged(nameof(PreviewInfo));
        }
    }

    public bool Mirror
    {
        get => _mirror;
        set => SetProperty(ref _mirror, value);
    }

    public int Fps => _selectedFrameRate.Value;
    public string ResolutionText => $"{_width} × {_height}";
    public string FrameRateText => $"{Fps} fps";
    public string PreviewInfo => $"{ResolutionText} · {FrameRateText} · {_selectedFitMode.Display}";

    public RelayCommand ChooseMediaCommand { get; }
    public RelayCommand ClearMediaCommand { get; }
    public RelayCommand TogglePreviewCommand { get; }

    /// <summary>Whether the on-screen preview is shown. Closing it suspends
    /// preview rendering and frees the preview bitmap, while the virtual camera
    /// keeps producing frames when it is running.</summary>
    public bool IsPreviewVisible
    {
        get => _isPreviewVisible;
        set
        {
            if (!SetProperty(ref _isPreviewVisible, value))
                return;

            OnPropertyChanged(nameof(NeedsFrames));

            if (value)
            {
                _nextFrameTicks = 0;
                if (IsSelected || IsCameraOn)
                    _composer.Play();
            }
            else
            {
                if (!IsCameraOn)
                    _composer.Pause();

                ReleasePreviewResources();
            }

            _host.NotifyRenderDemandChanged();
        }
    }

    public bool NeedsFrames => !_disposed && (IsCameraOn || (IsSelected && IsPreviewVisible));

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!SetProperty(ref _isSelected, value))
                return;

            OnPropertyChanged(nameof(NeedsFrames));

            if (value)
            {
                _nextFrameTicks = 0;
                if (IsPreviewVisible || IsCameraOn)
                    _composer.Play();
            }
            else
            {
                if (!IsCameraOn)
                    _composer.Pause();

                ReleasePreviewResources();
            }

            _host.NotifyRenderDemandChanged();
        }
    }

    public bool IsCameraOn
    {
        get => _isCameraOn;
        private set
        {
            if (!SetProperty(ref _isCameraOn, value))
                return;

            OnPropertyChanged(nameof(CameraStatusShort));
            OnPropertyChanged(nameof(NeedsFrames));

            if (value)
            {
                _nextFrameTicks = 0;
                _composer.Play();
            }
            else if (!IsSelected || !IsPreviewVisible)
            {
                _composer.Pause();
                ReleasePreviewResources();
            }

            _host.NotifyRenderDemandChanged();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
                OnPropertyChanged(nameof(IsNotBusy));
        }
    }

    public bool IsNotBusy => !_isBusy;

    public string CameraStatus
    {
        get => _cameraStatus;
        private set => SetProperty(ref _cameraStatus, value);
    }

    public Brush CameraStatusBrush
    {
        get => _cameraStatusBrush;
        private set => SetProperty(ref _cameraStatusBrush, value);
    }

    public string CameraStatusShort => IsCameraOn ? "LIVE" : "OFF";

    public string? CameraError
    {
        get => _cameraError;
        private set
        {
            if (SetProperty(ref _cameraError, value))
                OnPropertyChanged(nameof(HasCameraError));
        }
    }

    public bool HasCameraError => !string.IsNullOrWhiteSpace(_cameraError);

    public bool HasMedia => _composer.HasMedia;
    public string MediaDisplayName => HasMedia && !string.IsNullOrWhiteSpace(_mediaPath)
        ? Path.GetFileName(_mediaPath)
        : "测试画面";

    public string MediaStatus
    {
        get => _mediaStatus;
        private set => SetProperty(ref _mediaStatus, value);
    }

    public string MediaGlyph => _mediaKind switch
    {
        MediaKind.Image => "图",
        MediaKind.Video => "视",
        _ => "测"
    };

    public string MediaDetail
    {
        get
        {
            if (!HasMedia)
                return string.IsNullOrWhiteSpace(_mediaPath) ? "未选择素材 · 输出动态测试画面" : "素材不可用 · 输出测试画面";

            if (_composer.Kind == MediaKind.Image)
                return $"{_composer.VideoWidth} × {_composer.VideoHeight} · 图片";

            if (_composer.Kind == MediaKind.Video)
            {
                var duration = _composer.Duration;
                string length = duration > TimeSpan.Zero ? $" · {duration:mm\\:ss}" : string.Empty;
                return $"{_composer.VideoWidth} × {_composer.VideoHeight} · 视频{length}";
            }

            return "未选择素材";
        }
    }

    public ImageSource? PreviewSource
    {
        get => _previewSource;
        private set => SetProperty(ref _previewSource, value);
    }

    public CameraConfig ToConfig() => new()
    {
        Title = _title,
        Slot = Slot,
        MediaPath = _mediaPath,
        MediaKind = _mediaKind,
        Width = _width,
        Height = _height,
        Fps = Fps,
        FitMode = _selectedFitMode.Value,
        Mirror = _mirror,
        ShowPreview = _isPreviewVisible
    };

    public async Task StopForShutdownAsync()
    {
        // A tab can be closed while a start/stop operation is still running.
        // Wait for it to settle instead of disposing the controller underneath it.
        for (int i = 0; i < 100 && IsBusy && !_disposed; i++)
            await Task.Delay(50);

        if (!_disposed && IsCameraOn)
            await StopCameraAsync(silent: true);
    }

    public async Task SetCameraOnAsync(bool desired)
    {
        if (_disposed || desired == IsCameraOn || IsBusy)
            return;

        if (desired)
            await StartCameraAsync(silent: false);
        else
            await StopCameraAsync(silent: false);
    }

    public void SetMediaFromPath(string path, bool showError = true)
    {
        if (!File.Exists(path))
        {
            _mediaPath = path;
            _mediaKind = MediaKind.None;
            _composer.Clear();
            MediaStatus = "素材缺失";
            UpdateMediaProperties();
            if (showError)
                _host.ShowError("找不到素材文件：\n" + path);
            return;
        }

        var kind = MediaTypeDetector.Detect(path);
        if (kind == MediaKind.None)
        {
            if (showError)
                _host.ShowError("不支持该文件类型。请选择图片或视频文件。");
            return;
        }

        _mediaPath = path;
        _mediaKind = kind;
        _composer.SetMedia(path, kind);
        MediaStatus = string.IsNullOrWhiteSpace(_composer.Error)
            ? kind == MediaKind.Image ? "图片素材" : "视频素材"
            : "素材加载失败";
        UpdateMediaProperties();
    }

    public void ClearMedia()
    {
        _mediaPath = null;
        _mediaKind = MediaKind.None;
        _composer.Clear();
        MediaStatus = "测试画面";
        UpdateMediaProperties();
    }

    private void ChooseMedia()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择虚拟摄像头素材",
            Filter = MediaTypeDetector.BuildDialogFilter(),
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() == true)
            SetMediaFromPath(dialog.FileName);
    }

    private async Task StartCameraAsync(bool silent)
    {
        if (_disposed)
            return;

        IsBusy = true;
        CameraError = null;
        CameraStatus = "正在启动…";
        CameraStatusBrush = StatusStarting;

        try
        {
            if (!await _host.EnsureComponentsInstalledAsync())
            {
                SetCameraFailed("虚拟摄像头组件未安装，无法启动。", silent);
                return;
            }

            if (_disposed)
                return;

            var options = new SlotOptions { Width = _width, Height = _height, Fps = Fps };
            if (!SlotOptions.TrySave(Slot, options, out string? error))
            {
                SetCameraFailed("无法写入虚拟摄像头参数：" + error, silent);
                return;
            }

            if (_disposed)
                return;

            var controller = _controller ?? new VirtualCameraController();
            controller.Start(Slot, _title);
            _controller = controller;
            IsCameraOn = true;
            _composer.Play();
            _writer.TryOpen(Slot);
            _nextBridgeAttemptTicks = 0;
            CameraStatus = "运行中";
            CameraStatusBrush = StatusLive;
            CameraError = null;
            _host.NotifyCameraStateChanged();
        }
        catch (Exception ex)
        {
            AppLog.Write("ERROR", $"启动虚拟摄像头失败 (slot {Slot}): {ex}");
            _controller?.Dispose();
            _controller = null;
            IsCameraOn = false;
            _writer.Close();
            SetCameraFailed(ToUserMessage(ex), silent);
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(IsNotBusy));
        }
    }

    private Task StopCameraAsync(bool silent)
    {
        if (!IsCameraOn || IsBusy)
            return Task.CompletedTask;

        IsBusy = true;
        CameraStatus = "正在停止…";
        CameraStatusBrush = StatusStarting;

        try
        {
            // Disable rather than Remove/Dispose: removing a virtual camera
            // raises a system device-change notification that can disturb other
            // active virtual cameras. Full removal happens when the tab closes.
            _controller?.Disable();
            _writer.Close();
            IsCameraOn = false;
            CameraStatus = "已关闭";
            CameraStatusBrush = StatusOff;
            CameraError = null;
            _host.NotifyCameraStateChanged();

            if (!IsSelected)
                _composer.Pause();
        }
        catch (Exception ex)
        {
            AppLog.Write("ERROR", $"停止虚拟摄像头失败 (slot {Slot}): {ex}");
            CameraStatus = IsCameraOn ? "运行中" : "已关闭";
            CameraStatusBrush = IsCameraOn ? StatusLive : StatusOff;
            _host.NotifyCameraStateChanged();
            if (!silent && !_disposed)
                _host.ShowError("停止虚拟摄像头时出错：" + ex.Message);
        }
        finally
        {
            // The bridge must never stay attached to a camera that is being
            // stopped, even if IMFVirtualCamera.Stop reports an error.
            _writer.Close();
            IsBusy = false;
            OnPropertyChanged(nameof(IsNotBusy));
        }

        return Task.CompletedTask;
    }

    private void SetCameraFailed(string message, bool silent)
    {
        IsCameraOn = false;
        CameraStatus = "启动失败";
        CameraStatusBrush = StatusError;
        CameraError = message;
        if (!silent && !_disposed)
            _host.ShowError(message);
    }

    private static string ToUserMessage(Exception ex)
    {
        if (ex is VirtualCameraException)
            return ex.Message;

        return "启动虚拟摄像头失败：" + ex.Message;
    }

    private void ScheduleSettingsRestart()
    {
        if (!IsCameraOn || _disposed)
            return;

        _restartCts?.Cancel();
        _restartCts = new CancellationTokenSource();
        _ = RestartAfterDelayAsync(_restartCts.Token);
    }

    private async Task RestartAfterDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(350, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            // The tab was closed while the restart delay was pending.
            return;
        }

        if (token.IsCancellationRequested || !IsCameraOn || _disposed)
            return;

        await StopCameraAsync(silent: true);
        if (!token.IsCancellationRequested && !_disposed && !IsCameraOn)
            await StartCameraAsync(silent: false);
    }

    /// <summary>Called by the frame pump on the UI thread.</summary>
    public void RenderTick(long now)
    {
        if (_disposed)
            return;

        bool produceOutput = IsCameraOn;
        bool showPreview = IsSelected && IsPreviewVisible;

        // When the preview is closed and the camera is off there is nothing to
        // compose. The frame pump is normally stopped in this state; this guard
        // also covers the tick that races with closing the preview.
        if (!produceOutput && !showPreview)
        {
            _composer.Pause();
            return;
        }

        long interval = Math.Max(1, Stopwatch.Frequency / Math.Max(1, Fps));
        if (now < _nextFrameTicks)
            return;

        _nextFrameTicks = now + interval;
        _composer.Play();

        int targetWidth = produceOutput ? _width : Math.Min(960, _width);
        int targetHeight = (int)Math.Round(targetWidth * (double)_height / Math.Max(1, _width));
        if (targetHeight <= 0)
            targetHeight = 1;

        EnsureRenderTargets(targetWidth, targetHeight, showPreview);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            _composer.Render(dc, targetWidth, targetHeight, _selectedFitMode.Value, _mirror, OutputBackgroundBrush);
        }

        _renderBitmap!.Render(visual);
        _renderBitmap.CopyPixels(_pixelBuffer!, targetWidth * 4, 0);

        // Copying into the preview bitmap is only needed while the preview is
        // actually on screen. Camera output still uses the rendered pixels.
        if (showPreview)
        {
            _previewBitmap!.WritePixels(
                new Int32Rect(0, 0, targetWidth, targetHeight),
                _pixelBuffer!,
                targetWidth * 4,
                0);
        }

        if (produceOutput)
            PublishFrame(now, targetWidth, targetHeight);
    }

    private void EnsureRenderTargets(int width, int height, bool preview)
    {
        if (_renderBitmap == null || _pixelBuffer == null ||
            _renderBitmap.PixelWidth != width || _renderBitmap.PixelHeight != height)
        {
            _renderBitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            _pixelBuffer = new byte[width * height * 4];
        }

        if (!preview)
            return;

        if (_previewBitmap == null ||
            _previewBitmap.PixelWidth != width || _previewBitmap.PixelHeight != height)
        {
            _previewBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Pbgra32, null);
        }

        if (!ReferenceEquals(PreviewSource, _previewBitmap))
            PreviewSource = _previewBitmap;
    }

    /// <summary>
    /// Drops the preview bitmap immediately and, when the camera is off, the
    /// render target and pixel buffer too. They are recreated on the next frame
    /// that needs them.
    /// </summary>
    private void ReleasePreviewResources()
    {
        _previewBitmap = null;
        if (_previewSource != null)
            PreviewSource = null;

        if (IsCameraOn)
            return;

        _renderBitmap = null;
        _pixelBuffer = null;
    }

    private void PublishFrame(long now, int width, int height)
    {
        if (!_writer.IsOpen && now >= _nextBridgeAttemptTicks)
        {
            _writer.TryOpen(Slot);
            _nextBridgeAttemptTicks = now + Stopwatch.Frequency;
        }

        if (_writer.IsOpen)
        {
            try
            {
                _writer.Write(_pixelBuffer!, width, height, width * 4, Fps, _frameNumber++);
            }
            catch (Exception ex)
            {
                // A broken bridge must not take down the UI thread. Close it and
                // let the retry timer re-open the section when it becomes available.
                AppLog.Write("WARN", $"写入共享帧失败 (slot {Slot}): {ex.Message}");
                _writer.Close();
            }
        }
    }

    private void OnComposerStateChanged(object? sender, EventArgs e)
    {
        if (_disposed)
            return;

        UpdateMediaProperties();
        if (!string.IsNullOrWhiteSpace(_composer.Error))
            MediaStatus = "素材加载失败";
    }

    private void UpdateMediaProperties()
    {
        OnPropertyChanged(nameof(HasMedia));
        OnPropertyChanged(nameof(MediaDisplayName));
        OnPropertyChanged(nameof(MediaDetail));
        OnPropertyChanged(nameof(MediaGlyph));
        ClearMediaCommand.RaiseCanExecuteChanged();
    }

    private void EnsureResolution(int width, int height)
    {
        if (Resolutions.Any(option => option.Width == width && option.Height == height))
            return;

        Resolutions.Insert(0, new ResolutionOption(width, height));
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _restartCts?.Cancel();
        _restartCts?.Dispose();
        _restartCts = null;

        _controller?.Dispose();
        _controller = null;
        _writer.Dispose();

        _composer.StateChanged -= OnComposerStateChanged;
        _composer.Dispose();

        _renderBitmap = null;
        _previewBitmap = null;
        _pixelBuffer = null;
        PreviewSource = null;
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
