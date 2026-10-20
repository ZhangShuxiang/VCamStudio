using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VCamStudio.Models;
using VCamStudio.Services;

namespace VCamStudio.Rendering;

/// <summary>
/// Renders an image, video or test pattern into a DrawingContext. The same
/// composition is used for both the on-screen preview and the frames sent to the
/// virtual camera, so what the user sees is exactly what applications receive.
/// </summary>
public sealed class FrameComposer : IDisposable
{
    private readonly string _label;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    private MediaPlayer? _player;
    private BitmapSource? _image;
    private MediaKind _kind;
    private bool _videoReady;
    private bool _playing;
    private bool _wantsPlayback;
    private bool _disposed;
    private string? _error;
    private int _videoWidth;
    private int _videoHeight;
    private TimeSpan _duration;

    public FrameComposer(string label)
    {
        _label = label;
    }

    public event EventHandler? StateChanged;

    public bool HasMedia => _image != null || _player != null;
    public MediaKind Kind => _kind;
    public string? Error => _error;
    public int VideoWidth => _image?.PixelWidth ?? _videoWidth;
    public int VideoHeight => _image?.PixelHeight ?? _videoHeight;

    public TimeSpan Duration
    {
        get
        {
            if (_player == null)
                return _duration;

            var duration = _player.NaturalDuration;
            return duration.HasTimeSpan ? duration.TimeSpan : _duration;
        }
    }

    public void SetMedia(string? path, MediaKind kind)
    {
        if (_disposed)
            return;

        Clear();

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            if (!string.IsNullOrWhiteSpace(path))
                SetError("素材文件不存在。");

            RaiseStateChanged();
            return;
        }

        if (kind == MediaKind.None)
            kind = MediaTypeDetector.Detect(path);

        try
        {
            if (kind == MediaKind.Image)
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();

                _image = bitmap;
                _kind = MediaKind.Image;
            }
            else if (kind == MediaKind.Video)
            {
                _kind = MediaKind.Video;
                _player = new MediaPlayer
                {
                    Volume = 0,
                    IsMuted = true,
                    ScrubbingEnabled = true
                };
                _player.MediaOpened += OnMediaOpened;
                _player.MediaEnded += OnMediaEnded;
                _player.MediaFailed += OnMediaFailed;
                _player.Open(new Uri(path, UriKind.Absolute));
            }
            else
            {
                _kind = MediaKind.None;
                _error = "不支持的文件类型。";
            }
        }
        catch (Exception ex)
        {
            ClosePlayer();
            _image = null;
            _kind = MediaKind.None;
            _error = ex.Message;
            AppLog.Write("ERROR", $"加载素材失败 {path}: {ex}");
        }

        RaiseStateChanged();
    }

    public void Clear()
    {
        ClosePlayer();

        _image = null;
        _videoReady = false;
        _playing = false;
        _wantsPlayback = false;
        _videoWidth = 0;
        _videoHeight = 0;
        _duration = TimeSpan.Zero;
        _error = null;
        _kind = MediaKind.None;
    }

    private void ClosePlayer()
    {
        var player = _player;
        _player = null;
        if (player == null)
            return;

        try
        {
            player.MediaOpened -= OnMediaOpened;
            player.MediaEnded -= OnMediaEnded;
            player.MediaFailed -= OnMediaFailed;
            player.Close();
        }
        catch
        {
            // Ignore media teardown races.
        }

        _videoReady = false;
        _playing = false;
    }

    public void Play()
    {
        if (_disposed)
            return;

        _wantsPlayback = true;
        if (_player != null && _videoReady && !_playing)
        {
            try
            {
                _player.Play();
                _playing = true;
            }
            catch
            {
                // MediaPlayer can transiently reject Play while opening.
            }
        }
    }

    public void Pause()
    {
        if (_disposed)
            return;

        _wantsPlayback = false;
        if (_player != null)
        {
            try { _player.Pause(); } catch { }
            _playing = false;
        }
    }

    public void Render(DrawingContext dc, int width, int height, FitMode fitMode, bool mirror, Brush background)
    {
        if (_disposed)
            return;

        var bounds = new Rect(0, 0, width, height);
        dc.DrawRectangle(background, null, bounds);

        bool drewMedia = false;

        if (_image != null)
        {
            var destination = CalculateDestination(width, height, _image.PixelWidth, _image.PixelHeight, fitMode);
            if (mirror)
                dc.PushTransform(new ScaleTransform(-1, 1, width / 2.0, 0));

            if (fitMode == FitMode.Cover)
                dc.PushClip(new RectangleGeometry(bounds));

            dc.DrawImage(_image, destination);

            if (fitMode == FitMode.Cover)
                dc.Pop();
            if (mirror)
                dc.Pop();

            drewMedia = true;
        }
        else if (_player != null && _videoReady)
        {
            double sourceWidth = _videoWidth > 0 ? _videoWidth : 16.0;
            double sourceHeight = _videoHeight > 0 ? _videoHeight : 9.0;
            var destination = CalculateDestination(width, height, sourceWidth, sourceHeight, fitMode);
            if (mirror)
                dc.PushTransform(new ScaleTransform(-1, 1, width / 2.0, 0));

            if (fitMode == FitMode.Cover)
                dc.PushClip(new RectangleGeometry(bounds));

            dc.DrawVideo(_player, destination);

            if (fitMode == FitMode.Cover)
                dc.Pop();
            if (mirror)
                dc.Pop();

            drewMedia = true;
        }

        if (!drewMedia)
        {
            WpfTestPattern.Draw(dc, width, height, _label, _clock.Elapsed);

            if (!string.IsNullOrWhiteSpace(_error))
                DrawError(dc, width, height);
            else if (_player != null && !_videoReady)
                DrawHint(dc, width, height, "正在读取视频…");
        }
    }

    private static Rect CalculateDestination(int width, int height, double sourceWidth, double sourceHeight, FitMode fitMode)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
            return new Rect(0, 0, width, height);

        if (fitMode == FitMode.Stretch)
            return new Rect(0, 0, width, height);

        double scale = fitMode == FitMode.Cover
            ? Math.Max(width / sourceWidth, height / sourceHeight)
            : Math.Min(width / sourceWidth, height / sourceHeight);

        double destinationWidth = sourceWidth * scale;
        double destinationHeight = sourceHeight * scale;
        return new Rect(
            (width - destinationWidth) / 2.0,
            (height - destinationHeight) / 2.0,
            destinationWidth,
            destinationHeight);
    }

    private static void DrawError(DrawingContext dc, int width, int height)
    {
        double unit = Math.Max(9, Math.Min(width, height) / 48.0);
        var text = new FormattedText(
            "素材加载失败",
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            unit,
            new SolidColorBrush(Color.FromRgb(254, 202, 202)),
            1.0);

        var background = new SolidColorBrush(Color.FromArgb(180, 127, 29, 29));
        var rect = new Rect(width * 0.07, height * 0.90, text.Width + unit * 1.2, text.Height + unit * 0.6);
        dc.DrawRoundedRectangle(background, null, rect, unit * 0.4, unit * 0.4);
        dc.DrawText(text, new Point(rect.X + unit * 0.6, rect.Y + unit * 0.3));
    }

    private static void DrawHint(DrawingContext dc, int width, int height, string message)
    {
        double unit = Math.Max(9, Math.Min(width, height) / 48.0);
        var text = new FormattedText(
            message,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            unit,
            new SolidColorBrush(Color.FromArgb(220, 226, 232, 240)),
            1.0);

        dc.DrawText(text, new Point((width - text.Width) / 2.0, height * 0.50));
    }

    private void OnMediaOpened(object? sender, EventArgs e)
    {
        var player = _player;
        if (player == null)
            return;

        _videoReady = true;
        _videoWidth = player.NaturalVideoWidth;
        _videoHeight = player.NaturalVideoHeight;
        _duration = player.NaturalDuration.HasTimeSpan ? player.NaturalDuration.TimeSpan : TimeSpan.Zero;
        if (_wantsPlayback)
        {
            try
            {
                player.Play();
                _playing = true;
            }
            catch
            {
                _playing = false;
            }
        }

        RaiseStateChanged();
    }

    private void OnMediaEnded(object? sender, EventArgs e)
    {
        var player = _player;
        if (player == null || !_wantsPlayback)
            return;

        try
        {
            player.Position = TimeSpan.Zero;
            player.Play();
        }
        catch
        {
            // Ignore loop failures.
        }
    }

    private void OnMediaFailed(object? sender, ExceptionEventArgs e)
    {
        _error = e.ErrorException?.Message ?? "视频无法播放。";
        _videoReady = false;
        _playing = false;
        AppLog.Write("ERROR", "视频播放失败: " + _error);
        RaiseStateChanged();
    }

    private void SetError(string message)
    {
        _error = message;
        _kind = MediaKind.None;
    }

    private void RaiseStateChanged()
    {
        var handler = StateChanged;
        if (handler == null)
            return;

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            try
            {
                dispatcher.BeginInvoke(new Action(() => handler(this, EventArgs.Empty)));
                return;
            }
            catch
            {
                // The dispatcher can already be shutting down.
                return;
            }
        }

        handler(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Clear();
    }
}
