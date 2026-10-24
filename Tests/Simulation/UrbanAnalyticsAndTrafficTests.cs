using Godot;
using NUnit.Framework;
using System.Collections.Generic;
using CitySim.Simulation;

namespace CitySim.Tests.Simulation;

[TestFixture]
public class UrbanAnalyticsAndTrafficTests
{
    private CityGrid _grid;
    private RoadGraph _roadGraph;

    [SetUp]
    public void Setup()
    {
        // 4x4 grid
        _grid = new CityGrid(4, 4, 64f);

        // Zone (1, 1) as Residential with population
        _grid.ZoneCell(1, 1, ZoneType.Residential, population: 5000);
        _grid.Zones[_grid.GetZoneId(1, 1)].Population = 5000;

        // Zone (2, 1) as Commercial with jobs
        _grid.ZoneCell(2, 1, ZoneType.Commercial, jobs: 3000);

        // Zone (2, 2) as Industrial with jobs
        _grid.ZoneCell(2, 2, ZoneType.Industrial, jobs: 2000);

        _roadGraph = new RoadGraph();
        _roadGraph.BuildFromGrid(_grid);
    }

    [Test]
    public void CalculateModeSplit_AlwaysSumsTo100Percent()
    {
        // Zero trips case
        var (car0, trans0) = CommuteAnalytics.CalculateModeSplit(0f, 0f);
        Assert.That(car0 + trans0, Is.EqualTo(100f).Within(0.001f));

        // Mixed trips
        var (car1, trans1) = CommuteAnalytics.CalculateModeSplit(65f, 35f);
        Assert.That(car1 + trans1, Is.EqualTo(100f).Within(0.001f));
        Assert.That(car1, Is.EqualTo(65f).Within(0.01f));
        Assert.That(trans1, Is.EqualTo(35f).Within(0.01f));

        // Asymmetric trips
        var (car2, trans2) = CommuteAnalytics.CalculateModeSplit(120.5f, 29.5f);
        Assert.That(car2 + trans2, Is.EqualTo(100f).Within(0.001f));
        Assert.That(car2, Is.GreaterThan(trans2));

        // 3-mode split (Car, Transit, Walk)
        var (c3, t3, w3) = CommuteAnalytics.CalculateModeSplit(50f, 30f, 20f);
        Assert.That(c3 + t3 + w3, Is.EqualTo(100f).Within(0.001f));
        Assert.That(c3, Is.EqualTo(50f).Within(0.01f));
        Assert.That(t3, Is.EqualTo(30f).Within(0.01f));
        Assert.That(w3, Is.EqualTo(20f).Within(0.01f));
    }

    [Test]
    public void CarTrafficManager_ZeroAgentFlow_ZeroDemandEdgesHaveNoActiveTraffic()
    {
        var carMgr = new CarTrafficManager();

        // Initially all edges have 0 volume
        foreach (var edge in _roadGraph.Edges)
        {
            edge.CurrentVolume = 0f;
        }

        carMgr.Initialize(_roadGraph);
        Assert.That(carMgr.Cars.Count, Is.EqualTo(0), "Zero-agent architecture maintains zero ticking agent objects");
        Assert.That(carMgr.GetActiveDemandEdgeIds(_roadGraph).Count, Is.EqualTo(0), "No active demand edges when all edge demand is zero");
        Assert.That(carMgr.CalculateTargetCarCount(_roadGraph), Is.EqualTo(0));

        // Now set volume on just one specific edge
        var targetEdge = _roadGraph.Edges[0];
        targetEdge.CurrentVolume = 15f;

        carMgr.Initialize(_roadGraph);
        var activeEdges = carMgr.GetActiveDemandEdgeIds(_roadGraph);
        Assert.That(activeEdges.Count, Is.EqualTo(1));
        Assert.That(activeEdges[0], Is.EqualTo(0));
        Assert.That(CarTrafficManager.HasDemand(targetEdge), Is.True);
        Assert.That(carMgr.CalculateTargetCarCount(_roadGraph), Is.GreaterThan(0));
        Assert.That(carMgr.Cars.Count, Is.EqualTo(0), "Zero-agent architecture does not allocate discrete car structs");
    }

    [Test]
    public void CarTrafficManager_ZeroAgentFlow_DeactivatesWhenEdgeDemandDropsToZero()
    {
        var carMgr = new CarTrafficManager();
        var edge0 = _roadGraph.Edges[0];
        edge0.CurrentVolume = 25f;

        carMgr.Initialize(_roadGraph);
        Assert.That(CarTrafficManager.HasDemand(edge0), Is.True);
        Assert.That(carMgr.CalculateTargetCarCount(_roadGraph), Is.GreaterThan(0));

        // Drop demand to zero
        edge0.CurrentVolume = 0f;

        // Run update tick
        carMgr.Update(0.1f, 1.0f, _roadGraph, null);

        Assert.That(CarTrafficManager.HasDemand(edge0), Is.False, "Edge must have no demand when volume drops to zero");
        Assert.That(carMgr.CalculateTargetCarCount(_roadGraph), Is.EqualTo(0));
        Assert.That(carMgr.GetActiveDemandEdgeIds(_roadGraph).Count, Is.EqualTo(0));
    }

    [Test]
    public void CarTrafficManager_ScaleCarCountWithVolume()
    {
        var carMgr = new CarTrafficManager();

        // 0 volume -> 0 target cars
        _roadGraph.ClearVolumes();
        Assert.That(carMgr.CalculateTargetCarCount(_roadGraph), Is.EqualTo(0));

        // Low volume
        _roadGraph.Edges[0].CurrentVolume = 5f;
        int lowCount = carMgr.CalculateTargetCarCount(_roadGraph);
        Assert.That(lowCount, Is.GreaterThanOrEqualTo(1));

        // High volume
        _roadGraph.Edges[0].CurrentVolume = 50f;
        for (int i = 1; i < _roadGraph.Edges.Count; i++)
        {
            _roadGraph.Edges[i].CurrentVolume = 20f;
        }

        int highCount = carMgr.CalculateTargetCarCount(_roadGraph);
        Assert.That(highCount, Is.GreaterThan(lowCount));
        Assert.That(highCount, Is.LessThanOrEqualTo(CarTrafficManager.MaxCars));
    }

    [Test]
    public void PedestrianManager_NeverSpawnsPedestriansOnInactiveEdges()
    {
        var pedMgr = new PedestrianManager();

        // Clear population and jobs from all zones
        for (int i = 0; i < _grid.ZoneCount; i++)
        {
            var z = _grid.GetZone(i);
            z.Population = 0;
            z.Jobs = 0;
        }
        _roadGraph.ClearVolumes();

        pedMgr.Initialize(_roadGraph, _grid, null);
        Assert.That(pedMgr.Pedestrians.Count, Is.EqualTo(0), "No pedestrians should spawn in completely inactive areas");
        Assert.That(pedMgr.CalculateTargetPedestrianCount(_roadGraph, _grid, null), Is.EqualTo(0), "No target flow in completely inactive areas");

        foreach (var edge in _roadGraph.Edges)
        {
            Assert.That(PedestrianManager.IsEdgeActiveUrban(edge, _roadGraph, _grid, null), Is.False);
        }

        // Activate one residential zone with population
        int resId = _grid.GetZoneId(1, 1);
        _grid.Zones[resId].Population = 1000;

        pedMgr.Initialize(_roadGraph, _grid, null);
        Assert.That(pedMgr.Pedestrians.Count, Is.EqualTo(0), "No discrete agent structs in zero-agent mode");
        Assert.That(pedMgr.CalculateTargetPedestrianCount(_roadGraph, _grid, null), Is.GreaterThan(0));

        // All active edges must connect to active urban activity
        bool foundActive = false;
        foreach (var edge in _roadGraph.Edges)
        {
            if (PedestrianManager.IsEdgeActiveUrban(edge, _roadGraph, _grid, null))
            {
                foundActive = true;
                break;
            }
        }
        Assert.That(foundActive, Is.True);
    }

    [Test]
    public void PedestrianManager_ScalesDynamicallyWithUrbanActivity()
    {
        var pedMgr = new PedestrianManager();

        // Inactive network
        for (int i = 0; i < _grid.ZoneCount; i++)
        {
            _grid.GetZone(i).Population = 0;
            _grid.GetZone(i).Jobs = 0;
        }
        _roadGraph.ClearVolumes();

        int count0 = pedMgr.CalculateTargetPedestrianCount(_roadGraph, _grid, null);
        Assert.That(count0, Is.EqualTo(0));

        // Populated network
        _grid.Zones[_grid.GetZoneId(1, 1)].Population = 5000;
        _grid.Zones[_grid.GetZoneId(2, 1)].Jobs = 3000;

        int count1 = pedMgr.CalculateTargetPedestrianCount(_roadGraph, _grid, null);
        Assert.That(count1, Is.GreaterThan(0));
        Assert.That(count1, Is.LessThanOrEqualTo(PedestrianManager.MaxPedestrians));
    }

}
