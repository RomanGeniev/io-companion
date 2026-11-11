using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace GraphiteCompanion.Services
{
    public enum AutoClickButton { Left, Right, Middle }
    public sealed record AutoClickerSettings
    {
        public bool HoldToRun { get; set; }
        public bool TimeLimited { get; set; }
        public int RunSeconds { get; set; } = 30;
        public bool ShowIndicator { get; set; } = true;
        public int IntervalMs { get; set; } = 100;
        public AutoClickButton Button { get; set; }
        public bool DoubleClick { get; set; }
        public bool Limited { get; set; }
        public int RepeatCount { get; set; } = 100;
        public bool FixedPosition { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int StartDelayMs { get; set; } = 1000;

        public AutoClickerSettings Normalize() => this with
        {
            RunSeconds = Math.Clamp(RunSeconds, 1, 86400),
            IntervalMs = Math.Clamp(IntervalMs, 10, 3_600_000),
            RepeatCount = Math.Clamp(RepeatCount, 1, 1_000_000),
            StartDelayMs = Math.Clamp(StartDelayMs, 0, 60_000),
            Button = Enum.IsDefined(Button) ? Button : AutoClickButton.Left,
        };
    }

    public enum AutoClickResult { Sent, ProtectedWindow, Failed }
    public interface IAutoClickInput
    {
        AutoClickResult Click(AutoClickerSettings settings);
    }
    public sealed record AutoClickerState(bool Running, long Completed, bool WaitingForTarget, DateTimeOffset StartsAt, string? Error, double? RemainingSeconds = null);

    /// <summary>One cancellable worker, no held mouse buttons and no UI-thread click loop.</summary>
    public sealed class AutoClickerService : IDisposable
    {
        private readonly object gate = new();
        private readonly IAutoClickInput input;
        private CancellationTokenSource? cancellation;
        private Task worker = Task.CompletedTask;
        private bool disposed;
        private Stopwatch? runClock;
        private double? durationSeconds;
        private AutoClickerState state = new(false, 0, false, default, null);
        public AutoClickerService(IAutoClickInput? input = null) => this.input = input ?? new WindowsAutoClickInput();
        public AutoClickerState State { get { lock (gate) return state with { RemainingSeconds = durationSeconds is double seconds ? Math.Max(0, seconds - (runClock?.Elapsed.TotalSeconds ?? 0)) : null }; } }
        public Task Completion { get { lock (gate) return worker; } }

        public bool Start(AutoClickerSettings settings)
        {
            lock (gate)
            {
                if (disposed || state.Running) return false;
                var options = settings.Normalize(); // snapshot: editing cannot change an in-flight click
                durationSeconds = options.TimeLimited ? options.RunSeconds : null; runClock = null;
                cancellation = new CancellationTokenSource();
                var token = cancellation.Token;
                state = new(true, 0, false, DateTimeOffset.UtcNow.AddMilliseconds(options.StartDelayMs), null);
                worker = Task.Run(() => Run(options, token));
                return true;
            }
        }

        public void Stop()
        {
            // The same lock surrounds an atomic down/up pair, so nothing can be sent after Stop returns.
            lock (gate) cancellation?.Cancel();
        }

        private async Task Run(AutoClickerSettings options, CancellationToken token)
        {
            try
            {
                await Task.Delay(options.StartDelayMs, token).ConfigureAwait(false);
                var elapsed = Stopwatch.StartNew();
                lock (gate) runClock = elapsed;
                while (true)
                {
                    AutoClickResult result;
                    lock (gate)
                    {
                        token.ThrowIfCancellationRequested();
                        double remaining = options.RunSeconds - elapsed.Elapsed.TotalSeconds;
                        if (options.TimeLimited && remaining <= 0) return;
                        state = state with { RemainingSeconds = options.TimeLimited ? remaining : null };
                        result = input.Click(options);
                        if (result == AutoClickResult.Failed)
                        {
                            state = state with { Error = "Клик не отправлен. Проверьте доступ к выбранному окну и положение точки." };
                            return;
                        }
                        state = state with { WaitingForTarget = result == AutoClickResult.ProtectedWindow,
                            Completed = state.Completed + (result == AutoClickResult.Sent ? 1 : 0) };
                        if (options.Limited && state.Completed >= options.RepeatCount) return;
                    }
                    int wait = result == AutoClickResult.ProtectedWindow ? 80 : options.IntervalMs;
                    double remainingMs = options.RunSeconds * 1000d - elapsed.Elapsed.TotalMilliseconds;
                    bool stopAfterWait = options.TimeLimited && remainingMs <= wait;
                    if (stopAfterWait) wait = Math.Max(1, (int)Math.Ceiling(remainingMs));
                    await Task.Delay(wait, token).ConfigureAwait(false);
                    if (stopAfterWait) return;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { lock (gate) state = state with { Error = "Автокликер остановлен: " + ex.Message }; }
            finally
            {
                lock (gate)
                {
                    runClock?.Stop();
                    state = state with { Running = false, WaitingForTarget = false };
                    cancellation?.Dispose();
                    cancellation = null;
                }
            }
        }

        public void Dispose() { lock (gate) { disposed = true; cancellation?.Cancel(); } }
    }

    public sealed class WindowsAutoClickInput : IAutoClickInput
    {
        internal static readonly UIntPtr Marker = new(0x5348434C);
        [StructLayout(LayoutKind.Sequential)] public struct ScreenPoint { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
        [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public MouseInput Mouse; }
        [DllImport("user32.dll")] private static extern bool GetPhysicalCursorPos(out ScreenPoint point);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPhysicalPoint(ScreenPoint point);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(ScreenPoint point, uint flags);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] input, int size);

        public static ScreenPoint? Cursor() => GetPhysicalCursorPos(out var point) ? point : null;
        public AutoClickResult Click(AutoClickerSettings settings)
        {
            var point = settings.FixedPosition ? new ScreenPoint { X = settings.X, Y = settings.Y } : Cursor();
            if (!point.HasValue || MonitorFromPoint(point.Value, 0) == IntPtr.Zero) return AutoClickResult.Failed;
            var window = WindowFromPhysicalPoint(point.Value);
            if (window == IntPtr.Zero) return AutoClickResult.Failed;
            GetWindowThreadProcessId(window, out uint process);
            if (process == Environment.ProcessId) return AutoClickResult.ProtectedWindow;

            uint down = settings.Button switch { AutoClickButton.Right => 0x0008u, AutoClickButton.Middle => 0x0020u, _ => 0x0002u };
            uint up = down << 1;
            var events = new Input[settings.DoubleClick ? 4 : 2];
            int x = 0, y = 0;
            uint move = 0;
            if (settings.FixedPosition)
            {
                IntPtr previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
                try
                {
                    int left = GetSystemMetrics(76), top = GetSystemMetrics(77), width = GetSystemMetrics(78), height = GetSystemMetrics(79);
                    x = (int)Math.Clamp(((long)point.Value.X - left) * 65536 / Math.Max(1, width) + 32768 / Math.Max(1, width), 0, 65535);
                    y = (int)Math.Clamp(((long)point.Value.Y - top) * 65536 / Math.Max(1, height) + 32768 / Math.Max(1, height), 0, 65535);
                    move = 0x0001 | 0x8000 | 0x4000; // absolute position on the entire virtual desktop
                }
                finally { if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
            }
            for (int i = 0; i < events.Length; i++)
                events[i] = new Input { Mouse = new MouseInput { X = x, Y = y, Flags = (i % 2 == 0 ? down : up) | (i == 0 ? move : 0), Extra = Marker } };
            uint sent = SendInput((uint)events.Length, events, Marshal.SizeOf<Input>());
            if (sent == events.Length) return AutoClickResult.Sent;
            // A partially accepted batch must not leave a button held down.
            SendInput(1, new[] { new Input { Mouse = new MouseInput { Flags = up, Extra = Marker } } }, Marshal.SizeOf<Input>());
            return AutoClickResult.Failed;
        }
    }
}
