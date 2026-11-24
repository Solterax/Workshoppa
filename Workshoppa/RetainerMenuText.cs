using System;

namespace Workshoppa;

internal static class RetainerMenuText
{
    internal static bool MatchesInventoryOption(string displayedText, string localizedText)
    {
        var expected = WithoutSlotCount(localizedText);
        return !expected.IsEmpty && WithoutSlotCount(displayedText).SequenceEqual(expected);
    }

    private static ReadOnlySpan<char> WithoutSlotCount(string text)
    {
        var label = text.AsSpan();
        int suffix = label.IndexOfAny("([（［");
        return (suffix >= 0 ? label[..suffix] : label).Trim();
    }
}
