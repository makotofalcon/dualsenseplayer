using System.Threading.Channels;

namespace DualSensePlayer.Core;

/// <summary>Prebuffers the following video while preserving a single playlist frame sequence.</summary>
public sealed class PlaylistVideoDecoder : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<VideoFrame> _frames = Channel.CreateBounded<VideoFrame>(new BoundedChannelOptions(4)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _worker;
    public Exception? Error { get; private set; }
    public Task Ready => _ready.Task;
    public bool TryPeek(out VideoFrame? frame) => _frames.Reader.TryPeek(out frame);
    public bool TryRead(out VideoFrame? frame) => _frames.Reader.TryRead(out frame);

    public PlaylistVideoDecoder(FfmpegTools tools, PlaylistTimeline timeline, double position)
    {
        _worker = Task.Run(async () =>
        {
            VideoDecoder? current = null, next = null;
            var point = timeline.Locate(position);
            var index = point.Index;
            var readingIndex = index;
            try
            {
                current = new(tools, timeline.Items[index], point.LocalSeconds);
                if (timeline.Items.Count > 1) next = new(tools, timeline.Items[(index + 1) % timeline.Items.Count], 0);
                await current.Ready.WaitAsync(_stop.Token);
                if (next is not null)
                {
                    readingIndex = (index + 1) % timeline.Items.Count;
                    await next.Ready.WaitAsync(_stop.Token);
                    readingIndex = index;
                }
                var remaining = timeline.Items[index].FrameCount - timeline.Items[index].FrameAt(point.LocalSeconds);
                var sequence = timeline.FrameAt(position);
                var prepared = 0;
                while (!_stop.IsCancellationRequested)
                {
                    for (var i = 0; i < remaining; i++)
                    {
                        var frame = await current.ReadAsync(_stop.Token);
                        frame.Sequence = sequence++; frame.ItemIndex = index;
                        try { await _frames.Writer.WriteAsync(frame, _stop.Token); }
                        catch { frame.Dispose(); throw; }
                        if (++prepared >= 3) _ready.TrySetResult();
                    }
                    index = (index + 1) % timeline.Items.Count;
                    readingIndex = index;
                    if (timeline.Items.Count == 2)
                    {
                        // Both processes already have their next loop buffered; reuse them indefinitely.
                        (current, next) = (next!, current);
                    }
                    else if (timeline.Items.Count > 2)
                    {
                        var previous = current;
                        current = next!;
                        next = new(tools, timeline.Items[(index + 1) % timeline.Items.Count], 0);
                        await previous.DisposeAsync();
                    }
                    await current.Ready.WaitAsync(_stop.Token);
                    remaining = timeline.Items[index].FrameCount;
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (!_stop.IsCancellationRequested)
                {
                    Error = new IOException($"{readingIndex + 1}本目 {Path.GetFileName(timeline.Items[readingIndex].Path)}: {ex.Message}", ex);
                    _ready.TrySetException(Error);
                }
            }
            finally
            {
                if (current is not null) await current.DisposeAsync();
                if (next is not null) await next.DisposeAsync();
                _ready.TrySetCanceled(); _frames.Writer.TryComplete();
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _worker;
        while (TryRead(out var frame)) frame!.Dispose();
        _stop.Dispose();
    }
}
