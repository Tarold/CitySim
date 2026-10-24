using Godot;
using System.Collections.Generic;
using CitySim.Simulation;
using CitySim.Simulation.Commands;
using CitySim.UI;

namespace CitySim.Tools;

/// <summary>
/// Demolition: click a cell, then an adjacent cell connected to it by road to demolish that road.
/// A cell with no road connections is demolished directly (isolated building or connector).
/// </summary>
public sealed class DemolishTool : ITool
{
    private static readonly (int Dx, int Dy)[] NeighbourOffsets = { (1, 0), (-1, 0), (0, 1), (0, -1) };

    private readonly IToolHost _host;

    public DemolishTool(IToolHost host)
    {
        _host = host;
    }

    /// <summary>Selected start cell of the road to demolish, or -1.</summary>
    public int PendingStartZoneId { get; private set; } = -1;

    public void OnHover(ToolInput input)
    {
        _host.Preview.SetBusLaneHoverEdge(null);
        ToolHelpers.HoverGridCell(_host, input.WorldPosition);
    }

    public void OnPrimaryClick(ToolInput input)
    {
        var grid = _host.World.Grid;
        var graph = _host.World.RoadGraph;
        int zoneId = ToolHelpers.GetCellAt(grid, input.WorldPosition);
        if (zoneId < 0) return;

        var zone = grid.GetZone(zoneId);
        if (zone == null) return;
        int gx = zone.GridPos.X;
        int gy = zone.GridPos.Y;

        if (PendingStartZoneId == -1)
        {
            // First step: select start zone
            var validTargets = GetConnectedNeighbours(zoneId);
            if (validTargets.Count == 0)
            {
                // If this is an isolated building or orphan node with degree 0, demolish/dezone it directly
                if (graph.GetNodeDegree(zoneId) == 0)
                {
                    _host.ReleaseZoneSelection(zoneId);
                }

                var isolatedResult = _host.Execute(new DemolishIsolatedCommand(zoneId));
                if (isolatedResult.IsApplied)
                {
                    _host.ShowCityOverview();
                }
                _host.ShowResult(isolatedResult);
                return;
            }

            PendingStartZoneId = zoneId;
            _host.Preview.SetPreview(InteractionMode.Demolish, PendingStartZoneId, validTargets);
            _host.SetHint($"💥 [Demolish] Selected start cell at ({gx}, {gy}). Click an adjacent connected cell to demolish (Right-click to cancel).", new Color(1f, 0.4f, 0.3f));
            return;
        }

        // Second step: click adjacent target zone to demolish connecting road
        if (zoneId == PendingStartZoneId)
        {
            Reset();
            _host.SetHint("💥 [Demolish] Selection cancelled. Click first cell to demolish road.", new Color(1f, 0.5f, 0.4f));
            return;
        }

        var startZone = grid.GetZone(PendingStartZoneId);
        int manhattanDist = Mathf.Abs(startZone.GridPos.X - gx) + Mathf.Abs(startZone.GridPos.Y - gy);

        if (manhattanDist != 1)
        {
            if (!TryReselectStart(zoneId))
            {
                _host.SetHint($"⚠️ Cells must be adjacent! Click a cell adjacent to ({startZone.GridPos.X}, {startZone.GridPos.Y}).", Colors.Yellow);
                return;
            }
            _host.SetHint($"⚠️ Cells must be adjacent! New start cell set at ({gx}, {gy}). Click an adjacent connected cell.", Colors.Yellow);
            return;
        }

        // Check if road connection exists
        if (!graph.HasEdge(PendingStartZoneId, zoneId) && !graph.HasEdge(zoneId, PendingStartZoneId))
        {
            if (!TryReselectStart(zoneId))
            {
                _host.SetHint($"⚠️ No road exists between ({startZone.GridPos.X}, {startZone.GridPos.Y}) and ({gx}, {gy}).", Colors.Yellow);
                return;
            }
            _host.SetHint($"⚠️ No road exists between these cells! New start cell set at ({gx}, {gy}).", Colors.Yellow);
            return;
        }

        var result = _host.Execute(new DemolishRoadCommand(PendingStartZoneId, zoneId));
        Reset();
        _host.ShowResult(result);
    }

    public bool OnCancel() => false;

    public void Reset()
    {
        PendingStartZoneId = -1;
        _host.Preview.ClearPreview();
    }

    /// <summary>Adjacent cells (4-neighbourhood) joined to <paramref name="zoneId"/> by a road in either direction.</summary>
    public List<int> GetConnectedNeighbours(int zoneId)
    {
        var grid = _host.World.Grid;
        var graph = _host.World.RoadGraph;
        var list = new List<int>();

        var zone = grid.GetZone(zoneId);
        if (zone == null) return list;

        foreach (var (dx, dy) in NeighbourOffsets)
        {
            int nx = zone.GridPos.X + dx;
            int ny = zone.GridPos.Y + dy;
            if (nx < 0 || nx >= grid.Width || ny < 0 || ny >= grid.Height) continue;

            int nId = grid.GetZoneId(nx, ny);
            if (graph.HasEdge(zoneId, nId) || graph.HasEdge(nId, zoneId))
            {
                list.Add(nId);
            }
        }

        return list;
    }

    private bool TryReselectStart(int zoneId)
    {
        var targets = GetConnectedNeighbours(zoneId);
        if (targets.Count == 0) return false;

        PendingStartZoneId = zoneId;
        _host.Preview.SetPreview(InteractionMode.Demolish, PendingStartZoneId, targets);
        return true;
    }
}
