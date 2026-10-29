using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DualSensePlayer.Core;

namespace DualSensePlayer.App;

public sealed record DisplayTarget(string Id, string Name, int X, int Y, int Width, int Height)
{
    public override string ToString() => $"{Name} · {Width}×{Height}";
}

internal static class DisplaySupport
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }
    public static DisplayTarget[] Enumerate() => System.Windows.Forms.Screen.AllScreens.Select(screen =>
    {
        var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>(), DeviceName = "", DeviceString = "", DeviceId = "", DeviceKey = "" };
        EnumDisplayDevices(screen.DeviceName, 0, ref device, 1);
        return new DisplayTarget(screen.DeviceName + "|" + device.DeviceId, screen.DeviceName + " " + device.DeviceString,
            screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height);
    }).ToArray();
    public static void Place(Window window, DisplayTarget display)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        SetWindowPos(handle, new IntPtr(-1), display.X, display.Y, display.Width, display.Height, 0x0010);
    }
    public static void KeepAwake(bool active) => SetThreadExecutionState(active ? 0x80000003u : 0x80000000u);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumDisplayDevices(string name, uint number, ref DisplayDevice device, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
}

public sealed class VisitorWindow : Window
{
    private readonly System.Windows.Controls.Image _image = new() { Stretch = Stretch.Uniform };
    private readonly System.Windows.Controls.TextBlock _notice = new() { Text = "ただいま調整中", FontSize = 34, Foreground = Brushes.White,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly DispatcherTimer _cursorTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private double _lastMouse = PlaybackClock.QpcSeconds;
    private WriteableBitmap? _bitmap;
    public bool AllowClose { get; set; }
    public VisitorWindow(Action<KeyEventArgs> shortcut, Action requestQuit)
    {
        Title = "DualSense 展示"; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        Background = Brushes.Black; Topmost = true; ShowActivated = false;
        var panel = new System.Windows.Controls.Grid(); panel.Children.Add(_image); panel.Children.Add(_notice); Content = panel;
        PreviewKeyDown += (_, e) => shortcut(e);
        MouseMove += (_, _) => { _lastMouse = PlaybackClock.QpcSeconds; Cursor = Cursors.Arrow; };
        _cursorTimer.Tick += (_, _) => { if (PlaybackClock.QpcSeconds - _lastMouse >= 3) Cursor = Cursors.None; };
        _cursorTimer.Start();
        Closing += (_, e) => { if (!AllowClose) { e.Cancel = true; requestQuit(); } };
        Closed += (_, _) => _cursorTimer.Stop();
    }
    public void Configure(MediaInfo media, bool clear = true)
    {
        if (!clear && _bitmap?.PixelWidth == media.Width && _bitmap.PixelHeight == media.Height) return;
        _bitmap = new(media.Width, media.Height, 96, 96, PixelFormats.Bgra32, null);
        _image.Source = _bitmap; _notice.Visibility = Visibility.Collapsed;
    }
    public bool TryPresent(VideoFrame frame)
    {
        if (frame.Width > 0 && frame.Height > 0 && (_bitmap?.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height))
        {
            _bitmap = new(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);
            _image.Source = _bitmap;
        }
        if (_bitmap is null) return false;
        var stride = checked(_bitmap.PixelWidth * 4);
        if (frame.Length != checked(stride * _bitmap.PixelHeight)) throw new InvalidDataException("映像フレームの大きさが一致しません。");
        // WritePixels waits for the compositor's previous copy while holding the UI
        // thread. Retry next rendering event instead, keeping the latest due frame.
        if (!_bitmap.TryLock(new Duration(TimeSpan.Zero))) return false;
        try
        {
            if (_bitmap.BackBufferStride == stride)
                Marshal.Copy(frame.Pixels, 0, _bitmap.BackBuffer, frame.Length);
            else
                for (var row = 0; row < _bitmap.PixelHeight; row++)
                    Marshal.Copy(frame.Pixels, row * stride, _bitmap.BackBuffer + row * _bitmap.BackBufferStride, stride);
            _bitmap.AddDirtyRect(new Int32Rect(0, 0, _bitmap.PixelWidth, _bitmap.PixelHeight));
        }
        finally { _bitmap.Unlock(); }
        _notice.Visibility = Visibility.Collapsed;
        return true;
    }
    public void SetFault(bool fault) { _notice.Visibility = fault ? Visibility.Visible : Visibility.Collapsed; _image.Visibility = fault ? Visibility.Hidden : Visibility.Visible; }
}
