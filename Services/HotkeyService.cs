using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Linq;
using System.Windows.Threading;

namespace GraphiteCompanion.Services
{
    /// <summary>System-wide hotkeys (work even when the window is hidden in the tray).</summary>
    public sealed class HotkeyService : IDisposable
    {
        public const uint Alt = 0x1;
        public const uint Ctrl = 0x2;
        public const uint Shift = 0x4;
        public const uint Win = 0x8;
        private const uint NoRepeat = 0x4000;
        private const int WmHotkey = 0x0312;

        [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        private readonly HwndSource source;
        private readonly Dictionary<int, (uint Mods, uint Key, Action Release)> holds = new();
        private readonly HashSet<int> active = new();
        private readonly DispatcherTimer releaseTimer = new() { Interval = TimeSpan.FromMilliseconds(15) };
        private readonly Func<int, bool> isDown;
        private bool Held(uint mods, uint key) => isDown((int)key)
            && ((mods & Ctrl) == 0 || isDown(0x11))
            && ((mods & Alt) == 0 || isDown(0x12))
            && ((mods & Shift) == 0 || isDown(0x10))
            && ((mods & Win) == 0 || (isDown(0x5b) || isDown(0x5c)));
        private void Release(int id) { if (active.Remove(id) && holds.TryGetValue(id, out var h)) h.Release(); }
        private readonly Dictionary<int, Action> actions = new();
        private int nextId = 0x5100;

        public HotkeyService(Window window, Func<int, bool>? keyState = null)
        {
            isDown = keyState ?? (key => (GetAsyncKeyState(key) & 0x8000) != 0);
            var handle = new WindowInteropHelper(window).EnsureHandle();
            source = HwndSource.FromHwnd(handle);
            source.AddHook(WndProc);
            releaseTimer.Tick += (_, _) => PollReleased();
        }

        private void PollReleased() { foreach (int id in active.ToArray()) if (holds.TryGetValue(id, out var h) && !Held(h.Mods, h.Key)) Release(id); if (active.Count == 0) releaseTimer.Stop(); }

        public bool Register(uint modifiers, uint virtualKey, Action action, Action? released = null, Func<bool>? requireHeld = null)
        {
            int id = nextId++;
            if (!RegisterHotKey(source.Handle, id, modifiers | NoRepeat, virtualKey)) return false;
            if (released != null) holds[id] = (modifiers, virtualKey, released);
            actions[id] = () => {
                if (released != null && (requireHeld?.Invoke() ?? true)) {
                    if (!Held(modifiers, virtualKey) || !active.Add(id)) return;
                    releaseTimer.Start();
                }
                action();
            };
            return true;
        }

        /// <summary>Unregisters everything (used while a key is being recorded in the settings window).</summary>
        public void Clear()
        {
            releaseTimer.Stop();
            foreach (int id in active.ToArray()) Release(id);
            holds.Clear();
            foreach (var id in actions.Keys) UnregisterHotKey(source.Handle, id);
            actions.Clear();
        }

        /// <summary>True when Windows would accept this combination right now (nobody else holds it).</summary>
        public bool IsFree(uint modifiers, uint virtualKey)
        {
            int id = nextId++;
            if (!RegisterHotKey(source.Handle, id, modifiers | NoRepeat, virtualKey)) return false;
            UnregisterHotKey(source.Handle, id);
            return true;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WmHotkey && actions.TryGetValue(wParam.ToInt32(), out var action))
            {
                action();
                handled = true;
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            releaseTimer.Stop();
            foreach (int id in active.ToArray()) Release(id);
            holds.Clear();
            foreach (var id in actions.Keys) UnregisterHotKey(source.Handle, id);
            actions.Clear();
            source.RemoveHook(WndProc);
        }
    }
}
