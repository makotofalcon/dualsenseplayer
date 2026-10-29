using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using DualSensePlayer.Core;

namespace DualSensePlayer.App;

public partial class MainWindow
{
    private readonly ObservableCollection<PlaylistEntry> _entries = new();
    private PlaylistEntry? _editing;

    private void InitializePlaylist()
    {
        PlaylistList.ItemsSource = _entries;
        foreach (var settings in _settings.Playlist) _entries.Add(new(settings));
        ReindexPlaylist();
        PlaylistList.SelectedIndex = _entries.Count > 0 ? 0 : -1;
        _editing = PlaylistList.SelectedItem as PlaylistEntry;
        ShowEntryEditor();
    }

    private void SaveEntryEditor()
    {
        if (_editing?.Info is null) return;
        _editing.Settings = _editing.Settings with
        {
            AudioStreamIndex = (TrackCombo.SelectedItem as AudioTrack)?.Index ?? -1,
            ChannelMap = new[] { Map1, Map2, Map3, Map4 }.Select(combo => combo.SelectedIndex).ToArray(),
            MappingConfirmed = MappingConfirmed.IsChecked == true
        };
    }

    private void ShowEntryEditor()
    {
        var wasUpdating = _updating; _updating = true;
        try
        {
            var settings = _editing?.Settings ?? new MediaSettings();
            var info = _editing?.Info;
            MediaPath.Text = _editing is null ? "一覧から動画を選択してください" : settings.Path;
            MediaDescription.Text = info?.Summary ?? "";
            TrackCombo.ItemsSource = info?.AudioTracks.Where(track => track.Supported).ToArray();
            TrackCombo.SelectedItem = info?.AudioTracks.FirstOrDefault(track => track.Supported && track.Index == settings.AudioStreamIndex);
            var maps = new[] { Map1, Map2, Map3, Map4 };
            for (var i = 0; i < 4; i++) maps[i].SelectedIndex = settings.ChannelMap[i];
            MappingConfirmed.IsChecked = settings.MappingConfirmed;
            ValidationText.Text = string.Join(Environment.NewLine, _editing?.Warnings ?? []);
            DescribeTrack();
        }
        finally { _updating = wasUpdating; }
    }

    private void ReindexPlaylist()
    {
        _seekPending = false;
        for (var i = 0; i < _entries.Count; i++) _entries[i].Number = i + 1;
        var seconds = _entries.Sum(entry => entry.Info?.Duration ?? 0);
        var uninspected = _entries.Count(entry => entry.Info is null);
        PlaylistSummary.Text = $"{_entries.Count}本 · 合計 {TimeSpan.FromSeconds(seconds):hh\\:mm\\:ss}" +
            (uninspected > 0 ? $"（未検査{uninspected}本）" : "") + " · 動画ごとの4ch設定は一覧で選択して編集します。";
    }

    private void OnPlaylistSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        SaveEntryEditor();
        _editing = PlaylistList.SelectedItem as PlaylistEntry;
        ShowEntryEditor(); RefreshState();
    }

    private static string MediaKey(PlayerSettings settings) => JsonSerializer.Serialize(settings.Playlist.Select(item =>
        new { item.Path, item.AudioStreamIndex, item.ChannelMap }));

    private static void ResolveTrack(PlaylistEntry entry)
    {
        var tracks = entry.Info!.AudioTracks.Where(track => track.Supported).ToArray();
        if (!tracks.Any(track => track.Index == entry.Settings.AudioStreamIndex))
            entry.Settings = entry.Settings with { AudioStreamIndex = tracks.Length == 1 ? tracks[0].Index : -1, MappingConfirmed = false };
    }

    private async Task InspectEntriesAsync()
    {
        SaveEntryEditor();
        foreach (var entry in _entries)
        {
            Status.Text = $"{entry.Number} / {_entries.Count} 本目を検査しています: {Path.GetFileName(entry.Settings.Path)}";
            try { entry.Info = await _tools.InspectAsync(entry.Settings.Path, _operationStop?.Token ?? CancellationToken.None); }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            { throw new InvalidDataException($"{entry.Number}本目 {Path.GetFileName(entry.Settings.Path)}: {ex.Message}", ex); }
            _operationStop?.Token.ThrowIfCancellationRequested();
            ResolveTrack(entry);
        }
        _preparedKey = ""; ReindexPlaylist(); ShowEntryEditor();
    }

    private async Task AddFilesAsync(IEnumerable<string> input)
    {
        if (_engine.Clock.Snapshot.State != PlaybackState.Stopped) throw new InvalidOperationException("動画の追加は停止中に行ってください。");
        SaveEntryEditor();
        var paths = input.SelectMany(path => Directory.Exists(path)
            ? Directory.EnumerateFiles(path).Where(IsVideoPath).Order(StringComparer.OrdinalIgnoreCase)
            : new[] { path }.AsEnumerable()).ToArray();
        if (paths.Length == 0) throw new InvalidDataException("追加できるMOV / MP4動画がありません。フォルダーの直下を確認してください。");
        if (_entries.Count + paths.Length > PlaylistTimeline.MaximumItems)
            throw new InvalidDataException($"プレイリストは{PlaylistTimeline.MaximumItems}本以内にしてください。");
        var added = new List<PlaylistEntry>();
        foreach (var path in paths)
        {
            if (!IsVideoPath(path)) throw new InvalidDataException("MOV / MP4動画、または動画を含むフォルダーを指定してください。");
            Status.Text = $"追加する動画を検査しています: {Path.GetFileName(path)}";
            MediaInfo info;
            try { info = await _tools.InspectAsync(path, _operationStop?.Token ?? CancellationToken.None); }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            { throw new InvalidDataException($"{Path.GetFileName(path)}: {ex.Message} この追加操作は中止しました。", ex); }
            var entry = new PlaylistEntry(new() { Path = info.Path }) { Info = info };
            ResolveTrack(entry); added.Add(entry);
        }
        _operationStop?.Token.ThrowIfCancellationRequested();
        _updating = true;
        try
        {
            foreach (var entry in added) _entries.Add(entry);
            PlaylistList.SelectedItem = added[0]; _editing = added[0];
        }
        finally { _updating = false; }
        _preparedKey = ""; ReindexPlaylist(); ShowEntryEditor();
    }

    private static bool IsVideoPath(string path) => Path.GetExtension(path).Equals(".mov", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase);

    private async Task PrepareAsync()
    {
        if (_entries.Count == 0) throw new InvalidOperationException("プレイリストに動画を追加してください。");
        if (_entries.Any(entry => entry.Info is null)) await InspectEntriesAsync();
        var updated = ReadControls(true);
        for (var i = 0; i < updated.Playlist.Length; i++)
        {
            if (_entries[i].Info!.AudioTracks.Any(track => track.Supported && track.Index == updated.Playlist[i].AudioStreamIndex)) continue;
            PlaylistList.SelectedIndex = i;
            throw new InvalidOperationException($"{i + 1}本目の4ch音声トラックを選択してください。");
        }
        var infos = _entries.Select(entry => entry.Info!).ToArray();
        PlaylistTimeline.CheckMemoryBudget(infos);
        Status.Text = $"全{_entries.Count}本の4ch PCMを先読みしています…";
        await _engine.LoadPlaylistAsync(infos, updated, _operationStop?.Token ?? CancellationToken.None);
        _operationStop?.Token.ThrowIfCancellationRequested();
        var prepared = _engine.Playlist!;
        var settings = new MediaSettings[prepared.Items.Count];
        var changed = new List<string>();
        for (var i = 0; i < prepared.Items.Count; i++)
        {
            var item = prepared.Items[i]; var previous = updated.Playlist[i];
            var hashChanged = previous.Sha256.Length > 0 && previous.Sha256 != item.Hash;
            if (hashChanged) changed.Add(Path.GetFileName(item.Info.Path));
            settings[i] = previous with { Sha256 = item.Hash, MappingConfirmed = previous.MappingConfirmed && !hashChanged };
            _entries[i].Settings = settings[i]; _entries[i].Warnings = item.Warnings;
        }
        updated = updated with { Playlist = settings };
        Persist(updated); _preparedKey = MediaKey(updated);
        PositionSlider.Maximum = prepared.Timeline.Duration;
        ShowEntryEditor();
        if (changed.Count > 0) Warning.Text = "内容が変わった動画のch対応を再確認してください: " + string.Join("、", changed);
    }

    private async Task EnsurePlaylistPreparedAsync()
    {
        var updated = ReadControls(true);
        if (_engine.Playlist is null || MediaKey(updated) != _preparedKey) await PrepareAsync();
        else Persist(updated);
    }

    private async void OnAddFolder(object sender, RoutedEventArgs e) => await RunUiAsync(async () =>
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "追加する動画のフォルダーを選択" };
        if (dialog.ShowDialog(this) == true) await AddFilesAsync([dialog.FolderName]);
    });

    private void OnPlaylistDragOver(object sender, DragEventArgs e)
    {
        e.Effects = !_busy && _engine.Clock.Snapshot.State == PlaybackState.Stopped && e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnPlaylistDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) await RunUiAsync(() => AddFilesAsync(paths));
    }

    private void MoveItem(int direction)
    {
        if (_busy || _engine.Clock.Snapshot.State != PlaybackState.Stopped) return;
        var index = PlaylistList.SelectedIndex;
        if (index < 0 || index + direction < 0 || index + direction >= _entries.Count) return;
        SaveEntryEditor(); _updating = true;
        try { _entries.Move(index, index + direction); PlaylistList.SelectedIndex = index + direction; }
        finally { _updating = false; }
        _preparedKey = ""; ReindexPlaylist(); RefreshState();
    }
    private void OnMoveUp(object sender, RoutedEventArgs e) => MoveItem(-1);
    private void OnMoveDown(object sender, RoutedEventArgs e) => MoveItem(1);
    private void OnRemoveItem(object sender, RoutedEventArgs e)
    {
        if (_busy || _engine.Clock.Snapshot.State != PlaybackState.Stopped) return;
        var index = PlaylistList.SelectedIndex;
        if (index < 0) return;
        SaveEntryEditor(); _updating = true;
        try
        {
            _entries.RemoveAt(index);
            PlaylistList.SelectedIndex = _entries.Count == 0 ? -1 : Math.Min(index, _entries.Count - 1);
            _editing = PlaylistList.SelectedItem as PlaylistEntry;
        }
        finally { _updating = false; }
        _preparedKey = ""; ReindexPlaylist(); ShowEntryEditor(); RefreshState();
    }

    private async void OnPlaySelected(object sender, RoutedEventArgs e) => await RunUiAsync(async () =>
    {
        var index = PlaylistList.SelectedIndex;
        if (index < 0) return;
        await EnsurePlaylistPreparedAsync();
        await _engine.SeekAsync(_engine.Playlist!.Timeline.StartOf(index), false, _operationStop?.Token ?? CancellationToken.None);
        await PlayAsync();
    });

    private sealed class PlaylistEntry(MediaSettings settings) : INotifyPropertyChanged
    {
        private MediaSettings _settings = settings;
        private MediaInfo? _info;
        private int _number;
        public MediaSettings Settings { get => _settings; set { _settings = value; Changed(); } }
        public MediaInfo? Info { get => _info; set { _info = value; Changed(); } }
        public int Number { get => _number; set { _number = value; Changed(); } }
        public string[] Warnings { get; set; } = [];
        public string Label => $"{Number:00}.  {Path.GetFileName(Settings.Path)}  ·  {(Info is null ? "未検査" : TimeSpan.FromSeconds(Info.Duration).ToString(@"mm\:ss"))}" +
            (Settings.AudioStreamIndex < 0 ? "  · トラック選択待ち" : Settings.MappingConfirmed ? "  · 自動再生の確認済み" : "  · 手動再生可／自動再生の確認待ち");
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed() => PropertyChanged?.Invoke(this, new(nameof(Label)));
    }
}
