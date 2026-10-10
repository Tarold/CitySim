using Godot;
using NUnit.Framework;
using CitySim.Simulation;
using System.Collections.Generic;
using System.Linq;

namespace CitySim.Tests.Simulation;

[TestFixture]
public class TutorialAndVectorTransitTests
{
    private RoadGraph _roadGraph;
    private ParcelManager _parcelManager;
    private TransitManager _transitManager;
    private TrafficEngine _trafficEngine;
    private CarTrafficManager _carTrafficManager;

    [SetUp]
    public void Setup()
    {
        _roadGraph = new RoadGraph();
        _parcelManager = new ParcelManager();
        _parcelManager.Initialize(_roadGraph);
        _transitManager = new TransitManager();
        _trafficEngine = new TrafficEngine();
        _carTrafficManager = new CarTrafficManager();
    }

    [Test]
    public void MidRoadTransitStopInsertion_SplitsEdgeAndRegistersValidStopWithPathContinuity()
    {
        // 1. Setup a straight road edge between Node 1 (0, 0) and Node 2 (200, 0)
        var n1 = _roadGraph.CreateNode(new Vector2(0, 0));
        var n2 = _roadGraph.CreateNode(new Vector2(200, 0));
        var curve = new CurveSegment(n1.WorldPosition, n2.WorldPosition);
        _roadGraph.AddCurvedRoadSegment(n1.Id, n2.Id, curve);

        int origEdgeId = _roadGraph.FindEdgeId(n1.Id, n2.Id);
        Assert.That(origEdgeId, Is.Not.EqualTo(-1));

        // 2. Insert mid-road stop by clicking/splitting at (100, 0)
        Vector2 clickPos = new Vector2(100, 0);
        var midNode = _roadGraph.SplitEdgeAtPoint(origEdgeId, clickPos);

        Assert.That(midNode, Is.Not.Null);
        Assert.That(midNode.WorldPosition.X, Is.EqualTo(100f).Within(1f));
        Assert.That(midNode.WorldPosition.Y, Is.EqualTo(0f).Within(1f));

        // 3. Verify graph continuity: n1 -> midNode and midNode -> n2
        int edge1 = _roadGraph.FindEdgeId(n1.Id, midNode.Id);
        int edge2 = _roadGraph.FindEdgeId(midNode.Id, n2.Id);
        Assert.That(edge1, Is.Not.EqualTo(-1));
        Assert.That(edge2, Is.Not.EqualTo(-1));

        var fullPath = _roadGraph.GetShortestNodePath(n1.Id, n2.Id);
        Assert.That(fullPath, Is.Not.Null);
        Assert.That(fullPath, Is.EqualTo(new List<int> { n1.Id, midNode.Id, n2.Id }));

        // 4. Create a Transit Route with the mid-road stop
        _transitManager.RegisterStop(n1.Id, -1, "Start Station");
        _transitManager.RegisterStop(midNode.Id, -1, "Mid-Road Stop");
        _transitManager.RegisterStop(n2.Id, -1, "End Station");

        var route = _transitManager.CreateRoute(
            "Test Line",
            fullPath,
            new List<int> { n1.Id, midNode.Id, n2.Id },
            Colors.Blue,
            isLoop: false,
            fleetSize: 2
        );

        Assert.That(route, Is.Not.Null);
        Assert.That(route.StopNodeIds.Contains(midNode.Id), Is.True);
        Assert.That(_transitManager.Vehicles.Count, Is.GreaterThanOrEqualTo(2));

        // 5. Test bus stopping at the mid-road stop
        var bus = _transitManager.Vehicles[0];
        bus.CurrentPathIndex = 0; // At n1
        bus.ProgressToNext = 0.99f; // Right before arriving at midNode

        _transitManager.Update(0.1f, 1.0f, _roadGraph, null);

        // Bus advances to midNode and dwells
        Assert.That(bus.CurrentPathIndex, Is.EqualTo(1));
        Assert.That(bus.State, Is.EqualTo(VehicleState.AtStop));
        Assert.That(bus.StateTimer, Is.GreaterThan(0f));
    }

    [Test]
    public void LocalizedTrafficTermination_DownstreamEdgeHasZeroVolumeAndNoVehicles()
    {
        // 1. Setup a long continuous road: A (0, 0) -> C (600, 0)
        var nodeA = _roadGraph.CreateNode(new Vector2(0, 0));
        var nodeC = _roadGraph.CreateNode(new Vector2(600, 0));
        var curve = new CurveSegment(nodeA.WorldPosition, nodeC.WorldPosition);
        _roadGraph.AddCurvedRoadSegment(nodeA.Id, nodeC.Id, curve, capacity: 1000f);

        int origEdgeId = _roadGraph.FindEdgeId(nodeA.Id, nodeC.Id);
        _parcelManager.RefreshParcels(_roadGraph);

        // Find a parcel in the first third of the road (around x = 200)
        var parcel = _parcelManager.FindClosestParcel(new Vector2(200, 20));
        Assert.That(parcel, Is.Not.Null);

        // 2. Zone parcel as Residential and ensure dedicated access node
        parcel.SetZone(ZoneType.Residential, population: 300, residentialCap: 300);
        var accessNode = parcel.EnsureAccessNode(_roadGraph);
        Assert.That(accessNode, Is.Not.Null);
        Assert.That(parcel.AccessNodeId, Is.EqualTo(accessNode.Id));

        // Graph now has A -> accessNode (ingress) and accessNode -> C (continuation)
        int ingressEdgeId = _roadGraph.FindEdgeId(nodeA.Id, accessNode.Id);
        int continuationEdgeId = _roadGraph.FindEdgeId(accessNode.Id, nodeC.Id);

        Assert.That(ingressEdgeId, Is.Not.EqualTo(-1));
        Assert.That(continuationEdgeId, Is.Not.EqualTo(-1));

        // 3. Setup OD travel demand with origin at nodeA and destination at the parcel
        var od = new DynamicODMatrix(0);
        od.DynamicTrips.Add(new DynamicTrip
        {
            OriginAccessNodeId = nodeA.Id,
            OriginPos = nodeA.WorldPosition,
            OriginType = ZoneType.Commercial,
            DestAccessNodeId = parcel.AccessNodeId,
            DestPos = parcel.Center,
            DestType = ZoneType.Residential,
            CarTrips = 80f,
            TotalTrips = 80f
        });

        // 4. Assign traffic flows
        _trafficEngine.AssignFlows(od, _roadGraph);

        // 5. Verify localized traffic termination
        float ingressVolume = _roadGraph.Edges[ingressEdgeId].CurrentVolume;
        float continuationVolume = _roadGraph.Edges[continuationEdgeId].CurrentVolume;

        Assert.That(ingressVolume, Is.EqualTo(80f).Within(0.01f), "Ingress edge must receive traffic volume leading to destination parcel");
        Assert.That(continuationVolume, Is.EqualTo(0f), "Downstream continuation edge must have strictly zero assigned volume");

        // 6. Verify CarTrafficManager demand filtering
        Assert.That(CarTrafficManager.HasDemand(_roadGraph.Edges[ingressEdgeId]), Is.True);
        Assert.That(CarTrafficManager.HasDemand(_roadGraph.Edges[continuationEdgeId]), Is.False);

        // 7. Verify visual cars only populate and travel on the ingress edge
        _carTrafficManager.Initialize(_roadGraph);
        var activeDemandEdges = _carTrafficManager.GetActiveDemandEdgeIds(_roadGraph);

        Assert.That(activeDemandEdges.Contains(ingressEdgeId), Is.True);
        Assert.That(activeDemandEdges.Contains(continuationEdgeId), Is.False);

        // Spawn a car on the ingress edge and advance
        var car = _carTrafficManager.CreateCar(ingressEdgeId, progress: 0.98f, speed: 50f);
        _carTrafficManager.Update(0.1f, 1.0f, _roadGraph, null);

        // Car reached end of ingress edge; since continuation has zero demand, car must NOT enter continuation edge
        Assert.That(car.EdgeId, Is.Not.EqualTo(continuationEdgeId));
    }

    [Test]
    public void TutorialCityGenerator_GeneratesCurvedRoadsParcelsAndTransitRoute()
    {
        var grid = new CityGrid(20, 20, 64f);
        var economy = new EconomyManager();

        TutorialCityGenerator.Generate(grid, _roadGraph, _parcelManager, _transitManager, economy);

        // 1. Verify RoadGraph has rich curved network
        Assert.That(_roadGraph.Nodes.Count, Is.GreaterThanOrEqualTo(12));
        Assert.That(_roadGraph.Edges.Count, Is.GreaterThanOrEqualTo(20));

        int curvedEdges = _roadGraph.Edges.Count(e => e.Curve != null && e.Curve.IsCurved);
        Assert.That(curvedEdges, Is.GreaterThanOrEqualTo(8), "Tutorial city must showcase organic curved Bézier roads");

        // 2. Verify pre-zoned roadside parcels with starter population and employment
        Assert.That(_parcelManager.Parcels.Count, Is.GreaterThan(0));
        Assert.That(_parcelManager.TotalPopulation, Is.GreaterThan(500), "West suburbs must contain starter residential population");
        Assert.That(_parcelManager.TotalJobs, Is.GreaterThan(300), "Downtown and industrial zones must contain starter jobs");

        var activeParcels = _parcelManager.ActiveParcels.ToList();
        Assert.That(activeParcels.Any(p => p.ZoneType == ZoneType.Residential), Is.True);
        Assert.That(activeParcels.Any(p => p.ZoneType == ZoneType.Commercial), Is.True);
        Assert.That(activeParcels.Any(p => p.ZoneType == ZoneType.Industrial), Is.True);

        // 3. Verify default public transit bus line
        Assert.That(_transitManager.Routes.Count, Is.GreaterThanOrEqualTo(1));
        var defaultRoute = _transitManager.Routes[0];
        Assert.That(defaultRoute.StopNodeIds.Count, Is.GreaterThanOrEqualTo(4));
        Assert.That(defaultRoute.PathNodeIds.Count, Is.GreaterThan(defaultRoute.StopNodeIds.Count));
        Assert.That(_transitManager.Vehicles.Count(v => v.RouteId == defaultRoute.Id), Is.GreaterThanOrEqualTo(2));

        // 4. Verify economy balance
        Assert.That(economy.Money, Is.EqualTo(75000f));

        // 5. Verify OD calculation and traffic assignment on curved tutorial city
        _roadGraph.BuildPathCache(grid.ZoneCount);
        var od = new DynamicODMatrix(grid.ZoneCount);
        od.Recalculate(8.0f, grid, null, hasTransit: true, parcelManager: _parcelManager, roadGraph: _roadGraph);

        Assert.That(od.TotalTrips, Is.GreaterThan(0f));
        _trafficEngine.AssignFlows(od, _roadGraph);
        float maxVol = _trafficEngine.GetMaxVolume(_roadGraph);
        Assert.That(maxVol, Is.GreaterThan(0f));
    }

    [Test]
    public void CityGrid_BoundsProtection_ReturnsNullOrZeroWithoutThrowing()
    {
        var grid = new CityGrid(10, 10, 64f);

        // 1. GetZone bounds protection
        Assert.That(grid.GetZone(-1), Is.Null);
        Assert.That(grid.GetZone(-100), Is.Null);
        Assert.That(grid.GetZone(100), Is.Null);
        Assert.That(grid.GetZone(9999), Is.Null);

        // 2. 2D GetZone bounds protection
        Assert.That(grid.GetZone(-1, 5), Is.Null);
        Assert.That(grid.GetZone(5, -1), Is.Null);
        Assert.That(grid.GetZone(10, 5), Is.Null);
        Assert.That(grid.GetZone(5, 10), Is.Null);

        // 3. GetWorldCenter bounds protection
        Assert.That(grid.GetWorldCenter(-1), Is.EqualTo(Vector2.Zero));
        Assert.That(grid.GetWorldCenter(-50), Is.EqualTo(Vector2.Zero));
        Assert.That(grid.GetWorldCenter(100), Is.EqualTo(Vector2.Zero));
        Assert.That(grid.GetWorldCenter(999), Is.EqualTo(Vector2.Zero));

        // 4. ZoneCell and DezoneCell bounds protection
        Assert.That(grid.ZoneCell(-1, ZoneType.Residential), Is.False);
        Assert.That(grid.ZoneCell(100, ZoneType.Residential), Is.False);
        Assert.That(grid.DezoneCell(-1), Is.False);
        Assert.That(grid.DezoneCell(100), Is.False);
    }

    [Test]
    public void TransitManager_VectorStops_CoverageMethodsExecuteWithoutThrowing()
    {
        var grid = new CityGrid(10, 10, 64f);
        int resZoneId = grid.GetZoneId(3, 3);
        grid.ZoneCell(resZoneId, ZoneType.Residential, population: 500);

        var node = _roadGraph.CreateNode(grid.GetWorldCenter(resZoneId));

        // Stop with ZoneId = -1 (vector road stop)
        _transitManager.RegisterStop(node.Id, -1, "Vector Curve Station");

        // 1. GetCoveredZoneIds must execute safely without throwing IndexOutOfRangeException
        HashSet<int>? covered = null;
        Assert.DoesNotThrow(() =>
        {
            covered = _transitManager.GetCoveredZoneIds(grid, 3, _roadGraph, _parcelManager);
        });

        Assert.That(covered, Is.Not.Null);
        Assert.That(covered.Contains(resZoneId), Is.True, "Vector stop at node position must cover adjacent grid zone");

        // 2. GetTransitCoverage must execute safely without throwing
        float coverage = 0f;
        Assert.DoesNotThrow(() =>
        {
            coverage = _transitManager.GetTransitCoverage(grid, _roadGraph, _parcelManager);
        });

        Assert.That(coverage, Is.GreaterThan(0f), "Vector stop within walking distance must provide transit coverage");
    }

    [Test]
    public void TransitManager_RoadsideResidentialParcels_ContributeToTransitCoverage()
    {
        // Grid with no residential zones
        var grid = new CityGrid(10, 10, 64f);

        var n1 = _roadGraph.CreateNode(new Vector2(100, 100));
        var n2 = _roadGraph.CreateNode(new Vector2(300, 100));
        _roadGraph.AddCurvedRoadSegment(n1.Id, n2.Id, new CurveSegment(n1.WorldPosition, n2.WorldPosition));

        _parcelManager.RefreshParcels(_roadGraph);
        Assert.That(_parcelManager.Parcels.Count, Is.GreaterThan(0));

        // Zone a roadside parcel as residential
        var resParcel = _parcelManager.Parcels[0];
        resParcel.SetZone(ZoneType.Residential, population: 100);

        // Transit stop located at n1 (near parcel)
        _transitManager.RegisterStop(n1.Id, -1, "Roadside Station");

        float coverage = _transitManager.GetTransitCoverage(grid, _roadGraph, _parcelManager);
        Assert.That(coverage, Is.GreaterThan(0f), "Roadside residential parcels must be included in transit coverage");
    }

    [Test]
    public void TutorialCityGenerator_ProducesNonOverlappingParcels()
    {
        var grid = new CityGrid(20, 20, 64f);
        var economy = new EconomyManager();

        TutorialCityGenerator.Generate(grid, _roadGraph, _parcelManager, _transitManager, economy);

        var parcels = _parcelManager.Parcels;
        Assert.That(parcels.Count, Is.GreaterThan(15), "Tutorial city must generate a realistic parcel set");

        // Comprehensive O(N^2) pairwise polygon intersection check across entire city
        for (int i = 0; i < parcels.Count; i++)
        {
            for (int j = i + 1; j < parcels.Count; j++)
            {
                bool overlaps = ParcelManager.ParcelsOverlap(parcels[i], parcels[j], overlapTolerance: 0.5f);
                Assert.That(overlaps, Is.False,
                    $"Collision detected! Parcel #{parcels[i].Id} (Edge {parcels[i].EdgeId}) and Parcel #{parcels[j].Id} (Edge {parcels[j].EdgeId}) overlap!");
            }
        }
    }

    [Test]
    public void ParcelManager_SharpInsideCurve_PreventsRearCornerOverlap()
    {
        // Sharp curve with tight curvature
        var n1 = _graphCreateNode(new Vector2(0, 0));
        var n2 = _graphCreateNode(new Vector2(160, 0));
        Vector2 sharpApex = new Vector2(80, 100);
        var sharpCurve = CurveSegment.CreateFromThreePoints(n1.WorldPosition, sharpApex, n2.WorldPosition);
        _roadGraph.AddCurvedRoadSegment(n1.Id, n2.Id, sharpCurve);

        int edgeId = _roadGraph.FindEdgeId(n1.Id, n2.Id);
        var parcels = _parcelManager.GenerateParcelsForEdge(_roadGraph.Edges[edgeId], _roadGraph);

        Assert.That(parcels.Count, Is.GreaterThan(0));

        // Verify no parcels on the sharp curve overlap
        for (int i = 0; i < parcels.Count; i++)
        {
            for (int j = i + 1; j < parcels.Count; j++)
            {
                bool overlaps = ParcelManager.ParcelsOverlap(parcels[i], parcels[j]);
                Assert.That(overlaps, Is.False, $"Adjacent parcels on sharp curve #{parcels[i].Id} and #{parcels[j].Id} must not overlap at rear corners");
            }
        }
    }

    private RoadNode _graphCreateNode(Vector2 pos) => _roadGraph.CreateNode(pos);
}
