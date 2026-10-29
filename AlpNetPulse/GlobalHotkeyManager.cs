using System;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using WpfApp = System.Windows.Application;

namespace AlpNetPulse
{
    /// <summary>
    /// Manages a global hotkey using the official Win32 RegisterHotKey API.
    /// Does NOT hook the keyboard (avoids EAC/BattlEye/Vanguard keylogger false-positives).
    /// </summary>
    public class GlobalHotkeyManager : IDisposable
    {
        #region Win32 API
        private const int WM_HOTKEY = 0x0312;
        private const int HOTKEY_ID = 9000;

        // Win32 Modifiers
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_WIN = 0x0008;
        private const uint MOD_NOREPEAT = 0x4000;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        #endregion

        private IntPtr _hWnd = IntPtr.Zero;
        private HwndSource? _hwndSource;
        private HwndSourceHook? _hookDelegate;
        private int _hotkeyVirtualKey;
        private int _hotkeyModifiers; // AlpNet convention: 1=Ctrl, 2=Shift, 4=Alt
        private bool _isRegistered = false;
        private bool _isCapturingKey = false;
        private bool _disposed = false;

        public event Action? HotkeyPressed;
        public event Action<int, int, string>? KeyCaptured;

        public GlobalHotkeyManager(int virtualKeyCode, int modifiers = 0, IntPtr hWnd = default)
        {
            _hotkeyVirtualKey = virtualKeyCode;
            _hotkeyModifiers = modifiers;
            _hWnd = hWnd;

            InitHwndSource();
        }

        private void InitHwndSource()
        {
            try
            {
                if (_hWnd == IntPtr.Zero && WpfApp.Current?.MainWindow != null)
                {
                    _hWnd = new WindowInteropHelper(WpfApp.Current.MainWindow).Handle;
                }

                if (_hWnd != IntPtr.Zero && _hwndSource == null)
                {
                    _hwndSource = HwndSource.FromHwnd(_hWnd);
                    if (_hwndSource != null)
                    {
                        _hookDelegate = WndProc;
                        _hwndSource.AddHook(_hookDelegate);
                    }
                }
            }
            catch { }
        }

        public void Start()
        {
            if (_isRegistered) return;

            if (_hWnd == IntPtr.Zero || _hwndSource == null)
            {
                InitHwndSource();
            }

            RegisterCurrentHotkey();
        }

        public void Stop()
        {
            UnregisterCurrentHotkey();
        }

        public void SetHotkey(int virtualKeyCode, int modifiers)
        {
            bool wasRegistered = _isRegistered;
            UnregisterCurrentHotkey();

            _hotkeyVirtualKey = virtualKeyCode;
            _hotkeyModifiers = modifiers;

            if (wasRegistered)
            {
                RegisterCurrentHotkey();
            }
        }

        private void RegisterCurrentHotkey()
        {
            if (_hotkeyVirtualKey <= 0 || _hWnd == IntPtr.Zero) return;

            uint win32Mods = ConvertToWin32Modifiers(_hotkeyModifiers) | MOD_NOREPEAT;
            _isRegistered = RegisterHotKey(_hWnd, HOTKEY_ID, win32Mods, (uint)_hotkeyVirtualKey);
        }

        private void UnregisterCurrentHotkey()
        {
            if (_isRegistered && _hWnd != IntPtr.Zero)
            {
                try
                {
                    UnregisterHotKey(_hWnd, HOTKEY_ID);
                }
                catch { }
                _isRegistered = false;
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
            {
                HotkeyPressed?.Invoke();
                handled = true;
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// Converts AlpNet modifier flags (1=Ctrl, 2=Shift, 4=Alt) to Win32 RegisterHotKey modifier flags.
        /// </summary>
        private static uint ConvertToWin32Modifiers(int alpnetMods)
        {
            uint win32Mods = 0;
            if ((alpnetMods & 1) != 0) win32Mods |= MOD_CONTROL;
            if ((alpnetMods & 2) != 0) win32Mods |= MOD_SHIFT;
            if ((alpnetMods & 4) != 0) win32Mods |= MOD_ALT;
            return win32Mods;
        }

        /// <summary>
        /// Converts Win32 modifier flags to AlpNet modifier flags.
        /// </summary>
        private static int ConvertFromWin32Modifiers(ModifierKeys keys)
        {
            int alpnetMods = 0;
            if ((keys & ModifierKeys.Control) != 0) alpnetMods |= 1;
            if ((keys & ModifierKeys.Shift) != 0) alpnetMods |= 2;
            if ((keys & ModifierKeys.Alt) != 0) alpnetMods |= 4;
            return alpnetMods;
        }

        public void StartCapturingKey()
        {
            _isCapturingKey = true;
            if (WpfApp.Current?.MainWindow != null)
            {
                WpfApp.Current.MainWindow.PreviewKeyDown -= OnWindowPreviewKeyDown;
                WpfApp.Current.MainWindow.PreviewKeyDown += OnWindowPreviewKeyDown;
            }
        }

        public void StopCapturingKey()
        {
            _isCapturingKey = false;
            if (WpfApp.Current?.MainWindow != null)
            {
                WpfApp.Current.MainWindow.PreviewKeyDown -= OnWindowPreviewKeyDown;
            }
        }

        private void OnWindowPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (!_isCapturingKey) return;

            Key key = (e.Key == Key.System ? e.SystemKey : e.Key);

            // Skip bare modifiers so the user can combine them (e.g., holding Ctrl before pressing F9)
            if (key == Key.LeftCtrl || key == Key.RightCtrl ||
                key == Key.LeftShift || key == Key.RightShift ||
                key == Key.LeftAlt || key == Key.RightAlt ||
                key == Key.LWin || key == Key.RWin)
            {
                return;
            }

            StopCapturingKey();
            e.Handled = true;

            int vk = KeyInterop.VirtualKeyFromKey(key);
            int mods = ConvertFromWin32Modifiers(Keyboard.Modifiers);

            bool ctrl = (mods & 1) != 0;
            bool shift = (mods & 2) != 0;
            bool alt = (mods & 4) != 0;

            string keyName = VirtualKeyToString(vk);
            string fullName = "";
            if (ctrl) fullName += "Ctrl + ";
            if (shift) fullName += "Shift + ";
            if (alt) fullName += "Alt + ";
            fullName += keyName;

            KeyCaptured?.Invoke(vk, mods, fullName);
        }

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
                StopCapturingKey();
                UnregisterCurrentHotkey();

                if (_hwndSource != null && _hookDelegate != null)
                {
                    try
                    {
                        _hwndSource.RemoveHook(_hookDelegate);
                    }
                    catch { }
                    _hwndSource = null;
                }

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
