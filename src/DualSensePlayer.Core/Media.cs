using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace DualSensePlayer.Core;

public sealed record AudioTrack(int Index, string Codec, string Profile, int Channels, int Rate, string Layout,
    double Start, double Duration)
{
    public bool Supported => Channels == 4 && Rate == 48000 &&
        (Codec is "pcm_s16le" or "pcm_s24le" || Codec == "aac" && Profile == "LC");
    public string DecodedOrder => Layout switch
    {
        "quad" => "FL, FR, BL, BR", "4.0" => "FL, FR, FC, BC",
        "quad(side)" => "FL, FR, SL, SR", "3.1" => "FL, FR, FC, LFE",
        _ => "番号順（レイアウト不明・試験素材での確認必須）"
    };
    public override string ToString() => $"トラック {Index}: {Codec} {Profile}, {Channels}ch / {Rate}Hz / {Layout}";
}

public sealed record MediaInfo(string Path, int Width, int Height, double FrameRate, double VideoStart,
    double Duration, int FrameCount, AudioTrack[] AudioTracks)
{
    public int FrameAt(double seconds) => Math.Clamp((int)Math.Floor((seconds % Duration) * FrameRate + 0.00001), 0, FrameCount - 1);
    public string Summary => $"H.264 {Width}×{Height} / {FrameRate:0.###}fps / {Duration:0.###}秒 / 映像開始PTS {VideoStart:0.######}秒";
}

public sealed record PreparedMedia(MediaInfo Info, AudioTrack Track, PcmClip Pcm, string Hash, string[] Warnings);

public sealed class FfmpegTools(string ffmpeg, string ffprobe)
{
    public string Ffmpeg { get; } = ffmpeg;
    public string Ffprobe { get; } = ffprobe;
    private static string Number(double value) => value.ToString("0.#########", CultureInfo.InvariantCulture);

    public static FfmpegTools Discover()
    {
        var executable = OperatingSystem.IsWindows() ? ".exe" : "";
        var directory = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg");
        return new(Environment.GetEnvironmentVariable("DUALSENSE_FFMPEG") ??
            (File.Exists(Path.Combine(directory, "ffmpeg" + executable)) ? Path.Combine(directory, "ffmpeg" + executable) : "ffmpeg" + executable),
            Environment.GetEnvironmentVariable("DUALSENSE_FFPROBE") ??
            (File.Exists(Path.Combine(directory, "ffprobe" + executable)) ? Path.Combine(directory, "ffprobe" + executable) : "ffprobe" + executable));
    }

    public async Task<MediaInfo> InspectAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("動画が見つかりません。", path);
        if (Path.GetExtension(path).ToLowerInvariant() is not (".mov" or ".mp4"))
            throw new InvalidDataException("MOVまたはMP4を選択してください。");
        using var json = JsonDocument.Parse(await CaptureAsync(Ffprobe,
            ["-v", "error", "-show_streams", "-show_format", "-of", "json", path], cancellationToken));
        var streams = json.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.FirstOrDefault(s => Text(s, "codec_type") == "video" &&
            (!s.TryGetProperty("disposition", out var disposition) || Integer(disposition, "attached_pic") == 0));
        if (video.ValueKind == JsonValueKind.Undefined || Text(video, "codec_name") != "h264")
            throw new InvalidDataException("H.264映像トラックが必要です。");
        var width = Integer(video, "width"); var height = Integer(video, "height");
        var rate = Fraction(Text(video, "avg_frame_rate"));
        if (width is < 1 or > 1920 || height is < 1 or > 1080 || !double.IsFinite(rate) || rate is <= 0 or > 60.0001)
            throw new InvalidDataException("映像は1920×1080以下、60fps以下の固定フレームレートが必要です。");
        var declaredDuration = Real(video, "duration", Real(json.RootElement.GetProperty("format"), "duration"));
        if (!double.IsFinite(declaredDuration) || declaredDuration is <= 0 or > 601)
            throw new InvalidDataException("初期対応範囲は10分以内の動画です。");
        var tracks = streams.Where(s => Text(s, "codec_type") == "audio").Select(s => new AudioTrack(
            Integer(s, "index"), Text(s, "codec_name"), Text(s, "profile"), Integer(s, "channels"),
            Integer(s, "sample_rate"), Text(s, "channel_layout"), Real(s, "start_time"), Real(s, "duration"))).ToArray();
        if (!tracks.Any(t => t.Supported)) throw new InvalidDataException("48kHz・4chのPCM 16/24bitまたはAAC-LCトラックが必要です。通常音声から振動は生成しません。");
        using var frames = JsonDocument.Parse(await CaptureAsync(Ffprobe,
            ["-v", "error", "-threads", "4", "-select_streams", "v:0", "-show_frames", "-show_entries", "frame=best_effort_timestamp_time", "-of", "json", path], cancellationToken));
        var pts = frames.RootElement.GetProperty("frames").EnumerateArray().Select(f => Real(f, "best_effort_timestamp_time", double.NaN)).ToArray();
        if (pts.Length == 0 || pts.Length > 36001 || pts.Any(p => !double.IsFinite(p)))
            throw new InvalidDataException("映像の表示時刻を取得できません。");
        // Validate actual presentation timestamps, rather than trusting avg_frame_rate.
        for (var i = 1; i < pts.Length; i++)
            if (Math.Abs(pts[i] - pts[0] - i / rate) > 0.0015)
                throw new InvalidDataException("可変フレームレートまたは映像PTSの不連続を検出しました。CFRで書き出してください。");
        var duration = pts.Length / rate;
        if (duration > 600.001) throw new InvalidDataException("動画が10分を超えています。");
        return new(Path.GetFullPath(path), width, height, rate, pts[0], duration, pts.Length, tracks);
    }

    public async Task<PreparedMedia> PrepareAsync(MediaInfo info, int streamIndex, int[] channelMap, CancellationToken cancellationToken = default)
    {
        var track = info.AudioTracks.SingleOrDefault(t => t.Index == streamIndex && t.Supported)
            ?? throw new InvalidDataException("対応する4ch音声トラックを選択してください。");
        var samples = new float[checked((int)Math.Ceiling(info.Duration * PcmClip.SampleRate) * 4)];
        // Keep demuxer PTS (including edit lists and AAC skip samples). Pad actual gaps and trim to the video timeline.
        var filter = $"atrim=start={Number(info.VideoStart)}:end={Number(info.VideoStart + info.Duration)}," +
            $"aresample=48000:async=1:first_pts={(long)Math.Round(info.VideoStart * 48000)},asetpts=PTS-STARTPTS";
        using var process = Start(Ffmpeg, ["-v", "error", "-xerror", "-nostdin", "-threads", "4", "-copyts", "-i", info.Path,
            "-map", $"0:{track.Index}", "-vn", "-sn", "-dn", "-af", filter, "-c:a", "pcm_f32le", "-f", "f32le", "pipe:1"]);
        using var cancel = cancellationToken.Register(() => Kill(process));
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        var bytes = new byte[65536];
        var bytePosition = 0;
        int count;
        while ((count = await process.StandardOutput.BaseStream.ReadAsync(bytes, cancellationToken)) > 0)
        {
            var available = Math.Min(count, samples.Length * sizeof(float) - bytePosition);
            if (available > 0) Buffer.BlockCopy(bytes, 0, samples, bytePosition, available);
            bytePosition += available;
        }
        await process.WaitForExitAsync(cancellationToken);
        var error = await errors;
        if (process.ExitCode != 0 || error.Length > 0) throw new InvalidDataException($"音声のデコードに失敗しました: {error}");
        if (bytePosition == 0 || bytePosition % 16 != 0) throw new InvalidDataException("動画の表示区間に有効な4ch音声がありません。");
        var pcm = new PcmClip(samples, info.Duration, channelMap);
        var (dc, peak, nonFinite) = pcm.Diagnose();
        if (nonFinite > 0) throw new InvalidDataException("音声にNaNまたは無限値が含まれています。");
        var warnings = new List<string>();
        if (Math.Abs(track.Start - info.VideoStart) > 0.001)
            warnings.Add($"音声開始差 {(track.Start - info.VideoStart) * 1000:+0.###;-0.###;0}ms。PTSに従って無音補完・切り詰めします。");
        if (Math.Abs(track.Duration - info.Duration) > 0.02)
            warnings.Add($"音声尺 {track.Duration:0.###}秒 / 映像尺 {info.Duration:0.###}秒。周回は映像尺で決定します。");
        for (var ch = 0; ch < 4; ch++)
        {
            warnings.Add($"デコードch{ch + 1}: DC={dc[ch]:0.00000}, peak={peak[ch]:0.00000}" +
                (Math.Abs(dc[ch]) > 0.01 || peak[ch] > 0.98 ? "（波形を確認してください。自動補正なし）" : ""));
        }
        using var file = File.OpenRead(info.Path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken));
        return new(info, track, pcm, hash, warnings.ToArray());
    }

    internal static Process Start(string executable, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            // FFmpeg writes UTF-8 even when Windows uses a legacy console code page.
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        try { return Process.Start(start) ?? throw new IOException($"{executable}を起動できません。"); }
        catch (System.ComponentModel.Win32Exception ex) { throw new IOException("FFmpeg/ffprobeが見つかりません。配布パッケージのtools/ffmpegを確認してください。", ex); }
    }

    public static async Task<string> CaptureAsync(string executable, IEnumerable<string> arguments, CancellationToken cancellationToken = default)
    {
        using var process = Start(executable, arguments);
        using var cancel = cancellationToken.Register(() => Kill(process));
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var error = await errors;
        if (process.ExitCode != 0 || error.Length > 0) throw new InvalidDataException($"素材の検査に失敗しました: {error}");
        return await output;
    }

    internal static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); }
        catch (InvalidOperationException) { }
    }

    private static string Text(JsonElement element, string key) => element.TryGetProperty(key, out var value) ? value.ToString() : "";
    private static int Integer(JsonElement element, string key) => int.TryParse(Text(element, key), CultureInfo.InvariantCulture, out var value) ? value : 0;
    private static double Real(JsonElement element, string key, double fallback = 0) => double.TryParse(Text(element, key), CultureInfo.InvariantCulture, out var value) ? value : fallback;
    private static double Fraction(string value)
    {
        var parts = value.Split('/');
        return parts.Length == 2 && double.TryParse(parts[0], CultureInfo.InvariantCulture, out var n) &&
            double.TryParse(parts[1], CultureInfo.InvariantCulture, out var d) && d != 0 ? n / d : 0;
    }
}

public sealed class VideoFrame(long sequence, byte[] pixels, int length, int width = 0, int height = 0) : IDisposable
{
    private byte[]? _pixels = pixels;
    public long Sequence { get; internal set; } = sequence;
    public int ItemIndex { get; internal set; }
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Pixels => _pixels ?? throw new ObjectDisposedException(nameof(VideoFrame));
    public int Length { get; } = length;
    public void Dispose() { var pixels = Interlocked.Exchange(ref _pixels, null); if (pixels is not null) ArrayPool<byte>.Shared.Return(pixels); }
}

/// <summary>A single decoder produces a bounded, prebuffered sequence, including successive loops.</summary>
public sealed class VideoDecoder : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<VideoFrame> _frames = Channel.CreateBounded<VideoFrame>(new BoundedChannelOptions(8)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _worker;
    public Exception? Error { get; private set; }
    public Task Ready => _ready.Task;
    public bool TryPeek(out VideoFrame? frame) => _frames.Reader.TryPeek(out frame);
    public bool TryRead(out VideoFrame? frame) => _frames.Reader.TryRead(out frame);
    internal async ValueTask<VideoFrame> ReadAsync(CancellationToken cancellationToken)
    {
        try { return await _frames.Reader.ReadAsync(cancellationToken); }
        catch (ChannelClosedException ex) { throw new IOException("映像のフレーム供給が停止しました。", Error ?? ex); }
    }

    public VideoDecoder(FfmpegTools tools, MediaInfo info, double position)
    {
        // Windows Process stdout is a synchronous anonymous pipe. Async reads of each
        // small pipe block otherwise repeatedly queue blocking work to the thread pool.
        // One dedicated reader also keeps video I/O separate from audio/control work.
        _worker = Task.Factory.StartNew(() =>
        {
            Process? process = null;
            try
            {
                var index = info.FrameAt(position);
                var sequence = (long)Math.Floor(position / info.Duration) * info.FrameCount + index;
                // Accurate timestamp seek, then demuxer looping. No player-local end-of-file clock.
                process = FfmpegTools.Start(tools.Ffmpeg, ["-v", "error", "-xerror", "-nostdin", "-threads", "4", "-filter_threads", "2",
                    "-stream_loop", "-1", "-ss", (info.VideoStart + index / info.FrameRate).ToString("0.#########", CultureInfo.InvariantCulture),
                    "-seek_timestamp", "1", "-noautorotate", "-i", info.Path, "-map", "0:v:0", "-an", "-sn", "-dn",
                    "-fps_mode", "passthrough", "-pix_fmt", "bgra", "-threads", "1", "-f", "rawvideo", "pipe:1"]);
                using var cancel = _stop.Token.Register(() => FfmpegTools.Kill(process));
                var errors = process.StandardError.ReadToEndAsync();
                var frameBytes = checked(info.Width * info.Height * 4);
                var prepared = 0;
                while (!_stop.IsCancellationRequested)
                {
                    var pixels = ArrayPool<byte>.Shared.Rent(frameBytes);
                    // Cancellation kills FFmpeg and closes the pipe, unblocking ReadExactly.
                    try { process.StandardOutput.BaseStream.ReadExactly(pixels.AsSpan(0, frameBytes)); }
                    catch { ArrayPool<byte>.Shared.Return(pixels); throw; }
                    var frame = new VideoFrame(sequence++, pixels, frameBytes, info.Width, info.Height);
                    try { _frames.Writer.WriteAsync(frame, _stop.Token).AsTask().GetAwaiter().GetResult(); }
                    catch { frame.Dispose(); throw; }
                    if (++prepared >= 3) _ready.TrySetResult();
                }
                errors.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (!_stop.IsCancellationRequested) { Error = ex; _ready.TrySetException(ex); }
            }
            finally
            {
                if (process is not null) { FfmpegTools.Kill(process); process.Dispose(); }
                _ready.TrySetCanceled();
                _frames.Writer.TryComplete();
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _worker;
        while (TryRead(out var frame)) frame!.Dispose();
        _stop.Dispose();
    }
}
