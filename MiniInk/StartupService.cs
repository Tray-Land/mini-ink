using System.Runtime.InteropServices;
using Windows.ApplicationModel;

namespace MiniInk;

// Starting with Windows is the package's startup task, declared off in Package.appxmanifest.
public static class StartupService
{
    private const string TaskId = "MiniInkStartup";

    // Null when there is no task to change, as when running unpackaged.
    public static async Task<StartupTaskState?> GetStateAsync()
    {
        try
        {
            return (await StartupTask.GetAsync(TaskId)).State;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    public static async Task<StartupTaskState?> SetEnabledAsync(bool enabled)
    {
        try
        {
            StartupTask task = await StartupTask.GetAsync(TaskId);
            if (enabled)
                return await task.RequestEnableAsync();
            task.Disable();
            return task.State;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }
}
