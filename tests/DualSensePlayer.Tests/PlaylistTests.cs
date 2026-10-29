using System.Text.Json;
using DualSensePlayer.Core;
using Xunit;

namespace DualSensePlayer.Tests;

public sealed class PlaylistTests
{
    private static MediaInfo Info(string path, int frames, double rate = 50) => new(path, 96, 64, rate, 0, frames / rate, frames, []);
    private static PcmClip Clip(double duration, float[] values, int[]? map = null)
    {
        var samples = new float[(int)Math.Ceiling(duration * 48000) * 4];
        for (var i = 0; i < samples.Length; i++) samples[i] = values[i % 4];
        return new(samples, duration, map ?? [0, 1, 2, 3]);
    }
    private static PlaylistPcm Audio()
    {
        var timeline = new PlaylistTimeline([Info("a.mov", 1), Info("b.mov", 2)]);
        return new(timeline, [Clip(.02, [.1f, .2f, .3f, .4f]), Clip(.04, [.5f, .6f, .7f, .8f], [3, 2, 1, 0])]);
    }

    [Fact] public void DifferentVideoDurationsShareOneLoopAndFrameSequence()
    {
        var timeline = new PlaylistTimeline([Info("a", 2, 25), Info("b", 3, 30), Info("c", 1, 20)]);
        Assert.Equal(.23, timeline.Duration, 9);
        Assert.Equal(0, timeline.Locate(.079).Index);
        Assert.Equal(1, timeline.Locate(.08).Index);
        Assert.Equal(2, timeline.FrameAt(.08));
        Assert.Equal(5, timeline.FrameAt(timeline.StartOf(2)));
        Assert.Equal(0, timeline.Locate(timeline.Duration).Index);
        Assert.Equal(6, timeline.FrameAt(timeline.Duration));
        var later = timeline.Locate(timeline.Duration * 1000 + .09);
        Assert.Equal(1000, later.Cycle); Assert.Equal(1, later.Index); Assert.Equal(.01, later.LocalSeconds, 8);
    }

    [Fact] public void AudioAndHapticsSwitchAtSameBoundaryWithPerVideoMapping()
    {
        var pcm = Audio(); var audio = new float[8]; var haptics = new float[16];
        var start = .02 - 1d / 48000;
        pcm.Render(audio, 2, start, 1d / 48000, new(OutputKind.Audio, 1, false, 0));
        pcm.Render(haptics, 4, start, 1d / 48000, new(OutputKind.Haptics, 1, false, 0));
        Assert.Equal(.1, audio[0], 5); Assert.Equal(.3, haptics[2], 5);
        Assert.Equal(.8, audio[2], 5); Assert.Equal(.6, haptics[6], 5);
        for (var frame = 0; frame < 4; frame++) { Assert.Equal(0, haptics[frame * 4]); Assert.Equal(0, haptics[frame * 4 + 1]); }
    }

    [Fact] public void CorrectionsReadAdjacentVideosAndWrapWholePlaylist()
    {
        var pcm = Audio(); var buffer = new float[4];
        pcm.Render(buffer, 4, .0205, 1d / 48000, new(OutputKind.Haptics, 1, false, 1));
        Assert.Equal(.3f, buffer[2]);
        pcm.Render(buffer, 4, .0195, 1d / 48000, new(OutputKind.Haptics, 1, false, -1));
        Assert.Equal(.6f, buffer[2]);
        pcm.Render(buffer, 4, .0605, 1d / 48000, new(OutputKind.Haptics, 1, false, 1));
        Assert.Equal(.6f, buffer[2]);
        Assert.Equal(.3, pcm.Read(.0605, 2), 5);
        Assert.Equal(0, pcm.Read(-.0001, 2));
    }

    [Fact] public void FractionalSampleAtSeamUsesFollowingVideoInsteadOfRepeatingCurrentHead()
    {
        var pcm = Audio();
        Assert.Equal(.45, pcm.Read(.02 - .5 / 48000, 2), 5);
        Assert.Equal(.45, pcm.Read(.06 - .5 / 48000, 2), 5);
    }

    [Fact] public void IndependentReadersAndEmergencyStopSurviveMultipleBoundaries()
    {
        var pcm = Audio(); var first = new float[16]; var second = new float[16];
        pcm.Render(first, 4, .01999, .02, new(OutputKind.Haptics, .25f, false, 0));
        pcm.Render(second, 4, .01999, .02, new(OutputKind.Haptics, .5f, false, 0));
        for (var i = 0; i < first.Length; i++) Assert.Equal(first[i] * 2, second[i], 6);
        pcm.Render(second, 4, .01999, .02, new(OutputKind.Haptics, 1, false, 0, EmergencyStop: true));
        Assert.All(second, value => Assert.Equal(0, value));
        var audio = new float[8];
        pcm.Render(audio, 2, .01999, .02, new(OutputKind.Audio, 1, false, 0, EmergencyStop: true));
        Assert.Contains(audio, value => value > 0);
    }

    [Fact] public void MemoryAndItemLimitsAreCheckedBeforeAllocatingPcm()
    {
        Assert.Throws<InvalidDataException>(() => new PlaylistTimeline([]));
        Assert.Throws<InvalidDataException>(() => new PlaylistTimeline(Enumerable.Repeat(Info("a", 1), 101)));
        PlaylistTimeline.CheckMemoryBudget(Enumerable.Repeat(Info("a", 18000, 30), 4));
        Assert.Throws<InvalidDataException>(() => PlaylistTimeline.CheckMemoryBudget(Enumerable.Repeat(Info("a", 18000, 30), 5)));
    }

    [Fact] public void LegacySingleVideoUpgradesWithoutLosingMappingDevicesOrStops()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dualsense-migration-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var store = new SettingsStore(directory);
            var old = new PlayerSettings { SchemaVersion = 1, Media = new() { Path = "old.mov", Sha256 = "saved-hash", AudioStreamIndex = 2, ChannelMap = [2, 3, 0, 1], MappingConfirmed = true },
                AutoPlay = true, HapticsStopped = true, ExhibitionStopped = false, Machine = new() { MonitorId = "screen", Controllers = [new() { EndpointId = "one", ContainerId = "container" }, new()] } };
            File.WriteAllText(store.SettingsPath, JsonSerializer.Serialize(old));
            var loaded = store.Load();
            Assert.Equal(2, loaded.SchemaVersion); Assert.Single(loaded.Playlist); Assert.Empty(loaded.Media.Path);
            Assert.Equal("old.mov", loaded.Playlist[0].Path); Assert.Equal("saved-hash", loaded.Playlist[0].Sha256);
            Assert.Equal(new[] { 2, 3, 0, 1 }, loaded.Playlist[0].ChannelMap); Assert.True(loaded.Playlist[0].MappingConfirmed);
            Assert.Equal("one", loaded.Machine.Controllers[0].EndpointId); Assert.Equal("screen", loaded.Machine.MonitorId);
            Assert.True(loaded.AutoPlay); Assert.True(loaded.HapticsStopped); Assert.False(loaded.ExhibitionStopped);
            store.Save(loaded); Assert.Single(store.Load().Playlist);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact] public void SaveRestoreKeepsOrderDuplicatesAndEachVideosTrack()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dualsense-order-" + Guid.NewGuid());
        try
        {
            var store = new SettingsStore(directory);
            store.Save(new() { Playlist = [new() { Path = "a.mov", AudioStreamIndex = 1 }, new() { Path = "b.mp4", AudioStreamIndex = 3 }, new() { Path = "a.mov", AudioStreamIndex = 2, ChannelMap = [1, 0, 3, 2] }] });
            var items = store.Load().Playlist;
            Assert.Equal(new[] { "a.mov", "b.mp4", "a.mov" }, items.Select(item => item.Path));
            Assert.Equal(new[] { 1, 3, 2 }, items.Select(item => item.AudioStreamIndex));
            Assert.Equal(new[] { 1, 0, 3, 2 }, items[2].ChannelMap);
            store.Save(store.Load() with { Playlist = [] }); Assert.Empty(store.Load().Playlist);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact] public void InvalidMappingInLaterItemRejectsWholeSettings()
    {
        Assert.Throws<InvalidDataException>(() => new PlayerSettings { Playlist = [new() { Path = "a.mov" }, new() { Path = "b.mov", ChannelMap = [0, 0, 2, 3] }] }.Validate());
    }
}
