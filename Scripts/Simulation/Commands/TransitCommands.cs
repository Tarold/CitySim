using Godot;
using System.Collections.Generic;

namespace CitySim.Simulation.Commands;

/// <summary>
/// Inserts a mid-road transit stop by splitting a road edge; existing routes through the edge are
/// re-routed via the new junction. <see cref="CommandResult.EntityId"/> is the new stop node id.
/// </summary>
public sealed class InsertTransitStopCommand : IWorldCommand
{
    private readonly int _edgeId;
    private readonly Vector2 _position;

    public InsertTransitStopCommand(int edgeId, Vector2 position)
    {
        _edgeId = edgeId;
        _position = position;
    }

    public CommandResult Execute(SimulationWorld world)
    {
        var junction = world.RoadGraph.SplitEdgeAtPoint(_edgeId, _position);
        if (junction == null)
            return CommandResult.Rejected("⚠️ Failed to insert mid-road stop at this position.");

        var topology = world.ApplyTopologyChange();
        return CommandResult.Applied(string.Empty, topology: topology, entityId: junction.Id);
    }
}

/// <summary>
/// Launches a drafted bus route with an optimal fleet. <see cref="CommandResult.EntityId"/> is the new route id.
/// </summary>
public sealed class CreateTransitRouteCommand : IWorldCommand
{
    private readonly string _name;
    private readonly List<int> _pathNodeIds;
    private readonly List<int> _stopNodeIds;
    private readonly Color _color;
    private readonly bool _isLoop;

    public CreateTransitRouteCommand(string name, IEnumerable<int> pathNodeIds, IEnumerable<int> stopNodeIds, Color color, bool isLoop)
    {
        _name = name;
        _pathNodeIds = new List<int>(pathNodeIds);
        _stopNodeIds = new List<int>(stopNodeIds);
        _color = color;
        _isLoop = isLoop;
    }

    public CommandResult Execute(SimulationWorld world)
    {
        if (_stopNodeIds.Count < 2 || _pathNodeIds.Count < 2)
            return CommandResult.Rejected("⚠️ At least 2 stops are required to launch a transit route.");

        if (!world.Economy.CanAfford(EconomyManager.TransitRouteBaseCost))
            return CommandResult.Rejected($"⚠️ Insufficient funds! Launching a route costs ${EconomyManager.TransitRouteBaseCost}.");

        int optimalFleet = TransitManager.CalculateOptimalFleetSize(_pathNodeIds.Count, _isLoop);
        var route = world.Transit.CreateRoute(
            _name,
            _pathNodeIds,
            _stopNodeIds,
            _color,
            _isLoop,
            fleetSize: optimalFleet,
            ticketPrice: SimulationWorld.DefaultTicketPrice);

        // The draft may traverse an edge that was split after it was drafted (mid-road stop insertion)
        var revalidation = world.Transit.RevalidateRoutes(world.RoadGraph);
        if (revalidation.RemovedRouteIds.Contains(route.Id))
            return CommandResult.Rejected("⚠️ The drafted route no longer follows a connected road. Redraw it.");

        world.Economy.Spend(EconomyManager.TransitRouteBaseCost);
        world.RecalculateMacroModel();

        return CommandResult.Applied(
            $"🎉 Route '{route.Name}' launched with {route.FleetSize} buses! Transit coverage updated.",
            cost: EconomyManager.TransitRouteBaseCost,
            entityId: route.Id);
    }
}

/// <summary>
/// Removes a bus route, decommissions its fleet and cleans up orphaned stops.
/// </summary>
public sealed class DeleteTransitRouteCommand : IWorldCommand
{
    private readonly int _routeId;

    public DeleteTransitRouteCommand(int routeId)
    {
        _routeId = routeId;
    }

    public CommandResult Execute(SimulationWorld world)
    {
        if (!world.Transit.RemoveRoute(_routeId))
            return CommandResult.Rejected(string.Empty);

        world.RecalculateMacroModel();
        return CommandResult.Applied("🗑️ Route deleted and fleet decommissioned.", CommandTone.Clearing);
    }
}
