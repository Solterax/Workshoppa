using System;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Workshoppa;

internal static class RetainerListSelection
{
    // The RetainerList addon has three header values and up to ten rows of ten values.
    private const int FirstRow = 3;
    private const int RowLength = 10;
    private const int ActiveOffset = 8;
    private const int MaxRows = 10;

    internal static uint? FindIndex(ReadOnlySpan<AtkValue> values, string name, out bool hasEntries)
    {
        hasEntries = false;
        for (uint index = 0; index < MaxRows; index++)
        {
            int row = FirstRow + (int)index * RowLength;
            if (row + ActiveOffset >= values.Length)
                break;

            ref readonly var nameValue = ref values[row];
            if (nameValue.Type is not (AtkValueType.String or AtkValueType.ConstString or
                AtkValueType.ManagedString or AtkValueType.WideString))
                continue;

            string displayedName = nameValue.GetValueAsString();
            if (string.IsNullOrEmpty(displayedName))
                continue;

            hasEntries = true;
            ref readonly var active = ref values[row + ActiveOffset];
            if (active.Type == AtkValueType.Bool && active.Byte != 0 &&
                string.Equals(displayedName, name.Trim(), StringComparison.OrdinalIgnoreCase))
                return index;
        }

        return null;
    }

    internal static unsafe void Select(AtkUnitBase* addon, uint index)
    {
        var args = stackalloc AtkValue[]
        {
            new() { Type = AtkValueType.Int, Int = 2 },
            new() { Type = AtkValueType.UInt, UInt = index },
            default,
            default,
        };
        addon->FireCallback(4, args, true);
    }
}
