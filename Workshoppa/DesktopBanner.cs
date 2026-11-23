using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Workshoppa;

internal static class DesktopBanner
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(9);
    private const uint PopupStyles = 0x08000000 | 0x00000080 | 0x00000008; // No activate, tool window, topmost.
    private const uint Popup = 0x80000000;
    private const uint Border = 0x00800000;
    private const uint StaticWhiteRectangle = 6;
    private const uint VisibleChild = 0x50000000;
    private const uint SetFont = 0x30;
    private const uint TimerMessage = 0x113;

    public static nint GetForegroundWindow() => NativeMethods.GetForegroundWindow();

    public static void FlashTaskbar(nint window)
    {
        var flash = new FlashWindowInfo { Size = (uint)Marshal.SizeOf<FlashWindowInfo>(), Window = window, Flags = 2, Count = 3 };
        NativeMethods.FlashWindowEx(ref flash);
    }

    public static void Show(nint relativeTo, string title, string message, Func<bool> keepOpen, Action onShown)
    {
        if (!keepOpen())
            return;

        nint monitor = NativeMethods.MonitorFromWindow(relativeTo, 2);
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!NativeMethods.GetMonitorInfoW(monitor, ref info))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        uint dpi = NativeMethods.GetDpiForWindow(relativeTo);
        int Scale(int value) => (int)Math.Round(value * (dpi == 0 ? 96 : dpi) / 96.0);

        int width = Scale(416);
        int height = Scale(130);
        nint window = NativeMethods.CreateWindowExW(PopupStyles, "STATIC", title, Popup | Border | StaticWhiteRectangle,
            info.Work.Right - width - Scale(18), info.Work.Bottom - height - Scale(18),
            width, height, 0, 0, 0, 0);
        if (window == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        nint titleFont = 0;
        nint bodyFont = 0;
        try
        {
            titleFont = CreateFont(Scale(19), 600);
            bodyFont = CreateFont(Scale(16), 400);
            AddLabel(window, titleFont, title, Scale(16), Scale(14), width - Scale(32), Scale(26));
            string body = message.Length > 320 ? message[..319] + "…" : message;
            AddLabel(window, bodyFont, body, Scale(16), Scale(48), width - Scale(32), height - Scale(56));

            // Ask Windows to display the popup above the desktop without moving keyboard focus.
            const uint showWithoutMovingOrActivating = 0x40 | 0x10 | 0x02 | 0x01;
            if (NativeMethods.SetTimer(window, 1, 250, 0) == 0 ||
                !NativeMethods.SetWindowPos(window, -1, 0, 0, 0, 0, showWithoutMovingOrActivating))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            NativeMethods.UpdateWindow(window);
            if (!NativeMethods.IsWindowVisible(window))
                throw new InvalidOperationException("Windows created the Workshoppa popup but did not make it visible.");
            onShown();
            long shownAt = Stopwatch.GetTimestamp();
            while (keepOpen() && Stopwatch.GetElapsedTime(shownAt) < Lifetime)
            {
                int result = NativeMethods.GetMessageW(out var nativeMessage, 0, 0, 0);
                if (result <= 0)
                    break;
                if (nativeMessage.Id != TimerMessage || nativeMessage.Window != window)
                    NativeMethods.DispatchMessageW(ref nativeMessage);
            }
        }
        finally
        {
            NativeMethods.DestroyWindow(window);
            if (titleFont != 0)
                NativeMethods.DeleteObject(titleFont);
            if (bodyFont != 0)
                NativeMethods.DeleteObject(bodyFont);
        }
    }

    private static nint CreateFont(int height, int weight) =>
        NativeMethods.CreateFontW(-height, 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");

    private static void AddLabel(nint parent, nint font, string text, int x, int y, int width, int height)
    {
        // SS_NOPREFIX displays ampersands from item names literally.
        nint label = NativeMethods.CreateWindowExW(0, "STATIC", text, VisibleChild | 0x80, x, y, width, height,
            parent, 0, 0, 0);
        if (label == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (font != 0)
            NativeMethods.SendMessageW(label, SetFont, font, 1);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWindowInfo
    {
        public uint Size;
        public nint Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public nint Window;
        public uint Id;
        public nint WParam, LParam;
        public uint Time;
        public int X, Y;
        public uint Private;
    }

    private static class NativeMethods
    {
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", ExactSpelling = true)]
        internal static extern nint GetForegroundWindow();

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FlashWindowEx(ref FlashWindowInfo info);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", ExactSpelling = true)]
        internal static extern nint MonitorFromWindow(nint window, uint flags);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", ExactSpelling = true)]
        internal static extern uint GetDpiForWindow(nint window);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern nint CreateWindowExW(uint exStyle, string className, string text, uint style,
            int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int cx, int cy, uint flags);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UpdateWindow(nint window);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(nint window);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyWindow(nint window);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        internal static extern nuint SetTimer(nint window, nuint id, uint interval, nint callback);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", ExactSpelling = true)]
        internal static extern int GetMessageW(out NativeMessage message, nint window, uint first, uint last);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", ExactSpelling = true)]
        internal static extern nint DispatchMessageW(ref NativeMessage message);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", ExactSpelling = true)]
        internal static extern nint SendMessageW(nint window, uint message, nint wParam, nint lParam);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("gdi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        internal static extern nint CreateFontW(int height, int width, int escapement, int orientation, int weight,
            uint italic, uint underline, uint strikeout, uint charset, uint outPrecision, uint clipPrecision,
            uint quality, uint pitchAndFamily, string face);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("gdi32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteObject(nint instance);
    }
}
