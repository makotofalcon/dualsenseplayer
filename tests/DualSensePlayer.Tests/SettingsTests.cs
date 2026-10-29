using DualSensePlayer.Core;
using Xunit;

namespace DualSensePlayer.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "dualsense-tests-" + Guid.NewGuid());
    [Fact] public void StopLatchSurvivesCorruptSettingsAndOlderBackup()
    {
        var store = new SettingsStore(_directory);
        store.Save(new() { HapticsStopped = false });
        store.Save(new() { HapticsStopped = true });
        File.WriteAllText(store.SettingsPath, "{broken");
        Assert.True(store.Load().HapticsStopped); Assert.NotNull(store.Warning);
        store.Save(store.Load() with { HapticsStopped = false });
        Assert.False(store.Load().HapticsStopped);
    }

    [Fact] public void DuplicateDeviceAndChannelMappingAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => new PlayerSettings { Media = new() { ChannelMap = [0, 0, 2, 3] } }.Validate());
        Assert.Throws<InvalidDataException>(() => new PlayerSettings { Machine = new() { Controllers = [new() { EndpointId = "a" }, new() { EndpointId = "a" }] } }.Validate());
        Assert.Throws<InvalidDataException>(() => new PlayerSettings { Machine = new() { Controllers = [new() { Gain = float.NaN }, new()] } }.Validate());
    }

    [Fact] public void OtherComputerMustReRegisterDevices()
    {
        var store = new SettingsStore(_directory);
        store.Save(new() { AutoPlay = true, ExhibitionStopped = false, Machine = new() { ComputerName = "another-computer", MonitorId = "display" } });
        var loaded = store.Load(); Assert.False(loaded.AutoPlay); Assert.True(loaded.ExhibitionStopped); Assert.Empty(loaded.Machine.MonitorId);
    }

    [Fact] public void SupervisorLimitsRestartsInSlidingWindow()
    {
        var budget = new RestartBudget();
        Assert.True(budget.TryRestart(0)); Assert.True(budget.TryRestart(10)); Assert.True(budget.TryRestart(20));
        Assert.False(budget.TryRestart(30)); Assert.False(budget.TryRestart(599)); Assert.True(budget.TryRestart(600));
    }
    [Fact] public void ExhibitionStopSurvivesCorruptionOfNewestSettings()
    {
        var store = new SettingsStore(_directory);
        store.Save(new() { ExhibitionStopped = false, AutoPlay = true });
        store.Save(new() { ExhibitionStopped = true, AutoPlay = true });
        File.WriteAllText(store.SettingsPath, "invalid");
        Assert.True(store.Load().ExhibitionStopped);
    }
    [Fact] public void MalformedNullDeviceFieldsRecoverToConfigurationMode()
    {
        var store = new SettingsStore(_directory);
        Directory.CreateDirectory(_directory);
        File.WriteAllText(store.SettingsPath, "{\"Machine\":{\"Audio\":{\"EndpointId\":null}}}");
        var settings = store.Load();
        Assert.True(settings.ExhibitionStopped); Assert.NotNull(store.Warning);
    }
    [Fact] public void CompletingAnOlderMediaLoadCannotReleaseAStaffStop()
    {
        var loading = new PlayerSettings { HapticsStopped = false, ExhibitionStopped = false };
        var stoppedDuringLoad = loading with { HapticsStopped = true, ExhibitionStopped = true };
        var completed = loading.PreserveStopIntent(stoppedDuringLoad);
        Assert.True(completed.HapticsStopped); Assert.True(completed.ExhibitionStopped);
        var released = completed.PreserveStopIntent(completed, hapticsStop: false);
        Assert.False(released.HapticsStopped); Assert.True(released.ExhibitionStopped);
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
