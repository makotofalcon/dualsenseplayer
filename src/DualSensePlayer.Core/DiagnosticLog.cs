using System.IO.Compression;
using System.Text.Json;
using System.Threading.Channels;

namespace DualSensePlayer.Core;

public sealed class DiagnosticLog : IAsyncDisposable
{
    private readonly string _directory;
    private readonly Channel<object> _queue = Channel.CreateBounded<object>(new BoundedChannelOptions(2048)
        { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly Task _writer;
    public DiagnosticLog(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        _writer = Task.Run(async () =>
        {
            await foreach (var entry in _queue.Reader.ReadAllAsync())
            {
                try
                {
                    Trim();
                    var path = Path.Combine(_directory, $"{DateTime.UtcNow:yyyyMMdd}-{DateTime.UtcNow.Hour / 4}-{Environment.ProcessId}.jsonl");
                    await File.AppendAllTextAsync(path, JsonSerializer.Serialize(entry) + Environment.NewLine);
                }
                catch (IOException) { /* Diagnostics must not block or crash the playback thread. */ }
                catch (UnauthorizedAccessException) { }
            }
        });
    }
    public void Write(string kind, object? details = null) => _queue.Writer.TryWrite(new { utc = DateTimeOffset.UtcNow, kind, details });
    private void Trim()
    {
        var files = new DirectoryInfo(_directory).GetFiles("*.jsonl").OrderBy(f => f.LastWriteTimeUtc).ToList();
        var size = files.Sum(f => f.Length);
        foreach (var file in files)
        {
            if (file.LastWriteTimeUtc >= DateTime.UtcNow.AddDays(-30) && size < 200L * 1024 * 1024) break;
            size -= file.Length;
            file.Delete();
        }
    }
    public void Export(string destination, PlayerSettings settings, string status)
    {
        using var archive = ZipFile.Open(destination, ZipArchiveMode.Create);
        foreach (var path in Directory.GetFiles(_directory, "*.jsonl")) archive.CreateEntryFromFile(path, "logs/" + Path.GetFileName(path));
        using (var writer = new StreamWriter(archive.CreateEntry("settings.json").Open())) writer.Write(JsonSerializer.Serialize(settings, SettingsStore.JsonOptions));
        using (var writer = new StreamWriter(archive.CreateEntry("status.txt").Open())) writer.Write(status);
    }
    public async ValueTask DisposeAsync() { _queue.Writer.TryComplete(); await _writer; }
}
