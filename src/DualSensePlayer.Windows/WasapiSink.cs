using System.Runtime.InteropServices;
using DualSensePlayer.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using PlaybackState = DualSensePlayer.Core.PlaybackState;

namespace DualSensePlayer.Windows;

public sealed class WasapiSink : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly AutoResetEvent _flush = new(false);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _worker;
    private long _framesWritten;
    public Task Ready => _ready.Task;
    public Task Completion => _worker;
    public long FramesWritten => Interlocked.Read(ref _framesWritten);
    public float LeftPeak { get; private set; }
    public float RightPeak { get; private set; }
    public double DriftPpm { get; private set; }
    public string FormatDescription { get; private set; } = "";

    public WasapiSink(AudioDevice device, OutputSettings settings, PcmTimeline clip, PlaybackClock clock,
        Func<RoutingOptions> routing, int fadeMs, DiagnosticLog log, Action progress)
    {
        _worker = Task.Factory.StartNew(() =>
        {
            try { Run(device, settings, clip, clock, routing, fadeMs, log, progress); }
            catch (Exception ex) { _ready.TrySetException(ex); throw; }
        },
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public void Flush()
    {
        try { _flush.Set(); }
        catch (ObjectDisposedException) { /* A disconnected sink may have just finished disposal. */ }
    }

    private unsafe void Run(AudioDevice device, OutputSettings settings, PcmTimeline clip, PlaybackClock clock,
        Func<RoutingOptions> routing, int fadeMs, DiagnosticLog log, Action progress)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var endpoint = enumerator.GetDevice(device.Id);
        using var signal = new AutoResetEvent(false);
        AudioClient? client = null;
        IntPtr scheduling = IntPtr.Zero;
        try
        {
            scheduling = AvSetMmThreadCharacteristics("Pro Audio", out _);
            var haptics = routing().Kind == OutputKind.Haptics;
            var channels = haptics ? 4 : 2;
            var share = settings.Exclusive ? AudioClientShareMode.Exclusive : AudioClientShareMode.Shared;
            client = endpoint.AudioClient;
            if (haptics && (client.MixFormat.Channels != 4 || client.MixFormat.SampleRate != 48000) && !settings.Exclusive)
                throw new InvalidDataException("DualSenseの共有形式を48kHz・4chに設定してください。2chへの変換は行いません。");
            WaveFormat? format = null;
            var candidates = haptics
                ? new WaveFormat[] { client.MixFormat, new WaveFormat(48000, 16, 4), new WaveFormatExtensible(48000, 16, 4) }
                : [WaveFormat.CreateIeeeFloatWaveFormat(client.MixFormat.SampleRate, 2), new WaveFormat(client.MixFormat.SampleRate, 16, 2)];
            foreach (var candidate in candidates)
            {
                if (candidate.Channels != channels || haptics && candidate.SampleRate != 48000 || !CanWrite(candidate)) continue;
                if (client.IsFormatSupported(share, candidate)) { format = candidate; break; }
            }
            if (format is null) throw new InvalidDataException($"{channels}chの対応出力形式がありません。Windowsの音声設定と排他モードを確認してください。");
            var duration = settings.Exclusive ? Math.Max(client.MinimumDevicePeriod, 100000) : 300000;
            try
            {
                client.Initialize(share, AudioClientStreamFlags.EventCallback, duration,
                    settings.Exclusive ? duration : 0, format, Guid.Empty);
            }
            catch (COMException ex) when (settings.Exclusive && ex.HResult == unchecked((int)0x88890019))
            {
                duration = (long)Math.Round(client.BufferSize * 10_000_000d / format.SampleRate);
                client.Dispose();
                client = endpoint.AudioClient;
                client.Initialize(share, AudioClientStreamFlags.EventCallback, duration, duration, format, Guid.Empty);
            }
            var bufferFrames = client.BufferSize;
            if (bufferFrames / (double)format.SampleRate > 0.08)
                throw new InvalidDataException("出力バッファが80msを超えています。停止応答を維持できる形式に設定してください。");
            client.SetEventHandle(signal.SafeWaitHandle.DangerousGetHandle());
            var render = client.AudioRenderClient;
            var hardwareClock = client.AudioClockClient;
            var tracker = new DeviceClockTracker();
            var samples = new float[bufferFrames * channels];
            var floatOutput = IsFloat(format);
            var started = false;
            var startedAt = PlaybackClock.QpcSeconds;
            var fadeStart = startedAt;
            var lastStats = startedAt;
            var lastWake = startedAt;
            var wasPrimed = false;
            FormatDescription = $"{share} {format.SampleRate}Hz {format.Channels}ch {format.BitsPerSample}bit; buffer={bufferFrames * 1000d / format.SampleRate:0.0}ms";
            log.Write("output-open", new { device.Id, FormatDescription });
            var handles = new[] { _stop.Token.WaitHandle, _flush, signal };
            while (!_stop.IsCancellationRequested)
            {
                var choice = started ? WaitHandle.WaitAny(handles, 100) : 2;
                if (choice == 0) break;
                if (choice == WaitHandle.WaitTimeout)
                {
                    if (PlaybackClock.QpcSeconds - lastWake > 1) throw new IOException("音声機器の供給イベントが途絶えました。");
                    continue;
                }
                if (choice == 1)
                {
                    client.Stop(); client.Reset();
                    _framesWritten = 0; started = false; wasPrimed = false;
                    tracker = new();
                }
                lastWake = PlaybackClock.QpcSeconds;
                var padding = settings.Exclusive ? 0 : client.CurrentPadding;
                var available = bufferFrames - padding;
                if (available <= 0) continue;
                var now = PlaybackClock.QpcSeconds;
                var nextQpc = now + client.StreamLatency / 10_000_000d;
                if (started && hardwareClock.GetPosition(out var position, out var qpc) && qpc > 0)
                {
                    var deviceSeconds = position / (double)hardwareClock.Frequency;
                    var qpcSeconds = qpc / 10_000_000d;
                    tracker.Observe(deviceSeconds, qpcSeconds);
                    nextQpc = qpcSeconds + (_framesWritten / (double)format.SampleRate - deviceSeconds) / tracker.RateRatio;
                }
                var underrun = started && wasPrimed && !settings.Exclusive && padding == 0;
                if (nextQpc < now - 0.02 || nextQpc > now + 0.15) underrun = true;
                if (underrun)
                {
                    log.Write("buffer-underrun", new { device.Id, lateMs = (now - nextQpc) * 1000 });
                    client.Stop(); client.Reset();
                    _framesWritten = 0; started = false; tracker = new();
                    available = bufferFrames;
                    nextQpc = now + client.StreamLatency / 10_000_000d;
                }
                var snapshot = clock.Snapshot;
                var options = routing();
                var count = available * channels;
                var step = 1d / (format.SampleRate * tracker.RateRatio);
                var fade = fadeMs == 0 ? 1 : (nextQpc - fadeStart) * 1000 / fadeMs;
                var fadeStep = fadeMs == 0 ? 0 : step * 1000 / fadeMs;
                if (snapshot.State != PlaybackState.Playing || underrun) samples.AsSpan(0, count).Clear();
                else clip.Render(samples.AsSpan(0, count), channels, snapshot.At(nextQpc), step, options, fade, fadeStep,
                    snapshot.ResumeFromPause ? 0 : snapshot.Anchor,
                    snapshot.ResumeFromPause ? snapshot.Anchor : double.NegativeInfinity);
                var pointer = render.GetBuffer(available);
                try
                {
                    var left = 0f; var right = 0f;
                    for (var i = 0; i < count; i++)
                    {
                        var value = samples[i];
                        if (floatOutput) ((float*)pointer)[i] = value;
                        else ((short*)pointer)[i] = (short)Math.Clamp((int)Math.Round(value * 32767), -32768, 32767);
                        if (i % channels == (haptics ? 2 : 0)) left = Math.Max(left, Math.Abs(value));
                        if (i % channels == (haptics ? 3 : 1)) right = Math.Max(right, Math.Abs(value));
                    }
                    LeftPeak = left; RightPeak = right;
                }
                finally { render.ReleaseBuffer(available, AudioClientBufferFlags.None); }
                Interlocked.Add(ref _framesWritten, available);
                progress();
                if (!started) { client.Start(); started = true; _ready.TrySetResult(); }
                wasPrimed = true;
                DriftPpm = tracker.DriftPpm;
                if (now - lastStats >= 10)
                {
                    log.Write("sync", new { device.Id, DriftPpm, padding, queueMs = (nextQpc - now) * 1000 });
                    lastStats = now;
                }
            }
        }
        catch (Exception ex) { _ready.TrySetException(ex); throw; }
        finally
        {
            if (client is not null)
            {
                try { client.Stop(); client.Reset(); } catch (COMException) { }
                client.Dispose();
            }
            if (scheduling != IntPtr.Zero) AvRevertMmThreadCharacteristics(scheduling);
            _ready.TrySetCanceled();
        }
    }

    private static bool IsFloat(WaveFormat format) => format.Encoding == WaveFormatEncoding.IeeeFloat ||
        format is WaveFormatExtensible extended && extended.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
    private static bool CanWrite(WaveFormat format) => IsFloat(format) && format.BitsPerSample == 32 ||
        format.BitsPerSample == 16 && (format.Encoding == WaveFormatEncoding.Pcm || format is WaveFormatExtensible e && e.SubFormat == new Guid("00000001-0000-0010-8000-00aa00389b71"));

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try { await _worker; } catch (Exception) { /* The owner observes Completion and records device failure. */ }
        _flush.Dispose(); _stop.Dispose();
    }
    [DllImport("avrt.dll", CharSet = CharSet.Unicode)] private static extern IntPtr AvSetMmThreadCharacteristics(string name, out uint index);
    [DllImport("avrt.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
}
