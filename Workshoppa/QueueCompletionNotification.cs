using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;

namespace Workshoppa;

internal sealed class QueueCompletionNotification(Configuration configuration, IPluginLog log) : IDisposable
{
    private static readonly TimeSpan CollectionTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan IconLifetime = TimeSpan.FromSeconds(30);
    private const uint AddIcon = 0;
    private const uint ModifyIcon = 1;
    private const uint DeleteIcon = 2;
    private const uint SetIconVersion = 4;
    private const uint IconAndTooltip = 0x02 | 0x04;
    private const uint BalloonInfo = 0x10;
    private const uint InfoIcon = 1;

    private Collection? _collection;
    private long _shownAt;
    private nint _iconWindow;

    public void WatchCollection(uint resultItem)
    {
        if (resultItem != 0 && !configuration.ItemQueue.Any(item => item.Quantity > 0))
            _collection = new Collection(resultItem, RetainerTransfer.CountInInventory(resultItem), Stopwatch.GetTimestamp());
    }

    public void Update(bool loggedIn, nint gameWindow)
    {
        if (_iconWindow != 0 && Stopwatch.GetElapsedTime(_shownAt) >= IconLifetime)
            RemoveIcon();

        if (_collection is not { } collection)
            return;

        if (!loggedIn || !configuration.NotifyOnQueueCompletionInBackground ||
            configuration.CurrentlyCraftedItem != null || configuration.ItemQueue.Any(item => item.Quantity > 0) ||
            Stopwatch.GetElapsedTime(collection.StartedAt) >= CollectionTimeout)
        {
            _collection = null;
            return;
        }

        // The confirmation callback can succeed before the game has delivered the finished item.
        if (RetainerTransfer.CountInInventory(collection.ResultItem) <= collection.InventoryCount)
            return;

        _collection = null;
        if (OperatingSystem.IsWindows() && gameWindow != 0 && NativeMethods.GetForegroundWindow() != gameWindow)
            ShowNotification(gameWindow);
    }

    private void ShowNotification(nint gameWindow)
    {
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
        data.Title = "Workshoppa queue complete";
        data.Message = "All queued workshop crafts have been collected.";
        data.InfoFlags = InfoIcon;
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

    private readonly record struct Collection(uint ResultItem, int InventoryCount, long StartedAt);

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
        internal static extern nint LoadIconW(nint instance, nint resource);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("shell32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Shell_NotifyIconW(uint operation, ref NotifyIconData data);
    }
}
