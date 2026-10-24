using System.Collections.Generic;

namespace CitySim.Simulation.Commands;

/// <summary>
/// Zones a grid cell and ensures it has a road node so roads can connect to it.
/// </summary>
public sealed class ZoneCellCommand : IWorldCommand
{
    private readonly int _zoneId;
    private readonly ZoneType _type;

    public ZoneCellCommand(int zoneId, ZoneType type)
    {
        _zoneId = zoneId;
        _type = type;
    }

    public CommandResult Execute(SimulationWorld world)
    {
        var zone = world.Grid.GetZone(_zoneId);
        if (zone == null) return CommandResult.Rejected(string.Empty);

        var pos = zone.GridPos;
        if (zone.Type == _type)
            return CommandResult.NoChange($"ℹ️ Cell ({pos.X}, {pos.Y}) is already zoned as {_type}.");

        if (!world.Economy.CanAfford(EconomyManager.ZoningCost))
            return CommandResult.Rejected($"⚠️ Insufficient funds! Zoning costs ${EconomyManager.ZoningCost}.");

        if (!world.Grid.ZoneCell(_zoneId, _type))
            return CommandResult.Rejected(string.Empty);

        world.Economy.Spend(EconomyManager.ZoningCost);
        world.RoadGraph.EnsureNode(_zoneId, world.Grid.GetWorldCenter(_zoneId));

        TopologyChangeResult? topology = null;
        if (world.Topology.HasPendingChanges)
        {
            topology = world.ApplyTopologyChange();
        }
        else
        {
            world.InvalidateMacroStructure();
            world.RecalculateMacroModel();
        }

        return CommandResult.Applied(
            $"✅ Designated cell ({pos.X}, {pos.Y}) as {CommandMessages.ZoneTypeName(_type)}! Click more cells to expand.",
            cost: EconomyManager.ZoningCost,
            topology: topology);
    }
}

/// <summary>
/// Clears a grid cell back to empty terrain and removes its road node when no road is attached.
/// On an already empty cell, removes an orphan connector node if one is left.
/// </summary>
public sealed class DezoneCellCommand : IWorldCommand
{
    private readonly int _zoneId;

    public DezoneCellCommand(int zoneId)
    {
        _zoneId = zoneId;
    }

    public CommandResult Execute(SimulationWorld world)
    {
        if (_zoneId < 0 || _zoneId >= world.Grid.ZoneCount) return CommandResult.Rejected(string.Empty);

        var zone = world.Grid.GetZone(_zoneId);
        var pos = zone.GridPos;
        bool isOrphanNode = world.RoadGraph.GetNode(_zoneId) != null && world.RoadGraph.GetNodeDegree(_zoneId) == 0;

        if (zone.Type == ZoneType.Empty)
        {
            if (!isOrphanNode)
                return CommandResult.NoChange($"ℹ️ Cell ({pos.X}, {pos.Y}) is already empty terrain.");

            world.RoadGraph.DetachAndRemoveNode(_zoneId);
            var orphanTopology = world.ApplyTopologyChange();
            return CommandResult.Applied(
                $"🧹 Cleared orphan connector dot at ({pos.X}, {pos.Y}).",
                CommandTone.Clearing,
                topology: orphanTopology);
        }

        world.Grid.DezoneCell(_zoneId);

        // If the associated RoadNode has degree 0 (no road edges attached), detach and remove it
        if (isOrphanNode)
        {
            world.RoadGraph.DetachAndRemoveNode(_zoneId);
        }

        var topology = world.ApplyTopologyChange();
        return CommandResult.Applied(
            $"🧹 Cleared cell ({pos.X}, {pos.Y}) back to empty terrain." + CommandMessages.DescribeTransitChanges(topology),
            CommandTone.Clearing,
            topology: topology);
    }
}

/// <summary>
/// Zones roadside parcels on one side of a road; parcels already of the target type are skipped and free.
/// </summary>
public sealed class ZoneParcelsCommand : IWorldCommand
{
    private readonly IReadOnlyList<RoadsideParcel> _parcels;
    private readonly ParcelSide _side;
    private readonly ZoneType _type;

    public ZoneParcelsCommand(IReadOnlyList<RoadsideParcel> parcels, ParcelSide side, ZoneType type)
    {
        _parcels = parcels;
        _side = side;
        _type = type;
    }

    public CommandResult Execute(SimulationWorld world)
    {
        var toZone = new List<RoadsideParcel>(_parcels.Count);
        foreach (var p in _parcels)
        {
            if (p.ZoneType != _type) toZone.Add(p);
        }

        if (toZone.Count == 0)
            return CommandResult.NoChange($"ℹ️ Selected roadside parcel(s) are already zoned as {_type}.");

        float totalCost = toZone.Count * EconomyManager.ZoningCost;
        if (!world.Economy.CanAfford(totalCost))
            return CommandResult.Rejected($"⚠️ Insufficient funds! Costs ${totalCost:F0} (${EconomyManager.ZoningCost}/parcel).");

        world.Economy.Spend(totalCost);
        foreach (var p in toZone)
        {
            world.Parcels.ZoneParcel(p.Id, _type, ensureAccessNode: false);
        }

        world.InvalidateMacroStructure();
        world.RecalculateMacroModel();

        string sideStr = CommandMessages.SideName(_side);
        string typeName = CommandMessages.ZoneTypeName(_type);
        string msg = toZone.Count == 1
            ? $"✅ Zoned roadside lot on {sideStr} side as {typeName} (-${EconomyManager.ZoningCost:F0})!"
            : $"✅ Zoned {toZone.Count} roadside lots on {sideStr} side as {typeName} (-${totalCost:F0})!";
        return CommandResult.Applied(msg, cost: totalCost);
    }
}

/// <summary>
/// Clears roadside parcels on one side of a road back to empty (free).
/// </summary>
public sealed class DezoneParcelsCommand : IWorldCommand
{
    private readonly IReadOnlyList<RoadsideParcel> _parcels;
    private readonly ParcelSide _side;

    public DezoneParcelsCommand(IReadOnlyList<RoadsideParcel> parcels, ParcelSide side)
    {
        _parcels = parcels;
        _side = side;
    }

    public CommandResult Execute(SimulationWorld world)
    {
        int cleared = 0;
        foreach (var p in _parcels)
        {
            if (p.ZoneType == ZoneType.Empty) continue;
            world.Parcels.DezoneParcel(p.Id);
            cleared++;
        }

        if (cleared == 0)
            return CommandResult.NoChange("ℹ️ Selected roadside parcel(s) are already empty.");

        world.InvalidateMacroStructure();
        world.RecalculateMacroModel();

        string sideStr = CommandMessages.SideName(_side);
        string msg = cleared == 1
            ? $"🧹 Cleared roadside lot on {sideStr} side back to empty."
            : $"🧹 Cleared {cleared} roadside lots on {sideStr} side back to empty.";
        return CommandResult.Applied(msg, CommandTone.Clearing);
    }
}
