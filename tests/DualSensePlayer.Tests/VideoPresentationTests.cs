using System.Buffers;
using DualSensePlayer.Core;
using Xunit;

namespace DualSensePlayer.Tests;

public sealed class VideoPresentationTests
{
    private static VideoFrame Frame(long sequence) => new(sequence, ArrayPool<byte>.Shared.Rent(16), 16, 2, 2);

    [Fact]
    public void BusyCompositorRetriesOnNextRefreshWithoutHalvingThirtyFpsVideo()
    {
        using var buffer = new VideoPresentationBuffer();
        var accepted = new List<long>();
        // A 30fps source, a 60Hz display, and a compositor busy on each first attempt.
        for (var index = 0; index < 30; index++)
        {
            var frame = Frame(index);
            buffer.Offer(frame);
            Assert.Null(buffer.TryPresent(_ => false));
            Assert.True(buffer.HasPending);
            Assert.NotEmpty(frame.Pixels);
            var sequence = buffer.TryPresent(f => { accepted.Add(f.Sequence); return true; });
            Assert.Equal(index, sequence);
            Assert.False(buffer.HasPending);
            Assert.Throws<ObjectDisposedException>(() => frame.Pixels);
        }
        Assert.Equal(Enumerable.Range(0, 30).Select(i => (long)i), accepted);
    }

    [Fact]
    public void NewerDueFrameReplacesBlockedFrameAndClearPreventsStaleSeekFrame()
    {
        using var buffer = new VideoPresentationBuffer();
        var old = Frame(80); var current = Frame(82);
        buffer.Offer(old);
        Assert.Null(buffer.TryPresent(_ => false));
        buffer.Offer(current);
        Assert.Throws<ObjectDisposedException>(() => old.Pixels);
        Assert.Equal(82, buffer.TryPresent(f => f.Sequence == 82));
        Assert.Throws<ObjectDisposedException>(() => current.Pixels);

        var pending = Frame(83);
        buffer.Offer(pending);
        buffer.Clear();
        Assert.Throws<ObjectDisposedException>(() => pending.Pixels);
        Assert.Null(buffer.TryPresent(_ => throw new Exception("Old frame reached the renderer after a seek.")));
        buffer.Offer(Frame(0));
        Assert.Equal(0, buffer.TryPresent(_ => true));
    }

    [Fact]
    public void MeterUsesElapsedTimeCountsGapsAndResetsAfterSeek()
    {
        var meter = new VideoPerformanceMeter();
        meter.Reset(100);
        for (var i = 0; i < 30; i++) meter.Presented(i, 2);
        Assert.False(meter.Measure(100.5).Measured);
        var first = meter.Measure(101);
        Assert.Equal(30, first.FramesPerSecond);
        Assert.Equal(2, first.CopyMilliseconds);
        Assert.Equal(0, first.SkippedFrames);
        meter.Busy(); meter.Presented(32, 3);
        var second = meter.Measure(102);
        Assert.Equal(1, second.FramesPerSecond);
        Assert.Equal(2, second.SkippedFrames);
        Assert.Equal(1, second.BusyAttempts);
        Assert.Equal(0, meter.Measure(103).FramesPerSecond);

        meter.Reset(200);
        meter.Presented(500, 1);
        var reset = meter.Measure(201);
        Assert.Equal(0, reset.SkippedFrames);
        Assert.Equal(0, reset.BusyAttempts);
        Assert.Equal(1, reset.FramesPerSecond);
    }
}
