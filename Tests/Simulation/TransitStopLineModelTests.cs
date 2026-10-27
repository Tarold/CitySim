using Godot;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using CitySim.Simulation;

namespace CitySim.Tests.Simulation;

[TestFixture]
public class TransitStopLineModelTests
{
    private CityGrid _grid;
    private RoadGraph _graph;
    private ParcelManager _parcels;
    private TransitManager _transit;
    private TazManager _taz;

    [SetUp]
    public void Setup()
    {
        _grid = new CityGrid(40, 4, 64f);
        _graph = new RoadGraph();
        _parcels = new ParcelManager();
        _parcels.Initialize(_graph);
        _transit = new TransitManager();
        _taz = new TazManager { CellSize = 320f };
    }

    private RoadNode[] BuildCorridor(params float[] xs)
    {
        var nodes = xs.Select(x => _graph.CreateNode(new Vector2(x, 40))).ToArray();
        for (int i = 0; i < nodes.Length - 1; i++) _graph.AddRoadSegment(nodes[i].Id, nodes[i + 1].Id);
        return nodes;
    }

    private void ZoneAt(int cellX, ZoneType type, int amount)
    {
        _grid.ZoneCell(cellX, 0, type, jobs: type == ZoneType.Residential ? 0 : amount);
        if (type == ZoneType.Residential) _grid.Zones[_grid.GetZoneId(cellX, 0)].Population = amount;
    }

    private (TransitNetworkSnapshot snapshot, TransitEngine engine) BuildSkim()
    {
        _taz.Rebuild(_grid, _parcels, _graph, _transit);
        var snapshot = new TransitNetworkSnapshot();
        snapshot.Rebuild(_transit, _graph);
        var engine = new TransitEngine();
        engine.ComputeSkim(_taz, snapshot);
        return (snapshot, engine);
    }

    [Test]
    public void Skim_ThreeLineChain_ReportsTwoTransfers()
    {
        // TAZ centres at x = 96, 608, 1120, 1632 (512 m apart: outside each other's 350 m catchment)
        var n = BuildCorridor(96, 608, 1120, 1632);
        ZoneAt(1, ZoneType.Residential, 500);
        ZoneAt(9, ZoneType.Commercial, 300);
        ZoneAt(17, ZoneType.Commercial, 300);
        ZoneAt(25, ZoneType.Commercial, 300);

        _transit.CreateRoute("A", new List<int> { n[0].Id, n[1].Id }, new[] { n[0].Id, n[1].Id }, Colors.Blue, false);
        _transit.CreateRoute("B", new List<int> { n[1].Id, n[2].Id }, new[] { n[1].Id, n[2].Id }, Colors.Green, false);
        _transit.CreateRoute("C", new List<int> { n[2].Id, n[3].Id }, new[] { n[2].Id, n[3].Id }, Colors.Red, false);

        var (_, engine) = BuildSkim();
        int first = _taz.GetTazIdForZoneTile(new Vector2I(1, 0));
        int last = _taz.GetTazIdForZoneTile(new Vector2I(25, 0));

        var conn = engine.Skim.GetConnection(first, last);
        Assert.That(conn.IsConnected, Is.True);
        Assert.That(conn.Transfers, Is.EqualTo(2));
        Assert.That(conn.InVehicleTime, Is.GreaterThan(0f));
        Assert.That(conn.WaitTime, Is.GreaterThan(0f));
    }

    [Test]
    public void Skim_WalkingTransferBetweenNearbyStops_ConnectsLines()
    {
        // Line A ends at x=608, line B starts 100 m further (no shared node, within the 160 m walking radius)
        var n = BuildCorridor(96, 608, 708, 1100);
        ZoneAt(1, ZoneType.Residential, 500);
        ZoneAt(16, ZoneType.Commercial, 300);

        _transit.CreateRoute("A", new List<int> { n[0].Id, n[1].Id }, new[] { n[0].Id, n[1].Id }, Colors.Blue, false);
        _transit.CreateRoute("B", new List<int> { n[2].Id, n[3].Id }, new[] { n[2].Id, n[3].Id }, Colors.Green, false);

        var (snapshot, engine) = BuildSkim();
        int from = _taz.GetTazIdForZoneTile(new Vector2I(1, 0));
        int to = _taz.GetTazIdForZoneTile(new Vector2I(16, 0));

        Assert.That(snapshot.StopCount, Is.EqualTo(4));
        var conn = engine.Skim.GetConnection(from, to);
        Assert.That(conn.IsConnected, Is.True, "A walking transfer must link the two lines");
        Assert.That(conn.Transfers, Is.EqualTo(1));
    }

    [Test]
    public void Skim_IsolatedTaz_IsUnconnected()
    {
        var n = BuildCorridor(96, 608);
        var far = _graph.CreateNode(new Vector2(1632, 40));
        _graph.AddRoadSegment(n[1].Id, far.Id);
        ZoneAt(1, ZoneType.Residential, 500);
        ZoneAt(25, ZoneType.Commercial, 300);
        _transit.CreateRoute("A", new List<int> { n[0].Id, n[1].Id }, new[] { n[0].Id, n[1].Id }, Colors.Blue, false);

        var (_, engine) = BuildSkim();
        int from = _taz.GetTazIdForZoneTile(new Vector2I(1, 0));
        int to = _taz.GetTazIdForZoneTile(new Vector2I(25, 0));

        Assert.That(engine.Skim.GetConnection(from, to).IsConnected, Is.False);
    }

    [Test]
    public void CrowdingMultiplier_IsNeutralBelowThresholdAndGrowsAbove()
    {
        Assert.That(TransitNetworkSnapshot.ComputeCrowdingMultiplier(0.5f), Is.EqualTo(1f));
        Assert.That(TransitNetworkSnapshot.ComputeCrowdingMultiplier(TransitNetworkSnapshot.CrowdingOccupancyThreshold), Is.EqualTo(1f).Within(1e-4f));
        float high = TransitNetworkSnapshot.ComputeCrowdingMultiplier(1.0f);
        float higher = TransitNetworkSnapshot.ComputeCrowdingMultiplier(1.5f);
        Assert.That(high, Is.GreaterThan(1f));
        Assert.That(higher, Is.GreaterThan(high));
    }

    [Test]
    public void AssignFlows_SameInput_IsDeterministicAndConservesDemand()
    {
        var n = BuildCorridor(96, 608, 1120);
        ZoneAt(1, ZoneType.Residential, 3000);
        ZoneAt(9, ZoneType.Commercial, 1500);
        ZoneAt(17, ZoneType.Commercial, 1500);
        _transit.CreateRoute("A", new List<int> { n[0].Id, n[1].Id }, new[] { n[0].Id, n[1].Id }, Colors.Blue, false);
        _transit.CreateRoute("B", new List<int> { n[1].Id, n[2].Id }, new[] { n[1].Id, n[2].Id }, Colors.Green, false);

        _taz.Rebuild(_grid, _parcels, _graph, _transit);
        var od = new OptimizedODMatrix();
        od.Recalculate(_taz, _graph, 8f);

        float[] Run()
        {
            var snapshot = new TransitNetworkSnapshot();
            snapshot.Rebuild(_transit, _graph);
            var engine = new TransitEngine();
            engine.AssignFlows(_taz, od, snapshot, _graph, transit: null, deferPublish: true);
            return engine.RouteHourlyDemand.ToArray();
        }

        var a = Run();
        var b = Run();
        Assert.That(a, Is.EqualTo(b));
        Assert.That(a.Sum(), Is.GreaterThan(0f), "Transit trips must load the lines");
    }

    [Test]
    public void SimulationWorld_Background_MatchesSynchronousTransitDemand()
    {
        using var sync = SimulationWorld.Create(ScenarioType.Tutorial);
        using var background = SimulationWorld.Create(ScenarioType.Tutorial);
        background.UseBackgroundMacroModel = true;

        for (int cycle = 0; cycle < 2; cycle++)
        {
            sync.RecalculateMacroModel();
            background.RecalculateMacroModel();
            background.WaitForMacroModel();
        }

        for (int i = 0; i < sync.Transit.Routes.Count; i++)
        {
            Assert.That(background.Transit.Routes[i].AssignedHourlyDemand,
                Is.EqualTo(sync.Transit.Routes[i].AssignedHourlyDemand), $"route {i}");
        }
        foreach (var kv in sync.Transit.Stops)
        {
            Assert.That(background.Transit.Stops[kv.Key].HourlyArrivalRate,
                Is.EqualTo(kv.Value.HourlyArrivalRate), $"stop {kv.Key}");
        }
    }
}
