using System;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Workshoppa;

partial class WorkshopPlugin
{
    private unsafe bool ClearCollectionDialogs()
    {
        var confirmation = (AddonSelectYesno*)GetVisibleInventoryCycleAddon("SelectYesno");
        if (confirmation != null)
        {
            if (confirmation->IsReady && ReadYesNoPrompt(confirmation) is { } text &&
                _gameStrings.RetrieveFinishedItem.IsMatch(text))
                CloseCollectionDialog(&confirmation->AtkUnitBase);
            return false;
        }

        var menu = (AddonSelectString*)GetVisibleInventoryCycleAddon("SelectString");
        if (menu != null)
        {
            if (menu->IsReady && IsWorkshopMenu(menu))
                CloseCollectionDialog(&menu->AtkUnitBase);
            return false;
        }

        var result = GetVisibleInventoryCycleAddon("SelectOk");
        if (result == null)
            result = GetVisibleInventoryCycleAddon("Talk");
        if (result == null)
            result = GetVisibleInventoryCycleAddon("CompanyCraftRecipeNoteBook");
        if (result != null)
        {
            if (result->IsReady)
                CloseCollectionDialog(result);
            return false;
        }

        return !_condition[ConditionFlag.OccupiedInEvent];
    }

    private unsafe bool IsWorkshopMenu(AddonSelectString* addon)
    {
        var menu = &addon->PopupMenu.PopupMenu;
        for (int index = 0; index < menu->EntryCount; index++)
        {
            var entry = menu->EntryNames[index];
            if (!entry.HasValue)
                continue;

            string choice = MemoryHelper.ReadSeStringNullTerminated(new nint(entry)).ToString();
            if (choice == _gameStrings.ViewCraftingLog ||
                choice == "Collect finished product." ||
                choice.StartsWith("Contribute materials.", StringComparison.Ordinal) ||
                choice.StartsWith("Advance to the next phase of production.", StringComparison.Ordinal) ||
                choice.StartsWith("Complete the construction of", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private unsafe void CloseCollectionDialog(AtkUnitBase* addon)
    {
        addon->Close(true);
        _continueAt = DateTime.Now.AddMilliseconds(250);
    }
}
