using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DualSensePlayer.Core;
using DualSensePlayer.Windows;
using Microsoft.Win32;

namespace DualSensePlayer.App;

public partial class MainWindow : Window
{
    private readonly SettingsStore _store;
    private readonly DiagnosticLog _log;
    private readonly FfmpegTools _tools = FfmpegTools.Discover();
    private readonly PlayerEngine _engine;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly double _started = PlaybackClock.QpcSeconds;
    private readonly string _heartbeatPath;
    private PlayerSettings _settings;
    private VisitorWindow? _visitor;
    private bool _updating = true, _busy, _exiting, _allowClose, _recovering, _resumeAfterDisplay, _resumeAfterSleep;
    private long _uiTick;
    private double _nextDisplayCheck, _nextRecovery, _nextSettingsSave;
    private string _preparedKey = "";
    private string _fault = "";
    private bool _seekPending;
    private CancellationTokenSource? _operationStop;

    public MainWindow(string[] args)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DualSenseExhibitionPlayer");
        Directory.CreateDirectory(directory);
        _store = new(directory); _settings = _store.Load();
        _log = new(Path.Combine(directory, "logs")); _engine = new(_tools, _log);
        var heartbeatArg = Array.IndexOf(args, "--heartbeat");
        _heartbeatPath = heartbeatArg >= 0 && heartbeatArg + 1 < args.Length ? args[heartbeatArg + 1] : Path.Combine(directory, "heartbeat.json");
        InitializeComponent();
        foreach (var combo in new[] { Map1, Map2, Map3, Map4 }) combo.ItemsSource = new[] { "1", "2", "3", "4" };
        RestoreControls();
        Warning.Text = _store.Warning ?? "";
        OperationStatus.Text = args.Contains("--supervised") ? "監視プロセスから起動しました。" : "直接起動中。無人展示では DualSensePlayer.Supervisor.exe から起動してください。";
        _engine.UpdateSettings(_settings);
        _updating = false;
        _timer.Tick += OnTick; _timer.Start();
        CompositionTarget.Rendering += OnRendering;
        SystemEvents.DisplaySettingsChanged += OnDisplaysChanged;
        SystemEvents.PowerModeChanged += OnPowerChanged;
        Loaded += async (_, _) => await RunUiAsync(async () =>
        {
            await RefreshDevicesAsync();
            if (_settings.Playlist.Length > 0)
            {
                await InspectEntriesAsync();
                await PrepareAsync();
                if (_settings.AutoPlay && !_settings.ExhibitionStopped && _settings.Playlist.All(item => item.MappingConfirmed)) await PlayAsync();
                else if (_settings.AutoPlay && _settings.Playlist.Any(item => !item.MappingConfirmed)) Warning.Text = "自動再生には全動画のch対応確認が必要です。各動画を手動で再生して確認してください。";
            }
        });
        _log.Write("startup", new { version = typeof(MainWindow).Assembly.GetName().Version?.ToString(), os = Environment.OSVersion.ToString(), supervised = args.Contains("--supervised") });
    }

    private void RestoreControls()
    {
        InitializePlaylist();
        Volume.Value = _settings.Machine.Audio.Gain * 100; AudioMute.IsChecked = _settings.Machine.Audio.Muted;
        AudioOffset.Text = _settings.Machine.Audio.OffsetMs.ToString(); AudioExclusive.IsChecked = _settings.Machine.Audio.Exclusive;
        Gain1.Value = _settings.Machine.Controllers[0].Gain * 100; Gain2.Value = _settings.Machine.Controllers[1].Gain * 100;
        Mute1.IsChecked = _settings.Machine.Controllers[0].Muted; Mute2.IsChecked = _settings.Machine.Controllers[1].Muted;
        Offset1.Text = _settings.Machine.Controllers[0].OffsetMs.ToString(); Offset2.Text = _settings.Machine.Controllers[1].OffsetMs.ToString();
        Exclusive1.IsChecked = _settings.Machine.Controllers[0].Exclusive; Exclusive2.IsChecked = _settings.Machine.Controllers[1].Exclusive;
        AutoPlay.IsChecked = _settings.AutoPlay; LoginStartup.IsChecked = _settings.StartAtLogin;
    }

    private async Task RunUiAsync(Func<Task> action)
    {
        if (_busy || _exiting) return;
        _busy = true; RefreshState();
        using var operation = new CancellationTokenSource();
        _operationStop = operation;
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Warning.Text = ex.Message; _log.Write("operation-error", ex.ToString()); ShowManagement(); }
        finally { _operationStop = null; _busy = false; RefreshState(); }
    }

    private async Task RefreshDevicesAsync()
    {
        var devices = await Task.Run(DeviceCatalog.Enumerate);
        _updating = true;
        try
        {
            var none = new AudioDevice("", "未使用", "", 0, 0, null);
            AudioCombo.ItemsSource = new[] { none }.Concat(devices.Where(d => d.Hid is null)).ToArray();
            RestoreDeviceSelection(AudioCombo, _settings.Machine.Audio);
            foreach (var pair in new[] { (Controller1, 0), (Controller2, 1) })
            {
                pair.Item1.ItemsSource = new[] { none }.Concat(devices.Where(d => d.Hid is not null)).ToArray();
                RestoreDeviceSelection(pair.Item1, _settings.Machine.Controllers[pair.Item2]);
            }
            var monitors = DisplaySupport.Enumerate();
            MonitorCombo.ItemsSource = monitors;
            MonitorCombo.SelectedItem = monitors.FirstOrDefault(m => m.Id == _settings.Machine.MonitorId);
            if (_settings.Machine.MonitorId.Length == 0 && monitors.Length > 0) MonitorCombo.SelectedIndex = monitors.Length > 1 ? 1 : 0;
            OperationStatus.Text = $"通常音声 {devices.Count(d => d.Hid is null)}出力、USB DualSense {devices.Count(d => d.Hid is not null)}台、ディスプレイ {monitors.Length}台。登録されていない機器は自動利用しません。";
        }
        finally { _updating = false; }
    }

    private static void RestoreDeviceSelection(ComboBox combo, OutputSettings settings)
    {
        var values = ((IEnumerable<AudioDevice>)combo.ItemsSource).ToList();
        var selected = values.FirstOrDefault(d => d.Id == settings.EndpointId);
        if (selected is null && settings.EndpointId.Length > 0)
        {
            selected = new(settings.EndpointId, "登録機器（現在未接続）", settings.ContainerId, 0, 0, null);
            values.Add(selected); combo.ItemsSource = values;
        }
        combo.SelectedItem = selected ?? values[0];
    }

    private PlayerSettings ReadControls(bool configuration)
    {
        OutputSettings Output(OutputSettings previous, ComboBox combo, Slider gain, CheckBox mute, TextBox offset, CheckBox exclusive)
        {
            var next = previous with { Gain = (float)(gain.Value / 100), Muted = mute.IsChecked == true };
            if (!configuration) return next;
            if (!int.TryParse(offset.Text, out var ms) || ms is < -500 or > 500) throw new InvalidDataException("補正値は−500〜＋500msの整数で入力してください。");
            var device = combo.SelectedItem as AudioDevice;
            return next with { EndpointId = device?.Id ?? previous.EndpointId,
                ContainerId = device?.ContainerId ?? previous.ContainerId,
                Serial = device?.Hid?.Serial ?? (device?.Id == previous.EndpointId ? previous.Serial : ""),
                OffsetMs = ms, Exclusive = exclusive.IsChecked == true };
        }
        var updated = _settings with
        {
            Machine = _settings.Machine with
            {
                MonitorId = configuration ? (MonitorCombo.SelectedItem as DisplayTarget)?.Id ?? _settings.Machine.MonitorId : _settings.Machine.MonitorId,
                Audio = Output(_settings.Machine.Audio, AudioCombo, Volume, AudioMute, AudioOffset, AudioExclusive),
                Controllers = [Output(_settings.Machine.Controllers[0], Controller1, Gain1, Mute1, Offset1, Exclusive1),
                    Output(_settings.Machine.Controllers[1], Controller2, Gain2, Mute2, Offset2, Exclusive2)]
            }
        };
        if (configuration)
        {
            SaveEntryEditor();
            updated = updated with { Playlist = _entries.Select(entry => entry.Settings).ToArray(), Media = new(),
                AutoPlay = AutoPlay.IsChecked == true, StartAtLogin = LoginStartup.IsChecked == true };
        }
        updated.Validate();
        return updated;
    }

    private void Persist(PlayerSettings updated, bool? hapticsStop = null, bool? exhibitionStop = null)
    {
        updated = updated.PreserveStopIntent(_settings, hapticsStop, exhibitionStop);
        // Releasing a durable stop must succeed on disk before it can enable live output.
        if (_settings.HapticsStopped && !updated.HapticsStopped)
        {
            _store.Save(updated); _settings = updated; _engine.UpdateSettings(updated); return;
        }
        _settings = updated; _engine.UpdateSettings(updated);
        _store.Save(updated);
    }

    private async Task PlayAsync()
    {
        if (_engine.Clock.Snapshot.State == PlaybackState.Playing) return;
        if (_engine.Clock.Snapshot.State == PlaybackState.Stopped)
        {
            await EnsurePlaylistPreparedAsync();
        }
        var display = DisplaySupport.Enumerate().FirstOrDefault(d => d.Id == _settings.Machine.MonitorId)
            ?? throw new InvalidOperationException("登録したディスプレイが見つかりません。停止中に表示先を選択してください。");
        if (_engine.CurrentMedia is null) throw new InvalidOperationException("プレイリストを先読みしてください。");
        if (_visitor is null)
        {
            _visitor = new(HandleShortcut, () => _ = RequestQuitAsync());
            _visitor.Show();
        }
        else _visitor.Show();
        _visitor.Configure(_engine.CurrentMedia.Info, _engine.Clock.Snapshot.State != PlaybackState.Paused);
        _visitor.SetFault(false); DisplaySupport.Place(_visitor, display);
        await _engine.PlayAsync(_operationStop?.Token ?? CancellationToken.None);
        _seekPending = false;
        _operationStop?.Token.ThrowIfCancellationRequested();
        Persist(_settings, exhibitionStop: false);
        DisplaySupport.KeepAwake(true); _fault = "";
        if (_settings.Playlist.Any(item => !item.MappingConfirmed)) Warning.Text = "ch対応が未確認の動画があります。各動画の4経路を確認し、停止後に確認済みを保存してください。";
        var staff = DisplaySupport.Enumerate().FirstOrDefault(d => d.Id != display.Id);
        if (staff is not null)
        {
            // Staff window uses WPF logical coordinates; the visitor is placed in physical monitor pixels.
            var dpi = VisualTreeHelper.GetDpi(this);
            Left = staff.X / dpi.DpiScaleX + 20; Top = staff.Y / dpi.DpiScaleY + 20;
            Topmost = true; Activate();
        }
        else Hide();
    }

    private async Task StopAsync(bool intentional)
    {
        _seekPending = false;
        _resumeAfterDisplay = false; _resumeAfterSleep = false;
        Exception? saveError = null;
        try { if (intentional) Persist(_settings, exhibitionStop: true); }
        catch (Exception ex) { saveError = ex; _log.Write("stop-save-error", ex.ToString()); }
        await _engine.StopAsync(); DisplaySupport.KeepAwake(false);
        _visitor?.Hide(); ShowManagement();
        if (saveError is not null) Warning.Text = "出力は停止しましたが、停止状態の保存に失敗しました: " + saveError.Message;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (_exiting || _recovering || _visitor is null) return;
        try { using var frame = _engine.TakeDueFrame(); if (frame is not null) _visitor.Present(frame); }
        catch (Exception ex) { _ = HandleVideoFaultAsync(ex); }
    }

    private async Task HandleVideoFaultAsync(Exception ex)
    {
        if (_recovering) return;
        _recovering = true; _fault = ex.Message;
        _log.Write("video-fault", ex.ToString());
        try { await _engine.StopAsync(); _visitor?.SetFault(true); DisplaySupport.KeepAwake(false); _nextRecovery = PlaybackClock.QpcSeconds + 5; }
        finally { _recovering = false; }
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        if (_exiting) return;
        _uiTick++;
        RefreshState();
        try
        {
            if (_uiTick % 2 == 0) WriteHeartbeat(false);
            if (_nextSettingsSave > 0 && PlaybackClock.QpcSeconds >= _nextSettingsSave) { _nextSettingsSave = 0; _store.Save(_settings); }
            if (!_busy && !_recovering && PlaybackClock.QpcSeconds >= _nextDisplayCheck)
            {
                _nextDisplayCheck = PlaybackClock.QpcSeconds + 1;
                await CheckDisplayAsync();
            }
            if (!_busy && !_recovering && _fault.Length > 0 && PlaybackClock.QpcSeconds >= _nextRecovery && !_settings.ExhibitionStopped)
            {
                _nextRecovery = PlaybackClock.QpcSeconds + 5;
                await RunUiAsync(async () => { await InspectEntriesAsync(); await PrepareAsync(); await PlayAsync(); });
            }
        }
        catch (Exception ex) { Warning.Text = ex.Message; _log.Write("maintenance-error", ex.ToString()); }
    }

    private void RefreshState()
    {
        var state = _engine.Clock.Snapshot.State;
        var prepared = _preparedKey.Length > 0 ? _engine.Playlist : null;
        var duration = prepared?.Timeline.Duration ?? _entries.Sum(entry => entry.Info?.Duration ?? 0);
        duration = duration > 0 ? duration : 1;
        var position = _engine.Clock.Position;
        PositionText.Text = $"{TimeSpan.FromSeconds(position % duration):mm\\:ss} / {TimeSpan.FromSeconds(duration):mm\\:ss} · {(long)(position / duration) + 1}周目";
        if (prepared is { } playlist)
        {
            var point = playlist.Timeline.Locate(position);
            var info = playlist.Items[point.Index].Info;
            NowPlayingText.Text = $"{point.Index + 1} / {playlist.Items.Count} 本目  {Path.GetFileName(info.Path)}  ·  動画内 {TimeSpan.FromSeconds(point.LocalSeconds):mm\\:ss} / {TimeSpan.FromSeconds(info.Duration):mm\\:ss}";
        }
        else NowPlayingText.Text = "プレイリストを配置して全動画を検証・先読みしてください";
        PositionSlider.Maximum = duration;
        if (!_seekPending && !PositionSlider.IsMouseCaptureWithin && !PositionSlider.IsKeyboardFocusWithin) PositionSlider.Value = position % duration;
        if (!_busy) Status.Text = _fault.Length > 0 ? _fault : state switch { PlaybackState.Playing => "展示運転中", PlaybackState.Paused => "一時停止中", _ => "停止中 — 素材と機器を設定できます" };
        HapticsStopButton.Content = _settings.HapticsStopped ? "振動全停止中（保存済み）" : "振動全停止";
        AudioStatus.Text = _engine.OutputStatus[0]; ControllerStatus1.Text = _engine.OutputStatus[1]; ControllerStatus2.Text = _engine.OutputStatus[2];
        Meter1.Text = $"L {_engine.Meter(1, false):0.000}  /  R {_engine.Meter(1, true):0.000}";
        Meter2.Text = $"L {_engine.Meter(2, false):0.000}  /  R {_engine.Meter(2, true):0.000}";
        var editable = state == PlaybackState.Stopped && !_busy;
        foreach (var control in new UIElement[] { BrowseButton, TrackCombo, MappingPanel, MappingConfirmed, PrepareButton, MonitorCombo, AudioCombo, AudioOffset, AudioExclusive,
            Controller1, Controller2, Offset1, Offset2, Exclusive1, Exclusive2, RefreshButton, Test1L, Test1R, Test2L, Test2R, AutoPlay, LoginStartup, SaveButton,
            PlaylistList, FolderButton, MoveUpButton, MoveDownButton, RemoveButton, PlaySelectedButton }) control.IsEnabled = editable;
        TrackCombo.IsEnabled = MappingPanel.IsEnabled = MappingConfirmed.IsEnabled = editable && _editing?.Info is not null;
        MoveUpButton.IsEnabled = editable && PlaylistList.SelectedIndex > 0;
        MoveDownButton.IsEnabled = editable && PlaylistList.SelectedIndex >= 0 && PlaylistList.SelectedIndex < _entries.Count - 1;
        RemoveButton.IsEnabled = PlaySelectedButton.IsEnabled = editable && PlaylistList.SelectedIndex >= 0;
        PlayButton.IsEnabled = !_busy;
        Title = $"DualSense 展示プレーヤー — 稼働 {TimeSpan.FromSeconds(PlaybackClock.QpcSeconds - _started):hh\\:mm\\:ss}";
    }

    private void WriteHeartbeat(bool normalExit) => SettingsStore.AtomicWrite(_heartbeatPath, JsonSerializer.Serialize(new Heartbeat(
        Environment.ProcessId, DateTimeOffset.UtcNow, _uiTick, _engine.OutputStopPending ? long.MinValue : _engine.Progress,
        _engine.Clock.Snapshot.State == PlaybackState.Playing || _engine.OutputStopPending, normalExit, _engine.Clock.Snapshot.State.ToString())));

    private async Task CheckDisplayAsync()
    {
        var exists = DisplaySupport.Enumerate().Any(d => d.Id == _settings.Machine.MonitorId);
        if (!exists && _engine.Clock.Snapshot.State == PlaybackState.Playing)
        {
            _resumeAfterDisplay = true;
            await _engine.StopAsync(); DisplaySupport.KeepAwake(false); _visitor?.Hide();
            Warning.Text = "指定ディスプレイの接続待機中です。音声・振動は停止しています。";
            _log.Write("display-disconnected");
        }
        else if (exists && _resumeAfterDisplay && !_settings.ExhibitionStopped)
        {
            _resumeAfterDisplay = false; await RunUiAsync(PlayAsync); _log.Write("display-reconnected");
        }
    }

    private void OnDisplaysChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() => _nextDisplayCheck = 0);
    private void OnPowerChanged(object sender, PowerModeChangedEventArgs e) => Dispatcher.BeginInvoke(async () =>
    {
        if (e.Mode == PowerModes.Suspend)
        {
            _resumeAfterSleep = _engine.Clock.Snapshot.State == PlaybackState.Playing;
            await _engine.StopAsync(); DisplaySupport.KeepAwake(false);
        }
        else if (e.Mode == PowerModes.Resume && _resumeAfterSleep && !_settings.ExhibitionStopped)
        {
            _resumeAfterSleep = false; await RunUiAsync(PlayAsync);
        }
    });

    private void DescribeTrack()
    {
        TrackDescription.Text = TrackCombo.SelectedItem is AudioTrack track ? $"デコード順: {track.DecodedOrder}\n音声PTS {track.Start:0.######}秒 / 音声尺 {track.Duration:0.###}秒。レイアウトから用途を推測して並べ替えません。" : "4chトラックが複数ある場合は対象を選択してください。";
    }
    private void OnTrackChanged(object sender, SelectionChangedEventArgs e) { if (_updating) return; MappingConfirmed.IsChecked = false; DescribeTrack(); }
    private void OnMapChanged(object sender, SelectionChangedEventArgs e) { if (!_updating && MappingConfirmed is not null) MappingConfirmed.IsChecked = false; }
    private void OnLiveChanged(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        try { _settings = ReadControls(false); _engine.UpdateSettings(_settings); _nextSettingsSave = PlaybackClock.QpcSeconds + .5; }
        catch (Exception ex) { Warning.Text = ex.Message; }
    }
    private async void OnBrowse(object sender, RoutedEventArgs e) => await RunUiAsync(async () =>
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "4ch動画 (*.mov;*.mp4)|*.mov;*.mp4", CheckFileExists = true, Multiselect = true };
        if (dialog.ShowDialog(this) == true) await AddFilesAsync(dialog.FileNames);
    });
    private async void OnPrepare(object sender, RoutedEventArgs e) => await RunUiAsync(PrepareAsync);
    private async void OnRefresh(object sender, RoutedEventArgs e) => await RunUiAsync(RefreshDevicesAsync);
    private async void OnPlay(object sender, RoutedEventArgs e) => await RunUiAsync(PlayAsync);
    private async void OnPause(object sender, RoutedEventArgs e) => await RunUiAsync(async () => { await _engine.PauseAsync(); DisplaySupport.KeepAwake(false); });
    private async void OnStop(object sender, RoutedEventArgs e)
    {
        _operationStop?.Cancel();
        try { await StopAsync(true); } catch (Exception ex) { Warning.Text = ex.Message; }
    }
    private async void OnRestart(object sender, RoutedEventArgs e) => await RunUiAsync(async () => { await _engine.StopAsync(); await PlayAsync(); });
    private void OnSeekSelection(object sender, MouseButtonEventArgs e) => _seekPending = true;
    private void OnSeekKey(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown) _seekPending = true;
    }
    private async void OnSeek(object sender, RoutedEventArgs e)
    {
        var seconds = PositionSlider.Value;
        await RunUiAsync(async () =>
        {
            if (_engine.Clock.Snapshot.State == PlaybackState.Stopped) await EnsurePlaylistPreparedAsync();
            if (_engine.Playlist is null) return;
            var playing = _engine.Clock.Snapshot.State == PlaybackState.Playing;
            await _engine.SeekAsync(seconds, playing, _operationStop?.Token ?? CancellationToken.None);
            _seekPending = false;
        });
    }
    private void OnHide(object sender, RoutedEventArgs e) { if (_visitor?.IsVisible == true) Hide(); }
    private async void OnTest(object sender, RoutedEventArgs e) => await RunUiAsync(async () =>
    {
        Persist(ReadControls(true));
        var tag = (string)((Button)sender).Tag;
        await _engine.TestGripAsync(tag[0] - '0', tag[1] == 'R', _operationStop?.Token ?? CancellationToken.None);
    });
    private void SetHapticsStop(bool stopped)
    {
        try { Persist(_settings, hapticsStop: stopped); _log.Write(stopped ? "haptics-stop" : "haptics-released"); }
        catch (Exception ex) { Warning.Text = "停止状態の保存に失敗しました: " + ex.Message; ShowManagement(); }
    }
    private void OnHapticsStop(object sender, RoutedEventArgs e) => SetHapticsStop(true);
    private void OnReleaseHaptics(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "保存された振動全停止を解除します。再生中の場合は振動が再開します。", "振動全停止の解除", MessageBoxButton.OKCancel) == MessageBoxResult.OK) SetHapticsStop(false);
    }
    private void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            Persist(ReadControls(true));
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (_settings.StartAtLogin)
            {
                var supervisor = Path.Combine(AppContext.BaseDirectory, "DualSensePlayer.Supervisor.exe");
                if (!File.Exists(supervisor)) throw new FileNotFoundException("監視プログラムを含む配布版から設定してください。", supervisor);
                key.SetValue("DualSenseExhibitionPlayer", "\"" + supervisor + "\"");
            }
            else key.DeleteValue("DualSenseExhibitionPlayer", false);
            Warning.Text = "設定を保存しました。";
        }
        catch (Exception ex) { Warning.Text = ex.Message; }
    }
    private void OnExport(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "診断ログ (*.zip)|*.zip", FileName = $"dualsense-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip" };
        if (dialog.ShowDialog(this) != true) return;
        try { if (File.Exists(dialog.FileName)) File.Delete(dialog.FileName); _log.Export(dialog.FileName, _settings, string.Join(Environment.NewLine, _engine.OutputStatus) + "\n" + ValidationText.Text); }
        catch (Exception ex) { Warning.Text = ex.Message; }
    }

    public void HandleShortcut(KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != (ModifierKeys.Control | ModifierKeys.Shift)) return;
        if (e.Key == Key.M) { ShowManagement(); e.Handled = true; }
        if (e.Key == Key.H) { SetHapticsStop(true); e.Handled = true; }
        if (e.Key == Key.Q) { _ = RequestQuitAsync(); e.Handled = true; }
    }
    private void OnKeyDown(object sender, KeyEventArgs e) => HandleShortcut(e);
    private void ShowManagement() { Show(); WindowState = WindowState.Normal; Topmost = true; Activate(); }
    private void OnClosing(object? sender, CancelEventArgs e) { if (!_allowClose) { e.Cancel = true; _ = RequestQuitAsync(); } }
    private async void OnQuit(object sender, RoutedEventArgs e) => await RequestQuitAsync();
    private async Task RequestQuitAsync()
    {
        if (_exiting) return;
        ShowManagement();
        if (MessageBox.Show(this, "映像・音声・振動を停止して終了しますか？", "終了確認", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        _exiting = true; _timer.Stop(); _operationStop?.Cancel();
        try
        {
            try { Persist(_settings, exhibitionStop: true); }
            catch (Exception ex) { _log.Write("exit-save-error", ex.ToString()); }
            try { WriteHeartbeat(true); }
            catch (IOException ex) { _log.Write("exit-heartbeat-error", ex.ToString()); }
            await _engine.DisposeAsync();
            DisplaySupport.KeepAwake(false);
            try { WriteHeartbeat(true); }
            catch (IOException ex) { _log.Write("exit-heartbeat-error", ex.ToString()); }
            _log.Write("normal-exit"); await _log.DisposeAsync();
        }
        finally
        {
            CompositionTarget.Rendering -= OnRendering;
            SystemEvents.DisplaySettingsChanged -= OnDisplaysChanged;
            SystemEvents.PowerModeChanged -= OnPowerChanged;
            if (_visitor is not null) { _visitor.AllowClose = true; _visitor.Close(); }
            _allowClose = true; Close(); Application.Current.Shutdown();
        }
    }
}
