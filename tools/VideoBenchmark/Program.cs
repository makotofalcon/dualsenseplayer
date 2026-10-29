using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using DualSensePlayer.Core;

var tools = FfmpegTools.Discover();
var sourceFps = args.Length == 0 ? 30 : int.Parse(args[0], CultureInfo.InvariantCulture);
if (sourceFps is not (30 or 60)) throw new ArgumentException("Choose a 30 or 60 fps benchmark.");
var directory = Path.Combine(Path.GetTempPath(), "dualsense-video-benchmark-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var path = Path.Combine(directory, $"1080p{sourceFps}.mov");
    await FfmpegTools.CaptureAsync(tools.Ffmpeg,
        ["-v", "error", "-nostdin", "-f", "lavfi", "-i", $"testsrc2=size=1920x1080:rate={sourceFps}:duration=2",
            "-f", "lavfi", "-i", "anullsrc=r=48000:cl=quad", "-t", "2", "-map", "0:v:0", "-map", "1:a:0",
            "-c:v", "libx264", "-preset", "fast", "-crf", "20", "-threads", "4", "-pix_fmt", "yuv420p",
            "-c:a", "pcm_s16le", path]);
    var info = await tools.InspectAsync(path);
    await using var decoder = new PlaylistVideoDecoder(tools, new PlaylistTimeline([info]), 0);
    await decoder.Ready.WaitAsync(TimeSpan.FromSeconds(30));
    const int count = 240;
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
    var elapsed = Stopwatch.StartNew();
    for (var index = 0; index < count;)
    {
        if (decoder.Error is not null) throw decoder.Error;
        if (decoder.TryRead(out var frame))
        {
            using (frame!)
            {
                if (frame!.Sequence != index || frame.Length != 1920 * 1080 * 4)
                    throw new InvalidDataException("The decoder dropped or resized a benchmark frame.");
            }
            index++;
        }
        else await Task.Delay(1, timeout.Token);
    }
    elapsed.Stop();
    var fps = count / elapsed.Elapsed.TotalSeconds;
    var result = new { width = 1920, height = 1080, sourceFps, frames = count,
        decodedFps = Math.Round(fps, 2), elapsedSeconds = Math.Round(elapsed.Elapsed.TotalSeconds, 3),
        processors = Environment.ProcessorCount, os = Environment.OSVersion.ToString() };
    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
        Console.WriteLine($"::notice title=1080p{sourceFps} decoder throughput::{fps.ToString("0.00", CultureInfo.InvariantCulture)} frames/s (no display or controller)");
    var summary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
    if (!string.IsNullOrEmpty(summary))
        await File.AppendAllTextAsync(summary, "\n### Video decoder throughput (no display or controller)\n\n" +
            "| Input | Frames | Decoded frames/s | Seconds |\n|---|---:|---:|---:|\n" +
            $"| 1920x1080 / {sourceFps}fps | {count} | {fps.ToString("0.00", CultureInfo.InvariantCulture)} | {elapsed.Elapsed.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture)} |\n");
}
finally { Directory.Delete(directory, true); }
