using System;

namespace Workshoppa;

internal sealed class CraftTiming(Configuration configuration, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private Configuration.CurrentItem? _runningItem;
    private long _startedAt;

    public bool DiscardInterruptedSample()
    {
        if (configuration.CurrentlyCraftedItem is not { CraftTimerRunning: true } item)
            return false;

        item.CraftTimerRunning = false;
        item.ActiveCraftSeconds = null;
        return true;
    }

    public bool SetActive(bool active)
    {
        if (!active || configuration.CurrentlyCraftedItem is not { ActiveCraftSeconds: not null } next)
            return Stop();

        if (next == _runningItem)
            return false;

        Stop();
        _runningItem = next;
        _startedAt = _clock.GetTimestamp();
        next.CraftTimerRunning = true;
        return true;
    }

    private bool Stop()
    {
        if (_runningItem == null)
            return false;

        _runningItem.ActiveCraftSeconds = GetElapsedSeconds(_runningItem);
        _runningItem.CraftTimerRunning = false;
        _runningItem = null;
        return true;
    }

    public double? GetElapsedSeconds(Configuration.CurrentItem item) =>
        item.ActiveCraftSeconds + (item == _runningItem ? _clock.GetElapsedTime(_startedAt).TotalSeconds : 0);

    public void RecordCompletion()
    {
        Stop();
        if (configuration.CurrentlyCraftedItem is not { ActiveCraftSeconds: > 0 } item)
            return;

        if (!configuration.CraftTimes.TryGetValue(item.WorkshopItemId, out var stats))
        {
            stats = new Configuration.CraftTimingStats();
            configuration.CraftTimes.Add(item.WorkshopItemId, stats);
        }

        stats.CompletedCrafts++;
        stats.TotalActiveSeconds += item.ActiveCraftSeconds.Value;
    }
}
