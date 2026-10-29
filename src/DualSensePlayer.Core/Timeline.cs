using System.Diagnostics;

namespace DualSensePlayer.Core;

public enum PlaybackState { Stopped, Paused, Playing, Faulted }

public sealed record ClockSnapshot(PlaybackState State, double Anchor, double Epoch, long Generation, bool ResumeFromPause = false)
{
    public double At(double qpcSeconds) => State == PlaybackState.Playing ? Anchor + qpcSeconds - Epoch : Anchor;
}

public sealed class PlaybackClock(Func<double>? now = null)
{
    private readonly Func<double> _now = now ?? (() => QpcSeconds);
    private ClockSnapshot _snapshot = new(PlaybackState.Stopped, 0, 0, 0);
    public static double QpcSeconds => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
    public ClockSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public double Position
    {
        get { var snapshot = Snapshot; return Math.Max(snapshot.Anchor, snapshot.At(_now())); }
    }

    public void Play(double leadInSeconds) => Volatile.Write(ref _snapshot,
        new(PlaybackState.Playing, Snapshot.Anchor, _now() + leadInSeconds, Snapshot.Generation + 1, Snapshot.ResumeFromPause));
    public void Pause() => Set(PlaybackState.Paused, Position, true);
    public void Stop() => Set(PlaybackState.Stopped, 0);
    public void Seek(double seconds) => Set(PlaybackState.Paused, Math.Max(0, seconds));
    public void Fault() => Set(PlaybackState.Faulted, Position);
    private void Set(PlaybackState state, double position, bool resumeFromPause = false) => Volatile.Write(ref _snapshot,
        new(state, position, _now(), Snapshot.Generation + 1, resumeFromPause));
}

/// <summary>Tracks device frequency against QPC, without trusting a UI timer.</summary>
public sealed class DeviceClockTracker
{
    private double _lastDevice, _lastQpc;
    private bool _observed;
    public double RateRatio { get; private set; } = 1;
    public double DriftPpm => (RateRatio - 1) * 1_000_000;

    public void Observe(double deviceSeconds, double qpcSeconds)
    {
        if (_observed && qpcSeconds - _lastQpc >= 0.05)
        {
            var ratio = (deviceSeconds - _lastDevice) / (qpcSeconds - _lastQpc);
            if (ratio is > 0.995 and < 1.005) RateRatio += (Math.Clamp(ratio, 0.9995, 1.0005) - RateRatio) * 0.05;
            _lastDevice = deviceSeconds;
            _lastQpc = qpcSeconds;
        }
        else if (!_observed)
        {
            _observed = true;
            _lastDevice = deviceSeconds;
            _lastQpc = qpcSeconds;
        }
    }
}

public sealed class RestartBudget(int maximum = 3, double windowSeconds = 600)
{
    private readonly Queue<double> _attempts = new();
    public bool TryRestart(double monotonicSeconds)
    {
        while (_attempts.TryPeek(out var attempt) && monotonicSeconds - attempt >= windowSeconds) _attempts.Dequeue();
        if (_attempts.Count >= maximum) return false;
        _attempts.Enqueue(monotonicSeconds);
        return true;
    }
}

public sealed record Heartbeat(int ProcessId, DateTimeOffset Utc, long UiTick, long EngineTick,
    bool Playing, bool NormalExit, string State);
