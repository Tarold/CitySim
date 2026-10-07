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
        Assert.That(odMatrix.CarTrips[zOrigin, zDest], Is.EqualTo(odMatrix.Trips[zOrigin, zDest]));

        // Create route covering both origin and destination
        var path = _roadGraph.GetShortestNodePath(zOrigin, zDest);
        _transitManager.CreateRoute("Commute Express", path, new HashSet<int> { zOrigin, zDest }, Colors.Yellow, false, fleetSize: 4);

        var coveredAfter = _transitManager.GetCoveredZoneIds(_grid, maxDistance: 3);
        Assert.That(coveredAfter.Contains(zOrigin), Is.True);
        Assert.That(coveredAfter.Contains(zDest), Is.True);

        float coverageRatio = _transitManager.GetTransitCoverage(_grid);
        Assert.That(coverageRatio, Is.GreaterThan(0f));

        // Recalculate with covered zones: mode split shifts 40% to transit, 60% to car
        odMatrix.Recalculate(8f, _grid, distMatrix, hasTransit: true, coveredZoneIds: coveredAfter);
        float totalTrips = odMatrix.Trips[zOrigin, zDest];
        Assert.That(odMatrix.TransitTrips[zOrigin, zDest], Is.EqualTo(totalTrips * 0.40f).Within(0.01f));
        Assert.That(odMatrix.CarTrips[zOrigin, zDest], Is.EqualTo(totalTrips * 0.60f).Within(0.01f));
    }
}
