using System;
using System.Linq;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace Workshoppa;

partial class WorkshopPlugin
{
    private unsafe void SelectYesNoPostSetup(AddonEvent type, AddonArgs args)
    {
        _pluginLog.Verbose("SelectYesNo post-setup");

        AddonSelectYesno* addonSelectYesNo = (AddonSelectYesno*)args.Addon.Address;
        string? text = ReadYesNoPrompt(addonSelectYesNo);
        if (text == null)
            return;
        _pluginLog.Verbose($"YesNo prompt: '{text}'");

        if (_repairKitWindow.IsOpen)
        {
            _pluginLog.Verbose($"Checking for Repair Kit YesNo ({_repairKitWindow.AutoBuyEnabled}, {_repairKitWindow.IsAwaitingYesNo})");
            if (_repairKitWindow.AutoBuyEnabled && _repairKitWindow.IsAwaitingYesNo && _gameStrings.PurchaseItemForGil.IsMatch(text))
            {
                _pluginLog.Information($"Selecting 'yes' ({text})");
                _repairKitWindow.IsAwaitingYesNo = false;
                addonSelectYesNo->AtkUnitBase.FireCallbackInt(0);
            }
            else
            {
                _pluginLog.Verbose("Not a purchase confirmation match");
            }
        }
        else if (_ceruleumTankWindow.IsOpen)
        {
            _pluginLog.Verbose($"Checking for Ceruleum Tank YesNo ({_ceruleumTankWindow.AutoBuyEnabled}, {_ceruleumTankWindow.IsAwaitingYesNo})");
            if (_ceruleumTankWindow.AutoBuyEnabled && _ceruleumTankWindow.IsAwaitingYesNo && _gameStrings.PurchaseItemForCompanyCredits.IsMatch(text))
            {
                _pluginLog.Information($"Selecting 'yes' ({text})");
                _ceruleumTankWindow.IsAwaitingYesNo = false;
                addonSelectYesNo->AtkUnitBase.FireCallbackInt(0);
            }
            else
            {
                _pluginLog.Verbose("Not a purchase confirmation match");
            }
        }
        else if (CurrentStage != Stage.Stopped)
        {
            if (CurrentStage == Stage.ConfirmMaterialDelivery && _gameStrings.TurnInHighQualityItem == text)
            {
                _pluginLog.Information($"Selecting 'yes' ({text})");
                addonSelectYesNo->AtkUnitBase.FireCallbackInt(0);
            }
            else if (CurrentStage == Stage.ConfirmMaterialDelivery && _gameStrings.ContributeItems.IsMatch(text))
            {
                _pluginLog.Information($"Selecting 'yes' ({text})");
                addonSelectYesNo->AtkUnitBase.FireCallbackInt(0);

                ConfirmMaterialDeliveryFollowUp();
            }
            else if (CurrentStage == Stage.ConfirmCollectProduct && _gameStrings.RetrieveFinishedItem.IsMatch(text))
            {
                var current = _configuration.CurrentlyCraftedItem;
                var craft = _workshopCache.Crafts.FirstOrDefault(x => x.WorkshopItemId == current?.WorkshopItemId);
                if (craft == null || craft.ResultItem == 0)
                {
                    FailCraft("Workshoppa could not identify the finished product.");
                    return;
                }

                _collection = new ProductCollection(craft, RetainerTransfer.CountInInventory(craft.ResultItem),
                    System.Diagnostics.Stopwatch.GetTimestamp());
                CurrentStage = Stage.WaitForCollectedProduct;
                _pluginLog.Information($"Selecting 'yes' ({text})");
                addonSelectYesNo->AtkUnitBase.FireCallbackInt(0);
            }
        }
    }

    private void ConfirmCollectProductFollowUp()
    {
        var current = _configuration.CurrentlyCraftedItem;
        bool storeBatch = _configuration.AutomateRetainers && current != null &&
            (current.QueueBatchId is { } batchId
                ? !_configuration.RunnableQueue.Any(item => item.BatchId == batchId)
                : _configuration.RunnableQueue.FirstOrDefault()?.WorkshopItemId != current.WorkshopItemId);
        _craftTiming.RecordCompletion();
        _configuration.CurrentlyCraftedItem = null;
        if (storeBatch)
            _configuration.PendingWorkshopStorageFor = _playerState.ContentId;
        _pluginInterface.SavePluginConfig(_configuration);

        _continueAt = DateTime.Now.AddSeconds(0.5);
        MarkCraftProgress();
        if (storeBatch && BeginRetainerTrip(TripReason.StoreBatch))
            return;

        if (_configuration.RunnableQueue.Any())
        {
            BeginInventoryCycle();
        }
        else
        {
            CurrentStage = Stage.TakeItemFromQueue;
            _queueNotification.Completed(_pluginInterface.UiBuilder.WindowHandlePtr,
                _configuration.ItemQueue.Any(item => item.IsPaused && item.Quantity > 0));
        }
    }
}
