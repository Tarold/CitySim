using Godot;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using CitySim.Simulation;

namespace CitySim.Tests.Simulation;

[TestFixture]
public class TazClusteringAndOptimizedODTests
{
    private CityGrid _grid;
    private RoadGraph _graph;
    private ParcelManager _parcelManager;
    private TransitManager _transitManager;
    private TazManager _tazManager;
    private OptimizedODMatrix _optimizedOd;
    private TrafficEngine _trafficEngine;

    [SetUp]
    public void Setup()
    {
        _grid = new CityGrid(10, 10, 64f);
        _graph = new RoadGraph();
        _parcelManager = new ParcelManager();
        _parcelManager.Initialize(_graph);
        _transitManager = new TransitManager();
        _tazManager = new TazManager { CellSize = 320f };
        _optimizedOd = new OptimizedODMatrix();
        _trafficEngine = new TrafficEngine();
    }

    [Test]
    public void TazManager_Rebuild_ClustersGridZonesIntoTazWithConnectors()
    {
        // 1. Create two residential zones at (1, 1) and (1, 2) and commercial at (8, 8)
        _grid.ZoneCell(1, 1, ZoneType.Residential);
        _grid.Zones[_grid.GetZoneId(1, 1)].Population = 500;
        _grid.ZoneCell(1, 2, ZoneType.Residential);
        _grid.Zones[_grid.GetZoneId(1, 2)].Population = 700;
        _grid.ZoneCell(8, 8, ZoneType.Commercial, jobs: 900);

        // Add corresponding road nodes
        int n1 = _grid.GetZoneId(1, 1);
        int n2 = _grid.GetZoneId(1, 2);
        int n3 = _grid.GetZoneId(8, 8);

        _graph.EnsureNode(n1, _grid.GetWorldCenter(n1));
        _graph.EnsureNode(n2, _grid.GetWorldCenter(n2));
        _graph.EnsureNode(n3, _grid.GetWorldCenter(n3));

        _graph.AddRoadSegment(n1, n2);
        _graph.AddRoadSegment(n2, n3);

        _tazManager.Rebuild(_grid, _parcelManager, _graph, _transitManager);

        Assert.That(_tazManager.ActiveZones.Count, Is.GreaterThanOrEqualTo(2));

        var resTaz = _tazManager.ActiveZones.FirstOrDefault(t => t.TotalPopulation > 0);
        Assert.That(resTaz, Is.Not.Null);
        Assert.That(resTaz.TotalPopulation, Is.EqualTo(1200));
        Assert.That(resTaz.PrimaryRoadNodeId, Is.GreaterThanOrEqualTo(0));
        Assert.That(resTaz.Connectors.Count, Is.GreaterThan(0));

        var comTaz = _tazManager.ActiveZones.FirstOrDefault(t => t.CommercialJobs > 0);
        Assert.That(comTaz, Is.Not.Null);
        Assert.That(comTaz.CommercialJobs, Is.EqualTo(900));
    }

    [Test]
    public void TazManager_Rebuild_SupportsColdStartCapacityWeighting()
    {
        // Newly zoned with population 0, but residential cap 7000
        _grid.ZoneCell(2, 2, ZoneType.Residential, population: 0);
        int n1 = _grid.GetZoneId(2, 2);
        _graph.EnsureNode(n1, _grid.GetWorldCenter(n1));

        _tazManager.Rebuild(_grid, _parcelManager, _graph, _transitManager);

        Assert.That(_tazManager.ActiveZones.Count, Is.EqualTo(1));
        var taz = _tazManager.ActiveZones[0];
        Assert.That(taz.TotalPopulation, Is.EqualTo(0));
        Assert.That(taz.TotalCapacity, Is.GreaterThan(0));
        Assert.That(taz.Centroid.DistanceTo(_grid.GetWorldCenter(n1)), Is.LessThan(0.1f));
    }

    [Test]
    public void RoadGraph_DijkstraWithPredecessors_ReturnsDistancesAndParentTrees()
    {
        var nodeA = _graph.CreateNode(new Vector2(0, 0));
        var nodeB = _graph.CreateNode(new Vector2(100, 0));
        var nodeC = _graph.CreateNode(new Vector2(200, 0));

        _graph.AddRoadSegment(nodeA.Id, nodeB.Id);
        _graph.AddRoadSegment(nodeB.Id, nodeC.Id);

        var (distances, parentEdges, parentNodes) = _graph.DijkstraWithPredecessors(nodeA.Id);

        Assert.That(distances[nodeA.Id], Is.EqualTo(0f));
        Assert.That(distances[nodeB.Id], Is.GreaterThan(0f).And.LessThan(float.MaxValue));
        Assert.That(distances[nodeC.Id], Is.GreaterThan(distances[nodeB.Id]));

        Assert.That(parentNodes[nodeC.Id], Is.EqualTo(nodeB.Id));
        Assert.That(parentNodes[nodeB.Id], Is.EqualTo(nodeA.Id));
        Assert.That(parentEdges[nodeC.Id], Is.GreaterThanOrEqualTo(0));
    }

    [Test]
    public void TazManager_PublicTransitTransfers_IdentifiesDirectAndTransferConnections()
    {
        var n1 = _graph.CreateNode(new Vector2(50, 50));
        var n2 = _graph.CreateNode(new Vector2(200, 50));
        var n3 = _graph.CreateNode(new Vector2(400, 50));
        _graph.AddRoadSegment(n1.Id, n2.Id);
        _graph.AddRoadSegment(n2.Id, n3.Id);

        _grid.ZoneCell(0, 0, ZoneType.Residential);
        _grid.Zones[_grid.GetZoneId(0, 0)].Population = 300;
        _grid.ZoneCell(6, 0, ZoneType.Commercial, jobs: 300);

        // Route 1 stops at n1 and n2
        var r1 = new TransitRoute { Id = 1, Name = "Route 1", FrequencyMinutes = 10f, FleetSize = 0 };
        r1.PathNodeIds.AddRange(new[] { n1.Id, n2.Id });
        r1.StopNodeIds.Add(n1.Id);
        r1.StopNodeIds.Add(n2.Id);
        _transitManager.Routes.Add(r1);

        // Route 2 stops at n2 and n3 (shares stop n2 with Route 1)
        var r2 = new TransitRoute { Id = 2, Name = "Route 2", FrequencyMinutes = 12f, FleetSize = 0 };
        r2.PathNodeIds.AddRange(new[] { n2.Id, n3.Id });
        r2.StopNodeIds.Add(n2.Id);
        r2.StopNodeIds.Add(n3.Id);
        _transitManager.Routes.Add(r2);

        _tazManager.Rebuild(_grid, _parcelManager, _graph, _transitManager);
        Assert.That(_tazManager.ActiveZones.Count, Is.GreaterThanOrEqualTo(2));

        var originTaz = _tazManager.ActiveZones[0];
        var destTaz = _tazManager.ActiveZones[_tazManager.ActiveZones.Count - 1];

        var snapshot = new TransitNetworkSnapshot();
        snapshot.Rebuild(_transitManager, _graph);
        var engine = new TransitEngine();
        engine.ComputeSkim(_tazManager, snapshot);

        var conn = engine.Skim.GetConnection(originTaz.Id, destTaz.Id);
        Assert.That(conn.IsConnected, Is.True, "TAZs must have a transit connection (direct or transfer)");
    }

    [Test]
    public void OptimizedODMatrix_Recalculate_ConservesTripsAndAssignsFlows()
    {
        var nA = _graph.CreateNode(new Vector2(50, 50));
        var nB = _graph.CreateNode(new Vector2(350, 50));
        _graph.AddRoadSegment(nA.Id, nB.Id);

        _grid.ZoneCell(0, 0, ZoneType.Residential);
        _grid.Zones[_grid.GetZoneId(0, 0)].Population = 1000;
        _grid.ZoneCell(5, 0, ZoneType.Commercial, jobs: 1000);

        _tazManager.Rebuild(_grid, _parcelManager, _graph, _transitManager);
        Assert.That(_tazManager.ActiveZones.Count, Is.EqualTo(2));

        _optimizedOd.Recalculate(_tazManager, _graph, 8.0f);

        Assert.That(_optimizedOd.TotalTrips, Is.GreaterThan(0f));
        Assert.That(_optimizedOd.TotalCarTrips + _optimizedOd.TotalTransitTrips + _optimizedOd.TotalWalkTrips,
            Is.EqualTo(_optimizedOd.TotalTrips).Within(0.5f));

        // Assign flows
        _trafficEngine.AssignFlowsFromTaz(_tazManager, _optimizedOd, _graph);

        float maxVolume = _graph.Edges.Max(e => e.CurrentVolume);
        Assert.That(maxVolume, Is.GreaterThan(0f), "Vehicle flow must be assigned to connecting road edges");
    }

    [Test]
    public void OptimizedODMatrix_InternalTrips_LoadNeighborhoodStreets()
    {
        // Single mixed-use TAZ with both residents and jobs
        var nA = _graph.CreateNode(new Vector2(50, 50));
        var nB = _graph.CreateNode(new Vector2(100, 50));
        _graph.AddRoadSegment(nA.Id, nB.Id);

        _grid.ZoneCell(1, 1, ZoneType.Residential);
        _grid.Zones[_grid.GetZoneId(1, 1)].Population = 800;
        _grid.ZoneCell(1, 2, ZoneType.Commercial, jobs: 800);

        _tazManager.Rebuild(_grid, _parcelManager, _graph, _transitManager);
        Assert.That(_tazManager.ActiveZones.Count, Is.EqualTo(1));

        _optimizedOd.Recalculate(_tazManager, _graph, 8.0f);

        _trafficEngine.AssignFlowsFromTaz(_tazManager, _optimizedOd, _graph);

        // Internal edges must receive volume from internal trips
        float totalVol = _graph.Edges.Sum(e => e.CurrentVolume);
        float totalPed = _graph.Edges.Sum(e => e.PedestrianVolume);

        Assert.That(totalVol + totalPed, Is.GreaterThan(0f), "Internal trips must load local neighbourhood edges");
    }

    [Test]
    public void AssignFlowsFromTazMsa_DistributesTrafficAcrossParallelRoutes()
    {
        // Setup two parallel routes between node A and node B:
        // Direct route: nA -> nB (length 100, low capacity 20)
        // Parallel bypass: nA -> nC -> nB (length 110, capacity 20)
        _grid.ZoneCell(0, 0, ZoneType.Residential);
        _grid.Zones[_grid.GetZoneId(0, 0)].Population = 2000;
        _grid.ZoneCell(5, 0, ZoneType.Commercial, jobs: 2000);

        var posA = _grid.GetWorldCenter(_grid.GetZoneId(0, 0));
        var posB = _grid.GetWorldCenter(_grid.GetZoneId(5, 0));
        var posC = (posA + posB) * 0.5f + new Vector2(0, 80);

        var nA = _graph.CreateNode(posA);
        var nB = _graph.CreateNode(posB);
        var nC = _graph.CreateNode(posC);

        _graph.AddRoadSegment(nA.Id, nB.Id, 5f);
        _graph.AddRoadSegment(nA.Id, nC.Id, 20f);
        _graph.AddRoadSegment(nC.Id, nB.Id, 20f);

        int eDirect = _graph.FindEdgeId(nA.Id, nB.Id);
        int eBypass1 = _graph.FindEdgeId(nA.Id, nC.Id);

        _tazManager.Rebuild(_grid, _parcelManager, _graph, _transitManager);
        Assert.That(_tazManager.ActiveZones.Count, Is.EqualTo(2));

        _optimizedOd.Recalculate(_tazManager, _graph, 8.0f);
        Assert.That(_optimizedOd.TotalCarTrips, Is.GreaterThan(30f));

        // 1. With All-or-Nothing (msaIterations = 1), inter-TAZ traffic only takes direct route
        _trafficEngine.AssignFlowsFromTazMsa(_tazManager, _optimizedOd, _graph, msaIterations: 1);
        float aonDirect = _graph.Edges[eDirect].CurrentVolume;
        float aonBypass = _graph.Edges[eBypass1].CurrentVolume;
        Assert.That(aonDirect, Is.GreaterThan(0f));

        // 2. With MSA (msaIterations = 4), congestion on direct route diverts traffic onto bypass
        _trafficEngine.AssignFlowsFromTazMsa(_tazManager, _optimizedOd, _graph, msaIterations: 4);
        float msaDirect = _graph.Edges[eDirect].CurrentVolume;
        float msaBypass = _graph.Edges[eBypass1].CurrentVolume;

        Assert.That(msaBypass, Is.GreaterThan(aonBypass), "MSA User Equilibrium must divert traffic onto alternative bypass route");
        Assert.That(msaDirect, Is.LessThan(aonDirect), "Direct route volume should decrease as vehicles divert to bypass");
    }

    [Test]
    public void ExtractPathFromTreeBuffer_SafelyReconstructsRoute()
    {
        var nA = _graph.CreateNode(new Vector2(0, 0));
        var nB = _graph.CreateNode(new Vector2(100, 0));
        var nC = _graph.CreateNode(new Vector2(200, 0));
        _graph.AddRoadSegment(nA.Id, nB.Id);
        _graph.AddRoadSegment(nB.Id, nC.Id);

        _grid.ZoneCell(0, 0, ZoneType.Residential);
        _grid.Zones[_grid.GetZoneId(0, 0)].Population = 1000;
        _grid.ZoneCell(5, 0, ZoneType.Commercial, jobs: 1000);

        _tazManager.Rebuild(_grid, _parcelManager, _graph, _transitManager);
        _optimizedOd.Recalculate(_tazManager, _graph, 8.0f);

        int origNode = _tazManager.ActiveZones[0].PrimaryRoadNodeId;
        int destNode = _tazManager.ActiveZones[1].PrimaryRoadNodeId;

        var path = CommuteAnalytics.ExtractPathFromTreeBuffer(_optimizedOd, origNode, destNode);
        Assert.That(path, Is.Not.Null);
        Assert.That(path.Count, Is.GreaterThan(0));

        // Edge case: invalid node IDs or null matrix
        var emptyPath = CommuteAnalytics.ExtractPathFromTreeBuffer(null, origNode, destNode);
        Assert.That(emptyPath, Is.Empty);

        var sameNodePath = CommuteAnalytics.ExtractPathFromTreeBuffer(_optimizedOd, origNode, origNode);
        Assert.That(sameNodePath, Is.Empty);
    }

    [Test]
    public void CommuteAnalytics_OnDemandTazDeaggregation_ConservesTotalTazTripsForZonesAndParcels()
    {
        // Setup TAZ 0 with two residential zones (pop 400 and 600) and a residential parcel (pop 200) -> total pop 1200
        int zRes1Id = _grid.GetZoneId(0, 0);
        int zRes2Id = _grid.GetZoneId(1, 0);
        _grid.ZoneCell(0, 0, ZoneType.Residential);
        _grid.Zones[zRes1Id].Population = 400;
        _grid.ZoneCell(1, 0, ZoneType.Residential);
        _grid.Zones[zRes2Id].Population = 600;

        // Setup TAZ 1 with a commercial zone (jobs 700) and an industrial zone (jobs 300) -> total jobs 1000
        int zComId = _grid.GetZoneId(7, 0);
        int zIndId = _grid.GetZoneId(8, 0);
        _grid.ZoneCell(7, 0, ZoneType.Commercial, jobs: 700);
        _grid.ZoneCell(8, 0, ZoneType.Industrial, jobs: 300);

        _graph.EnsureNode(zRes1Id, _grid.GetWorldCenter(zRes1Id));
        _graph.EnsureNode(zRes2Id, _grid.GetWorldCenter(zRes2Id));
        _graph.EnsureNode(zComId, _grid.GetWorldCenter(zComId));
        _graph.EnsureNode(zIndId, _grid.GetWorldCenter(zIndId));

        _graph.AddRoadSegment(zRes1Id, zRes2Id);
        _graph.AddRoadSegment(zRes2Id, zComId);
        _graph.AddRoadSegment(zComId, zIndId);

        _parcelManager.RefreshParcels(_graph);
        var resParcel = _parcelManager.Parcels.First(p => p.Center.X < 200f);
        _parcelManager.ZoneParcel(resParcel.Id, ZoneType.Residential, ensureAccessNode: false);
        resParcel.Population = 200;

        _tazManager.Rebuild(_grid, _parcelManager, _graph, _transitManager);
        _optimizedOd.Recalculate(_tazManager, _graph, 8.0f);

        int resTazId = _tazManager.GetTazIdForZoneTile(new Vector2I(0, 0));
        int workTazId = _tazManager.GetTazIdForZoneTile(new Vector2I(7, 0));
        Assert.That(resTazId, Is.GreaterThanOrEqualTo(0));
        Assert.That(workTazId, Is.GreaterThanOrEqualTo(0));
        Assert.That(resTazId, Is.Not.EqualTo(workTazId));

        // Expected outgoing row sum from resTazId in OptimizedODMatrix
        float expectedOutgoingTazTrips = 0f;
        for (int j = 0; j < _tazManager.ActiveZones.Count; j++)
        {
            float pair = _optimizedOd.GetCarTrips(resTazId, j) + _optimizedOd.GetTransitTrips(resTazId, j) + _optimizedOd.GetWalkTrips(resTazId, j);
            if (pair <= 0.001f)
                pair = _optimizedOd.GetCarTrips(j, resTazId) + _optimizedOd.GetTransitTrips(j, resTazId) + _optimizedOd.GetWalkTrips(j, resTazId);
            expectedOutgoingTazTrips += pair;
        }
        Assert.That(expectedOutgoingTazTrips, Is.GreaterThan(0f));

        var cRes1 = CommuteAnalytics.GetResidentialCommute(_grid.GetZone(zRes1Id), _grid, _tazManager, _optimizedOd);
        var cRes2 = CommuteAnalytics.GetResidentialCommute(_grid.GetZone(zRes2Id), _grid, _tazManager, _optimizedOd);
        var cParcel = CommuteAnalytics.GetResidentialCommuteForParcel(resParcel, _tazManager, _optimizedOd, _parcelManager, _grid);

        float sumDeaggregatedOutgoing = cRes1.totalTrips + cRes2.totalTrips + cParcel.totalTrips;
        Assert.That(sumDeaggregatedOutgoing, Is.EqualTo(expectedOutgoingTazTrips).Within(0.05f),
            "Sum of deaggregated residential trips across zones and parcels in TAZ must equal full TAZ outgoing flow");

        // Expected incoming column sum to workTazId in OptimizedODMatrix
        float expectedIncomingTazTrips = 0f;
        for (int i = 0; i < _tazManager.ActiveZones.Count; i++)
        {
            float pair = _optimizedOd.GetCarTrips(i, workTazId) + _optimizedOd.GetTransitTrips(i, workTazId) + _optimizedOd.GetWalkTrips(i, workTazId);
            if (pair <= 0.001f)
                pair = _optimizedOd.GetCarTrips(workTazId, i) + _optimizedOd.GetTransitTrips(workTazId, i) + _optimizedOd.GetWalkTrips(workTazId, i);
            expectedIncomingTazTrips += pair;
        }
        Assert.That(expectedIncomingTazTrips, Is.GreaterThan(0f));

        var cCom = CommuteAnalytics.GetIncomingCommute(_grid.GetZone(zComId), _grid, _tazManager, _optimizedOd);
        var cInd = CommuteAnalytics.GetIncomingCommute(_grid.GetZone(zIndId), _grid, _tazManager, _optimizedOd);

        float sumDeaggregatedIncoming = cCom.totalTrips + cInd.totalTrips;
        Assert.That(sumDeaggregatedIncoming, Is.EqualTo(expectedIncomingTazTrips).Within(0.05f),
            "Sum of deaggregated incoming trips across workplaces in TAZ must equal full TAZ incoming flow");

        // Verify CityOverview O(1) overload
        var overview = CommuteAnalytics.GetCityOverview(_grid, _optimizedOd, _parcelManager);
        Assert.That(overview.totalTrips, Is.EqualTo(_optimizedOd.TotalTrips).Within(0.01f));
        Assert.That(overview.comJobs, Is.EqualTo(700));
        Assert.That(overview.indJobs, Is.EqualTo(300));
    }

    [Test]
    public void CommuteAnalytics_ComputeCommuteTargets_ReconstructsNonEmptyEdgePathFromTreeBuffers()
    {
        int zResId = _grid.GetZoneId(0, 0);
        int zMidId = _grid.GetZoneId(3, 0);
        int zComId = _grid.GetZoneId(7, 0);

        _grid.ZoneCell(0, 0, ZoneType.Residential);
        _grid.Zones[zResId].Population = 1500;
        _grid.ZoneCell(7, 0, ZoneType.Commercial, jobs: 1500);

        _graph.EnsureNode(zResId, _grid.GetWorldCenter(zResId));
        _graph.EnsureNode(zMidId, _grid.GetWorldCenter(zMidId));
        _graph.EnsureNode(zComId, _grid.GetWorldCenter(zComId));

        _graph.AddRoadSegment(zResId, zMidId);
        _graph.AddRoadSegment(zMidId, zComId);

        _parcelManager.RefreshParcels(_graph);
        var resParcel = _parcelManager.Parcels.First(p => p.Center.X < 200f);
        _parcelManager.ZoneParcel(resParcel.Id, ZoneType.Residential, ensureAccessNode: false);
        resParcel.Population = 500;

        _tazManager.Rebuild(_grid, _parcelManager, _graph, _transitManager);
        _optimizedOd.Recalculate(_tazManager, _graph, 8.0f);
        _trafficEngine.AssignFlowsFromTazMsa(_tazManager, _optimizedOd, _graph, msaIterations: 4);

        var zoneTargets = CommuteAnalytics.ComputeCommuteTargets(zResId, _grid, _tazManager, _optimizedOd, _graph);
        Assert.That(zoneTargets, Is.Not.Empty);
        Assert.That(zoneTargets[0].EdgePath, Is.Not.Empty, "Zone commute target must have non-empty EdgePath from CachedTreeBuffers");

        var parcelTargets = CommuteAnalytics.ComputeCommuteTargetsForParcel(resParcel.Id, _parcelManager, _tazManager, _optimizedOd, _graph, _grid);
        Assert.That(parcelTargets, Is.Not.Empty);
        Assert.That(parcelTargets[0].EdgePath, Is.Not.Empty, "Parcel commute target must have non-empty EdgePath from CachedTreeBuffers");
    }

    [Test]
    public void TransitManager_AssignPassengerDemandFromTaz_DirectTransferAndIsolatedRoutes()
    {
        // Create 3 road nodes across 3 distinct 320m TAZ buckets:
        // TAZ 0 (bucket 0, X in [0,320)): Residential at (32, 32), stop n0 at (50, 50)
        // TAZ 1 (bucket 1, X in [320,640)): Commercial Hub at (480, 32), transfer stop n1 at (450, 50)
        // TAZ 2 (bucket 2, X in [640,960)): Industrial Park at (~850, 50), stop n2 at (900, 50)
        // Plus isolated node nIso1, nIso2 at (50, 750) in empty field
        var n0 = _graph.CreateNode(new Vector2(50, 50));
        var n1 = _graph.CreateNode(new Vector2(450, 50));
        var n2 = _graph.CreateNode(new Vector2(900, 50));
        var nIso1 = _graph.CreateNode(new Vector2(50, 750));
        var nIso2 = _graph.CreateNode(new Vector2(200, 750));

        _graph.AddRoadSegment(n0.Id, n1.Id);
        _graph.AddRoadSegment(n1.Id, n2.Id);
        _graph.AddRoadSegment(nIso1.Id, nIso2.Id);

        _grid.ZoneCell(0, 0, ZoneType.Residential);
        _grid.Zones[_grid.GetZoneId(0, 0)].Population = 2000;
        _grid.ZoneCell(7, 0, ZoneType.Commercial, jobs: 1000);
        // Place industrial parcel near n2 (900, 50) in bucket 2 (X > 820)
        _parcelManager.RefreshParcels(_graph);
        var indParcel = _parcelManager.Parcels.First(p => p.Center.X > 820f && p.Center.Y < 200f);
        _parcelManager.ZoneParcel(indParcel.Id, ZoneType.Industrial, ensureAccessNode: false);
        indParcel.Jobs = 1000;

        // Route 1: Direct between TAZ 0 (n0) and TAZ 1 (n1)
        var r1 = _transitManager.CreateRoute("Line 1 Direct", new List<int> { n0.Id, n1.Id }, new[] { n0.Id, n1.Id }, Colors.Blue, isLoop: false);
        // Route 2: Connects TAZ 1 (n1) to TAZ 2 (n2), sharing transfer stop n1 with Route 1
        var r2 = _transitManager.CreateRoute("Line 2 Transfer", new List<int> { n1.Id, n2.Id }, new[] { n1.Id, n2.Id }, Colors.Green, isLoop: false);
        // Route 3: Isolated route in empty field (nIso1 <-> nIso2)
        var rIso = _transitManager.CreateRoute("Line 3 Ghost", new List<int> { nIso1.Id, nIso2.Id }, new[] { nIso1.Id, nIso2.Id }, Colors.Gray, isLoop: false);

        _tazManager.Rebuild(_grid, _parcelManager, _graph, _transitManager);
        _optimizedOd.Recalculate(_tazManager, _graph, 8.0f);
        _transitManager.AssignPassengerDemandFromTaz(_tazManager, _optimizedOd, _graph);

        int taz0 = _tazManager.GetTazIdForZoneTile(new Vector2I(0, 0));
        int taz1 = _tazManager.GetTazIdForZoneTile(new Vector2I(7, 0));
        int taz2 = _tazManager.GetTazIdForParcel(indParcel.Id);

        Assert.That(taz0, Is.Not.EqualTo(taz1));
        Assert.That(taz1, Is.Not.EqualTo(taz2));
        Assert.That(taz0, Is.Not.EqualTo(taz2));

        float directFlow01 = _optimizedOd.GetTransitTrips(taz0, taz1) + _optimizedOd.GetTransitTrips(taz1, taz0);
        float transferFlow02 = _optimizedOd.GetTransitTrips(taz0, taz2) + _optimizedOd.GetTransitTrips(taz2, taz0);
        float directFlow12 = _optimizedOd.GetTransitTrips(taz1, taz2) + _optimizedOd.GetTransitTrips(taz2, taz1);

        Assert.That(directFlow01, Is.GreaterThan(0f), "Direct transit trips between TAZ 0 and TAZ 1 must be positive");
        Assert.That(transferFlow02, Is.GreaterThan(0f), "1-transfer transit trips between TAZ 0 and TAZ 2 must be positive");

        // Route 1 carries direct (0 <-> 1) + first leg of transfer (0 <-> 2)
        Assert.That(r1.AssignedHourlyDemand, Is.EqualTo(directFlow01 + transferFlow02).Within(0.05f));
        // Route 2 carries direct (1 <-> 2) + second leg of transfer (0 <-> 2)
        Assert.That(r2.AssignedHourlyDemand, Is.EqualTo(directFlow12 + transferFlow02).Within(0.05f));

        // Origin stop n0 and transfer stop n1 must have positive HourlyArrivalRate
        Assert.That(_transitManager.Stops[n0.Id].HourlyArrivalRate, Is.GreaterThan(0f));
        Assert.That(_transitManager.Stops[n1.Id].HourlyArrivalRate, Is.GreaterThan(0f));

        // Isolated route in empty field must have strictly 0 passengers and 0 revenue
        Assert.That(rIso.AssignedHourlyDemand, Is.EqualTo(0f));
        Assert.That(rIso.DailyPassengers, Is.EqualTo(0f));
        Assert.That(rIso.DailyRevenue, Is.EqualTo(0f));
        Assert.That(_transitManager.Stops[nIso1.Id].HourlyArrivalRate, Is.EqualTo(0f));
        Assert.That(_transitManager.Stops[nIso1.Id].WaitingPassengers, Is.EqualTo(0f));
    }
}
