using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using DualSensePlayer.Core;
using Xunit;

namespace DualSensePlayer.Tests;

// The Windows console code page is process-wide. Do not change it during other tests.
[CollectionDefinition("Japanese console", DisableParallelization = true)]
public sealed class JapaneseConsoleCollection { }

[Collection("Japanese console")]
public sealed class FfmpegEncodingTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "dualsense-encoding-" + Guid.NewGuid(), "デスクトップ");
    private readonly FfmpegTools _tools = FfmpegTools.Discover();
    private const string Title = "C:\\デスクトップ\\A \"日本語\"\\素材";
    private string _path = "";

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "A 日本語の映像.mov");
        await FfmpegTools.CaptureAsync("ffmpeg",
            ["-v", "error", "-nostdin", "-y", "-f", "lavfi", "-i", "testsrc2=size=96x64:rate=30:duration=0.4",
                "-f", "lavfi", "-i", "aevalsrc=0.1|0.2|0.3|-0.4:s=48000:d=0.4:c=quad",
                "-map", "0:v:0", "-map", "1:a:0", "-c:v", "libx264", "-threads", "2", "-pix_fmt", "yuv420p",
                "-c:a", "pcm_s16le", "-metadata", "title=" + Title, _path]);
    }

    [Fact]
    public async Task JapanesePathsAndMetadataSurviveProbeAndPreload()
    {
        using var console = new JapaneseConsole();
        using var json = JsonDocument.Parse(await FfmpegTools.CaptureAsync(_tools.Ffprobe,
            ["-v", "error", "-show_format", "-of", "json", _path]));
        var format = json.RootElement.GetProperty("format");
        Assert.Equal(_path, format.GetProperty("filename").GetString());
        Assert.Equal(Title, format.GetProperty("tags").GetProperty("title").GetString());

        var info = await _tools.InspectAsync(_path);
        Assert.Equal(_path, info.Path);
        Assert.Equal(12, info.FrameCount);
        var prepared = await _tools.PrepareAsync(info, info.AudioTracks.Single().Index, [0, 1, 2, 3]);
        Assert.Equal(.3, prepared.Pcm.Read(.1, 2), 3);
        Assert.Equal(-.4, prepared.Pcm.Read(.1, 3), 3);
    }

    [Fact]
    public async Task JapanesePathsInDecoderErrorsRemainReadable()
    {
        using var console = new JapaneseConsole();
        var missing = Path.Combine(_directory, "存在しない動画.mov");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => FfmpegTools.CaptureAsync(_tools.Ffprobe,
            ["-v", "error", "-show_format", "-of", "json", missing]));
        Assert.Contains(missing, error.Message);
    }

    public Task DisposeAsync()
    {
        Directory.Delete(Path.GetDirectoryName(_directory)!, true);
        return Task.CompletedTask;
    }

    private sealed class JapaneseConsole : IDisposable
    {
        private readonly bool _allocated;
        private readonly uint _previous;

        public JapaneseConsole()
        {
            if (!OperatingSystem.IsWindows()) return;
            if (GetConsoleOutputCP() == 0)
            {
                if (!AllocConsole()) throw new Win32Exception(Marshal.GetLastWin32Error());
                _allocated = true;
            }
            _previous = GetConsoleOutputCP();
            if (!SetConsoleOutputCP(932))
            {
                var error = Marshal.GetLastWin32Error();
                if (_allocated) FreeConsole();
                throw new Win32Exception(error);
            }
        }

        public void Dispose()
        {
            if (!OperatingSystem.IsWindows()) return;
            SetConsoleOutputCP(_previous);
            if (_allocated) FreeConsole();
        }

        [DllImport("kernel32.dll")] private static extern uint GetConsoleOutputCP();
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetConsoleOutputCP(uint codePage);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AllocConsole();
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FreeConsole();
    }
}
