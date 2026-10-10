using Godot;
using NUnit.Framework;
using CitySim.Simulation;
using System.Linq;

namespace CitySim.Tests;

[TestFixture]
public class MapEditingVisualTests
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

        MapEditingTestCityGenerator.Generate(_grid, _roadGraph, _parcelManager, _transitManager, _economyManager);
    }

    [Test]
    public void MapEditingTestCity_InitialCanvas_ValidAndClean()
    {
        Assert.That(_roadGraph.Nodes.Count, Is.GreaterThanOrEqualTo(6));
        Assert.That(_roadGraph.Edges.Count, Is.GreaterThan(0));
        Assert.That(_parcelManager.Parcels.Count, Is.GreaterThan(0));
        Assert.That(_economyManager.Money, Is.EqualTo(50000f));
    }

    [Test]
    public void StraightRoadConstruction_CreatesBidirectionalEdgesAndParcels()
    {
        var nA = _roadGraph.CreateNode(new Vector2(100, 150));
        var nB = _roadGraph.CreateNode(new Vector2(260, 150));
        var curve = new CurveSegment(nA.WorldPosition, nB.WorldPosition);

        bool added = _roadGraph.AddCurvedRoadSegment(nA.Id, nB.Id, curve, 1800f);
        Assert.That(added, Is.True);

        int fwdEdgeId = _roadGraph.FindEdgeId(nA.Id, nB.Id);
        int revEdgeId = _roadGraph.FindEdgeId(nB.Id, nA.Id);

        Assert.That(fwdEdgeId, Is.Not.EqualTo(-1));
        Assert.That(revEdgeId, Is.Not.EqualTo(-1));
        Assert.That(fwdEdgeId, Is.Not.EqualTo(revEdgeId));

        _parcelManager.RefreshParcels(_roadGraph);

        var leftParcels = _parcelManager.GetParcelsForEdge(fwdEdgeId, ParcelSide.Left);
        var rightParcels = _parcelManager.GetParcelsForEdge(fwdEdgeId, ParcelSide.Right);

        Assert.That(leftParcels.Count, Is.GreaterThan(0));
        Assert.That(rightParcels.Count, Is.GreaterThan(0));

        foreach (var p in leftParcels.Concat(rightParcels))
        {
            Assert.That(p.Boundary.Length, Is.EqualTo(4), "Parcel boundary must form a 4-point polygon");
            Assert.That(p.FrontageWidth, Is.GreaterThan(10f));
        }
    }

    [Test]
    public void CurvedRoadConstruction_CreatesSmoothBezierAndAdaptedParcels()
    {
        var nA = _roadGraph.CreateNode(new Vector2(150, 400));
        var nB = _roadGraph.CreateNode(new Vector2(150, 600));
        Vector2 apex = new Vector2(80, 500);

        var curve = CurveSegment.CreateFromThreePoints(nA.WorldPosition, apex, nB.WorldPosition);
        Assert.That(curve.Length, Is.GreaterThan(nA.WorldPosition.DistanceTo(nB.WorldPosition)),
            "Bézier curve arc length must exceed chord distance");

        bool added = _roadGraph.AddCurvedRoadSegment(nA.Id, nB.Id, curve, 1500f);
        Assert.That(added, Is.True);

        int edgeId = _roadGraph.FindEdgeId(nA.Id, nB.Id);
        _parcelManager.RefreshParcels(_roadGraph);

        var parcels = _parcelManager.GetParcelsForEdge(edgeId, ParcelSide.Left);
        Assert.That(parcels.Count, Is.GreaterThan(0));

        foreach (var p in parcels)
        {
            Assert.That(p.Boundary.Length, Is.EqualTo(4));
            Assert.That(p.Center, Is.Not.EqualTo(Vector2.Zero));
        }
    }

    [Test]
    public void MidpointEdgeSplitting_MaintainsTopologyAndRefreshesParcels()
    {
        var nA = _roadGraph.CreateNode(new Vector2(350, 850));
        var nB = _roadGraph.CreateNode(new Vector2(550, 850));
        _roadGraph.AddCurvedRoadSegment(nA.Id, nB.Id, new CurveSegment(nA.WorldPosition, nB.WorldPosition));

        int origEdgeId = _roadGraph.FindEdgeId(nA.Id, nB.Id);
        Assert.That(origEdgeId, Is.Not.EqualTo(-1));

        // Split edge at midpoint (450, 850)
        var splitNode = _roadGraph.SplitEdgeAtPoint(origEdgeId, new Vector2(450, 850));
        Assert.That(splitNode, Is.Not.Null);
        Assert.That(splitNode!.WorldPosition.X, Is.EqualTo(450f).Within(1.0f));

        // Original edge must be deactivated
        Assert.That(_roadGraph.Edges[origEdgeId].FromId, Is.EqualTo(-1));

        // Sub-edges must exist
        int edgeAtoMid = _roadGraph.FindEdgeId(nA.Id, splitNode.Id);
        int edgeMidtoB = _roadGraph.FindEdgeId(splitNode.Id, nB.Id);
        Assert.That(edgeAtoMid, Is.Not.EqualTo(-1));
        Assert.That(edgeMidtoB, Is.Not.EqualTo(-1));

        // Path continuity
        var shortestPath = _roadGraph.GetShortestNodePath(nA.Id, nB.Id);
        Assert.That(shortestPath, Is.EqualTo(new[] { nA.Id, splitNode.Id, nB.Id }));

        // Parcel manager refresh must execute cleanly
        _parcelManager.RefreshParcels(_roadGraph);
        foreach (var p in _parcelManager.Parcels)
        {
            Assert.That(p.EdgeId, Is.InRange(0, _roadGraph.Edges.Count - 1));
            Assert.That(_roadGraph.Edges[p.EdgeId].FromId, Is.Not.EqualTo(-1));
        }
    }

    [Test]
    public void RoadsideParcel_ZoningAndDezoningLifecycle()
    {
        var nA = _roadGraph.CreateNode(new Vector2(750, 850));
        var nB = _roadGraph.CreateNode(new Vector2(950, 850));
        _roadGraph.AddCurvedRoadSegment(nA.Id, nB.Id, new CurveSegment(nA.WorldPosition, nB.WorldPosition));

        int edgeId = _roadGraph.FindEdgeId(nA.Id, nB.Id);
        _parcelManager.RefreshParcels(_roadGraph);

        var parcels = _parcelManager.GetParcelsForEdge(edgeId, ParcelSide.Left);
        Assert.That(parcels.Count, Is.GreaterThan(0));
        var parcel = parcels[0];

        // 1. Zone Residential
        bool zonedRes = _parcelManager.ZoneParcel(parcel.Id, ZoneType.Residential, population: 60);
        Assert.That(zonedRes, Is.True);
        Assert.That(parcel.ZoneType, Is.EqualTo(ZoneType.Residential));
        Assert.That(parcel.Population, Is.EqualTo(60));
        Assert.That(parcel.ResidentialCap, Is.GreaterThan(0));

        // 2. Zone Commercial
        bool zonedCom = _parcelManager.ZoneParcel(parcel.Id, ZoneType.Commercial, jobs: 45);
        Assert.That(zonedCom, Is.True);
        Assert.That(parcel.ZoneType, Is.EqualTo(ZoneType.Commercial));
        Assert.That(parcel.Jobs, Is.EqualTo(45));
        Assert.That(parcel.Population, Is.EqualTo(0));

        // 3. Zone Industrial
        bool zonedInd = _parcelManager.ZoneParcel(parcel.Id, ZoneType.Industrial);
        Assert.That(zonedInd, Is.True);
        Assert.That(parcel.ZoneType, Is.EqualTo(ZoneType.Industrial));
        Assert.That(parcel.Jobs, Is.GreaterThan(0));

        // 4. Dezone
        bool dezoned = _parcelManager.DezoneParcel(parcel.Id);
        Assert.That(dezoned, Is.True);
        Assert.That(parcel.ZoneType, Is.EqualTo(ZoneType.Empty));
        Assert.That(parcel.Population, Is.EqualTo(0));
        Assert.That(parcel.Jobs, Is.EqualTo(0));
        Assert.That(parcel.ResidentialCap, Is.EqualTo(0));
        Assert.That(parcel.CommercialCap, Is.EqualTo(0));
    }

    [Test]
    public void RoadDemolition_RemovesEdgesAndCleansParcelsWithoutOrphans()
    {
        var nA = _roadGraph.CreateNode(new Vector2(800, 150));
        var nB = _roadGraph.CreateNode(new Vector2(1000, 150));
        _roadGraph.AddCurvedRoadSegment(nA.Id, nB.Id, new CurveSegment(nA.WorldPosition, nB.WorldPosition));

        int edgeId = _roadGraph.FindEdgeId(nA.Id, nB.Id);
        Assert.That(edgeId, Is.Not.EqualTo(-1));

        _parcelManager.RefreshParcels(_roadGraph);
        Assert.That(_parcelManager.GetParcelsForEdge(edgeId, ParcelSide.Left).Count, Is.GreaterThan(0));

        // Demolish the road segment
        bool removed = _roadGraph.RemoveRoadSegment(nA.Id, nB.Id);
        Assert.That(removed, Is.True);
        Assert.That(_roadGraph.HasEdge(nA.Id, nB.Id), Is.False);
        Assert.That(_roadGraph.HasEdge(nB.Id, nA.Id), Is.False);

        // Refresh parcel manager after demolition
        _parcelManager.RefreshParcels(_roadGraph);

        // No parcel should point to an invalid edge
        foreach (var p in _parcelManager.Parcels)
        {
            Assert.That(_roadGraph.Edges[p.EdgeId].FromId, Is.Not.EqualTo(-1));
        }
    }

    [Test]
    public void GraphTopology_IntegrityVerification()
    {
        // Check all active edges in RoadGraph
        for (int i = 0; i < _roadGraph.Edges.Count; i++)
        {
            var edge = _roadGraph.Edges[i];
            if (edge.FromId == -1 || edge.ToId == -1) continue;

            Assert.That(_roadGraph.NodeMap.ContainsKey(edge.FromId), Is.True, $"FromNode {edge.FromId} must exist in NodeMap");
            Assert.That(_roadGraph.NodeMap.ContainsKey(edge.ToId), Is.True, $"ToNode {edge.ToId} must exist in NodeMap");

            // Check adjacency list
            Assert.That(_roadGraph.AdjacencyEdges.ContainsKey(edge.FromId), Is.True);
            Assert.That(_roadGraph.AdjacencyEdges[edge.FromId].Contains(i), Is.True);

            // Check reverse edge exists
            int revId = _roadGraph.FindEdgeId(edge.ToId, edge.FromId);
            Assert.That(revId, Is.Not.EqualTo(-1), $"Reverse edge for ({edge.FromId} -> {edge.ToId}) must exist");
        }

        // Check all parcels
        foreach (var parcel in _parcelManager.Parcels)
        {
            Assert.That(parcel.EdgeId, Is.InRange(0, _roadGraph.Edges.Count - 1));
            var edge = _roadGraph.Edges[parcel.EdgeId];
            Assert.That(edge.FromId, Is.Not.EqualTo(-1), "Parcel must belong to an active road edge");
        }
    }
}
