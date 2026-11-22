using System;
using System.Runtime.InteropServices;

namespace Workshoppa;

internal static class GameWindowInput
{
    private const uint InventoryKey = 0x49; // VK_I
    private const uint KeyDown = 0x0100;
    private const uint KeyUp = 0x0101;
    private const uint ReleasedKeyFlags = (1u << 30) | (1u << 31);

    public static bool TryTapInventoryKey(nint gameWindow)
    {
        if (!OperatingSystem.IsWindows() || gameWindow == 0)
            return false;

        uint scanCode = NativeMethods.MapVirtualKeyW(InventoryKey, 0);
        uint keyData = 1 | (scanCode << 16);
        if (!NativeMethods.PostMessageW(gameWindow, KeyDown, InventoryKey, (nint)keyData))
            return false;

        return NativeMethods.PostMessageW(gameWindow, KeyUp, InventoryKey, unchecked((nint)(int)(keyData | ReleasedKeyFlags)));
    }

    private static class NativeMethods
    {
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", ExactSpelling = true)]
        internal static extern uint MapVirtualKeyW(uint code, uint mapType);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostMessageW(nint window, uint message, nuint wParam, nint lParam);
    }
}
