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
        _graph.RebuildAfterTopologyChange(_grid.ZoneCount);

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
        _graph.RebuildAfterTopologyChange(_grid.ZoneCount);

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
        _graph.RebuildAfterTopologyChange(100);

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
        _graph.RebuildAfterTopologyChange(100);

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

        int connType = _tazManager.GetTransitConnectionType(originTaz, destTaz);
        Assert.That(connType, Is.GreaterThan(0), "TAZs must have a transit connection (direct or 1 transfer)");
    }

    [Test]
    public void OptimizedODMatrix_Recalculate_ConservesTripsAndAssignsFlows()
    {
        var nA = _graph.CreateNode(new Vector2(50, 50));
        var nB = _graph.CreateNode(new Vector2(350, 50));
        _graph.AddRoadSegment(nA.Id, nB.Id);
        _graph.RebuildAfterTopologyChange(100);

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
        _graph.RebuildAfterTopologyChange(100);

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
        _graph.RebuildAfterTopologyChange(100);

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
        _graph.RebuildAfterTopologyChange(100);

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
}
