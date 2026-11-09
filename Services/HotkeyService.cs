using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

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

        private readonly HwndSource source;
        private readonly Dictionary<int, Action> actions = new();
        private int nextId = 0x5100;

        public HotkeyService(Window window)
        {
            var handle = new WindowInteropHelper(window).EnsureHandle();
            source = HwndSource.FromHwnd(handle);
            source.AddHook(WndProc);
        }

        public bool Register(uint modifiers, uint virtualKey, Action action)
        {
            int id = nextId++;
            if (!RegisterHotKey(source.Handle, id, modifiers | NoRepeat, virtualKey)) return false;
            actions[id] = action;
            return true;
        }

        /// <summary>Unregisters everything (used while a key is being recorded in the settings window).</summary>
        public void Clear()
        {
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
            foreach (var id in actions.Keys) UnregisterHotKey(source.Handle, id);
            actions.Clear();
            source.RemoveHook(WndProc);
        }
    }
}
