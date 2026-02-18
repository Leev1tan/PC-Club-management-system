using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Cms.Launcher
{
    /// <summary>
    /// Low-level keyboard hook that blocks Alt+Tab, Alt+F4, Win key, Ctrl+Esc
    /// when kiosk mode is active. Staff can still use Ctrl+Shift+U to unlock.
    /// </summary>
    public sealed class KioskKeyboardHook : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;

        // Virtual key codes
        private const int VK_TAB = 0x09;
        private const int VK_ESCAPE = 0x1B;
        private const int VK_F4 = 0x73;
        private const int VK_LWIN = 0x5B;
        private const int VK_RWIN = 0x5C;
        private const int VK_DELETE = 0x2E;

        // Modifier flags in lParam
        private const int LLKHF_ALTDOWN = 0x20;

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private IntPtr _hookId = IntPtr.Zero;
        private readonly LowLevelKeyboardProc _proc;
        private bool _disposed;

        public bool IsEnabled { get; set; } = true;

        public KioskKeyboardHook()
        {
            _proc = HookCallback;
        }

        public void Install()
        {
            if (_hookId != IntPtr.Zero) return;

            using var curProcess = Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule!;
            _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(curModule.ModuleName!), 0);
        }

        public void Uninstall()
        {
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && IsEnabled)
            {
                var hookStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                var vkCode = hookStruct.vkCode;
                var isAlt = (hookStruct.flags & LLKHF_ALTDOWN) != 0;
                var msg = (int)wParam;

                // Block Win key (left and right)
                if (vkCode == VK_LWIN || vkCode == VK_RWIN)
                    return (IntPtr)1;

                // Block Alt+Tab
                if (isAlt && vkCode == VK_TAB)
                    return (IntPtr)1;

                // Block Alt+F4
                if (isAlt && vkCode == VK_F4)
                    return (IntPtr)1;

                // Block Alt+Escape
                if (isAlt && vkCode == VK_ESCAPE)
                    return (IntPtr)1;

                // Block Ctrl+Escape (Start menu)
                if (vkCode == VK_ESCAPE && (NativeMethods.GetAsyncKeyState(0x11) & 0x8000) != 0) // VK_CONTROL
                    return (IntPtr)1;
            }

            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                Uninstall();
                _disposed = true;
            }
        }
    }

    internal static class NativeMethods
    {
        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);
    }
}
