using System.Text.Json;
using System.Text.Json.Serialization;

namespace DualSensePlayer.Core;

public sealed record MediaSettings
{
    public string Path { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public int AudioStreamIndex { get; init; } = -1;
    // Logical channels (audio L/R, haptics L/R) -> zero-based decoded channels.
    public int[] ChannelMap { get; init; } = [0, 1, 2, 3];
    public bool MappingConfirmed { get; init; }

    public void Validate(bool requirePath = false)
    {
        if (Path is null || Sha256 is null || AudioStreamIndex < -1 || requirePath && string.IsNullOrWhiteSpace(Path))
            throw new InvalidDataException("素材の識別情報が正しくありません。");
        if (ChannelMap is not { Length: 4 } || !ChannelMap.Order().SequenceEqual(new[] { 0, 1, 2, 3 }))
            throw new InvalidDataException("ch対応は1〜4を重複なく指定してください。");
    }
}

public sealed record OutputSettings
{
    public string EndpointId { get; init; } = "";
    public string ContainerId { get; init; } = "";
    public string Serial { get; init; } = "";
    public float Gain { get; init; } = 0.25f;
    public bool Muted { get; init; }
    public int OffsetMs { get; init; }
    public bool Exclusive { get; init; }
}

public sealed record MachineSettings
{
    public string ComputerName { get; init; } = Environment.MachineName;
    public string MonitorId { get; init; } = "";
    public OutputSettings Audio { get; init; } = new() { Gain = 1 };
    public OutputSettings[] Controllers { get; init; } = [new(), new()];
}

public sealed record PlayerSettings
{
    public int SchemaVersion { get; init; } = 2;
    // Kept only to import settings written by the original single-video release.
    public MediaSettings Media { get; init; } = new();
    public MediaSettings[] Playlist { get; init; } = [];
    public MachineSettings Machine { get; init; } = new();
    public bool AutoPlay { get; init; }
    public bool StartAtLogin { get; init; }
    public bool HapticsStopped { get; init; }
    public bool ExhibitionStopped { get; init; } = true;
    public float HapticLimit { get; init; } = 0.8f;
    public int ReconnectFadeMs { get; init; } = 50;

    // An asynchronous media load may finish with a settings snapshot taken before a stop.
    // Only explicit transport/staff actions may replace the latest stop intentions.
    public PlayerSettings PreserveStopIntent(PlayerSettings current, bool? hapticsStop = null, bool? exhibitionStop = null) =>
        this with { HapticsStopped = hapticsStop ?? current.HapticsStopped, ExhibitionStopped = exhibitionStop ?? current.ExhibitionStopped };

    public PlayerSettings UpgradePlaylist() => this with
    {
        SchemaVersion = 2,
        Playlist = Playlist.Length == 0 && Media.Path.Length > 0 ? [Media] : Playlist,
        Media = new()
    };

    public void Validate()
    {
        if (SchemaVersion is not (1 or 2) || Media is null || Machine is null || Machine.Audio is null || Playlist is null)
            throw new InvalidDataException("設定の形式が正しくありません。");
        if (Machine.ComputerName is null || Machine.MonitorId is null)
            throw new InvalidDataException("設定の識別情報が正しくありません。");
        Media.Validate();
        if (Playlist.Length > PlaylistTimeline.MaximumItems || Playlist.Any(item => item is null))
            throw new InvalidDataException($"プレイリストは{PlaylistTimeline.MaximumItems}本以内にしてください。");
        foreach (var item in Playlist) item.Validate(requirePath: true);
        if (Machine.Controllers is not { Length: 2 } || Machine.Controllers.Any(c => c is null))
            throw new InvalidDataException("コントローラー設定は2台分必要です。");
        foreach (var output in Machine.Controllers.Append(Machine.Audio))
            if (output.EndpointId is null || output.ContainerId is null || output.Serial is null ||
                !float.IsFinite(output.Gain) || output.Gain is < 0 or > 1 || output.OffsetMs is < -500 or > 500)
                throw new InvalidDataException("強度・音量または時刻補正が範囲外です。");
        if (!float.IsFinite(HapticLimit) || HapticLimit is <= 0 or > 1 || ReconnectFadeMs is < 0 or > 1000)
            throw new InvalidDataException("出力上限または復帰時間が範囲外です。");
        var controllers = Machine.Controllers.Where(c => c.EndpointId.Length > 0).ToArray();
        if (controllers.Select(c => c.EndpointId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != controllers.Length ||
            controllers.Where(c => c.ContainerId.Length > 0).GroupBy(c => c.ContainerId, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            throw new InvalidDataException("同じコントローラーを2枠に登録できません。");
        if (controllers.Any(c => c.EndpointId == Machine.Audio.EndpointId))
            throw new InvalidDataException("通常音声と振動に同じ出力先を指定できません。");
    }
}

public sealed class SettingsStore(string directory)
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    public string DirectoryPath { get; } = directory;
    public string SettingsPath => Path.Combine(DirectoryPath, "settings.json");
    private string StopPath => Path.Combine(DirectoryPath, "haptics-stopped");
    private string ExhibitionStopPath => Path.Combine(DirectoryPath, "exhibition-stopped");
    public string? Warning { get; private set; }

    public PlayerSettings Load()
    {
        Warning = null;
        PlayerSettings? settings = null;
        foreach (var path in new[] { SettingsPath, SettingsPath + ".bak" })
        {
            if (!File.Exists(path)) continue;
            try
            {
                settings = JsonSerializer.Deserialize<PlayerSettings>(File.ReadAllText(path), JsonOptions)
                    ?? throw new InvalidDataException("空の設定です。");
                settings.Validate();
                settings = settings.UpgradePlaylist();
                if (path.EndsWith(".bak", StringComparison.Ordinal)) Warning = "設定が破損していたため前回正常版を読み込みました。";
                break;
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or ArgumentException)
            {
                settings = null;
                Warning = "設定を復元できません。機器と素材を再設定してください。";
            }
        }
        settings ??= new();
        if (settings.Machine.ComputerName != Environment.MachineName)
        {
            settings = settings with { Machine = new(), ExhibitionStopped = true, AutoPlay = false };
            Warning = "別のPCの設定です。表示先と音声・コントローラーを再登録してください。";
        }
        // A separate durable latch survives recovery of an older settings backup.
        return settings with { HapticsStopped = settings.HapticsStopped || File.Exists(StopPath),
            ExhibitionStopped = settings.ExhibitionStopped || File.Exists(ExhibitionStopPath) };
    }

    public void Save(PlayerSettings settings)
    {
        settings.Validate();
        settings = settings.UpgradePlaylist();
        Directory.CreateDirectory(DirectoryPath);
        if (settings.HapticsStopped) AtomicWrite(StopPath, "stopped\n");
        if (settings.ExhibitionStopped) AtomicWrite(ExhibitionStopPath, "stopped\n");
        if (File.Exists(SettingsPath))
        {
            try
            {
                var previous = JsonSerializer.Deserialize<PlayerSettings>(File.ReadAllText(SettingsPath), JsonOptions);
                previous?.Validate();
                if (previous is not null) AtomicWrite(SettingsPath + ".bak", JsonSerializer.Serialize(previous, JsonOptions));
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException) { /* Do not back up corrupt input. */ }
        }
        AtomicWrite(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
        if (!settings.HapticsStopped && File.Exists(StopPath)) File.Delete(StopPath);
        if (!settings.ExhibitionStopped && File.Exists(ExhibitionStopPath)) File.Delete(ExhibitionStopPath);
    }

    public static void AtomicWrite(string path, string contents)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                using var writer = new StreamWriter(stream, leaveOpen: true);
                writer.Write(contents);
                writer.Flush();
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
