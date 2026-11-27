namespace Workshoppa;

partial class WorkshopPlugin
{
    internal double? GetRetainerTravelEstimate()
    {
        if (!_configuration.AutomateRetainers)
            return 0;

        long trips = _configuration.CountPlannedStorageTrips();
        bool active = _retainerTrip is { CountsTowardRemainingTime: true } trip &&
            (trip.ReachedBell || trip.Actions.Count > 0);
        bool pending = _retainerTrip == null && (PendingWorkshopStorage ||
            (CanResumeRetainerTrip && _configuration.CurrentlyCraftedItem != null &&
             _retryTrip is TripReason.MakeRoom or TripReason.ReplenishCraft));
        if (active || pending)
            trips++;

        return _retainerTravelTiming.EstimateRemaining(trips, active, active && _retainerTrip?.ReachedBell == true);
    }
}
