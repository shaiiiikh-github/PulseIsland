using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PulseIsland.Services
{
    public static class WindowHelper
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_TRANSPARENT = 0x00000020;

        public static void SetToolWindow(Window window)
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;

            int exStyle = GetWindowLong(handle, GWL_EXSTYLE);
            SetWindowLong(handle, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);
        }

        public static void SetClickThrough(Window window, bool clickThrough)
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;

            int exStyle = GetWindowLong(handle, GWL_EXSTYLE);
            if (clickThrough)
            {
                SetWindowLong(handle, GWL_EXSTYLE, exStyle | WS_EX_TRANSPARENT);
            }
            else
            {
                SetWindowLong(handle, GWL_EXSTYLE, exStyle & ~WS_EX_TRANSPARENT);
            }
        }

        #region Win32 P/Invoke
        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        private static int GetWindowLong(IntPtr hWnd, int nIndex)
        {
            if (IntPtr.Size == 8)
                return (int)GetWindowLongPtr64(hWnd, nIndex).ToInt64();
            return GetWindowLong32(hWnd, nIndex);
        }

        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        private static int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong)
        {
            if (IntPtr.Size == 8)
                return (int)SetWindowLongPtr64(hWnd, nIndex, new IntPtr(dwNewLong)).ToInt64();
            return SetWindowLong32(hWnd, nIndex, dwNewLong);
        }
        #endregion
    }
}
