using System.Windows;

namespace DualSensePlayer.App;

public partial class App : Application
{
    private Mutex? _singleInstance;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _singleInstance = new Mutex(true, "Local\\DualSenseExhibitionPlayer", out var first);
        if (!first) { MessageBox.Show("展示プレーヤーは既に起動しています。"); Shutdown(); return; }
        var window = new MainWindow(e.Args);
        MainWindow = window;
        window.Show();
    }
    protected override void OnExit(ExitEventArgs e) { _singleInstance?.Dispose(); base.OnExit(e); }
}
