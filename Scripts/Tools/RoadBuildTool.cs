using Godot;
using CitySim.Simulation;
using CitySim.Simulation.Commands;

namespace CitySim.Tools;

/// <summary>
/// Road construction: click start → click end (straight roads build here) → for curved roads,
/// move the cursor to bend the curve and click to build. Endpoints snap to nodes, along edges
/// (splitting them) or to angles/grid.
/// </summary>
public sealed class RoadBuildTool : ITool
{
    /// <summary>Segments shorter than this (world px) are refused.</summary>
    public const float MinSegmentLength = 8f;

    private readonly IToolHost _host;

    private RoadNode _startNode;
    private Vector2 _startPoint;
    private Vector2 _endPoint;
    private RoadSnapResult _pendingEndSnap;

    public RoadBuildTool(IToolHost host, bool curved)
    {
        _host = host;
        IsCurved = curved;
    }

    /// <summary>Curved (3-click) instead of straight (2-click) construction.</summary>
    public bool IsCurved { get; }

    /// <summary>0 = choosing start, 1 = choosing end, 2 = bending the curve (curved mode only).</summary>
    public int Step { get; private set; }

    public RoadNode StartNode => _startNode;

    private RoadGraph Graph => _host.World.RoadGraph;

    public void OnHover(ToolInput input)
    {
        Vector2 mouseWorld = input.WorldPosition;

        if (Step == 0)
        {
            var snap = RoadSnappingEngine.FindSnap(mouseWorld, Graph, null, null, input.Shift);
            _host.Preview.SetRoadBuildPreview(0, snap.Position, snap.Position, snap.Position, null, snap, 0f, true);
        }
        else if (Step == 1)
        {
            var snap = RoadSnappingEngine.FindSnap(mouseWorld, Graph, _startPoint, _startNode, input.Shift);
            var curve = new CurveSegment(_startPoint, snap.Position);
            float cost = BuildRoadCommand.CalculateCost(curve);
            _host.Preview.SetRoadBuildPreview(1, _startPoint, snap.Position, snap.Position, curve, snap, cost, _host.World.Economy.CanAfford(cost));
        }
        else if (Step == 2)
        {
            var curve = CurveSegment.CreateFromThreePoints(_startPoint, mouseWorld, _endPoint);
            float cost = BuildRoadCommand.CalculateCost(curve);
            var snap = RoadSnappingEngine.FindSnap(mouseWorld, Graph, null, null, false);
            _host.Preview.SetRoadBuildPreview(2, _startPoint, _endPoint, mouseWorld, curve, snap, cost, _host.World.Economy.CanAfford(cost));
        }
    }

    public void OnPrimaryClick(ToolInput input)
    {
        Vector2 mouseWorld = input.WorldPosition;

        if (Step == 0)
        {
            // Step 1: Click start point
            var snap = RoadSnappingEngine.FindSnap(mouseWorld, Graph, null, null, input.Shift);
            _startNode = ResolveEndpoint(snap, snap.Position);
            if (_startNode == null) return;

            _startPoint = _startNode.WorldPosition;
            Step = 1;
            OnHover(input);
            string stepTotal = IsCurved ? "3" : "2";
            _host.SetHint($"📍 [Step 2/{stepTotal}] Start set. Click end point (Hold Shift for angle snap).", new Color(0.2f, 1f, 0.7f));
        }
        else if (Step == 1)
        {
            // Step 2: Click end point
            var snap = RoadSnappingEngine.FindSnap(mouseWorld, Graph, _startPoint, _startNode, input.Shift);

            if (_startPoint.DistanceTo(snap.Position) < MinSegmentLength)
            {
                _host.SetHint("⚠️ Road segment too short. Choose a farther point.", Colors.Yellow);
                return;
            }

            if (IsCurved)
            {
                _endPoint = snap.Position;
                _pendingEndSnap = snap;
                Step = 2;
                OnHover(input);
                _host.SetHint("〰️ [Step 3/3] Move cursor to bend curvature, then click to build.", new Color(0.3f, 0.95f, 1f));
                return;
            }

            // Straight Road Mode: Build immediately
            var endNode = ResolveEndpoint(snap, snap.Position);
            if (endNode == null) return;

            var straightCurve = new CurveSegment(_startNode.WorldPosition, endNode.WorldPosition);
            Build(new BuildRoadCommand(_startNode.Id, endNode.Id, straightCurve));
        }
        else if (Step == 2)
        {
            // Step 3: Click to confirm curve bend apex
            var endNode = ResolveEndpoint(_pendingEndSnap, _endPoint);
            if (endNode == null) return;

            var curve = CurveSegment.CreateFromThreePoints(_startNode.WorldPosition, mouseWorld, endNode.WorldPosition);
            Build(new BuildRoadCommand(_startNode.Id, endNode.Id, curve));
        }
    }

    public bool OnCancel()
    {
        if (Step == 0) return false;

        Reset();
        _host.SetHint("🛣️ Road construction cancelled. Click to set new start point.", new Color(1f, 0.8f, 0.4f));
        return true;
    }

    public void Reset()
    {
        Step = 0;
        _startNode = null;
        _host.Preview.ClearRoadBuildPreview();
    }

    private void Build(BuildRoadCommand command)
    {
        var result = _host.Execute(command);
        if (result.IsApplied)
        {
            Reset();
        }
        _host.ShowResult(result);
    }

    /// <summary>
    /// Turns a snap into a road node: the snapped node, a junction splitting the snapped edge,
    /// the zoned cell's existing connector, or a new free-standing node.
    /// </summary>
    private RoadNode ResolveEndpoint(RoadSnapResult snap, Vector2 point)
    {
        if (snap.Type == SnapType.Node)
        {
            return snap.SnappedNode;
        }

        if (snap.Type == SnapType.Edge)
        {
            var junction = Graph.SplitEdgeAtPoint(snap.SnappedEdge.Id, point);
            if (junction == null)
            {
                _host.SetHint("⚠️ Failed to connect to the road at this position.", Colors.Coral);
            }
            return junction;
        }

        var grid = _host.World.Grid;
        int zoneId = ToolHelpers.GetCellAt(grid, point);
        if (zoneId >= 0)
        {
            var zone = grid.GetZone(zoneId);
            if (zone != null && zone.Type != ZoneType.Empty && Graph.NodeMap.TryGetValue(zoneId, out var connector))
            {
                return connector;
            }
        }

        return Graph.CreateNode(point, -1);
    }
}
