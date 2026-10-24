using NUnit.Framework;
using System.Linq;
using CitySim.Simulation;

namespace CitySim.Tests.Simulation;

[TestFixture]
public class MetropolisCityGeneratorTests
{
    private SimulationWorld _world;

    [OneTimeSetUp]
    public void BuildCity() => _world = SimulationWorld.CreateMetropolis(MetropolisOptions.Small);

    [OneTimeTearDown]
    public void TearDown() => _world.Dispose();

    [Test]
    public void Generate_Small_PopulationAndJobsMatchTargets()
    {
        var options = MetropolisOptions.Small;
        int population = _world.Grid.TotalPopulation();
        int jobs = _world.Grid.TotalJobs();

        Assert.That(population, Is.EqualTo(options.TargetPopulation).Within(options.TargetPopulation * 0.02));
        Assert.That(jobs, Is.EqualTo(options.TargetPopulation * options.JobsPerResident).Within(options.TargetPopulation * 0.02));
    }

    [Test]
    public void Generate_Small_RoadNetworkIsConnectedWithArterialsAndCurvedLocals()
    {
        var graph = _world.RoadGraph;
        var dist = graph.Dijkstra(graph.Nodes[0].Id);

        Assert.That(graph.Nodes.All(n => dist[n.Id] < float.MaxValue), Is.True, "Every node reachable");
        Assert.That(graph.Edges.Any(e => e.Capacity == MetropolisCityGenerator.ArterialCapacity), Is.True);
        Assert.That(graph.Edges.Any(e => e.Capacity == MetropolisCityGenerator.LocalCapacity), Is.True);
        Assert.That(graph.Nodes.Count(n => graph.GetNodeDegree(n.Id) == 2), Is.GreaterThan(graph.Nodes.Count / 2),
            "Curved local streets are split through degree-2 shape nodes");
    }

    [Test]
    public void Generate_Small_TransitLinesFollowRoads()
    {
        Assert.That(_world.Transit.Routes.Count, Is.EqualTo(MetropolisOptions.Small.TransitLines));
        Assert.That(_world.Transit.Routes.All(r => TransitManager.IsRoutePathValid(r, _world.RoadGraph)), Is.True);
        Assert.That(_world.Transit.Routes.All(r => r.StopNodeIds.Count >= 2), Is.True);
    }

    [Test]
    public void Generate_SameSeed_IsDeterministic()
    {
        using var other = SimulationWorld.CreateMetropolis(MetropolisOptions.Small);

        Assert.That(other.RoadGraph.Nodes.Select(n => n.WorldPosition), Is.EqualTo(_world.RoadGraph.Nodes.Select(n => n.WorldPosition)));
        Assert.That(other.Grid.TotalPopulation(), Is.EqualTo(_world.Grid.TotalPopulation()));
    }

    [Test]
    public void MacroModel_Small_ProducesTripsFlowsAndTransitRidership()
    {
        _world.RecalculateMacroModel();

        Assert.That(_world.Taz.ActiveZones.Count, Is.GreaterThan(10));
        Assert.That(_world.OdMatrix.TotalCarTrips, Is.GreaterThan(0f));
        Assert.That(_world.OdMatrix.TotalTransitTrips, Is.GreaterThan(0f));
        Assert.That(_world.RoadGraph.Edges.Any(e => e.CurrentVolume > 0f), Is.True);
    }
}
