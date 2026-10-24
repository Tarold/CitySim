using Godot;
using NUnit.Framework;
using CitySim.Simulation;

namespace CitySim.Tests.Simulation;

[TestFixture]
public class CityStatsSnapshotTests
{
    [Test]
    public void Capture_NullSubsystems_ReturnsZeroedSnapshot()
    {
        var stats = CityStatsSnapshot.Capture(null, null, null, null, null, null);

        Assert.That(stats.Population, Is.EqualTo(0));
        Assert.That(stats.Jobs, Is.EqualTo(0));
        Assert.That(stats.TotalTrips, Is.EqualTo(0f));
        Assert.That(stats.AverageCongestion, Is.EqualTo(0f));
        Assert.That(stats.TransitCoverage, Is.EqualTo(0f));
    }

    [Test]
    public void Capture_GridAndParcels_SumsPopulationJobsAndCongestion()
    {
        var grid = new CityGrid(10, 10, 64f);
        var graph = new RoadGraph();
        int a = grid.GetZoneId(2, 2);
        int b = grid.GetZoneId(3, 2);
        grid.ZoneCell(a, ZoneType.Residential);
        grid.ZoneCell(b, ZoneType.Commercial);
        grid.Zones[a].Population = 1200;
        grid.Zones[b].Jobs = 300;
        graph.EnsureNode(a, grid.GetWorldCenter(a));
        graph.EnsureNode(b, grid.GetWorldCenter(b));
        graph.AddRoadSegment(a, b);
        var edge = graph.Edges[graph.FindEdgeId(a, b)];
        edge.CurrentVolume = edge.Capacity * 0.5f;

        var parcels = new ParcelManager();
        parcels.Initialize(graph);
        var engine = new TrafficEngine();

        var stats = CityStatsSnapshot.Capture(grid, parcels, new OptimizedODMatrix(), engine, graph, new TransitManager());

        Assert.That(stats.Population, Is.EqualTo(grid.TotalPopulation() + parcels.TotalPopulation));
        Assert.That(stats.Jobs, Is.EqualTo(grid.TotalJobs() + parcels.TotalJobs));
        Assert.That(stats.AverageCongestion, Is.EqualTo(engine.GetAverageCongestion(graph)).Within(1e-5f));
        Assert.That(stats.AverageCongestion, Is.GreaterThan(0f));
        Assert.That(stats.TransitCoverage, Is.EqualTo(0f), "No stops means no coverage");
    }

    [Test]
    public void WithLandUse_PopulationGrowth_UpdatesCountsAndKeepsMacroTickStats()
    {
        var grid = new CityGrid(10, 10, 64f);
        int a = grid.GetZoneId(2, 2);
        grid.ZoneCell(a, ZoneType.Residential);
        grid.Zones[a].Population = 100;
        var original = new CityStatsSnapshot(100, 0, totalTrips: 42f, averageCongestion: 0.7f, transitCoverage: 0.25f);

        grid.Zones[a].Population = 900;
        var updated = original.WithLandUse(grid, null);

        Assert.That(updated.Population, Is.EqualTo(900));
        Assert.That(updated.TotalTrips, Is.EqualTo(42f));
        Assert.That(updated.AverageCongestion, Is.EqualTo(0.7f));
        Assert.That(updated.TransitCoverage, Is.EqualTo(0.25f));
    }
}
