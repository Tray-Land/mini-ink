using System.Diagnostics;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.System.Registry;

namespace MiniInk;

internal static class SystemThemeService
{
    private static readonly RegistryKey? Key = Registry.CurrentUser.OpenSubKey(
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
    private static readonly AutoResetEvent KeyChanged = new(false);
    private static readonly RegisteredWaitHandle? Wait;
    public static bool IsLight { get; private set; } = ReadFlag("SystemUsesLightTheme");
    public static bool ShowsAccentColor => !IsLight && ReadFlag("ColorPrevalence");
    public static event EventHandler? Changed;

    static SystemThemeService()
    {
        if (Key is null)
        {
            Debug.WriteLine("MiniInk: taskbar theme registry key unavailable; using dark taskbar assets.");
            return;
        }
        Wait = ThreadPool.RegisterWaitForSingleObject(KeyChanged, (_, _) =>
        {
            Watch();
            IsLight = ReadFlag("SystemUsesLightTheme");
            Changed?.Invoke(null, EventArgs.Empty);
        }, null, Timeout.Infinite, false);
        Watch();
    }

    private static bool ReadFlag(string name) => Key?.GetValue(name) is int value && value != 0;
    private static void Watch()
    {
        var result = PInvoke.RegNotifyChangeKeyValue(Key!.Handle, false,
            REG_NOTIFY_FILTER.REG_NOTIFY_CHANGE_LAST_SET | REG_NOTIFY_FILTER.REG_NOTIFY_THREAD_AGNOSTIC,
            KeyChanged.SafeWaitHandle, true);
        if ((int)result != 0)
            Debug.WriteLine($"MiniInk: taskbar theme watch failed: {result}");
    }
    public static void Dispose()
    {
        Wait?.Unregister(null);
        Key?.Dispose();
        KeyChanged.Dispose();
    }
}
