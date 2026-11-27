using System;
using System.Diagnostics;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace Workshoppa;

partial class WorkshopPlugin
{
    private static readonly TimeSpan WorkshopEmptyDelay = TimeSpan.FromSeconds(1);
    private long? _workshopEmptySince;
    private Stage? _deferredWorkshopDialog;

    internal bool WaitingForWorkshopVisitors { get; private set; }

    private bool HasWorkshopVisitor()
    {
        if (!_clientState.IsLoggedIn || !WorkshopTerritories.Contains(_clientState.TerritoryType) ||
            _condition[ConditionFlag.BetweenAreas] || _condition[ConditionFlag.BetweenAreas51] ||
            _objectTable.LocalPlayer is not { } player)
            return false;

        foreach (var obj in _objectTable.PlayerObjects)
            if (obj is IPlayerCharacter other && other.GameObjectId != player.GameObjectId)
                return true;

        return false;
    }

    private bool PauseForWorkshopVisitors()
    {
        if (CurrentStage is Stage.Stopped or Stage.RequestStop)
            return false;

        if (HasWorkshopVisitor())
        {
            _workshopEmptySince = null;
            if (!WaitingForWorkshopVisitors)
            {
                WaitingForWorkshopVisitors = true;
                SetCraftTimerActive(false);
                _retainerTravelTiming.Suspend();
                _workshopNavigation.Stop();
                _pluginLog.Information("Workshop crafting paused while another player is present.");
            }
            return true;
        }

        if (!WaitingForWorkshopVisitors)
            return false;

        _workshopEmptySince ??= Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(_workshopEmptySince.Value) < WorkshopEmptyDelay)
            return true;

        WaitingForWorkshopVisitors = false;
        _workshopEmptySince = null;
        MarkCraftProgress();
        long now = Stopwatch.GetTimestamp();
        _inventoryActionStartedAt = now;
        if (_collection is { } collection)
            _collection = collection with { StartedAt = now };
        if (_retainerTrip is { } trip)
        {
            trip.GoTo(trip.Step);
            _retainerTransfer.RefreshMoveTimeout();
        }
        if (CurrentStage == Stage.TravelToWorkshop)
            _workshopTravelStarted = now;

        _pluginLog.Information("Workshop is empty. Resuming crafting.");
        return false;
    }

    private bool DeferWorkshopDialogForVisitor()
    {
        if (!WaitingForWorkshopVisitors && !HasWorkshopVisitor())
            return false;

        _deferredWorkshopDialog = CurrentStage;
        return true;
    }

    private unsafe bool ReplayDeferredWorkshopDialog()
    {
        var stage = _deferredWorkshopDialog;
        if (stage == null)
            return false;
        if (stage != CurrentStage)
        {
            _deferredWorkshopDialog = null;
            return false;
        }
        if (!_clientState.IsLoggedIn || _condition[ConditionFlag.BetweenAreas] ||
            _condition[ConditionFlag.BetweenAreas51] || !WorkshopTerritories.Contains(_clientState.TerritoryType))
            return false;

        string addonName = stage switch
        {
            Stage.OpenRequestItemSelect => "ContextIconMenu",
            Stage.ConfirmMaterialDelivery or Stage.ConfirmCollectProduct => "SelectYesno",
            _ => "Request",
        };
        var addon = GetVisibleInventoryCycleAddon(addonName);
        if (addon == null || !addon->IsReady)
            return false;

        _deferredWorkshopDialog = null;
        switch (stage)
        {
            case Stage.OpenRequestItemWindow:
                OpenRequestItemSelection((AddonRequest*)addon);
                break;
            case Stage.OpenRequestItemSelect:
                SelectRequestedItem((AddonContextIconMenu*)addon);
                break;
            case Stage.ConfirmRequestItemWindow:
                ConfirmRequestItem((AddonRequest*)addon);
                break;
            case Stage.ConfirmMaterialDelivery:
            case Stage.ConfirmCollectProduct:
                var confirmation = (AddonSelectYesno*)addon;
                if (ReadYesNoPrompt(confirmation) is { } text)
                    HandleWorkshopConfirmation(confirmation, text);
                break;
        }

        return true;
    }
}
