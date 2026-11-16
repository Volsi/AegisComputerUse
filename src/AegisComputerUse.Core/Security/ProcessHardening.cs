using AegisComputerUse.Core.Native;

namespace AegisComputerUse.Core.Security;

public sealed record ProcessHardeningReport(
    bool DllDirectoriesRestricted,
    bool SafeSearchPathEnabled,
    bool HeapTerminationEnabled,
    bool DpiAwarenessEnabled);

public static class ProcessHardening
{
    private static int _initialized;
    private static ProcessHardeningReport? _report;

    public static ProcessHardeningReport Initialize()
    {
        if (Interlocked.CompareExchange(ref _initialized, 1, 0) == 0)
        {
            bool dllOk = TryCall(() => Win32Interop.SetDefaultDllDirectories(Win32Interop.LOAD_LIBRARY_SEARCH_SYSTEM32));
            bool searchOk = TryCall(() => Win32Interop.SetSearchPathMode(Win32Interop.BASE_SEARCH_PATH_ENABLE_SAFE_SEARCHMODE));
            bool heapOk = TryCall(() =>
            {
                IntPtr heap = Win32Interop.GetProcessHeap();
                return heap != IntPtr.Zero &&
                       Win32Interop.HeapSetInformation(heap, Win32Interop.HeapEnableTerminationOnCorruption, IntPtr.Zero, UIntPtr.Zero);
            });
            bool dpiOk = TryCall(Win32Interop.SetProcessDPIAware);

            _report = new ProcessHardeningReport(dllOk, searchOk, heapOk, dpiOk);
        }

        return _report ?? new ProcessHardeningReport(false, false, false, false);
    }

    private static bool TryCall(Func<bool> fn)
    {
        try { return fn(); }
        catch { return false; }
    }
}
