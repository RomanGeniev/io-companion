using System;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

namespace GraphiteCompanion.Services
{
    /// <summary>
    /// Gives memory back to Windows while the app only sits in the tray: a compacting GC
    /// (also of the large-object heap) followed by trimming the working set. Pages that are
    /// needed again later are simply loaded back, so nothing breaks - it just stops occupying RAM.
    /// </summary>
    public static class MemoryTrimmer
    {
        [DllImport("kernel32.dll")]
        private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

        public static void Trim()
        {
            try
            {
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                using var self = Process.GetCurrentProcess();
                SetProcessWorkingSetSize(self.Handle, (IntPtr)(-1), (IntPtr)(-1));
            }
            catch
            {
                // purely an optimisation
            }
        }
    }
}
