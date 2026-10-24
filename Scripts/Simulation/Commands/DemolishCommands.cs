namespace CitySim.Simulation.Commands;

/// <summary>
/// Demolishes an isolated building or connector: a cell whose road node has no attached roads.
/// Removes the node (if any) and clears the zoning (if any).
/// </summary>
public sealed class DemolishIsolatedCommand : IWorldCommand
{
    private readonly int _zoneId;

    public DemolishIsolatedCommand(int zoneId)
    {
        _zoneId = zoneId;
    }

    public CommandResult Execute(SimulationWorld world)
    {
        var zone = world.Grid.GetZone(_zoneId);
        if (zone == null) return CommandResult.Rejected(string.Empty);

        var pos = zone.GridPos;
        bool hasNode = world.RoadGraph.GetNode(_zoneId) != null;
        bool isZoned = zone.Type != ZoneType.Empty;

        if (world.RoadGraph.GetNodeDegree(_zoneId) != 0 || (!hasNode && !isZoned))
            return CommandResult.Rejected($"⚠️ Zone at ({pos.X}, {pos.Y}) has no road connections to demolish.");

        if (hasNode)
        {
            world.RoadGraph.DetachAndRemoveNode(_zoneId);
        }
        if (isZoned)
        {
            world.Grid.DezoneCell(_zoneId);
        }

        var topology = world.ApplyTopologyChange();
        return CommandResult.Applied(
            $"💥 Demolished isolated building/node at ({pos.X}, {pos.Y})." + CommandMessages.DescribeTransitChanges(topology),
            CommandTone.Demolition,
            topology: topology);
    }
}

/// <summary>
/// Demolishes the road segment (both directions) between two cells and removes endpoint nodes
/// that are left as empty, unconnected cells.
/// </summary>
public sealed class DemolishRoadCommand : IWorldCommand
{
    private readonly int _fromZoneId;
    private readonly int _toZoneId;

    public DemolishRoadCommand(int fromZoneId, int toZoneId)
    {
        _fromZoneId = fromZoneId;
        _toZoneId = toZoneId;
    }

    public CommandResult Execute(SimulationWorld world)
    {
        var fromZone = world.Grid.GetZone(_fromZoneId);
        var toZone = world.Grid.GetZone(_toZoneId);
        if (fromZone == null || toZone == null || !world.RoadGraph.RemoveRoadSegment(_fromZoneId, _toZoneId))
            return CommandResult.Rejected("⚠️ Failed to demolish road segment.");

        // If either endpoint is now an empty zone with degree 0, detach and remove it
        RemoveIfEmptyAndUnconnected(world, fromZone);
        RemoveIfEmptyAndUnconnected(world, toZone);

        var topology = world.ApplyTopologyChange();
        return CommandResult.Applied(
            $"💥 Road demolished between ({fromZone.GridPos.X}, {fromZone.GridPos.Y}) and ({toZone.GridPos.X}, {toZone.GridPos.Y})! Click to demolish another."
                + CommandMessages.DescribeTransitChanges(topology),
            CommandTone.Demolition,
            topology: topology);
    }

    private static void RemoveIfEmptyAndUnconnected(SimulationWorld world, Zone zone)
    {
        if (zone.Type == ZoneType.Empty && world.RoadGraph.GetNodeDegree(zone.Id) == 0)
        {
            world.RoadGraph.DetachAndRemoveNode(zone.Id);
        }
    }
}
