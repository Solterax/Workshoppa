using System.Numerics;

namespace Workshoppa.External;

internal static class StationApproach
{
    public const float InteractionRange = 3f;
    private const float EndpointRange = 2.7f;

    public static Vector3[] GetTargets(Vector3 station, Vector3 player, Vector3 previousPosition)
    {
        var approach = DirectionFrom(station, player, Vector3.UnitZ);
        var previousApproach = DirectionFrom(station, previousPosition, approach);
        var side = new Vector3(-approach.Z, 0, approach.X);
        var floor = new Vector3(station.X, previousPosition.Y, station.Z);
        return
        [
            floor + approach,
            floor + previousApproach,
            floor + Vector3.Normalize(approach + side),
            floor + Vector3.Normalize(approach - side),
            previousPosition,
        ];
    }

    public static bool IsEndpointInRange(Vector3 endpoint, Vector3 station) =>
        Vector3.DistanceSquared(endpoint, station) <= EndpointRange * EndpointRange;

    private static Vector3 DirectionFrom(Vector3 station, Vector3 position, Vector3 fallback)
    {
        var offset = new Vector3(position.X - station.X, 0, position.Z - station.Z);
        return offset.LengthSquared() > 0.01f ? Vector3.Normalize(offset) : fallback;
    }
}
