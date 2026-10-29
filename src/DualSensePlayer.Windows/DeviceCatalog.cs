using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using DualSensePlayer.Core;

namespace DualSensePlayer.Windows;

public sealed record HidDevice(string Path, string ContainerId, string Serial);
public sealed record AudioDevice(string Id, string Name, string ContainerId, int Channels, int Rate, HidDevice? Hid)
{
    public override string ToString() => $"{Name} · {Channels}ch/{Rate}Hz · {ContainerId[..Math.Min(ContainerId.Length, 8)]}";
}

public static class DeviceCatalog
{
    private static readonly PropertyKey ContainerKey = new(new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);
    public static AudioDevice[] Enumerate()
    {
        var hid = HidNative.Enumerate();
        var devices = new List<AudioDevice>();
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                try
                {
                    var container = device.Properties.Contains(ContainerKey) ? device.Properties[ContainerKey].Value?.ToString() ?? "" : "";
                    container = Guid.TryParse(container, out var guid) ? guid.ToString("D") : "";
                    using var client = device.AudioClient;
                    var format = client.MixFormat;
                    var matching = hid.Where(h => h.ContainerId == container && container.Length > 0).ToArray();
                    devices.Add(new(device.ID, device.FriendlyName, container, format.Channels, format.SampleRate,
                        matching.Length == 1 ? matching[0] : null));
                }
                catch (COMException) { /* An endpoint can disappear during enumeration. */ }
            }
        }
        return devices.ToArray();
    }

    public static AudioDevice? Match(OutputSettings registered, IEnumerable<AudioDevice> devices, bool controller)
    {
        if (registered.EndpointId.Length == 0) return null;
        if (!controller) return devices.SingleOrDefault(d => d.Id == registered.EndpointId && d.Hid is null);
        if (registered.ContainerId.Length == 0) return null;
        var candidates = devices.Where(d => d.Hid is not null && d.ContainerId == registered.ContainerId &&
            (registered.Serial.Length == 0 || d.Hid.Serial == registered.Serial)).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    public static void InitializeHaptics(HidDevice device)
    {
        using var handle = HidNative.CreateFile(device.Path, 0x40000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "DualSenseのHID出力を開けません。他の制御アプリを確認してください。");
        var report = DualSenseReport.EnableUsbAudioHaptics();
        using var stream = new FileStream(handle, FileAccess.Write, 48, isAsync: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        stream.WriteAsync(report, timeout.Token).AsTask().GetAwaiter().GetResult();
    }
}

internal static class HidNative
{
    [StructLayout(LayoutKind.Sequential)] private struct InterfaceData { public uint Size; public Guid Class; public uint Flags; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct DeviceInfo { public uint Size; public Guid Class; public uint Instance; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct DevicePropertyKey { public Guid Format; public uint Id; }
    [StructLayout(LayoutKind.Sequential)] private struct Attributes { public int Size; public ushort Vendor, Product, Version; }
    [StructLayout(LayoutKind.Sequential, Pack = 2)] private struct Caps
    {
        public ushort Usage, UsagePage, InputLength, OutputLength, FeatureLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort LinkNodes, InputButtons, InputValues, InputIndices, OutputButtons, OutputValues, OutputIndices, FeatureButtons, FeatureValues, FeatureIndices;
    }

    internal static HidDevice[] Enumerate()
    {
        HidD_GetHidGuid(out var hidGuid);
        var list = SetupDiGetClassDevs(ref hidGuid, null, IntPtr.Zero, 0x12);
        if (list == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var result = new List<HidDevice>();
        try
        {
            for (uint i = 0; ; i++)
            {
                var face = new InterfaceData { Size = (uint)Marshal.SizeOf<InterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(list, IntPtr.Zero, ref hidGuid, i, ref face))
                {
                    if (Marshal.GetLastWin32Error() == 259) break;
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                var info = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
                SetupDiGetDeviceInterfaceDetail(list, ref face, IntPtr.Zero, 0, out var required, ref info);
                if (required < 8) continue;
                var detail = Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(list, ref face, detail, required, out _, ref info)) continue;
                    var path = Marshal.PtrToStringUni(detail + 4)!;
                    using var handle = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    if (handle.IsInvalid) continue;
                    var attributes = new Attributes { Size = Marshal.SizeOf<Attributes>() };
                    if (!HidD_GetAttributes(handle, ref attributes) || attributes.Vendor != 0x054c || attributes.Product != 0x0ce6) continue;
                    if (!HidD_GetPreparsedData(handle, out var preparsed)) continue;
                    try
                    {
                        if (HidP_GetCaps(preparsed, out var caps) < 0 || caps.UsagePage != 1 || caps.Usage != 5 || caps.InputLength != 64 || caps.OutputLength != 48) continue;
                    }
                    finally { HidD_FreePreparsedData(preparsed); }
                    if (!HasUsbAncestor(info.Instance)) continue;
                    var key = new DevicePropertyKey { Format = new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), Id = 2 };
                    var container = new byte[16];
                    if (!SetupDiGetDeviceProperty(list, ref info, ref key, out _, container, 16, out _, 0)) continue;
                    var serial = new StringBuilder(128);
                    if (!HidD_GetSerialNumberString(handle, serial, 256)) serial.Clear();
                    result.Add(new(path, new Guid(container).ToString("D"), serial.ToString()));
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(list); }
        return result.ToArray();
    }

    private static bool HasUsbAncestor(uint instance)
    {
        for (var depth = 0; depth < 12; depth++)
        {
            var name = new StringBuilder(512);
            if (CM_Get_Device_ID(instance, name, name.Capacity, 0) == 0 && name.ToString().StartsWith("USB\\VID_054C&PID_0CE6", StringComparison.OrdinalIgnoreCase)) return true;
            if (CM_Get_Parent(out var parent, instance, 0) != 0) return false;
            instance = parent;
        }
        return false;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool WriteFile(SafeFileHandle file, byte[] buffer, int count, out int written, IntPtr overlapped);
    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetAttributes(SafeFileHandle handle, ref Attributes attributes);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, out Caps caps);
    [DllImport("hid.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetSerialNumberString(SafeFileHandle handle, StringBuilder serial, int bytes);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string? enumerator, IntPtr window, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr list, IntPtr device, ref Guid guid, uint index, ref InterfaceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr list, ref InterfaceData face, IntPtr detail, uint size, out uint required, ref DeviceInfo info);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDevicePropertyW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceProperty(IntPtr list, ref DeviceInfo info, ref DevicePropertyKey key, out uint type, byte[] data, uint size, out uint required, uint flags);
    [DllImport("setupapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr list);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern int CM_Get_Device_ID(uint instance, StringBuilder buffer, int size, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern int CM_Get_Parent(out uint parent, uint instance, uint flags);
}
