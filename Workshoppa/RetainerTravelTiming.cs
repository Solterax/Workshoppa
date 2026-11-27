using System;

namespace Workshoppa;

internal sealed class RetainerTravelTiming(Configuration configuration, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private Direction? _direction;
    private long? _startedAt;
    private double _elapsedSeconds;

    public enum Direction { ToBell, ToStation }

    public void Start(Direction direction)
    {
        if (_direction != direction)
        {
            Discard();
            _direction = direction;
        }
        _startedAt ??= _clock.GetTimestamp();
    }

    public void Suspend()
    {
        _elapsedSeconds = ElapsedSeconds;
        _startedAt = null;
    }

    public void Discard()
    {
        _direction = null;
        _startedAt = null;
        _elapsedSeconds = 0;
    }

    public bool Complete(Direction direction)
    {
        if (_direction != direction)
            return false;

        double seconds = ElapsedSeconds;
        Discard();
        if (seconds <= 0)
            return false;

        var stats = direction == Direction.ToBell
            ? (configuration.ToRetainerTravel ??= new())
            : (configuration.FromRetainerTravel ??= new());
        stats.Trips++;
        stats.TotalSeconds += seconds;
        return true;
    }

    public double? EstimateRemaining(long trips, bool hasActiveTrip, bool reachedBell)
    {
        if (trips == 0)
            return 0;

        double? toBell = configuration.ToRetainerTravel?.AverageSeconds;
        double? toStation = configuration.FromRetainerTravel?.AverageSeconds;
        long upcoming = trips - (hasActiveTrip ? 1 : 0);
        if (toStation == null || (toBell == null && (upcoming > 0 || !reachedBell)))
            return null;

        double estimate = upcoming * (toBell.GetValueOrDefault() + toStation.Value);
        if (!hasActiveTrip)
            return estimate;

        if (!reachedBell)
            estimate += Math.Max(0, toBell.GetValueOrDefault() - (_direction == Direction.ToBell ? ElapsedSeconds : 0));
        return estimate + Math.Max(0, toStation.Value - (_direction == Direction.ToStation ? ElapsedSeconds : 0));
    }

    private double ElapsedSeconds =>
        _elapsedSeconds + (_startedAt is { } startedAt ? _clock.GetElapsedTime(startedAt).TotalSeconds : 0);
}
