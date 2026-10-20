using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AlpNetPulse
{
    /// <summary>
    /// Manages a global low-level keyboard hook for capturing hotkeys 
    /// even when the app is not focused (e.g., during fullscreen games).
    /// </summary>
    public class GlobalHotkeyManager : IDisposable
    {
        #region Win32 Interop
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }
        #endregion

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private IntPtr _hookId = IntPtr.Zero;
        private LowLevelKeyboardProc _hookProc; // prevent GC collection
        private int _hotkeyVirtualKey;
        private int _hotkeyModifiers;
        private bool _disposed = false;
        private bool _isCapturingKey = false;

        /// <summary>
        /// Fired when the configured hotkey is pressed.
        /// </summary>
        public event Action? HotkeyPressed;

        /// <summary>
        /// Fired when in capture mode, returns the virtual key code, modifiers bitmask, and display string.
        /// </summary>
        public event Action<int, int, string>? KeyCaptured;

        public GlobalHotkeyManager(int virtualKeyCode, int modifiers = 0)
        {
            _hotkeyVirtualKey = virtualKeyCode;
            _hotkeyModifiers = modifiers;
            _hookProc = HookCallback;
        }

        /// <summary>
        /// Starts the global keyboard hook.
        /// </summary>
        public void Start()
        {
            if (_hookId != IntPtr.Zero) return;

            IntPtr hMod = IntPtr.Zero;
            try
            {
                using var curProcess = Process.GetCurrentProcess();
                using var curModule = curProcess.MainModule;
                if (curModule?.ModuleName != null)
                {
                    hMod = GetModuleHandle(curModule.ModuleName);
                }
            }
            catch { }

            _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, hMod, 0);
        }

        /// <summary>
        /// Stops the global keyboard hook.
        /// </summary>
        public void Stop()
        {
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
        }

        /// <summary>
        /// Updates the hotkey.
        /// </summary>
        public void SetHotkey(int virtualKeyCode, int modifiers)
        {
            _hotkeyVirtualKey = virtualKeyCode;
            _hotkeyModifiers = modifiers;
        }

        /// <summary>
        /// Enters capture mode: the next key press will be reported via KeyCaptured 
        /// instead of triggering HotkeyPressed.
        /// </summary>
        public void StartCapturingKey()
        {
            _isCapturingKey = true;
        }

        /// <summary>
        /// Cancels capture mode.
        /// </summary>
        public void StopCapturingKey()
        {
            _isCapturingKey = false;
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && wParam == (IntPtr)WM_KEYDOWN)
            {
                var hookStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                int vk = (int)hookStruct.vkCode;

                bool ctrl = (GetAsyncKeyState(0x11) & 0x8000) != 0;
                bool shift = (GetAsyncKeyState(0x10) & 0x8000) != 0;
                bool alt = (GetAsyncKeyState(0x12) & 0x8000) != 0;
                
                int mods = (ctrl ? 1 : 0) | (shift ? 2 : 0) | (alt ? 4 : 0);

                if (_isCapturingKey)
                {
                    // Ignore if only a modifier key was pressed
                    if (vk == 0x10 || vk == 0x11 || vk == 0x12 || vk == 0xA0 || vk == 0xA1 || vk == 0xA2 || vk == 0xA3 || vk == 0xA4 || vk == 0xA5)
                        return CallNextHookEx(_hookId, nCode, wParam, lParam);

                    _isCapturingKey = false;
                    string keyName = VirtualKeyToString(vk);
                    
                    string fullName = "";
                    if (ctrl) fullName += "Ctrl + ";
                    if (shift) fullName += "Shift + ";
                    if (alt) fullName += "Alt + ";
                    fullName += keyName;

                    KeyCaptured?.Invoke(vk, mods, fullName);
                }
                else if (vk == _hotkeyVirtualKey && mods == _hotkeyModifiers)
                {
                    HotkeyPressed?.Invoke();
                }
            }

            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        /// <summary>
        /// Converts a virtual key code to a display-friendly string.
        /// </summary>
        public static string VirtualKeyToString(int vk)
        {
            return vk switch
            {
                0x70 => "F1",
                0x71 => "F2",
                0x72 => "F3",
                0x73 => "F4",
                0x74 => "F5",
                0x75 => "F6",
                0x76 => "F7",
                0x77 => "F8",
                0x78 => "F9",
                0x79 => "F10",
                0x7A => "F11",
                0x7B => "F12",
                0x2C => "PrintScreen",
                0x91 => "ScrollLock",
                0x13 => "Pause",
                0x2D => "Insert",
                0x24 => "Home",
                0x23 => "End",
                0x21 => "PageUp",
                0x22 => "PageDown",
                0x14 => "CapsLock",
                0x90 => "NumLock",
                >= 0x60 and <= 0x69 => $"Numpad{vk - 0x60}",
                >= 0x30 and <= 0x39 => $"{(char)vk}",
                >= 0x41 and <= 0x5A => $"{(char)vk}",
                _ => $"Key({vk})"
            };
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                Stop();
                _disposed = true;
            }
            GC.SuppressFinalize(this);
        }

        ~GlobalHotkeyManager()
        {
            Dispose();
        }
    }
}
