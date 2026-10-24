using NUnit.Framework;
using System.Linq;
using CitySim.Simulation;

namespace CitySim.Tests.Simulation;

[TestFixture]
public class SimulationWorldTests
{
    private const float FrameDelta = 1f / 60f;

    private SimulationWorld _world;

    [TearDown]
    public void TearDown()
    {
        _world?.Dispose();
        _world = null;
    }

    private static int ActiveEdgeCount(RoadGraph graph) => graph.Edges.Count(e => e.FromId != -1 && e.ToId != -1);

    [TestCase(ScenarioType.Sandbox)]
    [TestCase(ScenarioType.Tutorial)]
    [TestCase(ScenarioType.EconomyTest)]
    [TestCase(ScenarioType.RoutingTest)]
    [TestCase(ScenarioType.MapEditingTest)]
    public void Create_EachScenario_BuildsConsistentWorldWithoutPendingChanges(ScenarioType scenario)
    {
        _world = SimulationWorld.Create(scenario);

        Assert.That(_world.Grid.Width, Is.EqualTo(SimulationWorld.DefaultGridWidth));
        Assert.That(_world.RoadGraph.Nodes, Is.Not.Empty);
        Assert.That(_world.Topology, Is.Not.Null);
        Assert.That(_world.Topology.HasPendingChanges, Is.False);
        Assert.That(_world.Clock.Hour, Is.EqualTo(SimulationClock.DefaultStartHour));
    }

    [TestCase(ScenarioType.Tutorial)]
    [TestCase(ScenarioType.EconomyTest)]
    [TestCase(ScenarioType.RoutingTest)]
    public void RecalculateMacroModel_PopulatedScenario_ProducesTripsFlowsAndStats(ScenarioType scenario)
    {
        _world = SimulationWorld.Create(scenario);
        int recalcEvents = 0;
        _world.MacroModelRecalculated += () => recalcEvents++;

        _world.RecalculateMacroModel();

        Assert.That(recalcEvents, Is.EqualTo(1));
        Assert.That(_world.Taz.ActiveZones, Is.Not.Empty);
        Assert.That(_world.Stats.Population, Is.EqualTo(_world.Grid.TotalPopulation() + _world.Parcels.TotalPopulation));
        Assert.That(_world.Stats.Population, Is.GreaterThan(0));
        Assert.That(_world.Stats.TotalTrips, Is.GreaterThan(0f));
        Assert.That(_world.RoadGraph.Edges.Any(e => e.CurrentVolume > 0f), Is.True, "MSA must load some edges");
    }

    [Test]
    public void Tick_ThirtyGameMinutes_RecalculatesMacroModelOnSchedule()
    {
        _world = SimulationWorld.Create(ScenarioType.Tutorial);
        _world.RecalculateMacroModel();
        int recalcEvents = 0;
        _world.MacroModelRecalculated += () => recalcEvents++;

        _world.Tick(29f);
        Assert.That(recalcEvents, Is.EqualTo(0));

        _world.Tick(1f);
        Assert.That(recalcEvents, Is.EqualTo(1));
        Assert.That(_world.Clock.Hour, Is.EqualTo(SimulationClock.DefaultStartHour + 0.5f).Within(1e-4f));
    }

    [Test]
    public void Tick_OneGameHour_SettlesBudget()
    {
        _world = SimulationWorld.Create(ScenarioType.EconomyTest);
        _world.RecalculateMacroModel();
        float moneyBefore = _world.Economy.Money;

        _world.Tick(60f);

        Assert.That(_world.Economy.LastDelta, Is.Not.EqualTo(0f));
        Assert.That(_world.Economy.Money, Is.EqualTo(moneyBefore + _world.Economy.LastDelta).Within(0.01f));
    }

    [Test]
    public void Tick_GrowthSteps_GrowPopulationAndRaiseLandUseEvent()
    {
        _world = SimulationWorld.Create(ScenarioType.Tutorial);
        _world.RecalculateMacroModel();
        int populationBefore = _world.Stats.Population;
        int landUseEvents = 0;
        _world.LandUseChanged += _ => landUseEvents++;

        for (int i = 0; i < 20; i++)
        {
            _world.Tick(1f); // one growth step per game minute at 1x
        }

        Assert.That(landUseEvents, Is.GreaterThan(0));
        Assert.That(_world.Stats.Population, Is.GreaterThan(populationBefore));
        Assert.That(_world.Stats.Population, Is.EqualTo(_world.Grid.TotalPopulation() + _world.Parcels.TotalPopulation));
    }

    [Test]
    public void Tick_FramesAtSpeed_MovesAgentsWithoutErrors()
    {
        _world = SimulationWorld.Create(ScenarioType.Tutorial);
        _world.RecalculateMacroModel();
        _world.Clock.Speed = 10f;

        Assert.DoesNotThrow(() =>
        {
            for (int frame = 0; frame < 300; frame++) _world.Tick(FrameDelta);
        });
        Assert.That(_world.Transit.Vehicles, Is.Not.Empty);
    }

    [Test]
    public void Tick_PausedClock_DoesNotAdvanceTime()
    {
        _world = SimulationWorld.Create(ScenarioType.Tutorial);
        _world.RecalculateMacroModel();
        _world.Clock.Speed = 0f;

        _world.Tick(120f);

        Assert.That(_world.Clock.Hour, Is.EqualTo(SimulationClock.DefaultStartHour));
    }

    [Test]
    public void Tick_PendingGraphEdit_AppliesTopologyBeforeSimulating()
    {
        _world = SimulationWorld.Create(ScenarioType.MapEditingTest);
        _world.RecalculateMacroModel();
        TopologyChangeResult? applied = null;
        _world.TopologyApplied += r => applied = r;

        var edge = _world.RoadGraph.Edges.First(e => e.FromId != -1 && e.ToId != -1);
        _world.RoadGraph.SplitEdge(edge.Id, 0.5f);
        Assert.That(_world.Topology.HasPendingChanges, Is.True);

        _world.Tick(FrameDelta);

        Assert.That(applied, Is.Not.Null);
        Assert.That(applied.Value.Applied, Is.True);
        Assert.That(_world.Topology.HasPendingChanges, Is.False);
    }

    [Test]
    public void ApplyTopologyChange_DemolishedRoad_RecalculatesAndReportsTransitChanges()
    {
        _world = SimulationWorld.Create(ScenarioType.Tutorial);
        _world.RecalculateMacroModel();
        var route = _world.Transit.Routes.First();
        int from = route.PathNodeIds[0];
        int to = route.PathNodeIds[1];
        int edgesBefore = ActiveEdgeCount(_world.RoadGraph);
        int recalcEvents = 0;
        TopologyChangeResult? applied = null;
        _world.MacroModelRecalculated += () => recalcEvents++;
        _world.TopologyApplied += r => applied = r;

        _world.RoadGraph.RemoveRoadSegment(from, to);
        var result = _world.ApplyTopologyChange();

        Assert.That(ActiveEdgeCount(_world.RoadGraph), Is.LessThan(edgesBefore));
        Assert.That(recalcEvents, Is.EqualTo(1));
        Assert.That(applied, Is.Not.Null);
        Assert.That(result.Transit.ReroutedRouteIds.Contains(route.Id) || result.Transit.RemovedRouteIds.Contains(route.Id), Is.True);
        Assert.That(_world.Transit.Routes.All(r => TransitManager.IsRoutePathValid(r, _world.RoadGraph)), Is.True);
    }

    [Test]
    public void TransitRidership_SumsPassengersOnAllBuses()
    {
        _world = SimulationWorld.Create(ScenarioType.Tutorial);
        foreach (var vehicle in _world.Transit.Vehicles) vehicle.Passengers = 3f;

        Assert.That(_world.TransitRidership, Is.EqualTo(3f * _world.Transit.Vehicles.Count));
    }

    [Test]
    public void Dispose_StopsTrackingGraphEdits()
    {
        _world = SimulationWorld.Create(ScenarioType.MapEditingTest);
        _world.Dispose();

        var edge = _world.RoadGraph.Edges.First(e => e.FromId != -1 && e.ToId != -1);
        _world.RoadGraph.SplitEdge(edge.Id, 0.5f);

        Assert.That(_world.Topology.HasPendingChanges, Is.False);
    }
}
