using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace PulseIsland.Services
{
    public class FullscreenDetectorService : IDisposable
    {
        private readonly Timer _timer;
        private bool _isCurrentlyFullscreen;
        private IntPtr _ownWindowHandle;

        public bool IsFullscreenActive => _isCurrentlyFullscreen;
        public event Action<bool>? FullscreenStateChanged;

        public FullscreenDetectorService()
        {
            _timer = new Timer(CheckForegroundWindow, null, Timeout.Infinite, Timeout.Infinite);
        }

        public void SetOwnWindow(IntPtr hWnd)
        {
            _ownWindowHandle = hWnd;
        }

        public void Start()
        {
            _timer.Change(1000, 1500);
        }

        public void Stop()
        {
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            if (_isCurrentlyFullscreen)
            {
                _isCurrentlyFullscreen = false;
                FullscreenStateChanged?.Invoke(false);
            }
        }

        private void CheckForegroundWindow(object? state)
        {
            try
            {
                IntPtr fgWindow = GetForegroundWindow();
                if (fgWindow == IntPtr.Zero || fgWindow == _ownWindowHandle)
                {
                    return;
                }

                IntPtr desktop = GetDesktopWindow();
                IntPtr shell = GetShellWindow();
                if (fgWindow == desktop || fgWindow == shell)
                {
                    UpdateState(false);
                    return;
                }

                if (GetWindowRect(fgWindow, out RECT appRect))
                {
                    IntPtr hMonitor = MonitorFromWindow(fgWindow, MONITOR_DEFAULTTONEAREST);
                    if (hMonitor != IntPtr.Zero)
                    {
                        var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf(typeof(MONITORINFO)) };
                        if (GetMonitorInfo(hMonitor, ref mi))
                        {
                            var monRect = mi.rcMonitor;
                            bool isFullscreen = (appRect.Left <= monRect.Left &&
                                                 appRect.Top <= monRect.Top &&
                                                 appRect.Right >= monRect.Right &&
                                                 appRect.Bottom >= monRect.Bottom);

                            UpdateState(isFullscreen);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Fullscreen detection error: {ex.Message}");
            }
        }

        private void UpdateState(bool isFullscreen)
        {
            if (_isCurrentlyFullscreen != isFullscreen)
            {
                _isCurrentlyFullscreen = isFullscreen;
                FullscreenStateChanged?.Invoke(isFullscreen);
            }
        }

        public void Dispose()
        {
            _timer.Dispose();
        }

        #region Win32
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public uint cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        private const uint MONITOR_DEFAULTTONEAREST = 2;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint dwFlags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
        #endregion
    }
}
