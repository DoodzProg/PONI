using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Poni.Services
{
    /// <summary>Desktop Window Manager tweaks. Every call is best-effort: older Windows builds simply ignore them.</summary>
    internal static class NativeMethods
    {
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>Dark window border/shadow (Windows 10 20H1+ / Windows 11).</summary>
        public static void SetDarkFrame(Window window, bool dark)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int value = dark ? 1 : 0;
            try { DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int)); } catch { }
        }

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hwnd, int command);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hwnd);

        private const int SW_RESTORE = 9;

        /// <summary>Single instance: bring the already-open PONI window to the front (restored if minimized).</summary>
        public static void BringToFront(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
            }
            catch { }
        }

        /// <summary>Rounded window corners (Windows 11; no effect on Windows 10).</summary>
        public static void SetRoundCorners(Window window)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int value = DWMWCP_ROUND;
            try { DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref value, sizeof(int)); } catch { }
        }
    }
}
