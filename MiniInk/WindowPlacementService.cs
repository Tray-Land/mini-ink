using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.Shell;
using WinUIEx;

namespace MiniInk;

internal static class WindowPlacementService
{
    private static nint _trayWindow;
    private static uint _trayId;

    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicFields, typeof(TrayIcon))]
    public static void SetTrayIcon(TrayIcon tray)
    {
        _trayId = tray.TrayIconId;
        FieldInfo? field = typeof(TrayIcon).GetField("_windowHandle", BindingFlags.Instance | BindingFlags.NonPublic);
        object? value = field?.GetValue(tray);
        if (value is nint handle)
            _trayWindow = handle;
        else if (value is not null && field?.FieldType.IsValueType == true && field.FieldType.Name == "HWND")
        {
            // WinUIEx's generated HWND is a blittable single-pointer struct from its own assembly.
            GCHandle pinned = GCHandle.Alloc(value, GCHandleType.Pinned);
            try
            {
                _trayWindow = Marshal.ReadIntPtr(pinned.AddrOfPinnedObject());
            }
            finally
            {
                pinned.Free();
            }
        }
        if (_trayWindow == 0)
            Debug.WriteLine("MiniInk: tray anchor handle unavailable; using cursor/work-area placement.");
    }

    public static void PositionNearTray(Window window, int width, int height)
    {
        NOTIFYICONIDENTIFIER icon = new()
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(), hWnd = (HWND)_trayWindow, uID = _trayId
        };
        RECT iconRect = default;
        bool haveIcon = _trayWindow != 0 && PInvoke.Shell_NotifyIconGetRect(in icon, out iconRect).Succeeded;
        PointInt32 anchor;
        if (haveIcon && iconRect.right > iconRect.left)
            anchor = new((iconRect.left + iconRect.right) / 2, (iconRect.top + iconRect.bottom) / 2);
        else
        {
            PInvoke.GetCursorPos(out System.Drawing.Point cursor);
            anchor = new(cursor.X, cursor.Y);
        }
        RectInt32 work = DisplayArea.GetFromPoint(anchor, DisplayAreaFallback.Primary).WorkArea;
        HMONITOR monitor = PInvoke.MonitorFromPoint(new System.Drawing.Point(anchor.X, anchor.Y), MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        uint dpi = PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out uint dpiX, out _).Succeeded ? dpiX : 96;
        int Scale(int size) => (int)Math.Ceiling(size * dpi / 96.0);
        int margin = Scale(12);
        int w = Math.Min(Scale(width), work.Width - margin * 2);
        int h = Math.Min(Scale(height), work.Height - margin * 2);
        int x = anchor.X - w / 2;
        int y = work.Y + work.Height - h - margin;
        APPBARDATA bar = new() { cbSize = (uint)Marshal.SizeOf<APPBARDATA>() };
        if (PInvoke.SHAppBarMessage(5, ref bar) != 0 &&
            bar.rc.right > work.X && bar.rc.left < work.X + work.Width &&
            bar.rc.bottom >= work.Y && bar.rc.top <= work.Y + work.Height)
        {
            switch (bar.uEdge)
            {
                case 0: x = Math.Max(bar.rc.right, work.X) + margin; y = anchor.Y - h; break;
                case 1: y = Math.Max(bar.rc.bottom, work.Y) + margin; break;
                case 2: x = Math.Min(bar.rc.left, work.X + work.Width) - w - margin; y = anchor.Y - h; break;
                case 3: y = Math.Min(bar.rc.top, work.Y + work.Height) - h - margin; break;
            }
        }
        x = Math.Clamp(x, work.X + margin, Math.Max(work.X + margin, work.X + work.Width - w - margin));
        y = Math.Clamp(y, work.Y + margin, Math.Max(work.Y + margin, work.Y + work.Height - h - margin));
        window.AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
    }
}
