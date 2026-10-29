namespace DualSensePlayer.Core;

public readonly record struct PlaylistPosition(int Index, double LocalSeconds, long Cycle, double ItemStart);

/// <summary>One cumulative timeline for video, ordinary audio, and both controllers.</summary>
public sealed class PlaylistTimeline
{
    public const int MaximumItems = 100;
    public const long MaximumPcmBytes = 2L * 1024 * 1024 * 1024;
    private readonly double[] _starts;
    private readonly long[] _frameStarts;
    public IReadOnlyList<MediaInfo> Items { get; }
    public double Duration { get; }
    public long FrameCount { get; }

    public PlaylistTimeline(IEnumerable<MediaInfo> media)
    {
        var items = media.ToArray();
        if (items.Length is < 1 or > MaximumItems) throw new InvalidDataException($"動画を1〜{MaximumItems}本配置してください。");
        if (items.Any(item => !double.IsFinite(item.Duration) || item.Duration <= 0 || item.FrameCount < 1 || !double.IsFinite(item.FrameRate) || item.FrameRate <= 0))
            throw new InvalidDataException("プレイリスト内の動画の時間情報が正しくありません。");
        Items = Array.AsReadOnly(items);
        _starts = new double[items.Length]; _frameStarts = new long[items.Length];
        for (var i = 0; i < items.Length; i++)
        {
            _starts[i] = Duration; _frameStarts[i] = FrameCount;
            Duration += items[i].Duration; FrameCount += items[i].FrameCount;
        }
    }

    public static void CheckMemoryBudget(IEnumerable<MediaInfo> items)
    {
        var bytes = items.Sum(item => checked((long)Math.Ceiling(item.Duration * PcmClip.SampleRate) * PcmClip.Channels * sizeof(float)));
        if (bytes > MaximumPcmBytes)
            throw new InvalidDataException($"プレイリストの音声先読みには{bytes / (1024d * 1024):0}MiB必要です。上限は2048MiB（合計約46分）です。動画を減らしてください。");
    }

    public double StartOf(int index) => _starts[index];

    public PlaylistPosition Locate(double absoluteSeconds)
    {
        if (!double.IsFinite(absoluteSeconds) || absoluteSeconds < 0) throw new ArgumentOutOfRangeException(nameof(absoluteSeconds));
        var cycle = (long)Math.Floor(absoluteSeconds / Duration);
        var within = absoluteSeconds % Duration;
        var index = Array.BinarySearch(_starts, within);
        if (index < 0) index = ~index - 1;
        return new(index, within - _starts[index], cycle, cycle * Duration + _starts[index]);
    }

    public long FrameAt(double absoluteSeconds)
    {
        var point = Locate(absoluteSeconds);
        return point.Cycle * FrameCount + _frameStarts[point.Index] + Items[point.Index].FrameAt(point.LocalSeconds);
    }
}

public sealed class PlaylistPcm : PcmTimeline
{
    private readonly PcmClip[] _clips;
    public PlaylistTimeline Timeline { get; }
    public override double Duration => Timeline.Duration;

    public PlaylistPcm(PlaylistTimeline timeline, IEnumerable<PcmClip> clips)
    {
        Timeline = timeline; _clips = clips.ToArray();
        if (_clips.Length != timeline.Items.Count || _clips.Where((clip, i) => Math.Abs(clip.Duration - timeline.Items[i].Duration) > 1e-9).Any())
            throw new ArgumentException("The PCM and video timelines must match.");
    }

    public override float Read(double absoluteSeconds, int logicalChannel)
    {
        if (!double.IsFinite(absoluteSeconds) || absoluteSeconds < 0) return 0;
        var point = Timeline.Locate(absoluteSeconds);
        var next = _clips[(point.Index + 1) % _clips.Length];
        return _clips[point.Index].ReadSegment(point.LocalSeconds, logicalChannel, next.Read(0, logicalChannel));
    }
}

public sealed class PreparedPlaylist
{
    public IReadOnlyList<PreparedMedia> Items { get; }
    public PlaylistTimeline Timeline { get; }
    public PlaylistPcm Pcm { get; }
    public PreparedPlaylist(IEnumerable<PreparedMedia> media)
    {
        var items = media.ToArray();
        Items = Array.AsReadOnly(items);
        Timeline = new(items.Select(item => item.Info));
        Pcm = new(Timeline, items.Select(item => item.Pcm));
    }

    public static async Task<PreparedPlaylist> PrepareAsync(FfmpegTools tools, IReadOnlyList<MediaInfo> infos,
        IReadOnlyList<MediaSettings> settings, CancellationToken cancellationToken = default)
    {
        _ = new PlaylistTimeline(infos);
        PlaylistTimeline.CheckMemoryBudget(infos);
        if (infos.Count != settings.Count) throw new ArgumentException("Playlist settings do not match the selected videos.");
        var prepared = new List<PreparedMedia>();
        for (var i = 0; i < infos.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            settings[i].Validate(requirePath: true);
            if (!string.Equals(infos[i].Path, settings[i].Path, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("動画の検査結果とプレイリストの順序が一致しません。");
            try { prepared.Add(await tools.PrepareAsync(infos[i], settings[i].AudioStreamIndex, settings[i].ChannelMap, cancellationToken)); }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            { throw new InvalidDataException($"{i + 1}本目 {Path.GetFileName(infos[i].Path)}: {ex.Message}", ex); }
        }
        return new(prepared);
    }
}
