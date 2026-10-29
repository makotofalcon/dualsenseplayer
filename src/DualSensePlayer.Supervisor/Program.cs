using System.Diagnostics;
using System.Text.Json;
using DualSensePlayer.Core;

namespace DualSensePlayer.Supervisor;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, "Local\\DualSenseExhibitionSupervisor", out var first);
        if (!first) return;
        try { RunAsync().GetAwaiter().GetResult(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "DualSense 監視 — 保守待ち"); }
    }

    private static async Task RunAsync()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DualSenseExhibitionPlayer");
        Directory.CreateDirectory(directory);
        await using var log = new DiagnosticLog(Path.Combine(directory, "logs"));
        var budget = new RestartBudget();
        var executable = Path.Combine(AppContext.BaseDirectory, "DualSensePlayer.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("DualSensePlayer.exeを同じフォルダーに配置してください。", executable);
        while (true)
        {
            var heartbeatPath = Path.Combine(directory, "heartbeat-" + Guid.NewGuid().ToString("N") + ".json");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
            start.ArgumentList.Add("--supervised"); start.ArgumentList.Add("--heartbeat"); start.ArgumentList.Add(heartbeatPath);
            using var process = Process.Start(start) ?? throw new IOException("プレーヤーを起動できません。");
            var launched = Stopwatch.GetTimestamp();
            var lastUi = launched; var lastEngine = launched;
            long uiTick = -1, engineTick = -1;
            var observed = false; var normalExit = false; var reason = "異常終了";
            while (!process.HasExited)
            {
                var beat = ReadHeartbeat(heartbeatPath);
                if (beat?.ProcessId == process.Id)
                {
                    observed = true; normalExit = beat.NormalExit;
                    if (beat.UiTick != uiTick) { lastUi = Stopwatch.GetTimestamp(); uiTick = beat.UiTick; }
                    if (!beat.Playing || beat.EngineTick != engineTick) { lastEngine = Stopwatch.GetTimestamp(); engineTick = beat.EngineTick; }
                    if (normalExit)
                    {
                        // Allow disposal, but still prevent a normal-exit hang from leaving output alive.
                        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                        catch (TimeoutException) { process.Kill(true); await process.WaitForExitAsync(); }
                        break;
                    }
                }
                if (observed && (Stopwatch.GetElapsedTime(lastUi).TotalSeconds > 5 || Stopwatch.GetElapsedTime(lastEngine).TotalSeconds > 5) ||
                    !observed && Stopwatch.GetElapsedTime(launched).TotalSeconds > 15)
                {
                    reason = "UIまたは映像再生処理のハング";
                    process.Kill(true);
                    await process.WaitForExitAsync(); // Never start a replacement while the old process is alive.
                    break;
                }
                await Task.Delay(250);
            }
            normalExit |= ReadHeartbeat(heartbeatPath)?.NormalExit == true;
            normalExit |= process.ExitCode == 0;
            if (File.Exists(heartbeatPath)) File.Delete(heartbeatPath);
            if (normalExit) { log.Write("supervisor-normal-exit"); return; }
            log.Write("supervisor-restart", new { reason, process.ExitCode });
            if (!budget.TryRestart(PlaybackClock.QpcSeconds))
            {
                log.Write("supervisor-maintenance", "10分間に3回の再起動上限に達しました。");
                MessageBox.Show("10分間に3回の再起動上限に達しました。機器・素材・診断ログを確認し、手動で起動してください。", "DualSense 監視 — 保守待ち");
                return;
            }
            await Task.Delay(1000);
        }
    }
    private static Heartbeat? ReadHeartbeat(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<Heartbeat>(File.ReadAllText(path)) : null; }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }
}
