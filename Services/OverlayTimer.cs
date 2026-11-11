using System;
using System.Windows.Threading;

namespace GraphiteCompanion.Services
{
    public enum OverlayTimerState { Ready, Running, Paused, Finished }

    /// <summary>Owned by MainWindow, so closing and reopening the overlay keeps the countdown.</summary>
    public sealed class OverlayTimer : IDisposable
    {
        private readonly TimeProvider clock;
        private readonly DispatcherTimer ticker;
        private TimeSpan remainingAtStart = TimeSpan.FromMinutes(5);
        private long startedAt;
        private bool disposed;

        public TimeSpan Duration { get; private set; } = TimeSpan.FromMinutes(5);
        public OverlayTimerState State { get; private set; }
        public bool IsShown { get; private set; }
        public TimeSpan Remaining => State == OverlayTimerState.Running
            ? MaxZero(remainingAtStart - clock.GetElapsedTime(startedAt)) : remainingAtStart;
        public event Action? Changed;
        public event Action? Completed;

        public OverlayTimer(TimeProvider? clock = null)
        {
            this.clock = clock ?? TimeProvider.System;
            ticker = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
            ticker.Tick += OnTick;
        }

        public void ToggleVisibility()
        {
            if (disposed) return;
            IsShown = !IsShown;
            Changed?.Invoke();
        }

        public void SetSeconds(int seconds)
        {
            if (disposed || State is OverlayTimerState.Running or OverlayTimerState.Paused) return;
            Duration = TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 10800));
            Reset();
        }

        public void StartOrPause()
        {
            if (disposed) return;
            if (State == OverlayTimerState.Running)
            {
                if (CompleteIfDue()) return;
                remainingAtStart = Remaining;
                State = OverlayTimerState.Paused;
                ticker.Stop();
            }
            else
            {
                if (State != OverlayTimerState.Paused) remainingAtStart = Duration;
                startedAt = clock.GetTimestamp();
                State = OverlayTimerState.Running;
                ticker.Start();
            }
            Changed?.Invoke();
        }

        public void Reset()
        {
            if (disposed) return;
            ticker.Stop();
            remainingAtStart = Duration;
            State = OverlayTimerState.Ready;
            Changed?.Invoke();
        }

        private void OnTick(object? sender, EventArgs e)
        {
            if (!CompleteIfDue()) Changed?.Invoke();
        }

        private bool CompleteIfDue()
        {
            if (disposed || State != OverlayTimerState.Running || Remaining > TimeSpan.Zero) return false;
            ticker.Stop();
            remainingAtStart = TimeSpan.Zero;
            State = OverlayTimerState.Finished;
            Changed?.Invoke();
            Completed?.Invoke();
            return true;
        }

        private static TimeSpan MaxZero(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            ticker.Stop();
            ticker.Tick -= OnTick;
            Changed = null;
            Completed = null;
        }
    }
}
