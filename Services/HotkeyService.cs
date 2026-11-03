using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace PulseIsland.Services
{
    public class HotkeyService : IDisposable
    {
        private const int WM_HOTKEY = 0x0312;
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_NOREPEAT = 0x4000;

        private const uint VK_I = 0x49;
        private const uint VK_P = 0x50;

        private const int HOTKEY_ID_CLICKTHROUGH = 9001;
        private const int HOTKEY_ID_VISIBILITY = 9002;

        private IntPtr _hWnd;
        private HwndSource? _source;
        private bool _isRegistered;

        public event Action? ClickThroughHotkeyPressed;
        public event Action? VisibilityHotkeyPressed;

        public void Register(IntPtr hWnd)
        {
            _hWnd = hWnd;
            _source = HwndSource.FromHwnd(_hWnd);
            _source?.AddHook(HwndHook);

            bool r1 = RegisterHotKey(_hWnd, HOTKEY_ID_CLICKTHROUGH, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_I);
            bool r2 = RegisterHotKey(_hWnd, HOTKEY_ID_VISIBILITY, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_P);

            _isRegistered = r1 || r2;
        }

        public void Unregister()
        {
            if (_isRegistered && _hWnd != IntPtr.Zero)
            {
                UnregisterHotKey(_hWnd, HOTKEY_ID_CLICKTHROUGH);
                UnregisterHotKey(_hWnd, HOTKEY_ID_VISIBILITY);
                _isRegistered = false;
            }

            if (_source != null)
            {
                _source.RemoveHook(HwndHook);
                _source = null;
            }
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                if (id == HOTKEY_ID_CLICKTHROUGH)
                {
                    ClickThroughHotkeyPressed?.Invoke();
                    handled = true;
                }
                else if (id == HOTKEY_ID_VISIBILITY)
                {
                    VisibilityHotkeyPressed?.Invoke();
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            Unregister();
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}
