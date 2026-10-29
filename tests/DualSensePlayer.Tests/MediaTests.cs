using DualSensePlayer.Core;
using Xunit;

namespace DualSensePlayer.Tests;

public sealed class MediaTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "dualsense-media-tests-" + Guid.NewGuid());
    private readonly FfmpegTools _tools = FfmpegTools.Discover();
    private string _pcm = "";
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _pcm = Path.Combine(_directory, "four-channel.mov");
        await Generate(_pcm, "aevalsrc=0.1|0.2|0.3|-0.4:s=48000:d=0.4:c=quad", "pcm_s16le");
    }
    private async Task Generate(string path, string audio, string codec, string offset = "0") => await FfmpegTools.CaptureAsync("ffmpeg",
        ["-v", "error", "-nostdin", "-y", "-f", "lavfi", "-i", "testsrc2=size=96x64:rate=30:duration=0.4", "-itsoffset", offset,
            "-f", "lavfi", "-i", audio, "-map", "0:v:0", "-map", "1:a:0", "-c:v", "libx264", "-threads", "2", "-pix_fmt", "yuv420p", "-c:a", codec, path]);

    [Fact] public async Task MovPcmDecodesFourIndependentChannels()
    {
        var info = await _tools.InspectAsync(_pcm);
        Assert.Equal(12, info.FrameCount); Assert.Equal(.4, info.Duration, 5);
        var prepared = await _tools.PrepareAsync(info, info.AudioTracks.Single().Index, [0, 1, 2, 3]);
        Assert.Equal(.1, prepared.Pcm.Read(.1, 0), 3); Assert.Equal(.2, prepared.Pcm.Read(.1, 1), 3);
        Assert.Equal(.3, prepared.Pcm.Read(.1, 2), 3); Assert.Equal(-.4, prepared.Pcm.Read(.1, 3), 3);
        Assert.Equal(64, prepared.Hash.Length);
    }

    [Fact] public async Task AudioPtsGapIsSilenceInsteadOfMovingAudioToZero()
    {
        var path = Path.Combine(_directory, "delayed.mov");
        await Generate(path, "aevalsrc=0.1|0.2|0.3|-0.4:s=48000:d=0.2:c=quad", "pcm_s16le", "0.2");
        var info = await _tools.InspectAsync(path);
        var prepared = await _tools.PrepareAsync(info, info.AudioTracks.Single().Index, [0, 1, 2, 3]);
        Assert.Equal(0, prepared.Pcm.Read(.1, 2)); Assert.Equal(.3, prepared.Pcm.Read(.3, 2), 3);
        Assert.Contains(prepared.Warnings, w => w.Contains("音声開始差"));
    }

    [Fact] public async Task AacPaddingDoesNotExtendVideoLoop()
    {
        var path = Path.Combine(_directory, "four-channel.mp4");
        await Generate(path, "aevalsrc=0.1*sin(2*PI*440*t)|0.2*sin(2*PI*550*t)|0.3*sin(2*PI*110*t)|0.4*sin(2*PI*220*t):s=48000:d=0.4:c=quad", "aac");
        var info = await _tools.InspectAsync(path);
        var prepared = await _tools.PrepareAsync(info, info.AudioTracks.Single().Index, [0, 1, 2, 3]);
        Assert.Equal("LC", prepared.Track.Profile); Assert.Equal(.4, prepared.Pcm.Duration, 6);
        Assert.Equal(19200, prepared.Pcm.Frames);
        var peak = Enumerable.Range(0, 4800).Select(i => Math.Abs(prepared.Pcm.Read(.1 + i / 48000d, 2))).Max();
        Assert.InRange(peak, .2f, .5f);
    }

    [Fact] public async Task StereoAndCorruptMediaAreRejected()
    {
        var path = Path.Combine(_directory, "stereo.mov");
        await Generate(path, "aevalsrc=0.1|0.2:s=48000:d=0.4:c=stereo", "pcm_s16le");
        await Assert.ThrowsAsync<InvalidDataException>(() => _tools.InspectAsync(path));
        File.WriteAllText(path, "invalid movie");
        await Assert.ThrowsAsync<InvalidDataException>(() => _tools.InspectAsync(path));
    }

    [Fact] public async Task NonzeroVideoPtsTrimsEarlierAudioAndPreservesVideoLoop()
    {
        var path = Path.Combine(_directory, "offset-video.mov");
        await FfmpegTools.CaptureAsync("ffmpeg",
            ["-v", "error", "-nostdin", "-y", "-itsoffset", "0.2", "-f", "lavfi", "-i", "testsrc2=size=96x64:rate=30:duration=0.4",
                "-f", "lavfi", "-i", "aevalsrc=t|t|t|t:s=48000:d=0.6:c=quad", "-copyts", "-map", "0:v:0", "-map", "1:a:0",
                "-c:v", "libx264", "-threads", "2", "-pix_fmt", "yuv420p", "-c:a", "pcm_s24le", "-fps_mode", "passthrough", path]);
        var info = await _tools.InspectAsync(path);
        Assert.Equal(.2, info.VideoStart, 5);
        var prepared = await _tools.PrepareAsync(info, info.AudioTracks.Single().Index, [0, 1, 2, 3]);
        Assert.Equal(.3, prepared.Pcm.Read(.1, 2), 3);
        var decoded = await Frames(info, 0, 24);
        Assert.Equal(decoded[0], decoded[12]);
    }

    [Fact] public async Task VideoDecoderLoopsAndSeeksWithoutLosingFrames()
    {
        var info = await _tools.InspectAsync(_pcm);
        var expected = await Frames(info, 0, info.FrameCount * 3);
        Assert.Equal(expected[0], expected[12]); Assert.Equal(expected[0], expected[24]);
        Assert.NotEqual(expected[0], expected[6]);
        var seek = await Frames(info, .2, 18);
        Assert.Equal(expected[6], seek[0]);
        Assert.Equal(expected[0], seek[6]);
        Assert.Equal(expected[11], seek[17]);
    }
    private async Task<List<string>> Frames(MediaInfo info, double position, int count)
    {
        await using var decoder = new VideoDecoder(_tools, info, position);
        await decoder.Ready.WaitAsync(TimeSpan.FromSeconds(10));
        var frames = new List<string>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (frames.Count < count)
        {
            if (decoder.Error is not null) throw decoder.Error;
            if (decoder.TryRead(out var frame))
            {
                using (frame!) frames.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(frame!.Pixels.AsSpan(0, frame.Length))));
            }
            else await Task.Delay(1, timeout.Token);
        }
        return frames;
    }
    public Task DisposeAsync() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); return Task.CompletedTask; }
}
