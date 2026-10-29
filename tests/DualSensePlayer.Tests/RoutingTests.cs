using DualSensePlayer.Core;
using Xunit;

namespace DualSensePlayer.Tests;

public sealed class RoutingTests
{
    private static PcmClip Clip(int[]? map = null)
    {
        var samples = new float[480 * 4];
        for (var i = 0; i < 480; i++) { samples[4 * i] = .1f; samples[4 * i + 1] = .2f; samples[4 * i + 2] = .3f; samples[4 * i + 3] = -.4f; }
        return new(samples, .01, map ?? [0, 1, 2, 3]);
    }

    [Fact] public void FourChannelsNeverDownmixIntoSpeakers()
    {
        var pcm = Clip(); var audio = new float[8]; var haptic = new float[16];
        pcm.Render(audio, 2, 0, 1d / 48000, new(OutputKind.Audio, 1, false, 0));
        pcm.Render(haptic, 4, 0, 1d / 48000, new(OutputKind.Haptics, .25f, false, 0));
        Assert.Equal(new[] { .1f, .2f, .1f, .2f, .1f, .2f, .1f, .2f }, audio);
        Assert.Equal(new[] { 0f, 0f, .075f, -.1f, 0f, 0f, .075f, -.1f, 0f, 0f, .075f, -.1f, 0f, 0f, .075f, -.1f }, haptic);
    }

    [Theory]
    [InlineData(5, 0, 0)] [InlineData(-5, -.004, .3)] [InlineData(5, .016, .3)]
    [InlineData(5, .004, 0)] [InlineData(-5, -.006, 0)]
    public void OffsetSignAndStartupDoNotLeakPreviousLoop(int offset, double time, double expected)
    {
        var buffer = new float[4];
        Clip().Render(buffer, 4, time, 1d / 48000, new(OutputKind.Haptics, 1, false, offset));
        Assert.Equal(expected, buffer[2], 6);
    }

    [Fact] public void ContinuousLoopUsesCumulativeTimeAcrossManyCycles()
    {
        var samples = new float[480 * 4]; samples[3] = .2f; samples[4 * 479 + 3] = .2f;
        var pcm = new PcmClip(samples, .01, [0, 1, 2, 3]);
        for (var loop = 1; loop <= 1000; loop++)
            Assert.Equal(.2, pcm.Read(loop * .01, 3), 5);
    }

    [Fact] public void MappingIsExplicitAndReadersAreIndependent()
    {
        var clip = Clip([2, 3, 0, 1]); var a = new float[4]; var b = new float[4];
        clip.Render(a, 4, 0, 1d / 48000, new(OutputKind.Haptics, 1, false, 0));
        clip.Render(b, 4, 0, 1d / 48000, new(OutputKind.Haptics, .5f, false, 0));
        Assert.Equal(.1f, a[2]); Assert.Equal(.05f, b[2]);
        Assert.Equal(.3f, clip.Read(0, 0));
    }

    [Fact] public void AudioMuteDoesNotMuteHapticsAndEmergencyStopDoesNotMuteAudio()
    {
        var audio = new float[2]; var haptic = new float[4];
        Clip().Render(audio, 2, 0, 1d / 48000, new(OutputKind.Audio, 1, false, 0, EmergencyStop: true));
        Clip().Render(haptic, 4, 0, 1d / 48000, new(OutputKind.Haptics, 1, false, 0, EmergencyStop: true));
        Assert.Equal(.1f, audio[0]); Assert.All(haptic, x => Assert.Equal(0, x));
        Clip().Render(audio, 2, 0, 1d / 48000, new(OutputKind.Audio, 1, true, 0));
        Clip().Render(haptic, 4, 0, 1d / 48000, new(OutputKind.Haptics, 1, false, 0));
        Assert.All(audio, x => Assert.Equal(0, x)); Assert.Equal(.3f, haptic[2]);
    }

    [Fact] public void LimitAndReconnectFadeDoNotNormalizeSource()
    {
        var buffer = new float[16];
        Clip().Render(buffer, 4, 0, 1d / 48000, new(OutputKind.Haptics, 1, false, 0, .1f), 0, .5);
        Assert.Equal(0, buffer[2]); Assert.Equal(.1f, buffer[6]); Assert.Equal(-.1f, buffer[7]);
    }

    [Fact] public void DeviceClockTracksDriftWithBoundedResampling()
    {
        var tracker = new DeviceClockTracker();
        for (var i = 0; i <= 1000; i++) tracker.Observe(i * .1 * 1.0001, 100 + i * .1);
        Assert.InRange(tracker.DriftPpm, 99, 101);
    }

    [Fact] public void PauseSeekAndStopReplaceClockGeneration()
    {
        double time = 100; var clock = new PlaybackClock(() => time);
        clock.Play(.5); Assert.Equal(-.5, clock.Snapshot.At(time));
        time += 2; Assert.Equal(1.5, clock.Position);
        var old = clock.Snapshot.Generation;
        clock.Pause(); time += 3; Assert.Equal(1.5, clock.Position);
        clock.Play(.1); Assert.True(clock.Snapshot.ResumeFromPause);
        clock.Seek(.2); clock.Play(.1); Assert.Equal(.2, clock.Position, 6);
        Assert.False(clock.Snapshot.ResumeFromPause);
        time += .1; Assert.Equal(.2, clock.Position, 6);
        clock.Stop(); Assert.Equal(0, clock.Position); Assert.True(clock.Snapshot.Generation > old);
    }

    [Fact] public void HidInitializationSelectsAudioWithoutRumbleOrTriggers()
    {
        var report = DualSenseReport.EnableUsbAudioHaptics();
        Assert.Equal(48, report.Length); Assert.Equal(2, report[0]); Assert.Equal(0, report[1]);
        Assert.All(report.Skip(2), b => Assert.Equal(0, b));
        Assert.Throws<InvalidDataException>(() => DualSenseReport.EnableUsbAudioHaptics(78));
    }
    [Fact] public void SeekPrerollNeverReplaysSamplesBeforeTheNewAnchor()
    {
        var pcm = Clip(); var buffer = new float[4];
        pcm.Render(buffer, 4, .49, 1d / 48000, new(OutputKind.Haptics, 1, false, 0), minimumSourceTime: .5);
        Assert.All(buffer, value => Assert.Equal(0, value));
        pcm.Render(buffer, 4, .497, 1d / 48000, new(OutputKind.Haptics, 1, false, -5), minimumSourceTime: .5);
        Assert.Equal(.3f, buffer[2]); // Negative correction may lead video, starting at the new anchor.
        pcm.Render(buffer, 4, .503, 1d / 48000, new(OutputKind.Haptics, 1, false, 5), minimumSourceTime: .5);
        Assert.All(buffer, value => Assert.Equal(0, value));
        pcm.Render(buffer, 4, .506, 1d / 48000, new(OutputKind.Haptics, 1, false, 5), minimumSourceTime: .5);
        Assert.Equal(.3f, buffer[2]);
    }
    [Fact] public void PauseResumeIsSilentDuringPrerollThenKeepsEachOutputsCorrectedPosition()
    {
        var pcm = Clip(); var buffer = new float[4];
        pcm.Render(buffer, 4, .497, 1d / 48000, new(OutputKind.Haptics, 1, false, -5), minimumTimelineTime: .5);
        Assert.All(buffer, value => Assert.Equal(0, value));
        pcm.Render(buffer, 4, .5, 1d / 48000, new(OutputKind.Haptics, 1, false, 5), minimumTimelineTime: .5);
        Assert.Equal(.3f, buffer[2]); // Continue at .495, the source position actually reached before pausing.
        pcm.Render(buffer, 4, .5, 1d / 48000, new(OutputKind.Haptics, 1, false, -5), minimumTimelineTime: .5);
        Assert.Equal(.3f, buffer[2]);
    }
}
