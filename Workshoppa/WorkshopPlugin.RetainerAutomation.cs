using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using LLib.GameUI;
using Lumina.Excel.Sheets;
using Workshoppa.External;
using Workshoppa.GameData;

namespace Workshoppa;

partial class WorkshopPlugin
{
    private readonly WorkshopNavigation _workshopNavigation;
    private readonly IDataManager _dataManager;
    private RetainerTrip? _retainerTrip;
    private TripReason? _retryTrip;
    private uint _returnTerritory;
    private ulong _returnCharacter;
    private Vector3 _stationReturnPosition;

    internal bool PendingWorkshopStorage => _configuration.CurrentlyCraftedItem == null &&
        _configuration.PendingWorkshopStorageFor != 0 &&
        _configuration.PendingWorkshopStorageFor == _playerState.ContentId;

    internal bool PendingCraftingOperations => _configuration.CurrentlyCraftedItem == null &&
        _configuration.PendingCraftingOperationsFor != 0 &&
        _configuration.PendingCraftingOperationsFor == _playerState.ContentId;

    internal bool CanResumeRetainerTrip => _configuration.AutomateRetainers &&
        _returnTerritory != 0 && _returnTerritory == _clientState.TerritoryType &&
        _returnCharacter == _playerState.ContentId;

    internal string? RetainerTripStatus => _retainerTrip is { } trip ? trip.Step switch
    {
        TripStep.ClearStation => "Closing the fabrication station.",
        TripStep.FindBell => "Walking to the summoning bell.",
        TripStep.OpenInventory => "Selecting Entrust or withdraw items.",
        TripStep.StartTransfer => "Waiting for the selected retainer's inventory.",
        TripStep.Return => "Returning to the fabrication station.",
        TripStep.Transfer => _retainerTransfer.Status,
        _ => trip.Actions.TryPeek(out var action) ? $"Using retainer {action.Retainer}." : "Leaving the summoning bell.",
    } : null;

    private Configuration.QueuedItem? NextAutomaticBatch() =>
        _configuration.ItemQueue.FirstOrDefault(item => item.Quantity > 0);

    private bool StartAutomaticRun(bool beginOperations)
    {
        if (!_configuration.AutomateRetainers)
            return beginOperations;

        if (string.IsNullOrWhiteSpace(_configuration.MaterialsRetainer) ||
            string.IsNullOrWhiteSpace(_configuration.SubmarinePartsRetainer))
        {
            FailCraft("Set both retainer names in Workshoppa configuration before starting automation.");
            return true;
        }

        if (_workshopCache.Crafts.Count == 0)
        {
            FailCraft("Workshop recipes are still loading. Try starting the queue again.");
            return true;
        }

        if (beginOperations || PendingCraftingOperations)
        {
            if (_configuration.CurrentlyCraftedItem != null || NextAutomaticBatch() == null)
            {
                FailCraft("Add a craft to the queue before beginning crafting operations.");
                return true;
            }

            _configuration.PendingCraftingOperationsFor = _playerState.ContentId;
            _pluginInterface.SavePluginConfig(_configuration);
            return BeginRetainerTrip(TripReason.BeginOperations);
        }

        if (PendingWorkshopStorage)
            return BeginRetainerTrip(TripReason.StoreBatch);

        if (CanResumeRetainerTrip && _retryTrip is { } reason &&
            (reason == TripReason.PrepareBatch || _configuration.CurrentlyCraftedItem != null))
            return BeginRetainerTrip(reason);

        if (_configuration.CurrentlyCraftedItem is { } current)
        {
            var row = current.QueueBatchId is { } id
                ? _configuration.RunnableQueue.FirstOrDefault(item => item.BatchId == id)
                : _configuration.RunnableQueue.FirstOrDefault();
            if (row != null && row.WorkshopItemId == current.WorkshopItemId)
            {
                row.BatchId ??= Guid.NewGuid();
                current.QueueBatchId = row.BatchId;
                int remaining = row.AutomaticBatchRemaining ?? Configuration.AutomaticBatchLimit;
                ActivateAutomaticBatch(row, (int)Math.Min(row.Quantity + 1L, remaining));
                _pluginInterface.SavePluginConfig(_configuration);
            }
            return false;
        }

        var batch = NextAutomaticBatch();
        if (batch == null)
            return false;
        if (!GetBatchMaterials(batch.WorkshopItemId, batch.AutomaticBatchQuantity).All(HasMaterial))
            return BeginRetainerTrip(TripReason.PrepareBatch);

        ActivateAutomaticBatch(batch, batch.AutomaticBatchQuantity);
        _pluginInterface.SavePluginConfig(_configuration);
        return false;
    }

    private bool BeginRetainerTrip(TripReason reason)
    {
        if (!_configuration.AutomateRetainers)
            return false;

        if (_objectTable.LocalPlayer == null ||
            (!CanResumeRetainerTrip &&
             GetDistanceToEventObject(_fabricationStationIds, out _) >= StationApproach.InteractionRange))
        {
            FailCraft("Start retainer automation at the fabrication station.");
            return true;
        }

        if (GetDistanceToEventObject(_fabricationStationIds, out _) < StationApproach.InteractionRange)
        {
            _returnTerritory = _clientState.TerritoryType;
            _returnCharacter = _playerState.ContentId;
            _stationReturnPosition = _objectTable.LocalPlayer.Position;
        }

        _inventoryCyclePending = false;
        _collection = null;
        _retryTrip = reason;
        _retainerTrip = new RetainerTrip(reason);
        if (reason is TripReason.MakeRoom or TripReason.StoreBatch or TripReason.PrepareBatch or TripReason.BeginOperations)
            _retainerTrip.Actions.Enqueue(new(_configuration.SubmarinePartsRetainer, RetainerTransfer.TransferKind.DepositSubmarineParts));

        if (reason == TripReason.BeginOperations)
            _retainerTrip.Actions.Enqueue(new(_configuration.MaterialsRetainer, RetainerTransfer.TransferKind.DepositMaterials));
        else if (reason == TripReason.StoreBatch)
            _retainerTrip.Actions.Enqueue(new(_configuration.MaterialsRetainer, RetainerTransfer.TransferKind.DepositMaterials,
                DepositItems: _workshopCache.Crafts.SelectMany(craft => craft.Phases).SelectMany(phase => phase.Items)
                    .Select(item => item.ItemId).ToHashSet()));

        if (reason == TripReason.ReplenishCraft && _configuration.CurrentlyCraftedItem is { } current)
            AddWithdrawal(_retainerTrip, GetBatchMaterials(current.WorkshopItemId, 1, current));
        else if (reason is TripReason.StoreBatch or TripReason.PrepareBatch or TripReason.BeginOperations &&
                 NextAutomaticBatch() is { } batch)
        {
            _retainerTrip.NextBatch = batch;
            _retainerTrip.NextBatchQuantity = reason == TripReason.BeginOperations
                ? Math.Min(batch.Quantity, Configuration.AutomaticBatchLimit)
                : batch.AutomaticBatchQuantity;
            AddWithdrawal(_retainerTrip, GetBatchMaterials(batch.WorkshopItemId, _retainerTrip.NextBatchQuantity));
        }

        SetCraftTimerActive(false);
        CurrentStage = Stage.RetainerTrip;
        return true;
    }

    private void AddWithdrawal(RetainerTrip trip, IReadOnlyList<Ingredient> materials) =>
        trip.Actions.Enqueue(new(_configuration.MaterialsRetainer, RetainerTransfer.TransferKind.WithdrawMaterials, materials));

    private void ActivateAutomaticBatch(Configuration.QueuedItem batch, int quantity)
    {
        foreach (var row in _configuration.ItemQueue)
            if (row.Quantity > 0)
            {
                row.IsPaused = !ReferenceEquals(row, batch);
                row.AutomaticBatchRemaining = row.IsPaused ? null : quantity;
            }
    }

    private static bool HasMaterial(Ingredient item) => RetainerTransfer.CountInInventory(item.ItemId) >= item.TotalQuantity;

    private List<Ingredient> GetBatchMaterials(uint workshopItemId, int quantity, Configuration.CurrentItem? current = null)
    {
        var craft = _workshopCache.Crafts.Single(x => x.WorkshopItemId == workshopItemId);
        var totals = craft.Phases.SelectMany(phase => phase.Items).GroupBy(item => item.ItemId)
            .ToDictionary(group => group.Key, group => new Ingredient
            {
                ItemId = group.Key,
                Name = group.First().Name,
                TotalQuantity = checked(group.Sum(item => item.TotalQuantity) * quantity),
                Type = Ingredient.EType.Craftable,
            });
        if (current != null)
        {
            foreach (var item in craft.Phases.Take((int)current.PhasesComplete).SelectMany(phase => phase.Items))
                totals[item.ItemId].TotalQuantity -= item.TotalQuantity;

            if (current.PhasesComplete < craft.Phases.Count)
            {
                foreach (var item in current.ContributedItemsInCurrentPhase)
                    if (totals.TryGetValue(item.ItemId, out var total))
                        total.TotalQuantity -= (int)item.QuantityComplete;
            }
        }

        return totals.Values.Where(item => item.TotalQuantity > 0).OrderBy(item => item.Name).ToList();
    }

    private unsafe void UpdateRetainerTrip(RetainerTrip trip)
    {
        if (!CanResumeRetainerTrip || _condition[ConditionFlag.BetweenAreas] ||
            _condition[ConditionFlag.BetweenAreas51])
        {
            FailCraft("Retainer automation was interrupted by a zone change. Return to the workshop and resume.");
            return;
        }

        if (Stopwatch.GetElapsedTime(trip.StepStarted) > trip.Timeout)
        {
            FailCraft($"Retainer automation timed out during: {RetainerTripStatus} Check the game and resume.");
            return;
        }

        if (DateTime.Now < _continueAt || _objectTable.LocalPlayer is not { } player)
            return;

        switch (trip.Step)
        {
            case TripStep.ClearStation:
                if (_condition[ConditionFlag.OccupiedSummoningBell])
                {
                    FailCraft("Close the summoning bell windows before resuming Workshoppa.");
                    break;
                }
                var delivery = GetMaterialDeliveryAddon();
                if (delivery != null)
                {
                    CloseCollectionDialog(delivery);
                    break;
                }
                if (!ClearCollectionDialogs())
                    break;
                SkipUnneededTransfers(trip);
                if (trip.Actions.Count == 0)
                    trip.GoTo(TripStep.Return);
                else if (TrySuppressAutoRetainer(trip))
                    trip.GoTo(TripStep.FindBell);
                break;
            case TripStep.FindBell:
                var bell = FindSummoningBell(player.Position);
                if (bell == null)
                {
                    FailCraft("No summoning bell was found in this Company Workshop.");
                    break;
                }
                if (Vector3.Distance(player.Position, bell.Position) <= 2.7f)
                {
                    _workshopNavigation.Stop();
                    trip.GoTo(TripStep.OpenBell);
                }
                else if (_workshopNavigation.Update(player.Position, bell.Position, out _) is { } bellError)
                    FailCraft(bellError);
                break;
            case TripStep.OpenBell:
                if (FindSummoningBell(player.Position) is { } nearbyBell &&
                    Vector3.Distance(player.Position, nearbyBell.Position) <= 2.7f &&
                    !_condition[ConditionFlag.OccupiedInEvent] && !_condition[ConditionFlag.OccupiedSummoningBell])
                {
                    InteractWithTarget(nearbyBell);
                    trip.GoTo(TripStep.SelectRetainer);
                }
                break;
            case TripStep.SelectRetainer:
                var listAddon = GetVisibleInventoryCycleAddon("RetainerList");
                if (!_condition[ConditionFlag.OccupiedSummoningBell] || listAddon == null || !LAddon.IsAddonReady(listAddon) ||
                    GetVisibleInventoryCycleAddon("SelectString") != null || listAddon->AtkValues == null)
                    break;
                var name = trip.Actions.Peek().Retainer;
                var values = new ReadOnlySpan<AtkValue>(listAddon->AtkValues, listAddon->AtkValuesCount);
                if (RetainerListSelection.FindIndex(values, name, out bool hasEntries) is { } index)
                {
                    RetainerListSelection.Select(listAddon, index);
                    trip.GoTo(TripStep.OpenInventory);
                }
                else if (hasEntries)
                    FailCraft($"Retainer \"{name}\" is unavailable. Check the name and venture status, then resume.");
                break;
            case TripStep.OpenInventory:
                if (_condition[ConditionFlag.OccupiedSummoningBell] &&
                    SelectSelectString("retainer inventory", 0, text => RetainerMenuText.MatchesInventoryOption(text,
                        _dataManager.GetExcelSheet<Addon>().GetRow(2378).Text.ExtractText())))
                    trip.GoTo(TripStep.StartTransfer);
                break;
            case TripStep.StartTransfer:
                StartRetainerTransfer(trip);
                break;
            case TripStep.Transfer:
                if (!_retainerTransfer.IsBusy)
                    FinishRetainerTransfer(trip);
                break;
            case TripStep.LeaveRetainer:
                if (_retainerTransfer.IsOpen)
                    break;
                if (SelectRetainerMenuItem(2383))
                {
                    _continueAt = DateTime.Now.AddMilliseconds(500);
                    trip.GoTo(trip.Actions.Count > 0 ? TripStep.SelectRetainer : TripStep.CloseBell);
                }
                break;
            case TripStep.CloseBell:
                var list = GetVisibleInventoryCycleAddon("RetainerList");
                if (list != null && LAddon.IsAddonReady(list) && GetVisibleInventoryCycleAddon("SelectString") == null)
                {
                    list->FireCallbackInt(-1);
                    trip.GoTo(TripStep.Return);
                }
                break;
            case TripStep.Return:
                if (_condition[ConditionFlag.OccupiedSummoningBell] || _condition[ConditionFlag.OccupiedInEvent])
                    break;
                ReturnToFabricationStation(trip, player.Position);
                break;
        }
    }

    private void ReturnToFabricationStation(RetainerTrip trip, Vector3 playerPosition)
    {
        float distance = GetDistanceToEventObject(_fabricationStationIds, out var station);
        if (station == null)
            return;

        if (distance < StationApproach.InteractionRange)
        {
            FinishRetainerTrip(trip);
            return;
        }

        Vector3 interactionPosition = GetEventObjectInteractionPosition(station);
        var targets = trip.StationApproaches ??=
            StationApproach.GetTargets(interactionPosition, playerPosition, _stationReturnPosition);
        Vector3 target = targets[trip.StationApproachIndex];
        var error = _workshopNavigation.Update(playerPosition, target, out bool canRetry,
            endpoint => StationApproach.IsEndpointInRange(endpoint, interactionPosition));
        if (error == null)
            return;

        if (canRetry && trip.StationApproachIndex + 1 < targets.Length)
        {
            _pluginLog.Debug($"Retrying the fabrication station approach from {distance:F1} yalms. {error}");
            _workshopNavigation.Stop();
            trip.StationApproachIndex++;
            return;
        }

        FailCraft($"Could not get within reach of the fabrication station ({distance:F1} yalms away). {error}");
    }

    private void SkipUnneededTransfers(RetainerTrip trip)
    {
        while (trip.Actions.TryPeek(out var action) && (action.Materials?.All(HasMaterial) ??
                   !_retainerTransfer.HasItemsToDeposit(action.Kind, action.DepositItems)))
            trip.Actions.Dequeue();
    }

    private void StartRetainerTransfer(RetainerTrip trip)
    {
        var action = trip.Actions.Peek();
        if (!IsSelectedRetainer(action.Retainer, out var retainerId) ||
            _retainerTransfer.OpenRetainerId != retainerId || !RetainerTransfer.InventoriesReady)
            return;

        trip.RetainerId = retainerId;
        _retainerTransfer.Start(action.Kind, action.Materials, action.DepositItems);
        trip.GoTo(TripStep.Transfer);
    }

    private unsafe void FinishRetainerTransfer(RetainerTrip trip)
    {
        var action = trip.Actions.Peek();
        if (!_retainerTransfer.FinishedNormally || _retainerTransfer.OpenRetainerId != trip.RetainerId ||
            !RetainerTransfer.InventoriesReady)
        {
            FailCraft($"The transfer with {action.Retainer} stopped. {_retainerTransfer.Status}");
            return;
        }

        if (action.Materials == null && _retainerTransfer.HasItemsToDeposit(action.Kind, action.DepositItems))
        {
            if (_retainerTransfer.HasItemsToDeposit(action.Kind, action.DepositItems, requireRetainerSpace: true))
            {
                FailCraft($"Some items that still fit on {action.Retainer} could not be deposited. Check both inventories and resume.");
                return;
            }

            _pluginLog.Information($"{action.Retainer} has no room for the remaining items. Leaving them in your inventory.");
        }

        if (action.Materials?.FirstOrDefault(item => !HasMaterial(item)) is { } missing)
        {
            int deficit = missing.TotalQuantity - RetainerTransfer.CountInInventory(missing.ItemId);
            FailCraft($"Still need {deficit}× {missing.Name}. Check {action.Retainer}'s stock and your inventory space.");
            return;
        }

        trip.Actions.Dequeue();
        SkipUnneededTransfers(trip);
        if (trip.Actions.TryPeek(out var next) && SameRetainerName(next.Retainer, action.Retainer))
        {
            trip.GoTo(TripStep.StartTransfer);
            return;
        }

        var agent = AgentRetainer.Instance();
        if (agent != null && agent->IsAgentActive())
            agent->Hide();
        trip.GoTo(TripStep.LeaveRetainer);
    }

    private void FinishRetainerTrip(RetainerTrip trip)
    {
        if (trip.Reason == TripReason.MakeRoom && !HasRoomForProduct(GetCurrentCraft()))
        {
            FailCraft("Inventory is still full after depositing submarine parts. Free some space and resume.");
            return;
        }

        if (trip.NextBatch is { } nextBatch)
            ActivateAutomaticBatch(nextBatch, trip.NextBatchQuantity);
        if (trip.Reason is TripReason.StoreBatch or TripReason.BeginOperations && PendingWorkshopStorage)
            _configuration.PendingWorkshopStorageFor = 0;
        if (trip.Reason == TripReason.BeginOperations)
            _configuration.PendingCraftingOperationsFor = 0;

        EndRetainerTrip();
        _retryTrip = null;
        _returnTerritory = 0;
        _pluginInterface.SavePluginConfig(_configuration);
        ResetCraftProgress();
        if (trip.Reason == TripReason.StoreBatch && trip.NextBatch == null)
        {
            StopCraft();
            _queueNotification.Completed(_pluginInterface.UiBuilder.WindowHandlePtr, false);
            return;
        }

        CurrentStage = Stage.TakeItemFromQueue;
        _continueAt = DateTime.Now.AddMilliseconds(500);
    }

    private bool TrySuppressAutoRetainer(RetainerTrip trip)
    {
        var busy = _pluginInterface.GetIpcSubscriber<bool>("AutoRetainer.PluginState.IsBusy");
        if (busy.HasFunction && busy.InvokeFunc())
        {
            FailCraft("AutoRetainer is busy. Let its task finish before resuming Workshoppa.");
            return false;
        }

        var getSuppressed = _pluginInterface.GetIpcSubscriber<bool>("AutoRetainer.GetSuppressed");
        var setSuppressed = _pluginInterface.GetIpcSubscriber<bool, object>("AutoRetainer.SetSuppressed");
        if (getSuppressed.HasFunction && setSuppressed.HasAction && !getSuppressed.InvokeFunc())
        {
            setSuppressed.InvokeAction(true);
            trip.SuppressedAutoRetainer = true;
        }
        return true;
    }

    private void EndRetainerTrip()
    {
        if (_retainerTrip is not { } trip)
            return;
        _retainerTrip = null;
        if (trip.Step == TripStep.Transfer && _retainerTransfer.IsBusy)
            _retainerTransfer.Stop();
        try
        {
            _workshopNavigation.Stop();
        }
        catch (Exception exception)
        {
            _pluginLog.Warning(exception, "Could not stop Workshoppa's navigation.");
        }
        try
        {
            var suppress = _pluginInterface.GetIpcSubscriber<bool, object>("AutoRetainer.SetSuppressed");
            if (trip.SuppressedAutoRetainer && suppress.HasAction)
                suppress.InvokeAction(false);
        }
        catch (Exception exception)
        {
            _pluginLog.Warning(exception, "Could not restore AutoRetainer after a Workshoppa trip.");
        }
    }

    private IGameObject? FindSummoningBell(Vector3 position)
    {
        string name = _dataManager.GetExcelSheet<EObjName>().GetRow(2000401).Singular.ExtractText();
        return _objectTable.Where(obj => obj.IsTargetable &&
                obj.ObjectKind is ObjectKind.EventObj or ObjectKind.HousingEventObject &&
                (SameRetainerName(obj.Name.TextValue, name) || obj.Name.TextValue == "リテイナーベル"))
            .MinBy(obj => Vector3.DistanceSquared(position, obj.Position));
    }

    private static bool SameRetainerName(string first, string second) =>
        string.Equals(first, second.Trim(), StringComparison.OrdinalIgnoreCase);

    private static unsafe bool IsSelectedRetainer(string name, out ulong retainerId)
    {
        var manager = RetainerManager.Instance();
        var retainer = manager == null ? null : manager->GetActiveRetainer();
        retainerId = retainer == null ? 0 : retainer->RetainerId;
        return retainerId != 0 && SameRetainerName(retainer->NameString, name);
    }

    private unsafe bool SelectRetainerMenuItem(uint textRow)
    {
        var menu = (AddonSelectString*)GetVisibleInventoryCycleAddon("SelectString");
        if (menu == null || !menu->IsReady || !_condition[ConditionFlag.OccupiedSummoningBell])
            return false;

        string choice = _dataManager.GetExcelSheet<Addon>().GetRow(textRow).Text.ExtractText().Trim();
        for (int index = 0; index < menu->PopupMenu.PopupMenu.EntryCount; ++index)
        {
            if (SelectSelectString("retainer", index, text => text.Trim() == choice))
                return true;
        }

        return false;
    }

    private enum TripReason { PrepareBatch, MakeRoom, StoreBatch, ReplenishCraft, BeginOperations }
    private enum TripStep { ClearStation, FindBell, OpenBell, SelectRetainer, OpenInventory, StartTransfer, Transfer, LeaveRetainer, CloseBell, Return }

    private sealed record RetainerAction(string Retainer, RetainerTransfer.TransferKind Kind,
        IReadOnlyList<Ingredient>? Materials = null, IReadOnlySet<uint>? DepositItems = null);

    private sealed class RetainerTrip(TripReason reason)
    {
        public TripReason Reason { get; } = reason;
        public TripStep Step { get; private set; }
        public long StepStarted { get; private set; } = Stopwatch.GetTimestamp();
        public TimeSpan Timeout => Step switch
        {
            TripStep.Transfer => TimeSpan.FromMinutes(5),
            TripStep.FindBell or TripStep.Return => TimeSpan.FromSeconds(90),
            _ => TimeSpan.FromSeconds(15),
        };
        public Queue<RetainerAction> Actions { get; } = new();
        public Configuration.QueuedItem? NextBatch { get; set; }
        public int NextBatchQuantity { get; set; }
        public ulong RetainerId { get; set; }
        public Vector3[]? StationApproaches { get; set; }
        public int StationApproachIndex { get; set; }
        public bool SuppressedAutoRetainer { get; set; }

        public void GoTo(TripStep step)
        {
            Step = step;
            StepStarted = Stopwatch.GetTimestamp();
        }
    }
}
