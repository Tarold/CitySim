using Godot;
using NUnit.Framework;
using CitySim.Simulation;
using System.Linq;

namespace CitySim.Tests;

[TestFixture]
public class GameplayRoutingTests
{
    private CityGrid _grid = null!;
    private RoadGraph _roadGraph = null!;
    private ParcelManager _parcelManager = null!;
    private TransitManager _transitManager = null!;
    private EconomyManager _economyManager = null!;
    private TrafficEngine _trafficEngine = null!;

    [SetUp]
    public void Setup()
    {
        _grid = new CityGrid(20, 20, 64f);
        _roadGraph = new RoadGraph();
        _parcelManager = new ParcelManager();
        _parcelManager.Initialize(_roadGraph);
        _transitManager = new TransitManager();
        _economyManager = new EconomyManager();
        _trafficEngine = new TrafficEngine();

        RoutingTestCityGenerator.Generate(_grid, _roadGraph, _parcelManager, _transitManager, _economyManager);
    }

    [Test]
    public void RoutingTestCity_Setup_HasParallelCorridorsAndBottleneck()
    {
        Assert.That(_roadGraph.Edges.Count, Is.GreaterThan(0));
        Assert.That(_transitManager.Routes.Count, Is.EqualTo(1));
        Assert.That(_parcelManager.TotalPopulation, Is.GreaterThan(0));
        Assert.That(_parcelManager.TotalJobs, Is.GreaterThan(0));

        // Locate bottleneck edge (between ~500, 650 and ~800, 650)
        var bottleneckEdge = _roadGraph.Edges.FirstOrDefault(e => e.FromId != -1 && e.Capacity <= 450f);
        Assert.That(bottleneckEdge, Is.Not.Null, "Benchmark should feature a narrow bottleneck choke point");
        Assert.That(bottleneckEdge!.Capacity, Is.EqualTo(400f));

        // Locate high-capacity bypass edge
        var bypassEdge = _roadGraph.Edges.FirstOrDefault(e => e.FromId != -1 && e.Capacity >= 2500f);
        Assert.That(bypassEdge, Is.Not.Null, "Benchmark should feature a high-capacity bypass curve");
        Assert.That(bypassEdge!.Capacity, Is.EqualTo(3000f));
    }

    [Test]
    public void ModalSplit_CalculatesCarTransitAndWalking_AllModesRepresented()
    {
        float[,] dist = _roadGraph.ComputeDistanceMatrix(_grid.ZoneCount);
        float[,] walkDist = _roadGraph.ComputeWalkingDistanceMatrix(_grid.ZoneCount);
        _roadGraph.BuildPathCache(_grid.ZoneCount);

        var odMatrix = new ODMatrix(_grid.ZoneCount);
        var coveredZones = _transitManager.GetCoveredZoneIds(_grid, 3, _roadGraph, _parcelManager);

        odMatrix.Recalculate(
            hour: 8.0f,
            grid: _grid,
            distances: dist,
            hasTransit: true,
            coveredZoneIds: coveredZones,
            averageTicketPrice: 8.0f,
            walkingDistances: walkDist,
            transitManager: _transitManager,
            parcelManager: _parcelManager,
            roadGraph: _roadGraph
        );

        Assert.That(odMatrix.TotalTrips, Is.GreaterThan(0f));
        Assert.That(odMatrix.TotalCarTrips, Is.GreaterThan(0f), "Private cars must take trips");
        Assert.That(odMatrix.TotalTransitTrips, Is.GreaterThan(0f), "Transit line must attract commuters");
        Assert.That(odMatrix.TotalWalkTrips, Is.GreaterThan(0f), "Walk corridor must produce walking trips");

        float sumModes = odMatrix.TotalCarTrips + odMatrix.TotalTransitTrips + odMatrix.TotalWalkTrips;
        Assert.That(sumModes, Is.EqualTo(odMatrix.TotalTrips).Within(0.01f), "Mode split must sum to total demand");
    }

    [Test]
    public void TransitCatchment_ModeChoiceShift_WhenTicketPriceChanges()
    {
        float[,] dist = _roadGraph.ComputeDistanceMatrix(_grid.ZoneCount);
        float[,] walkDist = _roadGraph.ComputeWalkingDistanceMatrix(_grid.ZoneCount);
        _roadGraph.BuildPathCache(_grid.ZoneCount);

        var coveredZones = _transitManager.GetCoveredZoneIds(_grid, 3, _roadGraph, _parcelManager);

        // 1. Inexpensive transit ($2 fare)
        var odCheap = new ODMatrix(_grid.ZoneCount);
        odCheap.Recalculate(
            hour: 8.0f,
            grid: _grid,
            distances: dist,
            hasTransit: true,
            coveredZoneIds: coveredZones,
            averageTicketPrice: 2.0f,
            walkingDistances: walkDist,
            transitManager: _transitManager,
            parcelManager: _parcelManager,
            roadGraph: _roadGraph
        );

        float transitShareCheap = odCheap.TotalTransitTrips / odCheap.TotalTrips;

        // 2. Expensive transit ($120 fare)
        var odExpensive = new ODMatrix(_grid.ZoneCount);
        odExpensive.Recalculate(
            hour: 8.0f,
            grid: _grid,
            distances: dist,
            hasTransit: true,
            coveredZoneIds: coveredZones,
            averageTicketPrice: 120.0f,
            walkingDistances: walkDist,
            transitManager: _transitManager,
            parcelManager: _parcelManager,
            roadGraph: _roadGraph
        );

        float transitShareExpensive = odExpensive.TotalTransitTrips / odExpensive.TotalTrips;

        Assert.That(transitShareCheap, Is.GreaterThan(transitShareExpensive),
            "Cheap transit fares must produce higher modal transit share than expensive fares");
    }

    [Test]
    public void TrafficAssignment_AssignsFlowsAcrossCorridors()
    {
        float[,] dist = _roadGraph.ComputeDistanceMatrix(_grid.ZoneCount);
        float[,] walkDist = _roadGraph.ComputeWalkingDistanceMatrix(_grid.ZoneCount);
        _roadGraph.BuildPathCache(_grid.ZoneCount);

        var odMatrix = new ODMatrix(_grid.ZoneCount);
        var coveredZones = _transitManager.GetCoveredZoneIds(_grid, 3, _roadGraph, _parcelManager);

        odMatrix.Recalculate(
            hour: 8.0f,
            grid: _grid,
            distances: dist,
            hasTransit: true,
            coveredZoneIds: coveredZones,
            averageTicketPrice: 8.0f,
            walkingDistances: walkDist,
            transitManager: _transitManager,
            parcelManager: _parcelManager,
            roadGraph: _roadGraph
        );

        _trafficEngine.AssignFlows(odMatrix, _roadGraph, _grid, _transitManager);

        float maxCarVol = _roadGraph.Edges.Max(e => e.CurrentVolume);
        float maxPedVol = _roadGraph.Edges.Max(e => e.PedestrianVolume);

        Assert.That(maxCarVol, Is.GreaterThan(0f), "Arterial and bypass corridors must receive vehicle volume");
        Assert.That(maxPedVol, Is.GreaterThan(0f), "Sidewalks and walk paths must receive pedestrian volume");
    }

    [Test]
    public void Congestion_IncreasesTravelTimeOnBottleneck_ShiftsShortestPath()
    {
        var bottleneckEdge = _roadGraph.Edges.First(e => e.FromId != -1 && e.Capacity == 400f);
        bottleneckEdge.CurrentVolume = 0f;
        float freeFlowTime = bottleneckEdge.GetTravelTime();

        // Under heavy congestion (twice capacity)
        bottleneckEdge.CurrentVolume = 800f;
        float congestedTime = bottleneckEdge.GetTravelTime();

        Assert.That(congestedTime, Is.GreaterThan(freeFlowTime * 2.5f),
            "Bottleneck edge travel time should jump significantly under high congestion volume");

        // Clear volume
        bottleneckEdge.CurrentVolume = 0f;
    }

    [Test]
    public void ShortDistanceTrips_FavorWalkingOverLongDistanceTrips()
    {
        float[,] dist = _roadGraph.ComputeDistanceMatrix(_grid.ZoneCount);
        float[,] walkDist = _roadGraph.ComputeWalkingDistanceMatrix(_grid.ZoneCount);
        _roadGraph.BuildPathCache(_grid.ZoneCount);

        var odMatrix = new ODMatrix(_grid.ZoneCount);
        var coveredZones = _transitManager.GetCoveredZoneIds(_grid, 3, _roadGraph, _parcelManager);

        odMatrix.Recalculate(
            hour: 8.0f,
            grid: _grid,
            distances: dist,
            hasTransit: true,
            coveredZoneIds: coveredZones,
            averageTicketPrice: 8.0f,
            walkingDistances: walkDist,
            transitManager: _transitManager,
            parcelManager: _parcelManager,
            roadGraph: _roadGraph
        );

        // Find short distance trips (<400m) vs long distance trips (>700m)
        var shortTrips = odMatrix.DynamicTrips.Where(t => t.NetworkDistance < 400f && t.TotalTrips > 0.01f).ToList();
        var longTrips = odMatrix.DynamicTrips.Where(t => t.NetworkDistance > 700f && t.TotalTrips > 0.01f).ToList();

        Assert.That(shortTrips.Count, Is.GreaterThan(0), "Short distance trips should exist");
        Assert.That(longTrips.Count, Is.GreaterThan(0), "Long distance trips should exist");

        float shortWalkFraction = shortTrips.Sum(t => t.WalkTrips) / shortTrips.Sum(t => t.TotalTrips);
        float longWalkFraction = longTrips.Sum(t => t.WalkTrips) / longTrips.Sum(t => t.TotalTrips);

        Assert.That(shortWalkFraction, Is.GreaterThan(longWalkFraction),
            "Short distance trips (<400m) must have significantly higher walking proportion than long-distance trips");
    }
}
