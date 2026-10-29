using System.Buffers;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DualSensePlayer.App;
using DualSensePlayer.Core;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new VisitorWindow(_ => { }, () => { }) { Width = 640, Height = 360, Topmost = false, AllowClose = true };
        using var pending = new VideoPresentationBuffer();
        var meter = new VideoPerformanceMeter();
        var elapsed = new Stopwatch();
        long offered = -1;
        var result = 0;
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        EventHandler rendering = (_, _) =>
        {
            try
            {
                if (!elapsed.IsRunning) { elapsed.Start(); meter.Reset(0); }
                var seconds = elapsed.Elapsed.TotalSeconds;
                if (seconds >= 4) { Finish(); return; }
                var due = (long)(seconds * 30);
                if (due > offered) { pending.Offer(Frame(due, 1920, 1080)); offered = due; }
                if (!pending.HasPending) return;
                var started = Stopwatch.GetTimestamp();
                var sequence = pending.TryPresent(window.TryPresent);
                if (sequence is { } accepted) meter.Presented(accepted, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                else meter.Busy();
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); result = 1; application.Shutdown(); }
        };
        void Finish()
        {
            var measured = meter.Measure(elapsed.Elapsed.TotalSeconds);
            Console.WriteLine($"WPF 1080p30 submissions: {measured.FramesPerSecond:F2} fps; copy {measured.CopyMilliseconds:F2} ms; skipped {measured.SkippedFrames}; busy {measured.BusyAttempts}; render tier {RenderCapability.Tier >> 16}");
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
                Console.WriteLine(FormattableString.Invariant($"::notice title=WPF 1080p30 submissions::{measured.FramesPerSecond:F2} fps; copy {measured.CopyMilliseconds:F2} ms; skipped {measured.SkippedFrames}; busy {measured.BusyAttempts}; render tier {RenderCapability.Tier >> 16} (runner desktop, no physical display measurement)"));
            if (offered < 0 || !measured.Measured || measured.FramesPerSecond <= 0) result = 1;
            application.Shutdown();
        }
        try
        {
            // Exercise the actual visitor renderer, including odd widths and playlist size changes.
            foreach (var size in new[] { (3, 2), (1920, 1080), (96, 64) })
            for (var index = 0; index < 7; index++)
            {
                using var frame = Frame(index, size.Item1, size.Item2);
                if (!window.TryPresent(frame)) throw new InvalidOperationException("An unattached bitmap was unexpectedly busy.");
                var bitmap = (WriteableBitmap)((Image)((Grid)window.Content).Children[0]).Source;
                var pixels = new byte[frame.Length];
                bitmap.CopyPixels(pixels, size.Item1 * 4, 0);
                if (!pixels.AsSpan().SequenceEqual(frame.Pixels.AsSpan(0, frame.Length)))
                    throw new InvalidOperationException("BGRA pixels changed during presentation or a size change.");
            }
            Console.WriteLine("PASS: actual WPF renderer preserves BGRA pixels across size changes.");
            CompositionTarget.Rendering += rendering;
            timeout.Tick += (_, _) => Finish(); timeout.Start();
            window.Show(); application.Run();
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); result = 1; }
        finally
        {
            timeout.Stop(); CompositionTarget.Rendering -= rendering;
            window.Close(); application.Shutdown();
        }
        return result;
    }

    private static VideoFrame Frame(long sequence, int width, int height)
    {
        var length = width * height * 4;
        var pixels = ArrayPool<byte>.Shared.Rent(length);
        pixels.AsSpan(0, length).Fill(255);
        // Include distinct channel values and opaque alpha to catch stride or pixel-format errors.
        pixels[0] = (byte)sequence; pixels[1] = 20; pixels[2] = 30;
        pixels[length - 4] = 40; pixels[length - 3] = 50; pixels[length - 2] = 60;
        return new(sequence, pixels, length, width, height);
    }
}
