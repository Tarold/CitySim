using Godot;
using NUnit.Framework;
using System.Linq;
using CitySim.Simulation;

namespace CitySim.Tests.Simulation;

[TestFixture]
public class StreamingAssignmentTests
{
    private SimulationWorld _world;

    [SetUp]
    public void Setup()
    {
        _world = SimulationWorld.CreateMetropolis(MetropolisOptions.Small);
    }

    [TearDown]
    public void TearDown() => _world.Dispose();

    private (TazManager Taz, OptimizedODMatrix Od, TrafficEngine Engine) ColdPipeline(int iterations = SimulationWorld.MsaIterations)
    {
        var taz = new TazManager();
        var od = new OptimizedODMatrix();
        var engine = new TrafficEngine();
        taz.Rebuild(_world.Grid, _world.Parcels, _world.RoadGraph, _world.Transit);
        od.Recalculate(taz, _world.RoadGraph, 8f);
        engine.AssignFlowsFromTazMsa(taz, od, _world.RoadGraph, iterations);
        return (taz, od, engine);
    }

    [Test]
    public void Skim_SingleColdIteration_MatchesShortestPathsOnPublishedVolumes()
    {
        var graph = _world.RoadGraph;
        var taz = new TazManager();
        var od = new OptimizedODMatrix();
        var engine = new TrafficEngine();
        taz.Rebuild(_world.Grid, _world.Parcels, graph, _world.Transit);
        od.Recalculate(taz, graph, 8f);
        var publishedBefore = graph.Edges.Select(e => e.CurrentVolume).ToArray();

        engine.AssignFlowsFromTazMsa(taz, od, graph, msaIterations: 1, deferPublish: true);

        for (int e = 0; e < graph.Edges.Count; e++) graph.Edges[e].CurrentVolume = publishedBefore[e];
        var skim = engine.Skim;
        for (int row = 0; row < skim.NodeCount; row += 7)
        {
            var dist = graph.Dijkstra(skim.GetNodeId(row));
            for (int col = 0; col < skim.NodeCount; col++)
            {
                Assert.That(skim.GetRoadTimeByIndex(row, col), Is.EqualTo(dist[skim.GetNodeId(col)]).Within(1e-3f));
            }
        }
    }

    [Test]
    public void Assignment_KeepsNoShortestPathTrees()
    {
        var (_, od, engine) = ColdPipeline();

        Assert.That(engine.Skim.NodeCount, Is.GreaterThan(0));
        Assert.That(typeof(OptimizedODMatrix).GetProperty("CachedTreeBuffers"), Is.Null);
        Assert.That(od.TotalCarTrips, Is.GreaterThan(0f));
    }

    [Test]
    public void Recalculate_CoveringSkim_IsUsedAndStaleSkimIsRejected()
    {
        var (taz, _, engine) = ColdPipeline();
        var fresh = new OptimizedODMatrix();
        fresh.Recalculate(taz, _world.RoadGraph, 8f, skim: engine.Skim);

        Assert.That(engine.Skim.Covers(taz, _world.RoadGraph), Is.True);
        int i = 0, j = taz.ActiveZones.Count - 1;
        var a = taz.ActiveZones[i];
        var b = taz.ActiveZones[j];
        float expected = engine.Skim.GetRoadTime(a.PrimaryRoadNodeId, b.PrimaryRoadNodeId) + a.Connectors[0].AccessTime + b.Connectors[0].AccessTime;
        Assert.That(fresh.GetTravelTime(i, j), Is.EqualTo(expected).Within(1e-4f));

        var edge = _world.RoadGraph.Edges.First(e => e.FromId != -1);
        _world.RoadGraph.SplitEdge(edge.Id, 0.5f);
        Assert.That(engine.Skim.Covers(taz, _world.RoadGraph), Is.False, "A topology change invalidates the skim");
    }

    [Test]
    public void WarmStart_ContinuesEquilibriumWithOneIterationPerCycle()
    {
        var taz = new TazManager();
        var od = new OptimizedODMatrix();
        var engine = new TrafficEngine { WarmStart = true };
        var graph = _world.RoadGraph;
        taz.Rebuild(_world.Grid, _world.Parcels, graph, _world.Transit);

        od.Recalculate(taz, graph, 8f, skim: engine.Skim);
        engine.AssignFlowsFromTazMsa(taz, od, graph, msaIterations: 4);
        Assert.That(engine.EquilibriumIterations, Is.EqualTo(4), "Cold start runs the full MSA");

        od.Recalculate(taz, graph, 8f, skim: engine.Skim);
        engine.AssignFlowsFromTazMsa(taz, od, graph, msaIterations: 4);
        Assert.That(engine.EquilibriumIterations, Is.EqualTo(5), "Warm cycle adds one iteration");

        var edge = graph.Edges.First(e => e.FromId != -1);
        graph.SplitEdge(edge.Id, 0.5f);
        Assert.That(engine.HasWarmEquilibrium(graph), Is.False);
        taz.MarkDirty();
        taz.Rebuild(_world.Grid, _world.Parcels, graph, _world.Transit);
        od.Recalculate(taz, graph, 8f, skim: engine.Skim);
        engine.AssignFlowsFromTazMsa(taz, od, graph, msaIterations: 4);
        Assert.That(engine.EquilibriumIterations, Is.EqualTo(4), "Topology change restarts cold");
    }

    [Test]
    public void Assignment_RepeatedRuns_ProduceIdenticalVolumes()
    {
        ColdPipeline();
        var first = _world.RoadGraph.Edges.Select(e => e.CurrentVolume).ToArray();
        foreach (var e in _world.RoadGraph.Edges) e.CurrentVolume = 0f;

        ColdPipeline();
        var second = _world.RoadGraph.Edges.Select(e => e.CurrentVolume).ToArray();

        Assert.That(second, Is.EqualTo(first));
    }

    [Test]
    public void WarmCycles_FollowDemandIncrease()
    {
        _world.RecalculateMacroModel();
        float totalBefore = _world.RoadGraph.Edges.Sum(e => e.CurrentVolume);

        foreach (var zone in _world.Grid.Zones.Where(z => z.Type == ZoneType.Residential))
        {
            zone.Population *= 2;
        }
        for (int cycle = 0; cycle < 6; cycle++) _world.RecalculateMacroModel();

        Assert.That(_world.TrafficEngine.HasWarmEquilibrium(_world.RoadGraph), Is.True);
        Assert.That(_world.RoadGraph.Edges.Sum(e => e.CurrentVolume), Is.GreaterThan(totalBefore * 1.3f));
    }

    [Test]
    public void GetPathTree_ComputedOnDemandAndDroppedOnRecalculation()
    {
        var (taz, od, _) = ColdPipeline();
        int origin = taz.ActiveZones[0].PrimaryRoadNodeId;

        var tree = od.GetPathTree(origin);
        Assert.That(tree, Is.Not.Null);
        Assert.That(od.GetPathTree(origin), Is.SameAs(tree), "Cached until the next recalculation");

        od.Recalculate(taz, _world.RoadGraph, 8f);
        Assert.That(od.GetPathTree(origin), Is.Not.SameAs(tree));
    }

    [Test]
    public void Recalculate_NoResidents_LeavesNoParkedScoresInWalkTrips()
    {
        var grid = new CityGrid(10, 10, 64f);
        var graph = new RoadGraph();
        int a = grid.GetZoneId(1, 1), b = grid.GetZoneId(7, 1);
        grid.ZoneCell(a, ZoneType.Commercial, jobs: 500);
        grid.ZoneCell(b, ZoneType.Commercial, jobs: 500);
        graph.EnsureNode(a, grid.GetWorldCenter(a));
        graph.EnsureNode(b, grid.GetWorldCenter(b));
        graph.AddRoadSegment(a, b);
        var taz = new TazManager();
        taz.Rebuild(grid, null, graph, null);
        var od = new OptimizedODMatrix();

        od.Recalculate(taz, graph, 8f);

        Assert.That(od.TotalTrips, Is.EqualTo(0f));
        for (int i = 0; i < taz.ActiveZones.Count; i++)
            for (int j = 0; j < taz.ActiveZones.Count; j++)
                Assert.That(od.GetWalkTrips(i, j), Is.EqualTo(0f));
    }
}
