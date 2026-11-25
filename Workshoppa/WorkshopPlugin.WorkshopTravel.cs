using System;
using System.Diagnostics;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Ipc;
using Lumina.Excel.Sheets;

namespace Workshoppa;

partial class WorkshopPlugin
{
    private static readonly TimeSpan WorkshopTravelTimeout = TimeSpan.FromMinutes(15);
    private ICallGateSubscriber<bool>? _lifestreamBusy;
    private long _workshopTravelStarted;
    private bool _workshopCommandSent;

    private bool IsOnWorkshopMap()
    {
        uint territory = _clientState.TerritoryType;
        uint map = _clientState.MapId;
        return map != 0 && WorkshopTerritories.Contains(territory) &&
            _dataManager.GetExcelSheet<TerritoryType>().TryGetRow(territory, out var row) &&
            row.Map.RowId == map;
    }

    private bool IsLifestreamBusy()
    {
        var ipc = _lifestreamBusy ??= _pluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        return ipc.HasFunction && ipc.InvokeFunc();
    }

    private void BeginWorkshopTravel()
    {
        _workshopCommandSent = false;
        _workshopTravelStarted = Stopwatch.GetTimestamp();
        CurrentStage = Stage.TravelToWorkshop;
    }

    private void UpdateWorkshopTravel()
    {
        if (Stopwatch.GetElapsedTime(_workshopTravelStarted) >= WorkshopTravelTimeout)
        {
            FailCraft("Timed out waiting for Lifestream and the Company Workshop. Check the travel route, then resume.");
            return;
        }

        if (!_clientState.IsLoggedIn || _playerState.ContentId == 0)
            return;

        if (!PendingCraftingOperations)
        {
            FailCraft("Crafting operations stopped because the active character changed during travel.");
            return;
        }

        if (_condition[ConditionFlag.BoundByDuty])
        {
            FailCraft("Leave the duty before travelling to the Company Workshop, then resume.");
            return;
        }

        if (_condition[ConditionFlag.BetweenAreas] || _condition[ConditionFlag.BetweenAreas51] ||
            _objectTable.LocalPlayer is not { } player)
            return;

        if (IsOnWorkshopMap())
        {
            if (IsLifestreamBusy() || FindSummoningBell(player.Position) == null)
                return;

            BeginRetainerTrip(TripReason.BeginOperations);
            if (CurrentStage == Stage.RetainerTrip && !_externalPluginHandler.Saved)
                _externalPluginHandler.Save();
            return;
        }

        if (_workshopCommandSent)
            return;

        if (IsLifestreamBusy())
        {
            FailCraft("Lifestream is already travelling. Let it finish, then resume crafting operations.");
            return;
        }

        if (!_commandManager.ProcessCommand("/li workshop"))
        {
            FailCraft("Enable Lifestream so Workshoppa can run /li workshop, then resume.");
            return;
        }

        _workshopCommandSent = true;
    }
}
