using System.Diagnostics;
using System.Windows.Threading;

namespace VCamStudio.Services;

/// <summary>
/// Drives all preview / frame production on the UI thread. Keeping rendering on
/// one DispatcherTimer avoids concurrent access to WPF MediaPlayer instances.
/// </summary>
public sealed class FramePump : IDisposable
{
    private readonly DispatcherTimer _timer;
    private bool _running;

    public FramePump()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(15)
        };
        _timer.Tick += OnTick;
    }

    public event Action<long>? Tick;

    public void Start()
    {
        if (_running)
            return;

        _running = true;
        _timer.Start();
    }

    public void Stop()
    {
        _running = false;
        _timer.Stop();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        Tick?.Invoke(Stopwatch.GetTimestamp());
    }

    public void Dispose()
    {
        Stop();
        _timer.Tick -= OnTick;
    }
}
