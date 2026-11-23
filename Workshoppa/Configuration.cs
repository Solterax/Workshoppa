using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Configuration;
using LLib.ImGui;
using Workshoppa.GameData;

namespace Workshoppa;

internal sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public CurrentItem? CurrentlyCraftedItem { get; set; }
    public List<QueuedItem> ItemQueue { get; set; } = new();
    internal IEnumerable<QueuedItem> RunnableQueue => ItemQueue.Where(item => item.IsRunnable);
    public bool EnableRepairKitCalculator { get; set; } = true;
    public bool EnableCeruleumTankCalculator { get; set; } = true;
    public bool NotifyOnQueueCompletionInBackground { get; set; }
    public bool UseWorkshoppaDesktopPopup { get; set; }
    public List<Preset> Presets { get; set; } = new();
    public Dictionary<uint, CraftTimingStats> CraftTimes { get; set; } = new();

    public WindowConfig MainWindowConfig { get; } = new();
    public WindowConfig ConfigWindowConfig { get; } = new();

    internal uint? TakeNextQueuedItem()
    {
        for (int index = 0; index < ItemQueue.Count;)
        {
            var item = ItemQueue[index];
            if (item.IsPaused)
            {
                index++;
                continue;
            }

            if (!item.IsRunnable)
            {
                ItemQueue.RemoveAt(index);
                continue;
            }

            if (--item.Quantity == 0)
                ItemQueue.RemoveAt(index);
            return item.WorkshopItemId;
        }

        return null;
    }

    internal sealed class QueuedItem
    {
        public uint WorkshopItemId { get; set; }
        public int Quantity { get; set; }
        public bool IsPaused { get; set; }
        internal bool IsRunnable => !IsPaused && Quantity > 0;
    }

    internal sealed class CurrentItem
    {
        public uint WorkshopItemId { get; set; }
        public bool StartedCrafting { get; set; }
        public double? ActiveCraftSeconds { get; set; }
        public bool CraftTimerRunning { get; set; }

        public uint PhasesComplete { get; set; }
        public List<PhaseItem> ContributedItemsInCurrentPhase { get; set; } = new();

        public bool UpdateFromCraftState(CraftState craftState)
        {
            bool changed = false;
            if (PhasesComplete != craftState.StepsComplete)
            {
                PhasesComplete = craftState.StepsComplete;
                changed = true;
            }

            if (ContributedItemsInCurrentPhase.Count != craftState.Items.Count)
            {
                ContributedItemsInCurrentPhase = craftState.Items.Select(x => new PhaseItem
                {
                    ItemId = x.ItemId,
                    QuantityComplete = x.QuantityComplete,
                }).ToList();
                changed = true;
            }
            else
            {
                for (int i = 0; i < ContributedItemsInCurrentPhase.Count; ++i)
                {
                    var contributedItem = ContributedItemsInCurrentPhase[i];
                    var craftItem = craftState.Items[i];
                    if (contributedItem.ItemId != craftItem.ItemId)
                    {
                        contributedItem.ItemId = craftItem.ItemId;
                        changed = true;
                    }

                    if (contributedItem.QuantityComplete != craftItem.QuantityComplete)
                    {
                        contributedItem.QuantityComplete = craftItem.QuantityComplete;
                        changed = true;
                    }
                }
            }

            return changed;
        }
    }

    internal sealed class CraftTimingStats
    {
        public long CompletedCrafts { get; set; }
        public double TotalActiveSeconds { get; set; }
        public double GetAverageSeconds() => TotalActiveSeconds / CompletedCrafts;
    }

    internal sealed class PhaseItem
    {
        public uint ItemId { get; set; }
        public uint QuantityComplete { get; set; }
    }

    internal sealed class Preset
    {
        public required Guid Id { get; set; }
        public required string Name { get; set; }
        public List<QueuedItem> ItemQueue { get; set; } = new();
    }
}
