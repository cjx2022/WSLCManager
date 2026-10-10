using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WSLCManager.Helpers;

/// <summary>
/// 回收工作集。WPF 一旦渲染过窗口，D3D 设备 + 已被 fault in 的显卡驱动页
/// 会被一直引用着，系统不会主动换出 —— 表现为"最小化了还占着几百兆"。
/// 注意顺序：必须先 GC.Collect 把托管引用松开，EmptyWorkingSet 才有意义。
/// </summary>
public static class MemoryTrimmer
{
    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    public static long LastTrimmedToBytes { get; private set; } = -1;

    public static void Trim()
    {
        try
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

            using var p = Process.GetCurrentProcess();
            if (EmptyWorkingSet(p.Handle))
                LastTrimmedToBytes = p.WorkingSet64;
        }
        catch { /* 回收失败不影响功能 */ }
    }

    /// <summary>当前进程工作集（MB）</summary>
    public static double CurrentWorkingSetMb()
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            p.Refresh();
            return p.WorkingSet64 / 1048576.0;
        }
        catch { return -1; }
    }
}
