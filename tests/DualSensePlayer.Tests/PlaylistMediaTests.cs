using System.Security.Cryptography;
using DualSensePlayer.Core;
using Xunit;

namespace DualSensePlayer.Tests;

public sealed class PlaylistMediaTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "dualsense-playlist-media-" + Guid.NewGuid());
    private readonly FfmpegTools _tools = FfmpegTools.Discover();
    private MediaInfo[] _infos = [];
    private sealed record Frame(long Sequence, int Index, int Width, int Height, string Hash);

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        var specifications = new[] { ("a.mov", "96x64", 30, "0.4", "pcm_s16le", "0"), ("b.mov", "80x48", 25, "0.24", "pcm_s24le", "120"), ("c.mp4", "64x48", 20, "0.3", "aac", "240") };
        var infos = new List<MediaInfo>();
        foreach (var (name, size, rate, duration, codec, hue) in specifications)
        {
            var path = Path.Combine(_directory, name);
            await FfmpegTools.CaptureAsync("ffmpeg", ["-v", "error", "-nostdin", "-y", "-f", "lavfi", "-i", $"testsrc2=size={size}:rate={rate}:duration={duration}",
                "-f", "lavfi", "-i", $"aevalsrc=0.1*sin(2*PI*440*t)|0.2*sin(2*PI*550*t)|0.3*sin(2*PI*110*t)|0.4*sin(2*PI*220*t):s=48000:d={duration}:c=quad",
                "-map", "0:v:0", "-map", "1:a:0", "-vf", "hue=h=" + hue, "-c:v", "libx264", "-threads", "2", "-pix_fmt", "yuv420p", "-c:a", codec, path]);
            infos.Add(await _tools.InspectAsync(path));
        }
        _infos = infos.ToArray();
    }

    [Theory] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task DifferentSizesAndFrameRatesPlayInOrderThenRepeatWholeList(int count)
    {
        var infos = _infos.Take(count).ToArray(); var timeline = new PlaylistTimeline(infos);
        var reference = new List<Frame>();
        for (var i = 0; i < infos.Length; i++)
            reference.AddRange((await ReadSingle(infos[i])).Select(frame => frame with { Index = i }));
        var frames = await ReadPlaylist(timeline, 0, (int)timeline.FrameCount * 2 + 1);
        for (var i = 0; i < frames.Count; i++)
        {
            var expected = reference[i % reference.Count];
            Assert.Equal(i, frames[i].Sequence); Assert.Equal(expected.Index, frames[i].Index);
            Assert.Equal(expected.Width, frames[i].Width); Assert.Equal(expected.Height, frames[i].Height);
            Assert.Equal(expected.Hash, frames[i].Hash);
        }
    }

    [Theory] [InlineData(2)] [InlineData(3)]
    public async Task SeekIntoLaterVideoKeepsCorrectFramesAcrossLastToFirstBoundary(int count)
    {
        var infos = _infos.Take(count).ToArray(); var timeline = new PlaylistTimeline(infos);
        var reference = new List<Frame>();
        for (var i = 0; i < infos.Length; i++) reference.AddRange((await ReadSingle(infos[i])).Select(frame => frame with { Index = i }));
        var position = timeline.StartOf(count - 1) + 2d / infos[^1].FrameRate;
        var first = timeline.FrameAt(position);
        var frames = await ReadPlaylist(timeline, position, (int)timeline.FrameCount + 2);
        for (var i = 0; i < frames.Count; i++)
        {
            Assert.Equal(first + i, frames[i].Sequence);
            Assert.Equal(reference[(int)((first + i) % reference.Count)].Hash, frames[i].Hash);
        }
    }

    [Fact] public async Task PreparationCombinesActualPcmAndAacAndKeepsIndependentMappings()
    {
        var settings = _infos.Select((info, i) => new MediaSettings { Path = info.Path, AudioStreamIndex = info.AudioTracks.Single().Index,
            ChannelMap = i == 1 ? [2, 3, 0, 1] : [0, 1, 2, 3] }).ToArray();
        var prepared = await PreparedPlaylist.PrepareAsync(_tools, _infos, settings);
        for (var i = 0; i < prepared.Items.Count; i++)
        {
            for (var ch = 0; ch < 4; ch++)
                Assert.Equal(prepared.Items[i].Pcm.Read(.1, ch), prepared.Pcm.Read(prepared.Timeline.StartOf(i) + .1, ch), 4);
        }
        Assert.Equal(_infos.Sum(info => info.Duration), prepared.Pcm.Duration, 8);
        Assert.Equal(prepared.Pcm.Read(.1, 2), prepared.Pcm.Read(prepared.Pcm.Duration + .1, 2), 4);
    }

    [Fact] public async Task DisposalCancelsBlockedPrefetchAndAllowsRestart()
    {
        var timeline = new PlaylistTimeline(_infos);
        var decoder = new PlaylistVideoDecoder(_tools, timeline, 0);
        await decoder.Ready.WaitAsync(TimeSpan.FromSeconds(10));
        await decoder.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var frames = await ReadPlaylist(timeline, 0, 3);
        Assert.Equal(0, frames[0].Sequence);
    }

    [Fact] public async Task UnreadableFollowingVideoReportsItsNameBeforePlaybackStarts()
    {
        var missing = _infos[1] with { Path = Path.Combine(_directory, "missing.mov") };
        await using var decoder = new PlaylistVideoDecoder(_tools, new PlaylistTimeline([_infos[0], missing]), 0);
        var error = await Assert.ThrowsAsync<IOException>(() => decoder.Ready.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("2本目 missing.mov", error.Message);
        Assert.Same(error, decoder.Error);
    }

    private async Task<List<Frame>> ReadSingle(MediaInfo info)
    {
        await using var decoder = new VideoDecoder(_tools, info, 0);
        await decoder.Ready.WaitAsync(TimeSpan.FromSeconds(10));
        var frames = new List<Frame>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (frames.Count < info.FrameCount)
        {
            if (decoder.Error is not null) throw decoder.Error;
            if (decoder.TryRead(out var frame)) { using (frame!) frames.Add(Summarize(frame!)); }
            else await Task.Delay(1, timeout.Token);
        }
        return frames;
    }
    private async Task<List<Frame>> ReadPlaylist(PlaylistTimeline timeline, double position, int count)
    {
        await using var decoder = new PlaylistVideoDecoder(_tools, timeline, position);
        await decoder.Ready.WaitAsync(TimeSpan.FromSeconds(10));
        var frames = new List<Frame>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (frames.Count < count)
        {
            if (decoder.Error is not null) throw decoder.Error;
            if (decoder.TryRead(out var frame)) { using (frame!) frames.Add(Summarize(frame!)); }
            else await Task.Delay(1, timeout.Token);
        }
        return frames;
    }
    private static Frame Summarize(VideoFrame frame) => new(frame.Sequence, frame.ItemIndex, frame.Width, frame.Height,
        Convert.ToHexString(SHA256.HashData(frame.Pixels.AsSpan(0, frame.Length))));
    public Task DisposeAsync() { Directory.Delete(_directory, true); return Task.CompletedTask; }
}
