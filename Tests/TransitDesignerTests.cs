using Godot;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using CitySim.Simulation;
using CitySim.UI;

namespace CitySim.Tests;

[TestFixture]
public class TransitDesignerTests
{
    private CityGrid _grid;
    private RoadGraph _roadGraph;
    private TrafficLightManager _trafficLights;
    private TransitManager _transitManager;

    [SetUp]
    public void Setup()
    {
        _grid = new CityGrid(10, 10, 64f);
        _roadGraph = new RoadGraph();
        _trafficLights = new TrafficLightManager();
        _transitManager = new TransitManager();

        // Build a 5-node horizontal road along y = 4 (from x = 2 to x = 6)
        for (int x = 2; x <= 6; x++)
        {
            _grid.ZoneCell(x, 4, ZoneType.Residential);
            _roadGraph.EnsureNode(_grid.GetZoneId(x, 4), _grid.GetWorldCenter(_grid.GetZoneId(x, 4)));
        }

        for (int x = 2; x < 6; x++)
        {
            int z1 = _grid.GetZoneId(x, 4);
            int z2 = _grid.GetZoneId(x + 1, 4);
            _roadGraph.AddRoadSegment(z1, z2);
        }

        _roadGraph.RebuildAfterTopologyChange(_grid.ZoneCount);
        _trafficLights.BuildIntersections(_roadGraph);
    }

    [Test]
    public void RoadGraph_GetShortestNodePath_ResolvesSequentialNodes()
    {
        int startId = _grid.GetZoneId(2, 4);
        int endId = _grid.GetZoneId(6, 4);

        var nodePath = _roadGraph.GetShortestNodePath(startId, endId);

        Assert.That(nodePath, Is.Not.Null);
        Assert.That(nodePath.Count, Is.EqualTo(5));
        Assert.That(nodePath[0], Is.EqualTo(startId));
        Assert.That(nodePath[4], Is.EqualTo(endId));

        for (int i = 0; i < 5; i++)
        {
            Assert.That(nodePath[i], Is.EqualTo(_grid.GetZoneId(2 + i, 4)));
        }
    }

    [Test]
    public void RoadGraph_GetShortestNodePath_SameNode_ReturnsSingleNodeList()
    {
        int nodeId = _grid.GetZoneId(3, 4);
        var path = _roadGraph.GetShortestNodePath(nodeId, nodeId);

        Assert.That(path, Is.Not.Null);
        Assert.That(path.Count, Is.EqualTo(1));
        Assert.That(path[0], Is.EqualTo(nodeId));
    }

    [Test]
    public void RoadGraph_GetShortestNodePath_DisconnectedNodes_ReturnsNull()
    {
        // Add an isolated node with no edges
        _grid.ZoneCell(9, 9, ZoneType.Commercial);
        int isolatedId = _grid.GetZoneId(9, 9);
        _roadGraph.EnsureNode(isolatedId, _grid.GetWorldCenter(isolatedId));

        int startId = _grid.GetZoneId(2, 4);
        var path = _roadGraph.GetShortestNodePath(startId, isolatedId);

        Assert.That(path, Is.Null);
    }

    [Test]
    public void CreateRoute_LinearRoute_RegistersStopsAndDeploysVehicles()
    {
        int startId = _grid.GetZoneId(2, 4);
        int midId = _grid.GetZoneId(4, 4);
        int endId = _grid.GetZoneId(6, 4);

        var pathNodes = _roadGraph.GetShortestNodePath(startId, endId);
        var stopNodes = new HashSet<int> { startId, midId, endId };
        var routeColor = new Color(0.65f, 0.25f, 0.95f);

        var route = _transitManager.CreateRoute(
            name: "Line 4 - Suburb Express",
            pathNodeIds: pathNodes,
            stopNodeIds: stopNodes,
            color: routeColor,
            isLoop: false,
            fleetSize: 4,
            ticketPrice: 15.0f
        );

        Assert.That(route, Is.Not.Null);
        Assert.That(_transitManager.Routes.Contains(route), Is.True);
        Assert.That(route.Name, Is.EqualTo("Line 4 - Suburb Express"));
        Assert.That(route.PathNodeIds.Count, Is.EqualTo(5));
        Assert.That(route.StopNodeIds.Count, Is.EqualTo(3));
        Assert.That(route.IsLoop, Is.False);
        Assert.That(route.TicketPrice, Is.EqualTo(15.0f));
        Assert.That(route.FleetSize, Is.EqualTo(4));

        // Verify stops are registered in transit manager with passenger queues
        Assert.That(_transitManager.Stops.ContainsKey(startId), Is.True);
        Assert.That(_transitManager.Stops.ContainsKey(midId), Is.True);
        Assert.That(_transitManager.Stops.ContainsKey(endId), Is.True);
        Assert.That(_transitManager.Stops[startId].WaitingPassengers, Is.GreaterThan(0f));

        // Verify vehicles are deployed and assigned to this route
        var routeVehicles = _transitManager.Vehicles.Where(v => v.RouteId == route.Id).ToList();
        Assert.That(routeVehicles.Count, Is.EqualTo(4));

        foreach (var v in routeVehicles)
        {
            Assert.That(v.CurrentPathIndex, Is.InRange(0, route.PathNodeIds.Count - 1));
            Assert.That(v.Capacity, Is.EqualTo(75));
            Assert.That(v.State, Is.EqualTo(VehicleState.Moving));
        }
    }

    [Test]
    public void CreateRoute_ClosedLoop_SetsIsLoopTrueAndSpacesVehiclesEvenly()
    {
        // Build a 4-node closed square loop: (2,2) -> (3,2) -> (3,3) -> (2,3) -> (2,2)
        int n0 = _grid.GetZoneId(2, 2);
        int n1 = _grid.GetZoneId(3, 2);
        int n2 = _grid.GetZoneId(3, 3);
        int n3 = _grid.GetZoneId(2, 3);

        int[] loopNodes = { n0, n1, n2, n3 };
        foreach (int nid in loopNodes)
        {
            _grid.ZoneCell(nid % 10, nid / 10, ZoneType.Commercial);
            _roadGraph.EnsureNode(nid, _grid.GetWorldCenter(nid));
        }

        _roadGraph.AddRoadSegment(n0, n1);
        _roadGraph.AddRoadSegment(n1, n2);
        _roadGraph.AddRoadSegment(n2, n3);
        _roadGraph.AddRoadSegment(n3, n0);
        _roadGraph.RebuildAfterTopologyChange(_grid.ZoneCount);

        var pathNodes = new List<int> { n0, n1, n2, n3 };
        var stopNodes = new HashSet<int> { n0, n2 };

        var loopRoute = _transitManager.CreateRoute(
            name: "Downtown Ring Loop",
            pathNodeIds: pathNodes,
            stopNodeIds: stopNodes,
            color: Colors.Cyan,
            isLoop: true,
            fleetSize: 4
        );

        Assert.That(loopRoute.IsLoop, Is.True);
        Assert.That(loopRoute.PathNodeIds.Count, Is.EqualTo(4));
        Assert.That(loopRoute.StopNodeIds.Count, Is.EqualTo(2));

        var vehicles = _transitManager.Vehicles.Where(v => v.RouteId == loopRoute.Id).ToList();
        Assert.That(vehicles.Count, Is.EqualTo(4));

        // In a 4-node loop with 4 vehicles, each node should have a vehicle
        var indices = vehicles.Select(v => v.CurrentPathIndex).Distinct().ToList();
        Assert.That(indices.Count, Is.EqualTo(4));
    }

    [Test]
    public void TransitManager_Update_AdvancesVehiclesAndBoardsPassengers()
    {
        int startId = _grid.GetZoneId(2, 4);
        int midId = _grid.GetZoneId(3, 4);
        int endId = _grid.GetZoneId(6, 4);

        var pathNodes = _roadGraph.GetShortestNodePath(startId, endId);
        var stopNodes = new HashSet<int> { startId, midId, endId };

        var route = _transitManager.CreateRoute(
            "Express Line",
            pathNodes,
            stopNodes,
            Colors.Orange,
            isLoop: false,
            fleetSize: 1,
            ticketPrice: 12.0f
        );

        // Put waiting passengers at midId stop
        _transitManager.Stops[midId].WaitingPassengers = 30f;

        var vehicle = _transitManager.Vehicles.First(v => v.RouteId == route.Id);
        vehicle.CurrentPathIndex = 0;
        vehicle.ProgressToNext = 0.10f;
        vehicle.Forward = true;
        vehicle.Passengers = 0f;

        // 1. Advance vehicle along road segment
        _transitManager.Update(delta: 0.2f, gameSpeedMultiplier: 1.0f, _roadGraph, _trafficLights);
        Assert.That(vehicle.ProgressToNext, Is.GreaterThan(0.10f));

        // 2. Position vehicle right before the mid stop (node index 1)
        vehicle.CurrentPathIndex = 0;
        vehicle.ProgressToNext = 0.95f;
        vehicle.Forward = true;

        // One step moves it onto node index 1 (midId) which is a designated stop
        _transitManager.Update(delta: 0.2f, gameSpeedMultiplier: 1.0f, _roadGraph, _trafficLights);

        Assert.That(vehicle.CurrentPathIndex, Is.EqualTo(1));
        Assert.That(vehicle.State, Is.EqualTo(VehicleState.AtStop));
        Assert.That(vehicle.StateTimer, Is.GreaterThan(0f));
        Assert.That(vehicle.Passengers, Is.GreaterThan(0f));
        Assert.That(route.DailyPassengers, Is.GreaterThan(0f));
        Assert.That(route.DailyRevenue, Is.GreaterThan(0f));
    }

    [Test]
    public void GameUI_CreateTransitRoute_PaletteAndInteractionModeDefined()
    {
        Assert.That(GameUI.RouteColorPalette.Length, Is.GreaterThanOrEqualTo(4));
        Assert.That(InteractionMode.CreateTransitRoute, Is.EqualTo((InteractionMode)7));

        var paletteNames = GameUI.RouteColorPalette.Select(p => p.Name).ToList();
        Assert.That(paletteNames.Any(n => n.Contains("Purple")), Is.True);
        Assert.That(paletteNames.Any(n => n.Contains("Orange")), Is.True);
        Assert.That(paletteNames.Any(n => n.Contains("Cyan")), Is.True);
    }

    [Test]
    public void RemoveRoute_DeallocatesVehicles_AndCleansOrphanedStopsWhilePreservingSharedStops()
    {
        int s1 = _grid.GetZoneId(2, 4);
        int s2 = _grid.GetZoneId(4, 4);
        int s3 = _grid.GetZoneId(6, 4);

        var pathA = _roadGraph.GetShortestNodePath(s1, s3);
        var routeA = _transitManager.CreateRoute("Route A", pathA, new HashSet<int> { s1, s2 }, Colors.Red, false, fleetSize: 3);

        var pathB = _roadGraph.GetShortestNodePath(s2, s3);
        var routeB = _transitManager.CreateRoute("Route B", pathB, new HashSet<int> { s2, s3 }, Colors.Blue, false, fleetSize: 2);

        Assert.That(_transitManager.Routes.Count, Is.EqualTo(2));
        Assert.That(_transitManager.Vehicles.Count, Is.EqualTo(5));
        Assert.That(_transitManager.Stops.ContainsKey(s1), Is.True);
        Assert.That(_transitManager.Stops.ContainsKey(s2), Is.True);
        Assert.That(_transitManager.Stops.ContainsKey(s3), Is.True);

        // Remove route A
        bool removed = _transitManager.RemoveRoute(routeA.Id);
        Assert.That(removed, Is.True);
        Assert.That(_transitManager.Routes.Count, Is.EqualTo(1));

        // Vehicles of route A must be gone, only route B vehicles remain
        Assert.That(_transitManager.Vehicles.Count, Is.EqualTo(2));
        Assert.That(_transitManager.Vehicles.All(v => v.RouteId == routeB.Id), Is.True);

        // Stop s1 was only in route A -> orphaned, must be removed
        Assert.That(_transitManager.Stops.ContainsKey(s1), Is.False);
        // Stop s2 is shared with route B -> must be kept
        Assert.That(_transitManager.Stops.ContainsKey(s2), Is.True);
        // Stop s3 belongs to route B -> must be kept
        Assert.That(_transitManager.Stops.ContainsKey(s3), Is.True);
    }

    [Test]
    public void Route_FleetSizeAndTicketPrice_CanBeAdjusted()
    {
        int s1 = _grid.GetZoneId(2, 4);
        int s2 = _grid.GetZoneId(6, 4);
        var path = _roadGraph.GetShortestNodePath(s1, s2);
        var route = _transitManager.CreateRoute("Adjustable Route", path, new HashSet<int> { s1, s2 }, Colors.Green, false, fleetSize: 2, ticketPrice: 10f);

        Assert.That(route.FleetSize, Is.EqualTo(2));
        Assert.That(_transitManager.Vehicles.Count(v => v.RouteId == route.Id), Is.EqualTo(2));

        // Resize fleet to 5
        bool fleetChanged = _transitManager.SetRouteFleetSize(route.Id, 5);
        Assert.That(fleetChanged, Is.True);
        Assert.That(route.FleetSize, Is.EqualTo(5));
        Assert.That(_transitManager.Vehicles.Count(v => v.RouteId == route.Id), Is.EqualTo(5));

        // Change ticket price
        bool priceChanged = _transitManager.SetRouteTicketPrice(route.Id, 20f);
        Assert.That(priceChanged, Is.True);
        Assert.That(route.TicketPrice, Is.EqualTo(20f));
    }

    [Test]
    public void DynamicTransitCoverage_ExpandsCoverageAndShiftsODModeSplit()
    {
        int zOrigin = _grid.GetZoneId(2, 4);
        int zDest = _grid.GetZoneId(6, 4);

        // Populate zones
        _grid.GetZone(zOrigin).Population = 10000;
        _grid.GetZone(zDest).Jobs = 8000;
        _grid.GetZone(zDest).Type = ZoneType.Commercial;

        var distMatrix = _roadGraph.RebuildAfterTopologyChange(_grid.ZoneCount);
        var odMatrix = new ODMatrix(_grid.ZoneCount);

        // Before transit route: 0 coverage, 100% car trips
        var initialCovered = _transitManager.GetCoveredZoneIds(_grid);
        Assert.That(initialCovered.Count, Is.EqualTo(0));

        odMatrix.Recalculate(8f, _grid, distMatrix, hasTransit: false, coveredZoneIds: initialCovered);
        Assert.That(odMatrix.Trips[zOrigin, zDest], Is.GreaterThan(0f));
        Assert.That(odMatrix.TransitTrips[zOrigin, zDest], Is.EqualTo(0f));
        Assert.That(odMatrix.CarTrips[zOrigin, zDest] + odMatrix.WalkTrips[zOrigin, zDest], Is.EqualTo(odMatrix.Trips[zOrigin, zDest]).Within(0.01f));

        // Create route covering both origin and destination
        var path = _roadGraph.GetShortestNodePath(zOrigin, zDest);
        _transitManager.CreateRoute("Commute Express", path, new HashSet<int> { zOrigin, zDest }, Colors.Yellow, false, fleetSize: 4);

        var coveredAfter = _transitManager.GetCoveredZoneIds(_grid, maxDistance: 3);
        Assert.That(coveredAfter.Contains(zOrigin), Is.True);
        Assert.That(coveredAfter.Contains(zDest), Is.True);

        float coverageRatio = _transitManager.GetTransitCoverage(_grid);
        Assert.That(coverageRatio, Is.GreaterThan(0f));

        // Recalculate with covered zones: mode split uses dynamic 3-mode choice model
        odMatrix.Recalculate(8f, _grid, distMatrix, hasTransit: true, coveredZoneIds: coveredAfter);
        float totalTrips = odMatrix.Trips[zOrigin, zDest];
        
        Assert.That(odMatrix.TransitTrips[zOrigin, zDest], Is.GreaterThan(0f));
        Assert.That(odMatrix.TransitTrips[zOrigin, zDest] + odMatrix.CarTrips[zOrigin, zDest] + odMatrix.WalkTrips[zOrigin, zDest], 
            Is.EqualTo(totalTrips).Within(0.01f));
        Assert.That(odMatrix.TransitTrips[zOrigin, zDest] / totalTrips, Is.GreaterThan(0.05f));
    }

    [Test]
    public void CalculateOptimalFleetSize_EnforcesMinimumTwo_AndScalesWithRouteLength()
    {
        // 1. Minimum 2 buses strictly enforced even for tiny routes
        Assert.That(TransitManager.CalculateOptimalFleetSize(1, false), Is.EqualTo(2));
        Assert.That(TransitManager.CalculateOptimalFleetSize(2, false), Is.EqualTo(2));
        Assert.That(TransitManager.CalculateOptimalFleetSize(2, true), Is.EqualTo(2));
        Assert.That(TransitManager.CalculateOptimalFleetSize(3, false), Is.EqualTo(2));

        // 2. Linear route scaling with length
        int fleetShort = TransitManager.CalculateOptimalFleetSize(5, false); // 8 segments -> 2
        int fleetMedium = TransitManager.CalculateOptimalFleetSize(10, false); // 18 segments -> 4
        int fleetLong = TransitManager.CalculateOptimalFleetSize(16, false); // 30 segments -> 6
        int fleetExtra = TransitManager.CalculateOptimalFleetSize(25, false); // 48 segments -> 10

        Assert.That(fleetShort, Is.GreaterThanOrEqualTo(2));
        Assert.That(fleetMedium, Is.GreaterThan(fleetShort));
        Assert.That(fleetLong, Is.GreaterThan(fleetMedium));
        Assert.That(fleetExtra, Is.GreaterThan(fleetLong));

        // 3. Loop route scaling with length
        int loopShort = TransitManager.CalculateOptimalFleetSize(4, true); // 4 segments -> 2
        int loopMedium = TransitManager.CalculateOptimalFleetSize(15, true); // 15 segments -> 3
        int loopLong = TransitManager.CalculateOptimalFleetSize(28, true); // 28 segments -> 6

        Assert.That(loopShort, Is.GreaterThanOrEqualTo(2));
        Assert.That(loopMedium, Is.GreaterThan(loopShort));
        Assert.That(loopLong, Is.GreaterThan(loopMedium));

        // 4. Default routes scale appropriately
        var defaultMgr = new TransitManager();
        defaultMgr.CreateDefaultRoutes(_grid, _roadGraph);
        foreach (var route in defaultMgr.Routes)
        {
            Assert.That(route.FleetSize, Is.GreaterThanOrEqualTo(2));
            Assert.That(defaultMgr.Vehicles.Count(v => v.RouteId == route.Id), Is.EqualTo(route.FleetSize));
        }
    }

    [Test]
    public void AntiBunching_PhysicalSeparation_PreventsVehiclesFromStacking()
    {
        int s1 = _grid.GetZoneId(2, 4);
        int s2 = _grid.GetZoneId(6, 4);
        var path = _roadGraph.GetShortestNodePath(s1, s2);
        var route = _transitManager.CreateRoute("AntiStack Line", path, new HashSet<int> { s1, s2 }, Colors.Red, false, fleetSize: 2);

        var vehicles = _transitManager.Vehicles.Where(v => v.RouteId == route.Id).ToList();
        Assert.That(vehicles.Count, Is.EqualTo(2));

        var leadVehicle = vehicles[0];
        var trailVehicle = vehicles[1];

        // Deliberately position both on segment 0, moving forward, very close to each other
        leadVehicle.CurrentPathIndex = 0;
        leadVehicle.ProgressToNext = 0.50f;
        leadVehicle.Forward = true;
        leadVehicle.State = VehicleState.Moving;

        trailVehicle.CurrentPathIndex = 0;
        trailVehicle.ProgressToNext = 0.40f; // Only 0.10 behind (below minStopDistance 0.20)
        trailVehicle.Forward = true;
        trailVehicle.State = VehicleState.Moving;

        // Run an update tick
        _transitManager.Update(delta: 0.2f, gameSpeedMultiplier: 1.0f, _roadGraph, _trafficLights);

        // Verify trailing vehicle stopped or was clamped to maintain spacing and never stacked onto lead
        float separation = leadVehicle.ProgressToNext - trailVehicle.ProgressToNext;
        Assert.That(separation, Is.GreaterThanOrEqualTo(0.18f), "Trailing vehicle must maintain physical separation and never stack");
        Assert.That(trailVehicle.ProgressToNext, Is.LessThan(leadVehicle.ProgressToNext));

        // Now test queueing behind a stopped lead vehicle (e.g. at red light or stop)
        leadVehicle.CurrentPathIndex = 0;
        leadVehicle.ProgressToNext = 0.85f;
        leadVehicle.State = VehicleState.AtStop;
        leadVehicle.StateTimer = 5.0f; // Lead vehicle is stopped

        trailVehicle.CurrentPathIndex = 0;
        trailVehicle.ProgressToNext = 0.70f;
        trailVehicle.State = VehicleState.Moving;

        // Run update tick
        _transitManager.Update(delta: 0.2f, gameSpeedMultiplier: 1.0f, _roadGraph, _trafficLights);

        // Trail vehicle must stop behind lead vehicle and not advance onto 0.85f
        Assert.That(trailVehicle.ProgressToNext, Is.LessThanOrEqualTo(0.651f), "Trailing vehicle must stop behind leading vehicle");
        Assert.That(leadVehicle.ProgressToNext - trailVehicle.ProgressToNext, Is.GreaterThanOrEqualTo(0.199f));
    }

    [Test]
    public void ScheduleAdherence_HeadwayHoldingControl_RegulatesBunchedVehicles()
    {
        int s1 = _grid.GetZoneId(2, 4);
        int s2 = _grid.GetZoneId(4, 4);
        int s3 = _grid.GetZoneId(6, 4);
        var path = _roadGraph.GetShortestNodePath(s1, s3);
        var route = _transitManager.CreateRoute("Holding Line", path, new HashSet<int> { s1, s2, s3 }, Colors.Green, false, fleetSize: 2);

        var vehicles = _transitManager.Vehicles.Where(v => v.RouteId == route.Id).ToList();
        var leadVehicle = vehicles[0];
        var trailVehicle = vehicles[1];

        // Lead vehicle is just ahead, e.g. on node 2 moving forward
        leadVehicle.CurrentPathIndex = 2;
        leadVehicle.ProgressToNext = 0.20f;
        leadVehicle.Forward = true;

        // Trail vehicle is arriving at stop s2 (node index 2 is stop) but right behind lead vehicle (severely bunched!)
        trailVehicle.CurrentPathIndex = 1;
        trailVehicle.ProgressToNext = 0.98f;
        trailVehicle.Forward = true;

        // One update step moves trail vehicle into stop s2
        _transitManager.Update(delta: 0.2f, gameSpeedMultiplier: 1.0f, _roadGraph, _trafficLights);

        Assert.That(trailVehicle.CurrentPathIndex, Is.EqualTo(2));
        Assert.That(trailVehicle.State, Is.EqualTo(VehicleState.AtStop));
        // Holding time should be applied because it is bunched with the vehicle in front
        Assert.That(trailVehicle.StateTimer, Is.GreaterThan(2.5f), "Holding time must be added to base dwell time for bunched vehicle");
        Assert.That(trailVehicle.IsHolding, Is.True, "IsHolding flag must be set during schedule holding control");
    }

    [Test]
    public void PeakHour_DynamicFrequencyModulation_AdjustsHeadwayAndHolding()
    {
        // 1. Peak hour detection
        Assert.That(TransitManager.IsPeakHour(7.0f), Is.True);
        Assert.That(TransitManager.IsPeakHour(8.5f), Is.True);
        Assert.That(TransitManager.IsPeakHour(9.0f), Is.True);
        Assert.That(TransitManager.IsPeakHour(12.0f), Is.False);
        Assert.That(TransitManager.IsPeakHour(17.0f), Is.True);
        Assert.That(TransitManager.IsPeakHour(18.3f), Is.True);
        Assert.That(TransitManager.IsPeakHour(20.0f), Is.False);

        // 2. Night hour detection
        Assert.That(TransitManager.IsNightHour(3.0f), Is.True);
        Assert.That(TransitManager.IsNightHour(23.0f), Is.True);
        Assert.That(TransitManager.IsNightHour(12.0f), Is.False);

        // 3. Operational Headway modulation
        int s1 = _grid.GetZoneId(2, 4);
        int s2 = _grid.GetZoneId(6, 4);
        var path = _roadGraph.GetShortestNodePath(s1, s2);
        var route = _transitManager.CreateRoute("Frequency Line", path, new HashSet<int> { s1, s2 }, Colors.Blue, false, fleetSize: 4);

        float baseHeadway = route.HeadwayMinutes;
        float peakHeadway = route.GetOperationalHeadwayMinutes(8.0f); // 08:00 AM rush hour
        float offPeakHeadway = route.GetOperationalHeadwayMinutes(12.0f); // 12:00 PM midday
        float nightHeadway = route.GetOperationalHeadwayMinutes(2.0f); // 02:00 AM late night

        Assert.That(peakHeadway, Is.LessThan(baseHeadway), "Peak hour headway must be shorter (more frequent buses)");
        Assert.That(offPeakHeadway, Is.EqualTo(baseHeadway), "Off-peak headway must match base headway");
        Assert.That(nightHeadway, Is.GreaterThan(baseHeadway), "Night headway must be relaxed (less frequent buses)");

        // 4. Update loop tracks current game hour
        _transitManager.Update(0.1f, 1.0f, _roadGraph, _trafficLights, gameHour: 17.5f);
        Assert.That(_transitManager.CurrentGameHour, Is.EqualTo(17.5f));
    }

    [Test]
    public void TerminusTurnaroundDispatch_RegulatesSimultaneousDepartures()
    {
        int s1 = _grid.GetZoneId(2, 4);
        int s2 = _grid.GetZoneId(6, 4);
        var path = _roadGraph.GetShortestNodePath(s1, s2);
        var route = _transitManager.CreateRoute("Terminus Line", path, new HashSet<int> { s1, s2 }, Colors.Purple, false, fleetSize: 2);

        var v1 = _transitManager.Vehicles[0];
        var v2 = _transitManager.Vehicles[1];

        // Place v1 just departed from terminus 0 into forward trip
        v1.CurrentPathIndex = 0;
        v1.ProgressToNext = 0.15f;
        v1.Forward = true;
        v1.State = VehicleState.Moving;

        // Place v2 at the terminus finishing layover
        v2.CurrentPathIndex = 0;
        v2.ProgressToNext = 0.0f;
        v2.Forward = true;
        v2.State = VehicleState.TerminusLayover;
        v2.StateTimer = 0.01f; // Ready to depart

        // Update tick expires v2's timer, but v1 is still in departure corridor
        _transitManager.Update(delta: 0.1f, gameSpeedMultiplier: 1.0f, _roadGraph, _trafficLights);

        // v2 must NOT have entered moving service simultaneously
        Assert.That(v2.State, Is.EqualTo(VehicleState.TerminusLayover), "Turnaround dispatch must hold v2 while v1 is still clearing terminus");
        Assert.That(v2.StateTimer, Is.GreaterThan(0f));
    }
}
