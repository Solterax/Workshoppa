using System;
using System.Diagnostics;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using Workshoppa.GameData;
using Workshoppa.Windows;

namespace Workshoppa;

partial class WorkshopPlugin
{
    private static readonly TimeSpan CraftStallTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CutsceneStallTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan CollectionTimeout = TimeSpan.FromSeconds(10);

    private ProductCollection? _collection;
    private long _lastCraftProgress;
    private uint _observedPhase;
    private long _observedContribution;

    private void ResetCraftProgress()
    {
        _observedPhase = 0;
        _observedContribution = -1;
        MarkCraftProgress();
    }

    private void MarkCraftProgress() => _lastCraftProgress = Stopwatch.GetTimestamp();

    private void NoteMaterialProgress(CraftState craft)
    {
        long contribution = craft.Items.Sum(item => (long)item.QuantityComplete);
        if (craft.StepsComplete < _observedPhase ||
            (craft.StepsComplete == _observedPhase && contribution <= _observedContribution))
            return;

        _observedPhase = craft.StepsComplete;
        _observedContribution = contribution;
        MarkCraftProgress();
    }

    private bool CraftProgressTimedOut()
    {
        if (CurrentStage is Stage.Stopped or Stage.RequestStop)
            return false;

        var timeout = _condition[ConditionFlag.WatchingCutscene] || _condition[ConditionFlag.WatchingCutscene78]
            ? CutsceneStallTimeout
            : CraftStallTimeout;
        return Stopwatch.GetElapsedTime(_lastCraftProgress) >= timeout;
    }

    private bool CanBeginProductCollection(string choice)
    {
        if (choice != "Collect finished product.")
            return false;

        var craft = GetCurrentCraft();
        if (!HasRoomForProduct(craft))
        {
            FailCraft(InventoryFullMessage(craft));
            return false;
        }

        CurrentStage = Stage.ConfirmCollectProduct;
        _continueAt = DateTime.Now.AddSeconds(0.25);
        return true;
    }

    private void CheckCollectedProduct()
    {
        if (!_clientState.IsLoggedIn)
        {
            FailCraft("The character logged out or disconnected while collecting the finished product.");
            return;
        }

        if (_collection is not { } collection)
        {
            FailCraft("Workshoppa lost track of the finished product.");
            return;
        }

        if (RetainerTransfer.CountInInventory(collection.Craft.ResultItem) > collection.PreviousCount)
        {
            _collection = null;
            ConfirmCollectProductFollowUp();
        }
        else if (Stopwatch.GetElapsedTime(collection.StartedAt) >= CollectionTimeout)
        {
            FailCraft(HasRoomForProduct(collection.Craft)
                ? $"The game did not deliver {collection.Craft.Name}. Check for an open game prompt and resume."
                : InventoryFullMessage(collection.Craft));
        }
    }

    private static string InventoryFullMessage(WorkshopCraft craft) =>
        $"Inventory full: make room for {craft.Name}, then resume.";

    private static unsafe bool HasRoomForProduct(WorkshopCraft craft)
    {
        var inventory = InventoryManager.Instance();
        if (inventory == null)
            return true;

        for (InventoryType page = InventoryType.Inventory1; page <= InventoryType.Inventory4; ++page)
        {
            var container = inventory->GetInventoryContainer(page);
            if (container == null || !container->IsLoaded)
                return true;

            for (int index = 0; index < container->Size; ++index)
            {
                var item = container->GetInventorySlot(index);
                if (item == null || item->ItemId == 0 ||
                    (item->GetBaseItemId() == craft.ResultItem && item->Quantity < craft.ResultStackSize &&
                     (item->Flags & InventoryItem.ItemFlags.HighQuality) == 0 && !item->IsCollectable()))
                    return true;
            }
        }

        return false;
    }

    private void FailCraft(string reason)
    {
        StopCraft();
        _pluginLog.Warning(reason);
        if (_clientState.IsLoggedIn)
            _chatGui.PrintError($"[Workshoppa] {reason}");
        _queueNotification.Failed(_pluginInterface.UiBuilder.WindowHandlePtr, reason);
    }

    private void StopCraft()
    {
        bool wasActive = CurrentStage != Stage.Stopped;
        _mainWindow.State = MainWindow.ButtonState.None;
        _collection = null;
        _continueAt = DateTime.MinValue;
        CurrentStage = Stage.Stopped;
        if (wasActive)
        {
            _externalPluginHandler.RestoreTextAdvance();
            _externalPluginHandler.Restore();
        }
    }

    private readonly record struct ProductCollection(WorkshopCraft Craft, int PreviousCount, long StartedAt);
}
