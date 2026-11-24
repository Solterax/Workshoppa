namespace Workshoppa;

public enum Stage
{
    TakeItemFromQueue,
    TargetFabricationStation,

    OpenCraftingLog,
    SelectCraftCategory,
    SelectCraft,
    ConfirmCraft,

    SelectCraftBranch,
    ContributeMaterials,
    OpenRequestItemWindow,
    OpenRequestItemSelect,
    ConfirmRequestItemWindow,
    ConfirmMaterialDelivery,

    ConfirmCollectProduct,
    WaitForCollectedProduct,
    OpenInventoryBetweenCrafts,
    CloseInventoryBetweenCrafts,
    WaitForInventoryClosed,
    RetainerTrip,

    RequestStop,
    Stopped,
}
