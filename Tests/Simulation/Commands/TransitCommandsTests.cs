using Godot;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using CitySim.Simulation;
using CitySim.Simulation.Commands;

namespace CitySim.Tests.Simulation.Commands;

[TestFixture]
public class TransitCommandsTests
{
    private SimulationWorld _world;
    private int _macroRecalculations;

    [SetUp]
    public void Setup()
    {
        _world = CommandTestWorld.Create(ScenarioType.Tutorial);
        _macroRecalculations = 0;
        _world.MacroModelRecalculated += () => _macroRecalculations++;
    }

    [TearDown]
    public void TearDown() => _world.Dispose();

    /// <summary>A connected 3+ node road path between two distinct nodes, drafted like the route designer.</summary>
    private (List<int> Path, List<int> Stops) DraftRoute()
    {
        var edge = _world.RoadGraph.Edges.First(e => e.FromId != -1 && e.ToId != -1);
        int start = edge.FromId;
        var far = _world.RoadGraph.Nodes
            .Select(n => n.Id)
            .Select(id => _world.RoadGraph.GetShortestNodePath(start, id))
            .Where(p => p != null && p.Count >= 3)
            .OrderByDescending(p => p.Count)
            .First();
        return (far, new List<int> { far.First(), far.Last() });
    }

    [Test]
    public void CreateRoute_ValidDraft_LaunchesFleetSpendsAndRecalculates()
    {
        var (path, stops) = DraftRoute();
        int routesBefore = _world.Transit.Routes.Count;

        var result = new CreateTransitRouteCommand("Line X", path, stops, Colors.Orange, isLoop: false).Execute(_world);

        Assert.That(result.IsApplied, Is.True);
        Assert.That(result.Cost, Is.EqualTo(EconomyManager.TransitRouteBaseCost));
        Assert.That(result.Message, Does.Contain("Line X"));
        Assert.That(_world.Transit.Routes.Count, Is.EqualTo(routesBefore + 1));
        var route = _world.Transit.Routes.Single(r => r.Id == result.EntityId);
        Assert.That(route.FleetSize, Is.EqualTo(TransitManager.CalculateOptimalFleetSize(path.Count, false)));
        Assert.That(route.TicketPrice, Is.EqualTo(SimulationWorld.DefaultTicketPrice));
        Assert.That(_world.Transit.Vehicles.Count(v => v.RouteId == route.Id), Is.EqualTo(route.FleetSize));
        Assert.That(_world.Economy.Money, Is.EqualTo(CommandTestWorld.AmpleBudget - EconomyManager.TransitRouteBaseCost));
        Assert.That(_macroRecalculations, Is.EqualTo(1));
    }

    [Test]
    public void CreateRoute_FewerThanTwoStops_Rejected()
    {
        var (path, stops) = DraftRoute();

        var result = new CreateTransitRouteCommand("Short", path, stops.Take(1), Colors.Orange, false).Execute(_world);

        Assert.That(result.Status, Is.EqualTo(CommandStatus.Rejected));
        Assert.That(result.Message, Does.Contain("At least 2 stops"));
    }

    [Test]
    public void CreateRoute_InsufficientFunds_RejectedWithoutRoute()
    {
        var (path, stops) = DraftRoute();
        int routesBefore = _world.Transit.Routes.Count;
        _world.Economy.SetBalance(EconomyManager.TransitRouteBaseCost - 1f);

        var result = new CreateTransitRouteCommand("Broke", path, stops, Colors.Orange, false).Execute(_world);

        Assert.That(result.Status, Is.EqualTo(CommandStatus.Rejected));
        Assert.That(_world.Transit.Routes.Count, Is.EqualTo(routesBefore));
    }

    [Test]
    public void CreateRoute_DraftCrossesSplitEdge_RepairedOnLaunch()
    {
        var (path, stops) = DraftRoute();
        int edgeId = _world.RoadGraph.FindEdgeId(path[0], path[1]);
        _world.RoadGraph.SplitEdge(edgeId, 0.5f);
        _world.ApplyTopologyChange();

        var result = new CreateTransitRouteCommand("Repaired", path, stops, Colors.Orange, false).Execute(_world);

        Assert.That(result.IsApplied, Is.True);
        var route = _world.Transit.Routes.Single(r => r.Id == result.EntityId);
        Assert.That(TransitManager.IsRoutePathValid(route, _world.RoadGraph), Is.True);
        Assert.That(route.PathNodeIds.Count, Is.EqualTo(path.Count + 1));
    }

    [Test]
    public void CreateRoute_DraftNoLongerConnected_RejectedWithoutChargeOrRoute()
    {
        var (path, stops) = DraftRoute();
        float money = _world.Economy.Money;
        int routesBefore = _world.Transit.Routes.Count;
        var disconnectedPath = new List<int> { path[0], _world.RoadGraph.CreateNode(new Vector2(5f, 5f)).Id };
        _world.ApplyTopologyChange();

        var result = new CreateTransitRouteCommand("Ghost", disconnectedPath, disconnectedPath, Colors.Orange, false).Execute(_world);

        Assert.That(result.Status, Is.EqualTo(CommandStatus.Rejected));
        Assert.That(result.Message, Does.Contain("Redraw"));
        Assert.That(_world.Economy.Money, Is.EqualTo(money));
        Assert.That(_world.Transit.Routes.Count, Is.EqualTo(routesBefore));
    }

    [Test]
    public void DeleteRoute_ExistingRoute_RemovesFleetAndRecalculates()
    {
        var route = _world.Transit.Routes.First();

        var result = new DeleteTransitRouteCommand(route.Id).Execute(_world);

        Assert.That(result.IsApplied, Is.True);
        Assert.That(result.Tone, Is.EqualTo(CommandTone.Clearing));
        Assert.That(_world.Transit.Routes.Any(r => r.Id == route.Id), Is.False);
        Assert.That(_world.Transit.Vehicles.Any(v => v.RouteId == route.Id), Is.False);
        Assert.That(_macroRecalculations, Is.EqualTo(1));
    }

    [Test]
    public void DeleteRoute_UnknownRoute_Rejected()
    {
        var result = new DeleteTransitRouteCommand(int.MaxValue).Execute(_world);

        Assert.That(result.Status, Is.EqualTo(CommandStatus.Rejected));
        Assert.That(_macroRecalculations, Is.EqualTo(0));
    }

    [Test]
    public void InsertStop_OnEdge_SplitsEdgeAndReturnsJunctionId()
    {
        var route = _world.Transit.Routes.First();
        int edgeId = _world.RoadGraph.FindEdgeId(route.PathNodeIds[0], route.PathNodeIds[1]);
        var edge = _world.RoadGraph.GetEdge(edgeId);
        var midpoint = edge.Curve.Evaluate(0.5f);

        var result = new InsertTransitStopCommand(edgeId, midpoint).Execute(_world);

        Assert.That(result.IsApplied, Is.True);
        Assert.That(result.Topology, Is.Not.Null);
        Assert.That(_world.RoadGraph.GetNode(result.EntityId), Is.Not.Null);
        Assert.That(route.PathNodeIds, Does.Contain(result.EntityId), "Existing route re-routed through the new stop node");
        Assert.That(TransitManager.IsRoutePathValid(route, _world.RoadGraph), Is.True);
    }

    [Test]
    public void InsertStop_InvalidEdge_Rejected()
    {
        var result = new InsertTransitStopCommand(-1, Vector2.Zero).Execute(_world);

        Assert.That(result.Status, Is.EqualTo(CommandStatus.Rejected));
        Assert.That(result.Message, Does.Contain("Failed to insert"));
    }
}
