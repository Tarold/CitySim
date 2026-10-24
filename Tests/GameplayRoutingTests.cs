using Godot;
using NUnit.Framework;
using CitySim.Simulation;
using CitySim.Tests.Simulation;
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

    [SetUp]
    public void Setup()
    {
        _grid = new CityGrid(20, 20, 64f);
        _roadGraph = new RoadGraph();
        _parcelManager = new ParcelManager();
        _parcelManager.Initialize(_roadGraph);
        _transitManager = new TransitManager();
        _economyManager = new EconomyManager();

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
        var (_, od, _) = MacroTestPipeline.Run(_grid, _roadGraph, _parcelManager, _transitManager, ticketFare: 8f, assignFlows: false);

        Assert.That(od.TotalTrips, Is.GreaterThan(0f));
        Assert.That(od.TotalCarTrips, Is.GreaterThan(0f), "Private cars must take trips");
        Assert.That(od.TotalTransitTrips, Is.GreaterThan(0f), "Transit line must attract commuters");
        Assert.That(od.TotalWalkTrips, Is.GreaterThan(0f), "Walk corridor must produce walking trips");

        float sumModes = od.TotalCarTrips + od.TotalTransitTrips + od.TotalWalkTrips;
        Assert.That(sumModes, Is.EqualTo(od.TotalTrips).Within(0.01f * od.TotalTrips), "Mode split must sum to total demand");
    }

    [Test]
    public void TransitCatchment_ModeChoiceShift_WhenTicketPriceChanges()
    {
        var (_, odCheap, _) = MacroTestPipeline.Run(_grid, _roadGraph, _parcelManager, _transitManager, ticketFare: 2f, assignFlows: false);
        var (_, odExpensive, _) = MacroTestPipeline.Run(_grid, _roadGraph, _parcelManager, _transitManager, ticketFare: 120f, assignFlows: false);

        float transitShareCheap = odCheap.TotalTransitTrips / odCheap.TotalTrips;
        float transitShareExpensive = odExpensive.TotalTransitTrips / odExpensive.TotalTrips;

        Assert.That(transitShareCheap, Is.GreaterThan(transitShareExpensive),
            "Cheap transit fares must produce higher modal transit share than expensive fares");
    }

    [Test]
    public void TrafficAssignment_AssignsFlowsAcrossCorridors()
    {
        MacroTestPipeline.Run(_grid, _roadGraph, _parcelManager, _transitManager, ticketFare: 8f);

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
        var (taz, od, _) = MacroTestPipeline.Run(_grid, _roadGraph, _parcelManager, _transitManager, ticketFare: 8f, assignFlows: false);
        int k = taz.ActiveZones.Count;

        var pairs = (from i in Enumerable.Range(0, k)
                     from j in Enumerable.Range(0, k)
                     where i != j
                     let total = od.GetCarTrips(i, j) + od.GetTransitTrips(i, j) + od.GetWalkTrips(i, j)
                     let time = od.GetTravelTime(i, j)
                     where total > 0.01f && time < float.MaxValue
                     orderby time
                     select (Time: time, Walk: od.GetWalkTrips(i, j), Total: total)).ToList();
        Assert.That(pairs.Count, Is.GreaterThanOrEqualTo(4), "Need inter-TAZ trips to compare");

        var shortTrips = pairs.Take(pairs.Count / 3).ToList();
        var longTrips = pairs.Skip(pairs.Count - pairs.Count / 3).ToList();

        float shortWalkFraction = shortTrips.Sum(t => t.Walk) / shortTrips.Sum(t => t.Total);
        float longWalkFraction = longTrips.Sum(t => t.Walk) / longTrips.Sum(t => t.Total);

        Assert.That(shortWalkFraction, Is.GreaterThan(longWalkFraction),
            "The shortest third of TAZ pairs must have a higher walking share than the longest third");
    }
}
