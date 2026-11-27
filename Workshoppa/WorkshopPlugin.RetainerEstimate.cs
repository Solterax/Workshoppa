using System;
using System.Linq;

namespace Workshoppa;

partial class WorkshopPlugin
{
    private long _nextPreparationEstimateAt;
    private (uint Recipe, int Quantity, bool Needed) _preparationEstimate;

    internal (long Trips, double? TravelSeconds) GetRetainerTravelEstimate()
    {
        if (!_configuration.AutomateRetainers)
            return (0, 0);

        long trips = _configuration.CountPlannedStorageTrips();
        bool active = _retainerTrip is { } trip && (trip.ReachedBell || trip.Actions.Count > 0);
        if (active || (_retainerTrip == null && HasPreparationOrRecoveryTrip()))
            trips++;

        return (trips, _retainerTravelTiming.EstimateRemaining(trips, active, _retainerTrip?.ReachedBell == true));
    }

    private bool HasPreparationOrRecoveryTrip()
    {
        if (PendingWorkshopStorage || PendingCraftingOperations || (CanResumeRetainerTrip && _retryTrip != null))
            return true;

        if (!_clientState.IsLoggedIn || _configuration.CurrentlyCraftedItem != null ||
            NextAutomaticBatch() is not { } batch || !_workshopCache.Crafts.Any(craft => craft.WorkshopItemId == batch.WorkshopItemId))
            return false;

        int quantity = _configuration.GetAutomaticBatchQuantity(batch);
        long now = Environment.TickCount64;
        if (now >= _nextPreparationEstimateAt || _preparationEstimate.Recipe != batch.WorkshopItemId ||
            _preparationEstimate.Quantity != quantity)
        {
            _preparationEstimate = (batch.WorkshopItemId, quantity,
                !GetBatchMaterials(batch.WorkshopItemId, quantity).All(HasMaterial));
            _nextPreparationEstimateAt = now + 1000;
        }

        return _preparationEstimate.Needed;
    }
}
