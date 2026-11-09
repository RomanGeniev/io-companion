using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GraphiteCompanion.Services
{
    /// <summary>
    /// Takes Win+G away from the Xbox Game Bar: a low-level keyboard hook sees the key before Windows does,
    /// swallows G while a Win key is held and raises <see cref="Pressed"/>. A dummy key is injected so that
    /// releasing Win afterwards doesn't open the Start menu (the same trick PowerToys uses).
    /// </summary>
    public sealed class WinGHook : IDisposable
    {
        private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct KbdLlHook { public uint vkCode, scanCode, flags, time; public IntPtr extra; }

        [StructLayout(LayoutKind.Sequential)]
        private struct Input { public uint type; public KeybdInput ki; public long pad; }

        [StructLayout(LayoutKind.Sequential)]
        private struct KeybdInput { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr mod, uint thread);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
        [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? name);

        private const int WhKeyboardLl = 13, WmKeyDown = 0x100, WmSysKeyDown = 0x104;
        private const uint VkG = 0x47, VkLWin = 0x5B, VkRWin = 0x5C, LlkhfInjected = 0x10;
        private static readonly IntPtr Marker = new(0x10C0); // dwExtraInfo of our own injected key

        private readonly HookProc proc;   // kept alive: the hook holds only a native pointer
        private IntPtr hook;

        public event Action? Pressed;
        public bool Active => hook != IntPtr.Zero;

        public WinGHook()
        {
            proc = Callback;
            using var self = Process.GetCurrentProcess();
            hook = SetWindowsHookEx(WhKeyboardLl, proc, GetModuleHandle(self.MainModule?.ModuleName), 0);
        }

        private IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0 && (wParam == (IntPtr)WmKeyDown || wParam == (IntPtr)WmSysKeyDown))
            {
                var k = Marshal.PtrToStructure<KbdLlHook>(lParam);
                bool ours = (k.flags & LlkhfInjected) != 0 && k.extra == Marker;
                if (!ours && k.vkCode == VkG && WinDown())
                {
                    SuppressStartMenu();
                    // handled asynchronously: a hook must return quickly or Windows drops it
                    Pressed?.Invoke();
                    return (IntPtr)1; // swallow: neither Windows nor the Game Bar sees Win+G
                }
            }
            return CallNextHookEx(hook, code, wParam, lParam);
        }

        private static bool WinDown() =>
            (GetAsyncKeyState((int)VkLWin) & 0x8000) != 0 || (GetAsyncKeyState((int)VkRWin) & 0x8000) != 0;

        /// <summary>A harmless unassigned key press between Win down and Win up keeps Start closed.</summary>
        private static void SuppressStartMenu()
        {
            var inputs = new[]
            {
                new Input { type = 1, ki = new KeybdInput { wVk = 0xE8, dwExtraInfo = Marker } },
                new Input { type = 1, ki = new KeybdInput { wVk = 0xE8, dwFlags = 2, dwExtraInfo = Marker } },
            };
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        }

        public void Dispose()
        {
            if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
        }
    }
}
