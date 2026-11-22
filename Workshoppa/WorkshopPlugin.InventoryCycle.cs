using System;
using System.Diagnostics;
using System.Linq;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Workshoppa;

partial class WorkshopPlugin
{
    private static readonly TimeSpan InventoryActionTimeout = TimeSpan.FromSeconds(5);
    private static readonly string[] InventoryAddons = ["Inventory", "InventoryLarge", "InventoryExpansion"];
    private long _inventoryActionStartedAt;
    private bool _inventoryCyclePending;

    private void BeginInventoryCycle()
    {
        _inventoryCyclePending = true;
        _inventoryActionStartedAt = Stopwatch.GetTimestamp();
        CurrentStage = Stage.OpenInventoryBetweenCrafts;
    }

    private unsafe void OpenInventoryBetweenCrafts()
    {
        if (!_configuration.RunnableQueue.Any())
        {
            _inventoryCyclePending = false;
            CurrentStage = Stage.TakeItemFromQueue;
            return;
        }

        if (GetVisibleInventory() == null && !GameWindowInput.TryTapInventoryKey(_pluginInterface.UiBuilder.WindowHandlePtr))
        {
            if (InventoryActionTimedOut())
                FailCraft("Workshoppa could not send the I key to the game to open the inventory.");
            return;
        }

        _inventoryActionStartedAt = Stopwatch.GetTimestamp();
        _continueAt = DateTime.Now.AddMilliseconds(250);
        CurrentStage = Stage.CloseInventoryBetweenCrafts;
    }

    private unsafe void CloseInventoryBetweenCrafts()
    {
        if (GetVisibleInventory() == null)
        {
            if (InventoryActionTimedOut())
                FailCraft("The inventory did not open after pressing I. Check your Inventory keybind and any open prompts.");
            return;
        }

        if (!GameWindowInput.TryTapInventoryKey(_pluginInterface.UiBuilder.WindowHandlePtr))
        {
            if (InventoryActionTimedOut())
                FailCraft("Workshoppa could not send the I key to the game to close the inventory.");
            return;
        }

        _inventoryActionStartedAt = Stopwatch.GetTimestamp();
        CurrentStage = Stage.WaitForInventoryClosed;
    }

    private unsafe void WaitForInventoryClosed()
    {
        if (GetVisibleInventory() != null)
        {
            if (InventoryActionTimedOut())
                FailCraft("The inventory did not close after pressing I. Close it, then resume.");
            return;
        }

        _inventoryCyclePending = false;
        _continueAt = DateTime.Now.AddMilliseconds(250);
        MarkCraftProgress();
        CurrentStage = Stage.TakeItemFromQueue;
    }

    private bool InventoryActionTimedOut() =>
        Stopwatch.GetElapsedTime(_inventoryActionStartedAt) >= InventoryActionTimeout;

    private unsafe AtkUnitBase* GetVisibleInventory()
    {
        foreach (string name in InventoryAddons)
        {
            var addon = _gameGui.GetAddonByName<AtkUnitBase>(name);
            if (addon != null && addon->IsVisible)
                return addon;
        }

        return null;
    }
}
