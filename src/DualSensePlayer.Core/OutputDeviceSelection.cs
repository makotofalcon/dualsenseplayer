namespace DualSensePlayer.Core;

public sealed record HidDevice(string Path, string ContainerId, string Serial);

public sealed record AudioDevice(string Id, string Name, string ContainerId, int Channels, int Rate, HidDevice? Hid)
{
    // A manually selected four-channel endpoint can play audio even if HID discovery is unavailable.
    public bool CanSendHaptics => Hid is not null || Channels == 4 && Rate == 48000;
    public override string ToString() => $"{Name} · {Channels}ch/{Rate}Hz" +
        (Hid is not null ? " · USB DualSense" : CanSendHaptics ? " · 4ch音声出力" : "");
}

public static class OutputDeviceSelection
{
    public static AudioDevice? Match(OutputSettings registered, IEnumerable<AudioDevice> devices, bool controller)
    {
        if (registered.EndpointId.Length == 0) return null;
        var candidates = devices.Where(d => controller ? d.CanSendHaptics : d.Hid is null).ToArray();
        var exact = candidates.SingleOrDefault(d => d.Id == registered.EndpointId);
        if (!controller) return exact;
        if (exact is not null &&
            (registered.ContainerId.Length == 0 || exact.ContainerId.Length == 0 || exact.ContainerId == registered.ContainerId) &&
            (registered.Serial.Length == 0 || exact.Hid is null || exact.Hid.Serial.Length == 0 || exact.Hid.Serial == registered.Serial)) return exact;
        if (registered.ContainerId.Length == 0) return null;
        var sameController = candidates.Where(d => d.ContainerId == registered.ContainerId &&
            (registered.Serial.Length == 0 || d.Hid?.Serial == registered.Serial)).ToArray();
        return sameController.Length == 1 ? sameController[0] : null;
    }
}
