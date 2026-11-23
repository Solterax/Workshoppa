using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using Workshoppa.External;

namespace Workshoppa;

internal sealed class QueueCompletionNotification(Configuration configuration, IPluginLog log,
    NotificationMasterIpc notificationMaster) : IDisposable
{
    private static readonly TimeSpan IconLifetime = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TestDelay = TimeSpan.FromSeconds(3);
    private const uint AddIcon = 0;
    private const uint ModifyIcon = 1;
    private const uint DeleteIcon = 2;
    private const uint SetIconVersion = 4;
    private const uint IconAndTooltip = 0x02 | 0x04;
    private const uint BalloonInfo = 0x10;
    private const uint InfoIcon = 1;
    private const uint WarningIcon = 2;
    private const uint FlashTaskbar = 2;

    private long _shownAt;
    private nint _iconWindow;
    private (nint Window, long RequestedAt)? _test;

    public bool UsesNotificationMaster => notificationMaster.IsAvailable;

    public void Completed(nint gameWindow, bool pausedCraftsRemain) =>
        ShowIfBackground(gameWindow, "Workshoppa queue complete", pausedCraftsRemain
            ? "All unpaused workshop crafts have been collected. Paused crafts remain in the queue."
            : "All queued workshop crafts have been collected.", InfoIcon);

    public void Failed(nint gameWindow, string reason) =>
        ShowIfBackground(gameWindow, "Workshoppa queue stopped", reason, WarningIcon);

    public void ScheduleTest(nint gameWindow)
    {
        if (OperatingSystem.IsWindows())
            _test = (gameWindow, Stopwatch.GetTimestamp());
    }

    public void Update()
    {
        if (_iconWindow != 0 && Stopwatch.GetElapsedTime(_shownAt) >= IconLifetime)
            RemoveIcon();

        if (_test is { } test && Stopwatch.GetElapsedTime(test.RequestedAt) >= TestDelay)
        {
            _test = null;
            ShowNotification(test.Window, "Workshoppa desktop alert test",
                "This is how a stopped workshop queue alerts you.", WarningIcon);
        }
    }

    private void ShowIfBackground(nint gameWindow, string title, string message, uint severity)
    {
        if (configuration.NotifyOnQueueCompletionInBackground && IsGameInBackground(gameWindow))
        {
            _test = null;
            ShowNotification(gameWindow, title, message, severity);
        }
    }

    private bool IsGameInBackground(nint gameWindow)
    {
        if (notificationMaster.IsGameWindowActive() is { } isActive)
            return !isActive;

        return OperatingSystem.IsWindows() && gameWindow != 0 && NativeMethods.GetForegroundWindow() != gameWindow;
    }

    private void ShowNotification(nint gameWindow, string title, string message, uint severity)
    {
        if (severity == WarningIcon && IsGameInBackground(gameWindow) && !notificationMaster.TryFlashTaskbar() &&
            OperatingSystem.IsWindows() && gameWindow != 0)
        {
            var flash = new FlashWindowInfo
            {
                Size = (uint)Marshal.SizeOf<FlashWindowInfo>(),
                Window = gameWindow,
                Flags = FlashTaskbar,
                Count = 3,
            };
            NativeMethods.FlashWindowEx(ref flash);
        }

        if (notificationMaster.TryShowToast(title, message) || !OperatingSystem.IsWindows() || gameWindow == 0)
            return;

        RemoveIcon();
        var data = CreateIconData(gameWindow);
        data.Flags = IconAndTooltip;
        data.Icon = NativeMethods.LoadIconW(0, 32516); // IDI_INFORMATION, shared by Windows.
        data.Tooltip = "Workshoppa";
        if (!NativeMethods.Shell_NotifyIconW(AddIcon, ref data))
        {
            log.Warning("Windows could not create the Workshoppa notification icon.");
            return;
        }

        _iconWindow = gameWindow;
        _shownAt = Stopwatch.GetTimestamp();
        data.Version = 4;
        NativeMethods.Shell_NotifyIconW(SetIconVersion, ref data);

        data.Flags = BalloonInfo;
        data.Title = title;
        data.Message = message.Length > 255 ? message[..254] + "…" : message;
        data.InfoFlags = severity;
        if (!NativeMethods.Shell_NotifyIconW(ModifyIcon, ref data))
        {
            log.Warning("Windows could not display the Workshoppa queue notification.");
            RemoveIcon();
        }
    }

    private void RemoveIcon()
    {
        if (_iconWindow == 0)
            return;

        var data = CreateIconData(_iconWindow);
        NativeMethods.Shell_NotifyIconW(DeleteIcon, ref data);
        _iconWindow = 0;
    }

    private static NotifyIconData CreateIconData(nint window) => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        Window = window,
        Id = 0x5750,
    };

    public void Dispose() => RemoveIcon();

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWindowInfo
    {
        public uint Size;
        public nint Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string? Tooltip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string? Message;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string? Title;
        public uint InfoFlags;
        public Guid Guid;
        public nint BalloonIcon;
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
        internal static extern nint LoadIconW(nint instance, nint resource);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("shell32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Shell_NotifyIconW(uint operation, ref NotifyIconData data);
    }
}
