using Godot;
using NUnit.Framework;
using System.Collections.Generic;
using CitySim.Simulation;
using CitySim.Rendering;

namespace CitySim.Tests.Simulation;

[TestFixture]
public class UrbanAnalyticsAndTrafficTests
{
    private CityGrid _grid;
    private RoadGraph _roadGraph;
    private ODMatrix _odMatrix;
    private float[,] _distances;

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
        _distances = _roadGraph.ComputeDistanceMatrix(_grid.ZoneCount);
        _roadGraph.BuildPathCache(_grid.ZoneCount);

        _odMatrix = new ODMatrix(_grid.ZoneCount);
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
    public void Commercial_IncomingCommute_CalculatesDynamicOriginsAndModeSplit()
    {
        int resId = _grid.GetZoneId(1, 1);
        int comId = _grid.GetZoneId(2, 1);
        var comZone = _grid.GetZone(comId);

        // Inject simulated 3-mode trips
        _odMatrix.CarTrips[resId, comId] = 50f;
        _odMatrix.TransitTrips[resId, comId] = 30f;
        _odMatrix.WalkTrips[resId, comId] = 20f;
        _odMatrix.Trips[resId, comId] = 100f;

        var result = CommuteAnalytics.GetIncomingCommute(comZone, _grid, _odMatrix);

        Assert.That(result.totalTrips, Is.EqualTo(100f).Within(0.01f));
        Assert.That(result.incomingCar, Is.EqualTo(50f).Within(0.01f));
        Assert.That(result.incomingTransit, Is.EqualTo(30f).Within(0.01f));
        Assert.That(result.incomingWalk, Is.EqualTo(20f).Within(0.01f));
        Assert.That(result.carPct, Is.EqualTo(50f).Within(0.01f));
        Assert.That(result.transitPct, Is.EqualTo(30f).Within(0.01f));
        Assert.That(result.walkPct, Is.EqualTo(20f).Within(0.01f));
        Assert.That(result.carPct + result.transitPct + result.walkPct, Is.EqualTo(100f).Within(0.001f));

        Assert.That(result.origins.Count, Is.EqualTo(1));
        Assert.That(result.origins[0].Zone.Id, Is.EqualTo(resId));
        Assert.That(result.origins[0].Trips, Is.EqualTo(100f).Within(0.01f));
        Assert.That(result.origins[0].Percentage, Is.EqualTo(100f).Within(0.01f));
    }

    [Test]
    public void Commercial_IncomingCommute_SupportsReturnTripDirectionalBiasFallback()
    {
        int resId = _grid.GetZoneId(1, 1);
        int comId = _grid.GetZoneId(2, 1);
        var comZone = _grid.GetZone(comId);

        // During evening rush, trips flow Com -> Res
        _odMatrix.CarTrips[comId, resId] = 60f;
        _odMatrix.TransitTrips[comId, resId] = 20f;
        _odMatrix.WalkTrips[comId, resId] = 20f;
        _odMatrix.Trips[comId, resId] = 100f;

        // Res -> Com is 0
        _odMatrix.CarTrips[resId, comId] = 0f;
        _odMatrix.TransitTrips[resId, comId] = 0f;
        _odMatrix.WalkTrips[resId, comId] = 0f;
        _odMatrix.Trips[resId, comId] = 0f;

        var result = CommuteAnalytics.GetIncomingCommute(comZone, _grid, _odMatrix);

        Assert.That(result.totalTrips, Is.EqualTo(100f).Within(0.01f));
        Assert.That(result.carPct, Is.EqualTo(60f).Within(0.01f));
        Assert.That(result.transitPct, Is.EqualTo(20f).Within(0.01f));
        Assert.That(result.walkPct, Is.EqualTo(20f).Within(0.01f));
        Assert.That(result.carPct + result.transitPct + result.walkPct, Is.EqualTo(100f).Within(0.001f));
        Assert.That(result.origins.Count, Is.EqualTo(1));
        Assert.That(result.origins[0].Zone.Id, Is.EqualTo(resId));
    }

    [Test]
    public void Residential_CommuteAnalytics_CalculatesWorkplaceBreakdownAndTopDestinations()
    {
        int resId = _grid.GetZoneId(1, 1);
        int comId = _grid.GetZoneId(2, 1);
        int indId = _grid.GetZoneId(2, 2);
        var resZone = _grid.GetZone(resId);

        _odMatrix.CarTrips[resId, comId] = 50f;
        _odMatrix.TransitTrips[resId, comId] = 30f;
        _odMatrix.WalkTrips[resId, comId] = 20f;
        _odMatrix.Trips[resId, comId] = 100f;

        _odMatrix.CarTrips[resId, indId] = 30f;
        _odMatrix.TransitTrips[resId, indId] = 10f;
        _odMatrix.WalkTrips[resId, indId] = 10f;
        _odMatrix.Trips[resId, indId] = 50f;

        var result = CommuteAnalytics.GetResidentialCommute(resZone, _grid, _odMatrix);

        Assert.That(result.totalTrips, Is.EqualTo(150f).Within(0.01f));
        Assert.That(result.comTrips, Is.EqualTo(100f).Within(0.01f));
        Assert.That(result.indTrips, Is.EqualTo(50f).Within(0.01f));

        float expectedComPct = (100f / 150f) * 100f;
        float expectedIndPct = (50f / 150f) * 100f;
        Assert.That(result.comPct, Is.EqualTo(expectedComPct).Within(0.1f));
        Assert.That(result.indPct, Is.EqualTo(expectedIndPct).Within(0.1f));
        Assert.That(result.comPct + result.indPct, Is.EqualTo(100f).Within(0.01f));

        Assert.That(result.carPct + result.transitPct + result.walkPct, Is.EqualTo(100f).Within(0.001f));

        Assert.That(result.destinations.Count, Is.EqualTo(2));
        Assert.That(result.destinations[0].Zone.Id, Is.EqualTo(comId));
        Assert.That(result.destinations[1].Zone.Id, Is.EqualTo(indId));
    }

    [Test]
    public void CityOverview_CalculatesDynamicJobStructureAndCityWideModeSplit()
    {
        int resId = _grid.GetZoneId(1, 1);
        int comId = _grid.GetZoneId(2, 1);
        int indId = _grid.GetZoneId(2, 2);

        _odMatrix.CarTrips[resId, comId] = 50f;
        _odMatrix.TransitTrips[resId, comId] = 30f;
        _odMatrix.WalkTrips[resId, comId] = 20f;
        _odMatrix.CarTrips[resId, indId] = 40f;
        _odMatrix.TransitTrips[resId, indId] = 10f;
        _odMatrix.WalkTrips[resId, indId] = 10f;

        var city = CommuteAnalytics.GetCityOverview(_grid, _odMatrix);

        Assert.That(city.comJobs, Is.EqualTo(3000));
        Assert.That(city.indJobs, Is.EqualTo(2000));
        Assert.That(city.totalJobs, Is.EqualTo(5000));
        Assert.That(city.comPct, Is.EqualTo(60f).Within(0.01f));
        Assert.That(city.indPct, Is.EqualTo(40f).Within(0.01f));

        Assert.That(city.totalCarTrips, Is.EqualTo(90f).Within(0.01f));
        Assert.That(city.totalTransitTrips, Is.EqualTo(40f).Within(0.01f));
        Assert.That(city.totalWalkTrips, Is.EqualTo(30f).Within(0.01f));
        Assert.That(city.totalTrips, Is.EqualTo(160f).Within(0.01f));
        Assert.That(city.carPct + city.transitPct + city.walkPct, Is.EqualTo(100f).Within(0.001f));
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

    [Test]
    public void CommuteAnalytics_ComputeCommuteTargets_ResolvesResidentialOriginsForCommercialZone()
    {
        int resId = _grid.GetZoneId(1, 1);
        int comId = _grid.GetZoneId(2, 1);

        // Evening return bias: trips flow from commercial to residential
        _odMatrix.Trips[comId, resId] = 50f;
        _odMatrix.Trips[resId, comId] = 0f;

        var targets = CommuteAnalytics.ComputeCommuteTargets(comId, _grid, _odMatrix, _distances, _roadGraph);

        Assert.That(targets.Count, Is.GreaterThan(0), "Should find residential origin even when directional bias favors return trips");
        Assert.That(targets[0].ZoneId, Is.EqualTo(resId));
        Assert.That(targets[0].Volume, Is.EqualTo(50f).Within(0.01f));
        Assert.That(targets[0].EdgePath.Count, Is.GreaterThan(0));
    }

    [Test]
    public void Multimodal_ThreeModeChoiceModel_ConservesTripsAndSumsTo100Percent()
    {
        var walkingDistances = _roadGraph.ComputeWalkingDistanceMatrix(_grid.ZoneCount);
        _odMatrix.Recalculate(8f, _grid, _distances, hasTransit: false, walkingDistances: walkingDistances);

        Assert.That(_odMatrix.TotalTrips, Is.GreaterThan(0f));
        Assert.That(_odMatrix.TotalCarTrips + _odMatrix.TotalTransitTrips + _odMatrix.TotalWalkTrips, 
            Is.EqualTo(_odMatrix.TotalTrips).Within(0.01f));

        var (carPct, transPct, walkPct) = CommuteAnalytics.CalculateModeSplit(
            _odMatrix.TotalCarTrips, _odMatrix.TotalTransitTrips, _odMatrix.TotalWalkTrips);
        Assert.That(carPct + transPct + walkPct, Is.EqualTo(100f).Within(0.001f));
        Assert.That(walkPct, Is.GreaterThan(0f));
    }

    [Test]
    public void WalkingMode_DominatesOnShortDistances_AndCarDominatesOnLongDistances()
    {
        // Setup a 10x1 grid to have clear short vs long distance pairs along a contiguous road
        var testGrid = new CityGrid(10, 1, 64f);
        for (int x = 0; x < 10; x++)
        {
            testGrid.ZoneCell(x, 0, ZoneType.Residential);
        }
        int originId = testGrid.GetZoneId(0, 0);
        int nearDestId = testGrid.GetZoneId(1, 0); // 64m away (adjacent cell)
        int farDestId = testGrid.GetZoneId(9, 0);  // 576m away (distant cell)

        testGrid.GetZone(originId).Population = 5000;
        testGrid.GetZone(nearDestId).Type = ZoneType.Commercial;
        testGrid.GetZone(nearDestId).Jobs = 2000;
        testGrid.GetZone(farDestId).Type = ZoneType.Commercial;
        testGrid.GetZone(farDestId).Jobs = 2000;

        var graph = new RoadGraph();
        graph.BuildFromGrid(testGrid);
        var dist = graph.RebuildAfterTopologyChange(testGrid.ZoneCount);
        var walkDist = graph.ComputeWalkingDistanceMatrix(testGrid.ZoneCount);

        var od = new ODMatrix(testGrid.ZoneCount);
        od.Recalculate(8f, testGrid, dist, hasTransit: false, walkingDistances: walkDist);

        float nearTotal = od.Trips[originId, nearDestId];
        float nearWalk = od.WalkTrips[originId, nearDestId];
        float nearCar = od.CarTrips[originId, nearDestId];

        float farTotal = od.Trips[originId, farDestId];
        float farWalk = od.WalkTrips[originId, farDestId];
        float farCar = od.CarTrips[originId, farDestId];

        Assert.That(nearTotal, Is.GreaterThan(0f));
        Assert.That(farTotal, Is.GreaterThan(0f));

        // On short adjacent distance (64m), walking share dominates over car
        float nearWalkShare = nearWalk / nearTotal;
        float nearCarShare = nearCar / nearTotal;
        Assert.That(nearWalkShare, Is.GreaterThan(nearCarShare), "Walking must dominate on short distances");

        // On long distance (576m), car share dominates overwhelmingly over walking
        float farWalkShare = farWalk / farTotal;
        float farCarShare = farCar / farTotal;
        Assert.That(farCarShare, Is.GreaterThan(farWalkShare), "Car must dominate on long distances");
        Assert.That(farCarShare, Is.GreaterThan(0.90f), "Car share must be > 90% for far trips");
    }

    [Test]
    public void TrafficEngine_AssignFlows_PopulatesVehicleAndPedestrianVolumes()
    {
        var walkingDistances = _roadGraph.ComputeWalkingDistanceMatrix(_grid.ZoneCount);
        _odMatrix.Recalculate(8f, _grid, _distances, hasTransit: false, walkingDistances: walkingDistances);

        var trafficEngine = new TrafficEngine();
        trafficEngine.AssignFlows(_odMatrix, _roadGraph, _grid);

        float maxVehicleVol = trafficEngine.GetMaxVolume(_roadGraph);
        float maxPedVol = trafficEngine.GetMaxPedestrianVolume(_roadGraph);

        Assert.That(maxVehicleVol, Is.GreaterThan(0f), "Vehicle flow must be assigned to road edges");
        Assert.That(maxPedVol, Is.GreaterThan(0f), "Pedestrian flow must be assigned to sidewalk edges");

        float avgPedCongestion = trafficEngine.GetAveragePedestrianCongestion(_roadGraph);
        Assert.That(avgPedCongestion, Is.GreaterThanOrEqualTo(0f));
    }

    [Test]
    public void TrafficEngine_AssignFlows_ZeroDemandEdgesHaveZeroFlow()
    {
        // Build a grid with active zones on one side and an inactive isolated road on the other
        var testGrid = new CityGrid(5, 5, 64f);
        testGrid.ZoneCell(0, 0, ZoneType.Residential, population: 1000);
        testGrid.ZoneCell(1, 0, ZoneType.Commercial, jobs: 1000);

        // Inactive zone pair far away
        testGrid.ZoneCell(4, 4, ZoneType.Residential, population: 0);
        testGrid.ZoneCell(4, 3, ZoneType.Commercial, jobs: 0);

        var graph = new RoadGraph();
        graph.BuildFromGrid(testGrid);
        var dist = graph.RebuildAfterTopologyChange(testGrid.ZoneCount);
        var walkDist = graph.ComputeWalkingDistanceMatrix(testGrid.ZoneCount);

        var od = new ODMatrix(testGrid.ZoneCount);
        od.Recalculate(8f, testGrid, dist, hasTransit: false, walkingDistances: walkDist);

        var trafficEngine = new TrafficEngine();
        trafficEngine.AssignFlows(od, graph, testGrid);

        // Find edge between inactive zones (4, 4) and (4, 3)
        int inactiveZ1 = testGrid.GetZoneId(4, 4);
        int inactiveZ2 = testGrid.GetZoneId(4, 3);
        var inactiveEdge = graph.Edges.Find(e => (e.FromId == inactiveZ1 && e.ToId == inactiveZ2) ||
                                                 (e.FromId == inactiveZ2 && e.ToId == inactiveZ1));

        Assert.That(inactiveEdge, Is.Not.Null);
        Assert.That(inactiveEdge.CurrentVolume, Is.EqualTo(0f), "Zero demand edge must have zero vehicle volume");
        Assert.That(inactiveEdge.PedestrianVolume, Is.EqualTo(0f), "Zero demand edge must have zero pedestrian volume");
    }
}
