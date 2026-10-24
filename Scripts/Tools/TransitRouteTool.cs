using Godot;
using System.Collections.Generic;
using CitySim.Simulation;
using CitySim.Simulation.Commands;

namespace CitySim.Tools;

/// <summary>
/// Transit route designer: click intersections or road edges (inserting mid-road stops) to add stops;
/// segments follow the shortest road path. Clicking the origin again closes a loop. The UI launch
/// button calls <see cref="Launch"/>.
/// </summary>
public sealed class TransitRouteTool : ITool
{
    private readonly IToolHost _host;
    private readonly List<int> _draftStops = new List<int>();
    private readonly List<int> _draftPath = new List<int>();

    public TransitRouteTool(IToolHost host)
    {
        _host = host;
    }

    public IReadOnlyList<int> DraftStops => _draftStops;
    public IReadOnlyList<int> DraftPath => _draftPath;
    public bool DraftIsLoop { get; private set; }

    private RoadGraph Graph => _host.World.RoadGraph;

    public void OnHover(ToolInput input)
    {
        _host.Preview.SetBusLaneHoverEdge(null);

        var snap = RoadSnappingEngine.FindSnap(input.WorldPosition, Graph, snapRadius: ToolHelpers.PickSnapRadius);
        _host.Preview.SetTransitHoverSnap(snap);

        if (snap.Type == SnapType.Node && snap.SnappedNode != null)
        {
            _host.SetHint($"🚏 [Transit Stop] Snapped to Node #{snap.SnappedNode.Id}. Click to add stop.", new Color(0.3f, 0.95f, 1f));
        }
        else if (snap.Type == SnapType.Edge && snap.SnappedEdge != null)
        {
            _host.SetHint($"🚏 [Transit Stop] Snapped to Mid-Road (Edge #{snap.SnappedEdge.Id}). Click to insert mid-road stop.", new Color(0.3f, 1f, 0.7f));
        }
        else
        {
            _host.SetHint("🚌 [Transit Designer] Click on a road edge or intersection to place a transit stop.", new Color(0.4f, 0.8f, 1f));
        }
    }

    public void OnPrimaryClick(ToolInput input)
    {
        int stopNodeId = ResolveStopNode(input.WorldPosition);
        if (stopNodeId == -1) return;

        if (_draftStops.Count == 0)
        {
            // First stop: Route origin
            _draftStops.Add(stopNodeId);
            _draftPath.Add(stopNodeId);
            DraftIsLoop = false;
            PublishDraft();
            _host.SetHint($"🚏 Origin stop set at Node #{stopNodeId}. Click along roads or intersections to add stops.", new Color(0.2f, 1f, 0.7f));
            return;
        }

        int lastStop = _draftStops[_draftStops.Count - 1];
        if (stopNodeId == lastStop)
        {
            _host.SetHint("ℹ️ This node is already the current stop. Click a different point along the road.", Colors.LightGray);
            return;
        }

        // Loop closure check: clicking back on origin stop
        if (stopNodeId == _draftStops[0] && _draftStops.Count >= 2)
        {
            var loopPath = Graph.GetShortestNodePath(lastStop, stopNodeId);
            if (loopPath == null || loopPath.Count < 2)
            {
                _host.SetHint("⚠️ No road path found to close loop back to start.", Colors.Coral);
                return;
            }

            // Append intermediate nodes (skip start node which is lastStop and end node which is origin)
            for (int i = 1; i < loopPath.Count - 1; i++)
            {
                _draftPath.Add(loopPath[i]);
            }
            DraftIsLoop = true;
            PublishDraft();
            _host.SetHint("🔄 Closed-loop route ready! Click 'Запустити кільце' to deploy buses.", new Color(0.3f, 1f, 0.5f));
            return;
        }

        // Normal next stop along shortest path
        var segPath = Graph.GetShortestNodePath(lastStop, stopNodeId);
        if (segPath == null || segPath.Count < 2)
        {
            _host.SetHint("⚠️ No road path found connecting to this stop. Choose a connected road.", Colors.Coral);
            return;
        }

        for (int i = 1; i < segPath.Count; i++)
        {
            _draftPath.Add(segPath[i]);
        }
        _draftStops.Add(stopNodeId);
        DraftIsLoop = false;
        PublishDraft();
        _host.SetHint($"🚏 Stop {_draftStops.Count} added! Click more stops, click start to loop, or click 'Запустити маршрут'.", new Color(0.2f, 1f, 0.7f));
    }

    /// <summary>Launches the drafted route (UI button).</summary>
    public CommandResult Launch(string routeName, Color color)
    {
        var result = _host.Execute(new CreateTransitRouteCommand(routeName, _draftPath, _draftStops, color, DraftIsLoop));
        if (result.IsApplied)
        {
            Reset();
            _host.ReturnToInspect();
        }
        _host.UpdateTransitRoutesView();
        _host.ShowResult(result);
        return result;
    }

    public bool OnCancel()
    {
        if (_draftStops.Count == 0) return false;

        Reset();
        _host.SetHint("🚌 [Transit Designer] Чернетку маршруту скинуто. Клікніть першу зупинку.", new Color(1f, 0.8f, 0.4f));
        return true;
    }

    public void Reset()
    {
        _draftStops.Clear();
        _draftPath.Clear();
        DraftIsLoop = false;
        _host.Preview.ClearTransitDraft();
        _host.UpdateRouteDesignerStatus(0, 0, false);
    }

    private void PublishDraft()
    {
        _host.Preview.SetTransitDraft(_draftStops, _draftPath, _host.RouteDesignerColor, DraftIsLoop);
        _host.UpdateRouteDesignerStatus(_draftStops.Count, _draftPath.Count, DraftIsLoop);
    }

    private int ResolveStopNode(Vector2 mouseWorld)
    {
        var snap = RoadSnappingEngine.FindSnap(mouseWorld, Graph, snapRadius: ToolHelpers.PickSnapRadius);

        if (snap.Type == SnapType.Node && snap.SnappedNode != null)
        {
            return snap.SnappedNode.Id;
        }

        if (snap.Type == SnapType.Edge && snap.SnappedEdge != null)
        {
            // Mid-road stop insertion: automatically split the edge at the clicked position
            var insertResult = _host.Execute(new InsertTransitStopCommand(snap.SnappedEdge.Id, snap.Position));
            if (!insertResult.IsApplied)
            {
                _host.ShowResult(insertResult);
                return -1;
            }
            return insertResult.EntityId;
        }

        // Fallback: check if clicked on an existing node near cursor
        int zoneId = ToolHelpers.GetCellAt(_host.World.Grid, mouseWorld);
        if (zoneId >= 0 && Graph.NodeMap.ContainsKey(zoneId))
        {
            return zoneId;
        }

        _host.SetHint("⚠️ Click an intersection or anywhere along a road edge to add a transit stop.", Colors.Coral);
        return -1;
    }
}
