using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace GraphiteCompanion.Services
{
    /// <summary>Reports the foreground window's process name and title whenever either changes.</summary>
    public sealed class ForegroundWatcher : IDisposable
    {
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out Rect r);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [StructLayout(LayoutKind.Sequential)] private struct Rect { public int L, T, R, B; }
        [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }

        private readonly DispatcherTimer timer;
        private string? lastProcess;
        private string? lastTitle;
        private bool lastFullscreen;
        private readonly int ownPid = Environment.ProcessId;

        /// <summary>Raised with (process name without .exe, window title, covers the whole monitor). Shell/own windows are skipped.</summary>
        public event Action<string, string, bool>? Changed;

        public ForegroundWatcher()
        {
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            timer.Tick += (_, _) => Check();
        }

        public void Start() => timer.Start();
        public void Stop() => timer.Stop();

        private static readonly string[] Ignored =
        {
            "explorer", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "SearchApp",
            "LockApp", "TextInputHost", "ApplicationFrameHost", "Taskmgr", "ShellHost",
        };

        private void Check()
        {
            try
            {
                var hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return;
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0 || pid == ownPid) return;
                string name;
                using (var p = Process.GetProcessById((int)pid)) name = p.ProcessName;
                if (Array.Exists(Ignored, n => n.Equals(name, StringComparison.OrdinalIgnoreCase))) return;
                var sb = new StringBuilder(512);
                GetWindowText(hwnd, sb, sb.Capacity);
                string title = sb.ToString();
                bool fullscreen = IsFullscreen(hwnd);
                if (name == lastProcess && title == lastTitle && fullscreen == lastFullscreen) return;
                lastProcess = name;
                lastTitle = title;
                lastFullscreen = fullscreen;
                Changed?.Invoke(name, title, fullscreen);
            }
            catch
            {
                // process exited between calls / access denied - ignore
            }
        }

        private static bool IsFullscreen(IntPtr hwnd)
        {
            if (!GetWindowRect(hwnd, out var w)) return false;
            var mi = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref mi)) return false;
            // exact match: a maximized window is slightly larger than the monitor (invisible borders)
            return w.L == mi.Monitor.L && w.T == mi.Monitor.T && w.R == mi.Monitor.R && w.B == mi.Monitor.B;
        }

        public void Dispose() => timer.Stop();
    }
}
