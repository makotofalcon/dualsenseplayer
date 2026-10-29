namespace DualSensePlayer.Core;

public enum OutputKind { Audio, Haptics }
public sealed record RoutingOptions(OutputKind Kind, float Gain, bool Muted, int OffsetMs,
    float Limit = 0.8f, bool EmergencyStop = false);

/// <summary>Read-only audio on a common timeline, shared by independent WASAPI readers.</summary>
public abstract class PcmTimeline
{
    public abstract double Duration { get; }
    public abstract float Read(double absoluteSeconds, int logicalChannel);

    public void Render(Span<float> destination, int outputChannels, double timelineStart, double secondsPerFrame,
        RoutingOptions options, double fadeAtStart = 1, double fadePerFrame = 0, double minimumSourceTime = 0,
        double minimumTimelineTime = double.NegativeInfinity)
    {
        if (outputChannels != (options.Kind == OutputKind.Audio ? 2 : 4) || destination.Length % outputChannels != 0)
            throw new ArgumentException("The output channel count must be exact.");
        destination.Clear();
        if (options.Muted || options.Gain == 0 || options.Kind == OutputKind.Haptics && options.EmergencyStop) return;
        var firstLogical = options.Kind == OutputKind.Audio ? 0 : 2;
        var firstOutput = options.Kind == OutputKind.Audio ? 0 : 2;
        var ceiling = options.Kind == OutputKind.Audio ? 1 : options.Limit;
        for (var frame = 0; frame < destination.Length / outputChannels; frame++)
        {
            var timeline = timelineStart + frame * secondsPerFrame;
            if (timeline < minimumTimelineTime) continue;
            var position = timeline - options.OffsetMs / 1000d;
            if (position < minimumSourceTime) continue;
            var gain = options.Gain * (float)Math.Clamp(fadeAtStart + frame * fadePerFrame, 0, 1);
            destination[frame * outputChannels + firstOutput] = Math.Clamp(Read(position, firstLogical) * gain, -ceiling, ceiling);
            destination[frame * outputChannels + firstOutput + 1] = Math.Clamp(Read(position, firstLogical + 1) * gain, -ceiling, ceiling);
        }
    }
}

/// <summary>Immutable resident PCM. No file I/O, locks, or consuming shared cursor on the audio thread.</summary>
public sealed class PcmClip : PcmTimeline
{
    public const int SampleRate = 48000;
    public const int Channels = 4;
    private readonly float[] _samples;
    private readonly int[] _map;
    public override double Duration { get; }
    public int Frames => _samples.Length / Channels;

    public PcmClip(float[] samples, double duration, int[] channelMap)
    {
        if (samples.Length == 0 || samples.Length % 4 != 0 || !double.IsFinite(duration) || duration <= 0)
            throw new ArgumentException("Invalid PCM clip");
        if (channelMap.Length != 4 || !channelMap.Order().SequenceEqual(new[] { 0, 1, 2, 3 }))
            throw new ArgumentException("Invalid channel map");
        _samples = samples;
        _map = channelMap.ToArray();
        Duration = duration;
    }

    public override float Read(double absoluteSeconds, int logicalChannel)
    {
        if (!double.IsFinite(absoluteSeconds) || absoluteSeconds < 0) return 0; // Never read the previous loop at startup.
        return ReadSegment(absoluteSeconds % Duration, logicalChannel, Sample(0, logicalChannel));
    }

    internal float ReadSegment(double localSeconds, int logicalChannel, float nextClipFirstSample)
    {
        var frame = localSeconds * SampleRate;
        var first = (int)Math.Floor(frame);
        var fraction = (float)(frame - first);
        var a = Sample(first, logicalChannel);
        var b = (first + 1) / (double)SampleRate >= Duration ? nextClipFirstSample : Sample(first + 1, logicalChannel);
        return a + (b - a) * fraction;
    }

    private float Sample(int frame, int channel)
    {
        if (frame < 0 || frame >= Frames) return 0;
        var value = _samples[frame * Channels + _map[channel]];
        return float.IsFinite(value) ? value : 0;
    }

    public (double[] Dc, float[] Peak, int NonFinite) Diagnose()
    {
        var sums = new double[4]; var peaks = new float[4]; var nonFinite = 0;
        for (var i = 0; i < _samples.Length; i++)
        {
            var sample = _samples[i];
            if (!float.IsFinite(sample)) { nonFinite++; continue; }
            sums[i % 4] += sample;
            peaks[i % 4] = Math.Max(peaks[i % 4], Math.Abs(sample));
        }
        return (sums.Select(s => s / Frames).ToArray(), peaks, nonFinite);
    }
}

public static class DualSenseReport
{
    public static byte[] EnableUsbAudioHaptics(int reportLength = 48)
    {
        if (reportLength != 48) throw new InvalidDataException("標準DualSenseのUSB出力レポートではありません。");
        var report = new byte[48];
        report[0] = 0x02; // USB output report ID.
        // Both rumble-emulation bits stay clear: bit 0x02 DISABLES audio haptics.
        // See SDL_hidapi_ps5.c, HIDAPI_DriverPS5_UpdateEffects (restore audio haptics).
        report[1] = 0x00;
        return report;
    }
}
