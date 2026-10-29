using DualSensePlayer.Core;

namespace DualSensePlayer.Windows;

public sealed class PlayerEngine(FfmpegTools tools, DiagnosticLog log) : IAsyncDisposable
{
    private readonly SemaphoreSlim _commands = new(1, 1);
    private PlayerSettings _settings = new();
    private RoutingOptions[] _routing = [new(OutputKind.Audio, 1, false, 0), new(OutputKind.Haptics, .25f, false, 0), new(OutputKind.Haptics, .25f, false, 0)];
    private CancellationTokenSource? _outputsStop;
    private Task[] _outputs = [];
    private readonly WasapiSink?[] _sinks = new WasapiSink?[3];
    private WasapiSink? _testSink;
    private long _progress;
    private long _lastLoop = -1;
    private long _lastFrame = -1;
    private int _lastItem = -1;
    private bool _outputStopPending;
    public PlaybackClock Clock { get; } = new();
    public PreparedPlaylist? Playlist { get; private set; }
    public PreparedMedia? CurrentMedia => Playlist is { } playlist ? playlist.Items[playlist.Timeline.Locate(Clock.Position).Index] : null;
    public PlaylistVideoDecoder? Video { get; private set; }
    public long Progress => Interlocked.Read(ref _progress);
    public bool OutputStopPending => Volatile.Read(ref _outputStopPending);
    public string[] OutputStatus { get; } = ["未登録", "未登録", "未登録"];
    public float Meter(int output, bool right) => right ? _sinks[output]?.RightPeak ?? 0 : _sinks[output]?.LeftPeak ?? 0;

    public void UpdateSettings(PlayerSettings settings)
    {
        settings.Validate();
        Volatile.Write(ref _settings, settings);
        Volatile.Write(ref _routing,
        [new(OutputKind.Audio, settings.Machine.Audio.Gain, settings.Machine.Audio.Muted, settings.Machine.Audio.OffsetMs),
            new(OutputKind.Haptics, settings.Machine.Controllers[0].Gain, settings.Machine.Controllers[0].Muted, settings.Machine.Controllers[0].OffsetMs, settings.HapticLimit, settings.HapticsStopped),
            new(OutputKind.Haptics, settings.Machine.Controllers[1].Gain, settings.Machine.Controllers[1].Muted, settings.Machine.Controllers[1].OffsetMs, settings.HapticLimit, settings.HapticsStopped)]);
        if (settings.HapticsStopped)
        {
            for (var i = 1; i < 3; i++) _sinks[i]?.Flush();
            _testSink?.Flush();
        }
    }

    public async Task LoadPlaylistAsync(IReadOnlyList<MediaInfo> infos, PlayerSettings settings, CancellationToken cancellationToken = default)
    {
        await _commands.WaitAsync(cancellationToken);
        try
        {
            if (Clock.Snapshot.State != PlaybackState.Stopped) throw new InvalidOperationException("素材変更は停止中に行ってください。");
            Playlist = null;
            Playlist = await PreparedPlaylist.PrepareAsync(tools, infos, settings.Playlist, cancellationToken);
            foreach (var media in Playlist.Items)
                log.Write("media-loaded", new { media.Info.Path, media.Hash, media.Info.Summary, media.Track, media.Warnings });
            log.Write("playlist-loaded", new { count = Playlist.Items.Count, Playlist.Timeline.Duration });
        }
        finally { _commands.Release(); }
    }

    public async Task PlayAsync(CancellationToken cancellationToken = default)
    {
        await _commands.WaitAsync();
        try { await PlayCoreAsync(cancellationToken); }
        finally { _commands.Release(); }
    }

    private async Task PlayCoreAsync(CancellationToken cancellationToken = default)
    {
        if (Clock.Snapshot.State == PlaybackState.Playing) return;
        if (Playlist is null) throw new InvalidOperationException("プレイリストを読み込んでください。");
        await CloseOutputsAsync();
        if (Video is not null) await Video.DisposeAsync();
        Video = new(tools, Playlist.Timeline, Clock.Position);
        try { await Video.Ready.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken); }
        catch { await Video.DisposeAsync(); Video = null; throw; }
        var lead = .15 + Math.Max(0, -_settings.Machine.Controllers.Append(_settings.Machine.Audio).Min(o => o.OffsetMs)) / 1000d;
        cancellationToken.ThrowIfCancellationRequested();
        _lastFrame = -1;
        Clock.Play(lead);
        _outputsStop = new();
        var token = _outputsStop.Token;
        _outputs = Enumerable.Range(0, 3).Select(i => Task.Run(() => MaintainOutputAsync(i, token), token)).ToArray();
        log.Write("play", new { Clock.Snapshot.Anchor, lead });
    }

    public async Task PauseAsync()
    {
        await _commands.WaitAsync();
        try
        {
            if (Clock.Snapshot.State != PlaybackState.Playing) return;
            Clock.Pause(); await CloseOutputsAsync(); log.Write("pause", new { Clock.Position });
        }
        finally { _commands.Release(); }
    }

    public async Task StopAsync()
    {
        await _commands.WaitAsync();
        try { await StopCoreAsync(); }
        finally { _commands.Release(); }
    }

    private async Task StopCoreAsync()
    {
        Clock.Stop();
        await CloseOutputsAsync();
        if (Video is not null) { await Video.DisposeAsync(); Video = null; }
        _lastLoop = -1;
        _lastFrame = -1;
        _lastItem = -1;
        log.Write("stop");
    }

    public async Task SeekAsync(double seconds, bool play, CancellationToken cancellationToken = default)
    {
        await _commands.WaitAsync();
        try
        {
            if (Playlist is null) return;
            Clock.Pause();
            await CloseOutputsAsync();
            if (Video is not null) { await Video.DisposeAsync(); Video = null; }
            Clock.Seek(Math.Clamp(seconds, 0, Math.Max(0, Playlist.Timeline.Duration - 1 / Playlist.Items[^1].Info.FrameRate)));
            _lastLoop = -1;
            _lastFrame = -1;
            if (play) await PlayCoreAsync(cancellationToken);
            else { Video = new(tools, Playlist.Timeline, Clock.Position); await Video.Ready.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken); }
            log.Write("seek", new { seconds, play });
        }
        finally { _commands.Release(); }
    }

    public VideoFrame? TakeDueFrame()
    {
        if (Playlist is null || Video is null) return null;
        if (Video.Error is not null) throw new IOException("映像デコードが停止しました。", Video.Error);
        var snapshot = Clock.Snapshot;
        if (snapshot.State == PlaybackState.Playing && snapshot.At(PlaybackClock.QpcSeconds) < snapshot.Anchor) return null;
        var position = Clock.Position;
        var point = Playlist.Timeline.Locate(position);
        var loop = point.Cycle;
        var target = Playlist.Timeline.FrameAt(position);
        VideoFrame? selected = null;
        while (Video.TryPeek(out var next) && next!.Sequence <= target && Video.TryRead(out var frame))
        {
            selected?.Dispose(); selected = frame;
            _lastFrame = frame!.Sequence;
            Interlocked.Increment(ref _progress);
        }
        // When no new frame is due, a healthy displayed frame is not a playback hang.
        if (_lastFrame == target) Interlocked.Increment(ref _progress);
        if (Clock.Snapshot.State == PlaybackState.Playing && loop != _lastLoop)
        {
            log.Write("loop", new { loop }); _lastLoop = loop;
        }
        if (point.Index != _lastItem)
        {
            log.Write("playlist-item", new { index = point.Index, Playlist.Items[point.Index].Info.Path, loop });
            _lastItem = point.Index;
        }
        return selected;
    }

    private async Task MaintainOutputAsync(int index, CancellationToken token)
    {
        var attempts = 0;
        while (!token.IsCancellationRequested)
        {
            var registered = index == 0 ? _settings.Machine.Audio : _settings.Machine.Controllers[index - 1];
            if (registered.EndpointId.Length == 0) { OutputStatus[index] = "未登録"; return; }
            WasapiSink? sink = null;
            try
            {
                OutputStatus[index] = "接続待機";
                var device = DeviceCatalog.Match(registered, DeviceCatalog.Enumerate(), index > 0)
                    ?? throw new IOException("登録機器が見つかりません。");
                if (index > 0) DeviceCatalog.InitializeHaptics(device.Hid!);
                sink = new(device, registered, Playlist!.Pcm, Clock, () => Volatile.Read(ref _routing)[index],
                    index > 0 ? _settings.ReconnectFadeMs : 0, log, () => { });
                _sinks[index] = sink;
                await sink.Ready.WaitAsync(TimeSpan.FromSeconds(5), token);
                OutputStatus[index] = "ストリーム稼働中 · " + sink.FormatDescription;
                log.Write("output-reconnected", new { index, device.Id });
                attempts = 0;
                await sink.Completion.WaitAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                OutputStatus[index] = "待機: " + ex.Message;
                log.Write("output-unavailable", new { index, reason = ex.ToString() });
            }
            finally
            {
                _sinks[index] = null;
                if (sink is not null) await sink.DisposeAsync();
            }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(5, ++attempts)), token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
        }
        OutputStatus[index] = "停止";
    }

    private async Task CloseOutputsAsync()
    {
        if (_outputsStop is null) return;
        Volatile.Write(ref _outputStopPending, true);
        try
        {
            await _outputsStop.CancelAsync();
            try { await Task.WhenAll(_outputs); } catch (OperationCanceledException) { }
            _outputsStop.Dispose(); _outputsStop = null; _outputs = [];
        }
        finally { Volatile.Write(ref _outputStopPending, false); }
    }

    public async Task TestGripAsync(int slot, bool right, CancellationToken cancellationToken = default)
    {
        await _commands.WaitAsync();
        try
        {
            if (Clock.Snapshot.State != PlaybackState.Stopped) throw new InvalidOperationException("左右テストは停止中に行ってください。");
            if (_settings.HapticsStopped) throw new InvalidOperationException("振動全停止を明示解除してからテストしてください。");
            var registered = _settings.Machine.Controllers[slot];
            var device = await Task.Run(() => DeviceCatalog.Match(registered, DeviceCatalog.Enumerate(), true))
                ?? throw new IOException("登録したDualSenseが見つかりません。");
            await Task.Run(() => DeviceCatalog.InitializeHaptics(device.Hid!));
            var samples = new float[48000 * 4];
            for (var i = 0; i < 38400; i++) samples[i * 4 + (right ? 3 : 2)] = (float)(.5 * Math.Sin(2 * Math.PI * 110 * i / 48000) * Math.Clamp(Math.Min(i, 38400 - i) / 960d, 0, 1));
            var clock = new PlaybackClock(); clock.Play(.1);
            await using var sink = new WasapiSink(device, registered, new PcmClip(samples, 1, [0, 1, 2, 3]), clock,
                () => Volatile.Read(ref _routing)[slot + 1] with { OffsetMs = 0 }, 50, log, () => { });
            _testSink = sink;
            await sink.Ready.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            await Task.Delay(1000, cancellationToken);
            if (sink.Completion.IsFaulted) await sink.Completion;
            log.Write("grip-test", new { slot, right });
        }
        finally { _testSink = null; _commands.Release(); }
    }

    public async ValueTask DisposeAsync() { await StopAsync(); _commands.Dispose(); }
}
