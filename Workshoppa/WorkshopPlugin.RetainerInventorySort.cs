using System;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace Workshoppa;

partial class WorkshopPlugin
{
    private unsafe void OpenInventoryForDeposit(RetainerTrip trip)
    {
        if (_condition[ConditionFlag.OccupiedInEvent] || _condition[ConditionFlag.OccupiedSummoningBell] ||
            GetVisibleInventoryCycleAddon("InventoryRetainer") != null ||
            GetVisibleInventoryCycleAddon("InventoryRetainerLarge") != null ||
            GetVisibleInventoryCycleAddon("RetainerList") != null ||
            GetVisibleInventoryCycleAddon("SelectString") != null)
            return;

        if (GetVisibleInventory() == null && !TapInventoryKey())
            return;

        trip.GoTo(TripStep.SortPlayerInventory);
        _continueAt = DateTime.Now.AddMilliseconds(250);
    }

    private unsafe void SortInventoryForDeposit(RetainerTrip trip)
    {
        var inventory = GetVisibleInventory();
        var ui = UIModule.Instance();
        var orderModule = ItemOrderModule.Instance();
        if (inventory == null || !inventory->IsReady || ui == null || orderModule == null ||
            orderModule->InventorySorter == null || orderModule->InventorySorter->SortFunctionIndex >= 0)
            return;

        RunInventorySortCommand(ui, "/itemsort condition inventory id asc");
        RunInventorySortCommand(ui, "/itemsort execute inventory");
        trip.GoTo(TripStep.CloseSortedInventory);
        _continueAt = DateTime.Now.AddMilliseconds(500);
    }

    private unsafe void CloseSortedInventory(RetainerTrip trip)
    {
        var orderModule = ItemOrderModule.Instance();
        if (orderModule == null || orderModule->InventorySorter == null ||
            orderModule->InventorySorter->SortFunctionIndex >= 0 || orderModule->InventorySorter->PercentComplete < 100)
            return;

        if (GetVisibleInventory() != null && !TapInventoryKey())
            return;

        trip.GoTo(TripStep.WaitForSortedInventoryClosed);
    }

    private unsafe void WaitForSortedInventoryClosed(RetainerTrip trip)
    {
        if (GetVisibleInventory() != null)
            return;

        trip.SortBeforeReopeningBell = false;
        trip.GoTo(TripStep.FindBell);
        _continueAt = DateTime.Now.AddMilliseconds(250);
    }

    private bool TapInventoryKey() => GameWindowInput.TryTapInventoryKey(_pluginInterface.UiBuilder.WindowHandlePtr);

    private static unsafe void RunInventorySortCommand(UIModule* ui, string command)
    {
        var text = Utf8String.FromString(command);
        if (text == null)
            throw new InvalidOperationException("Could not allocate the inventory sort command.");
        try
        {
            ui->ProcessChatBoxEntry(text);
        }
        finally
        {
            text->Dtor(true);
        }
    }
}
