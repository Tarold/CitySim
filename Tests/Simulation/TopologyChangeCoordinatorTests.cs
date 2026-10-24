using Godot;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using CitySim.Simulation;

namespace CitySim.Tests.Simulation;

[TestFixture]
public class TopologyChangeCoordinatorTests
{
    private CityGrid _grid;
    private RoadGraph _graph;
    private TrafficLightManager _trafficLights;
    private ParcelManager _parcelManager;
    private TransitManager _transitManager;
    private CarTrafficManager _carTrafficManager;
    private PedestrianManager _pedestrianManager;
    private TazManager _tazManager;
    private TopologyChangeCoordinator _coordinator;

    // Ladder layout: a top street (y = 4) and a parallel bottom street (y = 5), x = 2..6,
    // joined at both ends so every top-street segment has a detour along the bottom street.
    [SetUp]
    public void Setup()
    {
        _grid = new CityGrid(10, 10, 64f);
        _graph = new RoadGraph();

        for (int y = 4; y <= 5; y++)
        {
            for (int x = 2; x <= 6; x++)
            {
                int id = Node(x, y);
                _grid.ZoneCell(id, ZoneType.Residential);
                _graph.EnsureNode(id, _grid.GetWorldCenter(id));
            }
            for (int x = 2; x < 6; x++)
            {
                _graph.AddRoadSegment(Node(x, y), Node(x + 1, y));
            }
        }
        _graph.AddRoadSegment(Node(2, 4), Node(2, 5));
        _graph.AddRoadSegment(Node(6, 4), Node(6, 5));

        _trafficLights = new TrafficLightManager();
        _trafficLights.BuildIntersections(_graph);
        _parcelManager = new ParcelManager();
        _parcelManager.Initialize(_graph);
        _transitManager = new TransitManager();
        _carTrafficManager = new CarTrafficManager();
        _carTrafficManager.Initialize(_graph, _parcelManager);
        _pedestrianManager = new PedestrianManager();
        _pedestrianManager.Initialize(_graph, _grid, _transitManager);
        _tazManager = new TazManager();

        _coordinator = new TopologyChangeCoordinator(
            _graph, _grid, _trafficLights, _parcelManager,
            _transitManager, _carTrafficManager, _pedestrianManager, _tazManager);
    }

    [TearDown]
    public void TearDown()
    {
        _coordinator.Dispose();
    }

    private int Node(int x, int y) => _grid.GetZoneId(x, y);

    private TransitRoute CreateTopStreetRoute(params int[] stopXs)
    {
        var path = Enumerable.Range(2, 5).Select(x => Node(x, 4)).ToList();
        var stops = stopXs.Select(x => Node(x, 4));
        return _transitManager.CreateRoute("Top", path, stops, Colors.Orange, isLoop: false, fleetSize: 2);
    }

    [Test]
    public void Constructor_NoGraphEdits_HasNoPendingChanges()
    {
        Assert.That(_coordinator.HasPendingChanges, Is.False);
        Assert.That(_coordinator.ApplyPendingChanges().Applied, Is.False);
        Assert.That(_coordinator.AppliedChangeCount, Is.EqualTo(0));
    }

    [Test]
    public void GraphEvents_AddRemoveSplitAndCreateNode_FlagPendingChanges()
    {
        _graph.AddRoadSegment(Node(4, 4), Node(4, 5));
        Assert.That(_coordinator.HasPendingChanges, Is.True, "EdgeAdded");
        _coordinator.ApplyChanges();

        _graph.RemoveRoadSegment(Node(4, 4), Node(4, 5));
        Assert.That(_coordinator.HasPendingChanges, Is.True, "EdgeRemoved");
        _coordinator.ApplyChanges();

        _graph.SplitEdge(_graph.FindEdgeId(Node(2, 4), Node(3, 4)), 0.5f);
        Assert.That(_coordinator.HasPendingChanges, Is.True, "EdgeSplit");
        _coordinator.ApplyChanges();

        _graph.CreateNode(new Vector2(10f, 10f));
        Assert.That(_coordinator.HasPendingChanges, Is.True, "NodeAdded");
    }

    [Test]
    public void ApplyChanges_AfterEdit_ClearsCachesPendingFlagAndMarksTazDirty()
    {
        _graph.PathCache[(Node(2, 4), Node(6, 4))] = new List<int> { 0 };
        _graph.WalkingPathCache[(Node(2, 4), Node(6, 4))] = new List<int> { 0 };
        _tazManager.IsDirty = false;
        _graph.AddRoadSegment(Node(4, 4), Node(4, 5));

        var result = _coordinator.ApplyChanges();

        Assert.That(result.Applied, Is.True);
        Assert.That(_coordinator.HasPendingChanges, Is.False);
        Assert.That(_coordinator.AppliedChangeCount, Is.EqualTo(1));
        Assert.That(_graph.PathCache, Is.Empty);
        Assert.That(_graph.WalkingPathCache, Is.Empty);
        Assert.That(_tazManager.IsDirty, Is.True);
    }

    [Test]
    public void ApplyPendingChanges_CalledTwice_RunsChainOnlyOnce()
    {
        _graph.AddRoadSegment(Node(4, 4), Node(4, 5));

        Assert.That(_coordinator.ApplyPendingChanges().Applied, Is.True);
        Assert.That(_coordinator.ApplyPendingChanges().Applied, Is.False);
        Assert.That(_coordinator.AppliedChangeCount, Is.EqualTo(1));
    }

    [Test]
    public void ApplyChanges_NewCrossStreet_BuildsTrafficLightsAtNewJunctions()
    {
        Assert.That(_trafficLights.Intersections.ContainsKey(Node(4, 4)), Is.False);

        _graph.AddRoadSegment(Node(4, 4), Node(4, 5));
        _coordinator.ApplyChanges();

        Assert.That(_trafficLights.Intersections.ContainsKey(Node(4, 4)), Is.True);
        Assert.That(_trafficLights.Intersections.ContainsKey(Node(4, 5)), Is.True);
    }

    [Test]
    public void ApplyChanges_DemolishedRoad_RemovesItsParcels()
    {
        int edgeId = _graph.FindEdgeId(Node(3, 4), Node(4, 4));
        int reverseId = _graph.FindEdgeId(Node(4, 4), Node(3, 4));

        _graph.RemoveRoadSegment(Node(3, 4), Node(4, 4));
        _coordinator.ApplyChanges();

        Assert.That(_parcelManager.GetParcelsForEdge(edgeId), Is.Empty);
        Assert.That(_parcelManager.GetParcelsForEdge(reverseId), Is.Empty);
        Assert.That(_parcelManager.Parcels.All(p => _graph.Edges[p.EdgeId].FromId != -1), Is.True);
    }

    [Test]
    public void ApplyChanges_CarOnDemolishedRoad_IsMovedOffRemovedEdge()
    {
        int edgeId = _graph.FindEdgeId(Node(3, 4), Node(4, 4));
        var car = new VisualCar { EdgeId = edgeId, Progress = 0.5f, Speed = 1f, CarColor = Colors.White };
        _carTrafficManager.Cars.Add(car);

        _graph.RemoveRoadSegment(Node(3, 4), Node(4, 4));
        _coordinator.ApplyChanges();

        Assert.That(car.EdgeId, Is.Not.EqualTo(edgeId));
    }

    [Test]
    public void ApplyChanges_DemolishedRouteSegment_ReroutesAlongDetourAndRedeploysFleet()
    {
        var route = CreateTopStreetRoute(2, 6);

        _graph.RemoveRoadSegment(Node(3, 4), Node(4, 4));
        var result = _coordinator.ApplyChanges();

        Assert.That(result.Transit.ReroutedRouteIds, Is.EqualTo(new[] { route.Id }));
        Assert.That(result.Transit.RemovedRouteIds, Is.Empty);
        Assert.That(TransitManager.IsRoutePathValid(route, _graph), Is.True);
        Assert.That(route.PathNodeIds.First(), Is.EqualTo(Node(2, 4)));
        Assert.That(route.PathNodeIds.Last(), Is.EqualTo(Node(6, 4)));
        Assert.That(route.PathNodeIds, Does.Contain(Node(4, 5)), "Detour must use the bottom street");
        Assert.That(_transitManager.Vehicles.Count(v => v.RouteId == route.Id), Is.EqualTo(route.FleetSize));
        Assert.That(_transitManager.Vehicles.Where(v => v.RouteId == route.Id)
            .All(v => v.CurrentPathIndex >= 0 && v.CurrentPathIndex < route.PathNodeIds.Count), Is.True);
    }

    [Test]
    public void ApplyChanges_SplitRouteEdge_InsertsJunctionIntoRoutePath()
    {
        var route = CreateTopStreetRoute(2, 6);

        var junction = _graph.SplitEdge(_graph.FindEdgeId(Node(3, 4), Node(4, 4)), 0.5f);
        var result = _coordinator.ApplyChanges();

        Assert.That(junction, Is.Not.Null);
        Assert.That(result.Transit.ReroutedRouteIds, Does.Contain(route.Id));
        Assert.That(TransitManager.IsRoutePathValid(route, _graph), Is.True);
        int idx = route.PathNodeIds.IndexOf(junction.Id);
        Assert.That(idx, Is.GreaterThan(0));
        Assert.That(route.PathNodeIds[idx - 1], Is.EqualTo(Node(3, 4)));
        Assert.That(route.PathNodeIds[idx + 1], Is.EqualTo(Node(4, 4)));
    }

    [Test]
    public void ApplyChanges_UnrelatedEdit_LeavesValidRouteUntouched()
    {
        var route = CreateTopStreetRoute(2, 6);
        var pathBefore = route.PathNodeIds.ToList();
        var vehiclesBefore = _transitManager.Vehicles.ToList();

        _graph.RemoveRoadSegment(Node(4, 5), Node(5, 5));
        var result = _coordinator.ApplyChanges();

        Assert.That(result.Transit.HasChanges, Is.False);
        Assert.That(route.PathNodeIds, Is.EqualTo(pathBefore));
        Assert.That(_transitManager.Vehicles, Is.EqualTo(vehiclesBefore));
    }

    [Test]
    public void ApplyChanges_RouteStopsDisconnected_RemovesRouteFleetAndOrphanStops()
    {
        var route = CreateTopStreetRoute(2, 6);

        // Cut both streets between x = 3 and x = 4: the two halves are no longer connected
        _graph.RemoveRoadSegment(Node(3, 4), Node(4, 4));
        _graph.RemoveRoadSegment(Node(3, 5), Node(4, 5));
        var result = _coordinator.ApplyChanges();

        Assert.That(result.Transit.RemovedRouteIds, Is.EqualTo(new[] { route.Id }));
        Assert.That(_transitManager.Routes, Is.Empty);
        Assert.That(_transitManager.Vehicles, Is.Empty);
        Assert.That(_transitManager.Stops, Is.Empty);
    }

    [Test]
    public void ApplyChanges_DemolishedStopNode_DropsStopAndKeepsRouteWithRemainingStops()
    {
        var route = CreateTopStreetRoute(2, 4, 6);

        _graph.DetachAndRemoveNode(Node(4, 4));
        var result = _coordinator.ApplyChanges();

        Assert.That(result.Transit.ReroutedRouteIds, Does.Contain(route.Id));
        Assert.That(route.StopNodeIds, Is.EquivalentTo(new[] { Node(2, 4), Node(6, 4) }));
        Assert.That(route.PathNodeIds, Does.Not.Contain(Node(4, 4)));
        Assert.That(TransitManager.IsRoutePathValid(route, _graph), Is.True);
        Assert.That(_transitManager.Stops.ContainsKey(Node(4, 4)), Is.False);
    }

    [Test]
    public void ApplyChanges_BrokenLoopClosure_ReconnectsLoop()
    {
        // Clockwise loop: along the top street, down at x = 6, back along the bottom street; closure (2,5) -> (2,4)
        var path = Enumerable.Range(2, 5).Select(x => Node(x, 4))
            .Concat(Enumerable.Range(2, 5).Reverse().Select(x => Node(x, 5)))
            .ToList();
        var route = _transitManager.CreateRoute("Loop", path, new[] { Node(2, 4), Node(6, 5) }, Colors.Cyan, isLoop: true, fleetSize: 2);
        Assert.That(TransitManager.IsRoutePathValid(route, _graph), Is.True);

        // Replace the closing link with a cross street at x = 4 so the loop has to close through it
        _graph.AddRoadSegment(Node(4, 4), Node(4, 5));
        _graph.RemoveRoadSegment(Node(2, 4), Node(2, 5));
        var result = _coordinator.ApplyChanges();

        Assert.That(result.Transit.ReroutedRouteIds, Does.Contain(route.Id));
        Assert.That(route.IsLoop, Is.True);
        Assert.That(TransitManager.IsRoutePathValid(route, _graph), Is.True);
        Assert.That(route.PathNodeIds[0], Is.EqualTo(Node(2, 4)));
    }

    [Test]
    public void Dispose_StopsTrackingGraphEvents()
    {
        _coordinator.Dispose();

        _graph.AddRoadSegment(Node(4, 4), Node(4, 5));

        Assert.That(_coordinator.HasPendingChanges, Is.False);
    }
}
