using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace CrescentCompass.Integrations;

public sealed class VNavmeshIpc
{
    private readonly ICallGateSubscriber<bool> isReady;
    private readonly ICallGateSubscriber<Vector3, bool, bool> pathfindAndMoveTo;
    private readonly ICallGateSubscriber<Vector3, float, float, Vector3?> nearestReachable;
    private readonly ICallGateSubscriber<Vector3, Vector3, bool, CancellationToken, Task<List<Vector3>>> pathfindCancelable;
    private readonly ICallGateSubscriber<List<Vector3>, bool, object> moveTo;
    private readonly ICallGateSubscriber<float, object> setTolerance;
    private readonly ICallGateSubscriber<object> stop;
    private readonly ICallGateSubscriber<bool> isRunning;
    private readonly ICallGateSubscriber<bool> pathfindInProgress;
    private readonly ICallGateSubscriber<bool> navPathfindInProgress;
    private readonly ICallGateSubscriber<object> cancelAllPathfinds;
    private readonly ICallGateSubscriber<Vector3, bool, float, Vector3?> pointOnFloor;
    private readonly ICallGateSubscriber<List<Vector3>> listWaypoints;

    public VNavmeshIpc(IDalamudPluginInterface pluginInterface)
    {
        isReady = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        pathfindAndMoveTo = pluginInterface.GetIpcSubscriber<Vector3, bool, bool>(
            "vnavmesh.SimpleMove.PathfindAndMoveTo");
        nearestReachable = pluginInterface.GetIpcSubscriber<Vector3, float, float, Vector3?>(
            "vnavmesh.Query.Mesh.NearestPointReachable");
        pathfindCancelable = pluginInterface.GetIpcSubscriber<Vector3, Vector3, bool, CancellationToken, Task<List<Vector3>>>(
            "vnavmesh.Nav.PathfindCancelable");
        moveTo = pluginInterface.GetIpcSubscriber<List<Vector3>, bool, object>("vnavmesh.Path.MoveTo");
        setTolerance = pluginInterface.GetIpcSubscriber<float, object>("vnavmesh.Path.SetTolerance");
        stop = pluginInterface.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
        isRunning = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        pathfindInProgress = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress");
        navPathfindInProgress = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.PathfindInProgress");
        cancelAllPathfinds = pluginInterface.GetIpcSubscriber<object>("vnavmesh.Nav.PathfindCancelAll");
        pointOnFloor = pluginInterface.GetIpcSubscriber<Vector3, bool, float, Vector3?>(
            "vnavmesh.Query.Mesh.PointOnFloor");
        listWaypoints = pluginInterface.GetIpcSubscriber<List<Vector3>>("vnavmesh.Path.ListWaypoints");
    }

    public bool IsAvailable()
    {
        try
        {
            return isReady.HasFunction && isReady.InvokeFunc();
        }
        catch
        {
            return false;
        }
    }

    public bool NavigateTo(Vector3 destination)
    {
        try
        {
            return IsAvailable() && pathfindAndMoveTo.InvokeFunc(destination, false);
        }
        catch
        {
            return false;
        }
    }

    public Vector3? QueryNearestReachable(Vector3 destination, float horizontalRange, float verticalRange)
    {
        try
        {
            return nearestReachable.HasFunction
                ? nearestReachable.InvokeFunc(destination, horizontalRange, verticalRange)
                : destination;
        }
        catch
        {
            return null;
        }
    }

    public Task<List<Vector3>>? Pathfind(Vector3 origin, Vector3 destination, CancellationToken cancellationToken)
    {
        try
        {
            return pathfindCancelable.HasFunction
                ? pathfindCancelable.InvokeFunc(origin, destination, false, cancellationToken)
                : null;
        }
        catch
        {
            return null;
        }
    }

    public bool MoveAlong(List<Vector3> path)
    {
        try
        {
            if (!moveTo.HasAction || path.Count == 0) return false;
            if (setTolerance.HasAction) setTolerance.InvokeAction(1.25f);
            moveTo.InvokeAction(path, false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool IsBusy()
    {
        try
        {
            return (isRunning.HasFunction && isRunning.InvokeFunc()) ||
                   (pathfindInProgress.HasFunction && pathfindInProgress.InvokeFunc()) ||
                   (navPathfindInProgress.HasFunction && navPathfindInProgress.InvokeFunc());
        }
        catch
        {
            return false;
        }
    }

    public void Stop()
    {
        var cancelSimpleMoveQuery = false;
        try
        {
            cancelSimpleMoveQuery = pathfindInProgress.HasFunction && pathfindInProgress.InvokeFunc();
        }
        catch
        {
            // Continue with stopping the active path.
        }
        try
        {
            if (stop.HasAction) stop.InvokeAction();
        }
        catch
        {
            // The optional plugin may disappear while unloading; ownership is cleared by the caller.
        }
        try
        {
            // SimpleMove can finish its background query after Path.Stop and start moving again.
            // Cancel that outstanding query as well so manual input is final.
            if (cancelSimpleMoveQuery && cancelAllPathfinds.HasAction) cancelAllPathfinds.InvokeAction();
        }
        catch
        {
            // The optional plugin may disappear while unloading.
        }
    }

    public Vector3? QueryPointOnFloor(Vector3 position, float horizontalRange = 3f)
    {
        try
        {
            return pointOnFloor.HasFunction ? pointOnFloor.InvokeFunc(position, false, horizontalRange) : null;
        }
        catch
        {
            return null;
        }
    }

    public IReadOnlyList<Vector3> GetWaypoints()
    {
        try
        {
            if (!listWaypoints.HasFunction) return [];
            var result = listWaypoints.InvokeFunc();
            return result.Count > 0 && result[0] == Vector3.One ? result.Skip(1).ToArray() : result;
        }
        catch
        {
            return [];
        }
    }
}
