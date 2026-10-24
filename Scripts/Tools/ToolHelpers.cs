using Godot;
using CitySim.Simulation;

namespace CitySim.Tools;

/// <summary>
/// Shared lookups for map tools.
/// </summary>
public static class ToolHelpers
{
    /// <summary>Snap radius (world px) used to pick roads and intersections with the cursor.</summary>
    public const float PickSnapRadius = 28f;

    /// <summary>Grid cell id under a world position, or -1 outside the grid.</summary>
    public static int GetCellAt(CityGrid grid, Vector2 worldPosition)
    {
        int gx = Mathf.FloorToInt(worldPosition.X / grid.CellSize);
        int gy = Mathf.FloorToInt(worldPosition.Y / grid.CellSize);
        if (gx < 0 || gx >= grid.Width || gy < 0 || gy >= grid.Height) return -1;
        return grid.GetZoneId(gx, gy);
    }

    /// <summary>Road edge under the cursor; a snapped intersection resolves to its first outgoing edge.</summary>
    public static RoadEdge PickRoadEdge(RoadGraph graph, Vector2 worldPosition)
    {
        var snap = RoadSnappingEngine.FindSnap(worldPosition, graph, snapRadius: PickSnapRadius);
        RoadEdge edge = snap.SnappedEdge;
        if (edge == null && snap.Type == SnapType.Node && snap.SnappedNode != null)
        {
            if (graph.AdjacencyEdges.TryGetValue(snap.SnappedNode.Id, out var inc) && inc.Count > 0)
            {
                edge = graph.GetEdge(inc[0]);
            }
        }
        return edge;
    }

    /// <summary>Highlights the grid cell under the cursor (or clears the highlight outside the grid).</summary>
    public static void HoverGridCell(IToolHost host, Vector2 worldPosition)
    {
        host.Preview.SetHoverZone(GetCellAt(host.World.Grid, worldPosition));
    }
}
