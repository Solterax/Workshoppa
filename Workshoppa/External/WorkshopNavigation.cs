using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace Workshoppa.External;

internal sealed class WorkshopNavigation(IDalamudPluginInterface pluginInterface) : IDisposable
{
    private readonly ICallGateSubscriber<bool> _ready = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
    private readonly ICallGateSubscriber<bool> _running = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
    private readonly ICallGateSubscriber<bool> _simpleMove = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress");
    private readonly ICallGateSubscriber<Vector3, float, float, Vector3?> _nearest =
        pluginInterface.GetIpcSubscriber<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPoint");
    private readonly ICallGateSubscriber<Vector3, Vector3, bool, CancellationToken, Task<List<Vector3>>> _pathfind =
        pluginInterface.GetIpcSubscriber<Vector3, Vector3, bool, CancellationToken, Task<List<Vector3>>>("vnavmesh.Nav.PathfindCancelable");
    private readonly ICallGateSubscriber<List<Vector3>, bool, object> _move =
        pluginInterface.GetIpcSubscriber<List<Vector3>, bool, object>("vnavmesh.Path.MoveTo");
    private readonly ICallGateSubscriber<object> _stop = pluginInterface.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
    private CancellationTokenSource? _cancelPath;
    private Task<List<Vector3>>? _waypoints;
    private bool _walking;

    public string? Update(Vector3 position, Vector3 destination)
    {
        if (!_ready.HasFunction || !_pathfind.HasFunction)
            return "Install and enable vnavmesh to walk between the fabrication station and summoning bell.";
        if (!_ready.InvokeFunc())
            return null;

        if (_walking)
            return _running.InvokeFunc() ? null : "vnavmesh stopped before reaching the destination.";

        if (_running.InvokeFunc() || _simpleMove.InvokeFunc())
            return "vnavmesh is already moving your character for another task.";

        if (_waypoints == null)
        {
            if (_nearest.InvokeFunc(destination, 3, 3) is not { } target)
                return "vnavmesh could not find a walkable point near the destination.";
            _cancelPath = new CancellationTokenSource();
            _waypoints = _pathfind.InvokeFunc(position, target, false, _cancelPath.Token);
            return null;
        }

        if (!_waypoints.IsCompleted)
            return null;
        if (!_waypoints.IsCompletedSuccessfully || _waypoints.Result.Count == 0)
        {
            _ = _waypoints.Exception;
            return "vnavmesh could not find a route to the destination.";
        }

        _move.InvokeAction(_waypoints.Result, false);
        _walking = true;
        return null;
    }

    public void Stop()
    {
        var waypoints = _waypoints;
        _cancelPath?.Cancel();
        _cancelPath?.Dispose();
        _cancelPath = null;
        _waypoints = null;
        if (waypoints != null)
            _ = waypoints.ContinueWith(task => _ = task.Exception, TaskScheduler.Default);
        if (!_walking)
            return;

        _walking = false;
        if (_stop.HasAction)
            _stop.InvokeAction();
    }

    public void Dispose() => Stop();
}
