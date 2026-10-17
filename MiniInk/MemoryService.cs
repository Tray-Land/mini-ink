using System.Diagnostics;
using System.Runtime;
using Windows.Win32;

namespace MiniInk;

internal static class MemoryService
{
    public static void ReleaseIdle(Func<bool> isIdle, TimeSpan? delay = null)
    {
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _ = Task.Run(async () =>
        {
            await Task.Delay(delay ?? TimeSpan.FromSeconds(5));
            TaskCompletionSource<bool> idle = new();
            if (!dispatcher.TryEnqueue(() => idle.SetResult(isIdle())))
                return;
            if (!await idle.Task)
                return;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
            if (!PInvoke.SetProcessWorkingSetSize(PInvoke.GetCurrentProcess(), nuint.MaxValue, nuint.MaxValue))
                Debug.WriteLine("MiniInk: idle working-set trim failed.");
        });
    }
}
