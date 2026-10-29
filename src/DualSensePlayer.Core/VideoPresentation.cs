namespace DualSensePlayer.Core;

/// <summary>Owns the latest due frame until the renderer can accept it. Use on the UI thread.</summary>
public sealed class VideoPresentationBuffer : IDisposable
{
    private VideoFrame? _pending;
    public bool HasPending => _pending is not null;

    public void Offer(VideoFrame frame)
    {
        _pending?.Dispose();
        _pending = frame;
    }

    public long? TryPresent(Func<VideoFrame, bool> present)
    {
        if (_pending is null || !present(_pending)) return null;
        var sequence = _pending.Sequence;
        Clear();
        return sequence;
    }

    public void Clear() { _pending?.Dispose(); _pending = null; }
    public void Dispose() => Clear();
}

public readonly record struct VideoPerformance(double FramesPerSecond, double CopyMilliseconds,
    long SkippedFrames, long BusyAttempts, bool Measured);

/// <summary>Counts frames submitted to WPF, not physical display refreshes.</summary>
public sealed class VideoPerformanceMeter
{
    private double _windowStart, _copyMilliseconds;
    private long _lastSequence = -1, _skipped, _busy;
    private int _frames;
    private VideoPerformance _last;

    public void Reset(double now)
    {
        _windowStart = now; _copyMilliseconds = 0;
        _lastSequence = -1; _skipped = _busy = _frames = 0;
        _last = default;
    }

    public void Presented(long sequence, double copyMilliseconds)
    {
        if (_lastSequence >= 0) _skipped += Math.Max(0, sequence - _lastSequence - 1);
        _lastSequence = sequence;
        _frames++; _copyMilliseconds += copyMilliseconds;
    }

    public void Busy() => _busy++;

    public VideoPerformance Measure(double now)
    {
        var seconds = now - _windowStart;
        if (seconds >= 1)
        {
            _last = new(_frames / seconds, _frames > 0 ? _copyMilliseconds / _frames : 0, _skipped, _busy, true);
            _frames = 0; _copyMilliseconds = 0; _windowStart = now;
        }
        return _last with { SkippedFrames = _skipped, BusyAttempts = _busy };
    }
}
