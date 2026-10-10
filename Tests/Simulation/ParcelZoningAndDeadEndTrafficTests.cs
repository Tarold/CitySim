using Godot;
using NUnit.Framework;
using CitySim.Simulation;
using System.Linq;

namespace CitySim.Tests.Simulation;

[TestFixture]
public class ParcelZoningAndDeadEndTrafficTests
{
    private RoadGraph _graph;
    private ParcelManager _manager;

    [SetUp]
    public void Setup()
    {
        _graph = new RoadGraph();
        _manager = new ParcelManager();
        _manager.Initialize(_graph);
    }

    [Test]
    public void ZoneParcel_DoesNotSplitRoadEdge_AndDoesNotFireEdgeSplit()
    {
        var n1 = _graph.CreateNode(new Vector2(0, 0));
        var n2 = _graph.CreateNode(new Vector2(200, 0));
        _graph.AddCurvedRoadSegment(n1.Id, n2.Id, new CurveSegment(n1.WorldPosition, n2.WorldPosition));

        int edgeId = _graph.FindEdgeId(n1.Id, n2.Id);
        var edge = _graph.Edges[edgeId];
        _manager.GenerateParcelsForEdge(edge, _graph);

        int initialEdgeCount = _graph.Edges.Count;
        int edgeSplitFireCount = 0;
        _graph.EdgeSplit += (eid, junction) => edgeSplitFireCount++;

        var parcels = _manager.GetParcelsForEdge(edgeId);
        Assert.That(parcels.Count, Is.GreaterThan(0));
        var targetParcel = parcels[0];

        // Zone parcel with default ensureAccessNode = false
        bool result = _manager.ZoneParcel(targetParcel.Id, ZoneType.Residential, population: 120);

        Assert.That(result, Is.True);
        Assert.That(_graph.Edges.Count, Is.EqualTo(initialEdgeCount), "Zoning must not add or split road edges.");
        Assert.That(edgeSplitFireCount, Is.EqualTo(0), "EdgeSplit event must not fire when zoning a parcel.");
        Assert.That(_manager.Parcels.Contains(targetParcel), Is.True, "Parcel must remain in ParcelManager.Parcels.");
        Assert.That(targetParcel.ZoneType, Is.EqualTo(ZoneType.Residential));
        Assert.That(targetParcel.Population, Is.EqualTo(120));
        Assert.That(_manager.TotalPopulation, Is.EqualTo(120));
    }

    [Test]
    public void ZoneParcel_PreservesOppositeSideParcels_BothSidesZonableAtSamePosition()
    {
        var n1 = _graph.CreateNode(new Vector2(0, 0));
        var n2 = _graph.CreateNode(new Vector2(200, 0));
        _graph.AddCurvedRoadSegment(n1.Id, n2.Id, new CurveSegment(n1.WorldPosition, n2.WorldPosition));

        int edgeId = _graph.FindEdgeId(n1.Id, n2.Id);
        _manager.GenerateParcelsForEdge(_graph.Edges[edgeId], _graph);

        var rightParcels = _manager.GetParcelsForEdge(edgeId, ParcelSide.Right);
        var leftParcels = _manager.GetParcelsForEdge(edgeId, ParcelSide.Left);

        Assert.That(rightParcels.Count, Is.GreaterThan(0));
        Assert.That(leftParcels.Count, Is.GreaterThan(0));

        var rightParcel = rightParcels[0];
        var leftParcel = leftParcels[0];

        // Zone Right parcel
        _manager.ZoneParcel(rightParcel.Id, ZoneType.Residential, population: 100);

        // Verify Left parcel on opposite side is completely intact and empty
        Assert.That(_manager.Parcels.Contains(leftParcel), Is.True, "Opposite side parcel must remain in ParcelManager.Parcels.");
        Assert.That(leftParcel.ZoneType, Is.EqualTo(ZoneType.Empty));
        Assert.That(rightParcel.ZoneType, Is.EqualTo(ZoneType.Residential));

        // Zone Left parcel
        _manager.ZoneParcel(leftParcel.Id, ZoneType.Commercial, jobs: 80);

        Assert.That(leftParcel.ZoneType, Is.EqualTo(ZoneType.Commercial));
        Assert.That(leftParcel.Jobs, Is.EqualTo(80));
        Assert.That(rightParcel.ZoneType, Is.EqualTo(ZoneType.Residential));
        Assert.That(rightParcel.Population, Is.EqualTo(100));

        Assert.That(_manager.TotalPopulation, Is.EqualTo(100));
        Assert.That(_manager.TotalJobs, Is.EqualTo(80));
        Assert.That(_manager.ActiveParcels.Count(), Is.EqualTo(2));
    }

    [Test]
    public void DeadEndTrafficExtent_CalculatesCorrectTMax_BasedOnFurthestActiveParcel()
    {
        var nodeC = _graph.CreateNode(new Vector2(-100, 0));
        var nodeA = _graph.CreateNode(new Vector2(0, 0));
        var nodeB = _graph.CreateNode(new Vector2(300, 0));

        // Node A connects to Node C and Node B
        _graph.AddCurvedRoadSegment(nodeC.Id, nodeA.Id, new CurveSegment(nodeC.WorldPosition, nodeA.WorldPosition));
        _graph.AddCurvedRoadSegment(nodeA.Id, nodeB.Id, new CurveSegment(nodeA.WorldPosition, nodeB.WorldPosition));

        int edgeAB = _graph.FindEdgeId(nodeA.Id, nodeB.Id);
        int edgeBA = _graph.FindEdgeId(nodeB.Id, nodeA.Id);

        _manager.GenerateParcelsForEdge(_graph.Edges[edgeAB], _graph);
        var parcels = _manager.GetParcelsForEdge(edgeAB);
        Assert.That(parcels.Count, Is.GreaterThan(1));

        // 1. When all parcels are Empty, T_max = max(0.15) + 0.05 = 0.20
        float extentEmpty = _graph.GetEdgeTrafficExtent(_graph.Edges[edgeAB], _manager);
        Assert.That(extentEmpty, Is.EqualTo(0.20f).Within(1e-4f));

        // 2. Zone parcel near the start (NormalizedT around 0.20 - 0.35)
        var firstParcel = parcels.OrderBy(p => p.NormalizedT).First();
        _manager.ZoneParcel(firstParcel.Id, ZoneType.Residential, population: 50);

        float expectedFirstTMax = Mathf.Clamp(Mathf.Max(firstParcel.NormalizedT, 0.15f) + 0.05f, 0.15f, 1.0f);
        float extentWithFirst = _graph.GetEdgeTrafficExtent(_graph.Edges[edgeAB], _manager);
        Assert.That(extentWithFirst, Is.EqualTo(expectedFirstTMax).Within(1e-4f));

        // 3. Zone a parcel further along the edge
        var furthestParcel = parcels.OrderByDescending(p => p.NormalizedT).First();
        _manager.ZoneParcel(furthestParcel.Id, ZoneType.Commercial, jobs: 60);

        float expectedFurthestTMax = Mathf.Clamp(Mathf.Max(furthestParcel.NormalizedT, 0.15f) + 0.05f, 0.15f, 1.0f);
        float extentWithBoth = _graph.GetEdgeTrafficExtent(_graph.Edges[edgeAB], _manager);
        Assert.That(extentWithBoth, Is.EqualTo(expectedFurthestTMax).Within(1e-4f));

        // 4. On reverse edge BA (leaving dead end heading to node A with degree > 1):
        // Node A connects to other roads, so traffic flows through, T_max = 1.0f.
        float extentReverse = _graph.GetEdgeTrafficExtent(_graph.Edges[edgeBA], _manager);
        Assert.That(extentReverse, Is.EqualTo(1.0f).Within(1e-4f));
    }

    [Test]
    public void DeadEndTrafficExtent_ThroughRoad_AlwaysReturnsOne()
    {
        var nodeA = _graph.CreateNode(new Vector2(0, 0));
        var nodeB = _graph.CreateNode(new Vector2(200, 0));
        var nodeC = _graph.CreateNode(new Vector2(400, 0));

        _graph.AddCurvedRoadSegment(nodeA.Id, nodeB.Id, new CurveSegment(nodeA.WorldPosition, nodeB.WorldPosition));
        _graph.AddCurvedRoadSegment(nodeB.Id, nodeC.Id, new CurveSegment(nodeB.WorldPosition, nodeC.WorldPosition));

        int edgeAB = _graph.FindEdgeId(nodeA.Id, nodeB.Id);
        _manager.GenerateParcelsForEdge(_graph.Edges[edgeAB], _graph);

        // Destination node B has degree 2 (connects to A and C), so traffic flows through
        float extentThrough = _graph.GetEdgeTrafficExtent(_graph.Edges[edgeAB], _manager);
        Assert.That(extentThrough, Is.EqualTo(1.0f));

        // Also via ParcelManager helper method
        float extentViaManager = _manager.GetEdgeTrafficExtent(edgeAB, _graph);
        Assert.That(extentViaManager, Is.EqualTo(1.0f));
    }

    [Test]
    public void CarTrafficManager_ClampsProgressToTMax_OnDeadEndEdges()
    {
        var nodeA = _graph.CreateNode(new Vector2(0, 0));
        var nodeB = _graph.CreateNode(new Vector2(300, 0));
        _graph.AddCurvedRoadSegment(nodeA.Id, nodeB.Id, new CurveSegment(nodeA.WorldPosition, nodeB.WorldPosition));

        int edgeAB = _graph.FindEdgeId(nodeA.Id, nodeB.Id);
        _graph.Edges[edgeAB].CurrentVolume = 100f; // Active volume so HasDemand is true

        _manager.GenerateParcelsForEdge(_graph.Edges[edgeAB], _graph);
        var parcels = _manager.GetParcelsForEdge(edgeAB);

        // Zone first parcel
        var parcel = parcels.OrderBy(p => p.NormalizedT).First();
        _manager.ZoneParcel(parcel.Id, ZoneType.Residential, population: 50);

        float tMax = _graph.GetEdgeTrafficExtent(_graph.Edges[edgeAB], _manager);
        Assert.That(tMax, Is.LessThan(0.9f));

        var carManager = new CarTrafficManager();
        carManager.Initialize(_graph, _manager);

        // Create car right near tMax on edgeAB
        var car = carManager.CreateCar(edgeAB, progress: tMax - 0.02f, speed: 100f);

        // Update car traffic with enough delta to reach and exceed tMax
        carManager.Update(delta: 0.5f, gameSpeed: 1.0f, _graph, trafficLights: null, _manager);

        // The car should have clamped to tMax or transitioned/despawned
        Assert.That(car.Progress <= tMax || car.EdgeId != edgeAB, Is.True,
            "Visual car must not advance past tMax on a dead-end road edge.");
    }

    [Test]
    public void GenuineRoadSplit_MigratesZonedParcelsToSubEdges_PreservesZoneTypeAndPopulation()
    {
        var nodeA = _graph.CreateNode(new Vector2(0, 0));
        var nodeB = _graph.CreateNode(new Vector2(400, 0));
        _graph.AddCurvedRoadSegment(nodeA.Id, nodeB.Id, new CurveSegment(nodeA.WorldPosition, nodeB.WorldPosition));

        int origEdgeId = _graph.FindEdgeId(nodeA.Id, nodeB.Id);
        _manager.GenerateParcelsForEdge(_graph.Edges[origEdgeId], _graph);

        var rightParcels = _manager.GetParcelsForEdge(origEdgeId, ParcelSide.Right);
        Assert.That(rightParcels.Count, Is.GreaterThanOrEqualTo(4));

        // Zone a parcel in the first quarter (around x = 80, far from junction at x = 200)
        var p1 = rightParcels.OrderBy(p => p.NormalizedT).First();
        _manager.ZoneParcel(p1.Id, ZoneType.Residential, population: 150);

        // Zone a parcel in the last quarter (around x = 320, far from junction at x = 200)
        var p2 = rightParcels.OrderByDescending(p => p.NormalizedT).First();
        _manager.ZoneParcel(p2.Id, ZoneType.Commercial, jobs: 90);

        Assert.That(_manager.TotalPopulation, Is.EqualTo(150));
        Assert.That(_manager.TotalJobs, Is.EqualTo(90));

        // Legitimate road construction split at midpoint t = 0.5 (x = 200)
        var junction = _graph.SplitEdge(origEdgeId, 0.5f);
        Assert.That(junction, Is.Not.Null);

        // Handle edge split in ParcelManager
        _manager.HandleEdgeSplit(origEdgeId, _graph);

        // Old edge must no longer have parcels
        Assert.That(_manager.GetParcelsForEdge(origEdgeId).Count, Is.EqualTo(0));

        // Find new split edges
        int newEdge1 = _graph.FindEdgeId(nodeA.Id, junction.Id);
        int newEdge2 = _graph.FindEdgeId(junction.Id, nodeB.Id);

        Assert.That(newEdge1, Is.Not.EqualTo(-1));
        Assert.That(newEdge2, Is.Not.EqualTo(-1));

        var subParcels1 = _manager.GetParcelsForEdge(newEdge1);
        var subParcels2 = _manager.GetParcelsForEdge(newEdge2);

        // Zoned parcels must be migrated to the sub-edges
        var migratedP1 = subParcels1.FirstOrDefault(p => p.ZoneType == ZoneType.Residential);
        var migratedP2 = subParcels2.FirstOrDefault(p => p.ZoneType == ZoneType.Commercial);

        Assert.That(migratedP1, Is.Not.Null, "Residential parcel should be migrated to sub-edge 1.");
        Assert.That(migratedP1.Population, Is.EqualTo(150));
        Assert.That(migratedP1.EdgeId, Is.EqualTo(newEdge1));

        Assert.That(migratedP2, Is.Not.Null, "Commercial parcel should be migrated to sub-edge 2.");
        Assert.That(migratedP2.Jobs, Is.EqualTo(90));
        Assert.That(migratedP2.EdgeId, Is.EqualTo(newEdge2));

        // Aggregates must remain completely intact
        Assert.That(_manager.TotalPopulation, Is.EqualTo(150));
        Assert.That(_manager.TotalJobs, Is.EqualTo(90));
        Assert.That(_manager.ActiveParcels.Count(), Is.EqualTo(2));
    }

    [Test]
    public void GenuineRoadSplit_PhysicalCollision_PreventsCollisionAtNewIntersection()
    {
        var nodeA = _graph.CreateNode(new Vector2(0, 0));
        var nodeB = _graph.CreateNode(new Vector2(400, 0));
        _graph.AddCurvedRoadSegment(nodeA.Id, nodeB.Id, new CurveSegment(nodeA.WorldPosition, nodeB.WorldPosition));

        int origEdgeId = _graph.FindEdgeId(nodeA.Id, nodeB.Id);
        _manager.GenerateParcelsForEdge(_graph.Edges[origEdgeId], _graph);

        var rightParcels = _manager.GetParcelsForEdge(origEdgeId, ParcelSide.Right);
        var leftParcels = _manager.GetParcelsForEdge(origEdgeId, ParcelSide.Left);

        // Far parcel (mid ~ 63.5, far from junction at 200)
        var farParcel = rightParcels.OrderBy(p => p.NormalizedT).First();
        _manager.ZoneParcel(farParcel.Id, ZoneType.Residential, population: 100);

        // Right-side parcel near midpoint (x ~ 180, where cross-road will branch into +Y)
        var nearJunctionRightParcel = rightParcels.First(p => Mathf.Abs(p.AccessPoint.X - 200f) < ParcelManager.JunctionMargin);
        _manager.ZoneParcel(nearJunctionRightParcel.Id, ZoneType.Commercial, jobs: 50);

        // Left-side parcel near midpoint (x ~ 180, continuous uninterrupted side opposite cross-road)
        var nearJunctionLeftParcel = leftParcels.First(p => Mathf.Abs(p.AccessPoint.X - 200f) < ParcelManager.JunctionMargin);
        _manager.ZoneParcel(nearJunctionLeftParcel.Id, ZoneType.Residential, population: 80);

        Assert.That(_manager.TotalPopulation, Is.EqualTo(180));
        Assert.That(_manager.TotalJobs, Is.EqualTo(50));

        // Split edge at midpoint (x = 200)
        var junction = _graph.SplitEdge(origEdgeId, 0.5f);
        Assert.That(junction, Is.Not.Null);

        // Add intersecting cross road extending along +Y (Right side)
        var crossNode = _graph.CreateNode(new Vector2(200, 100));
        _graph.AddCurvedRoadSegment(junction.Id, crossNode.Id, new CurveSegment(junction.WorldPosition, crossNode.WorldPosition));

        // Update ParcelManager
        _manager.RefreshParcels(_graph);

        // Far parcel survives and is migrated
        int newEdge1 = _graph.FindEdgeId(nodeA.Id, junction.Id);
        var subParcels1 = _manager.GetParcelsForEdge(newEdge1);
        var migratedFar = subParcels1.FirstOrDefault(p => p.Id == farParcel.Id);
        Assert.That(migratedFar, Is.Not.Null, "Far parcel should survive and be migrated.");
        Assert.That(migratedFar.Population, Is.EqualTo(100));

        // The Right parcel that physically intersects the branch road pavement is removed
        Assert.That(_manager.Parcels.Any(p => p.Id == nearJunctionRightParcel.Id), Is.False,
            "Right parcel physically colliding with branch road must be removed.");
        Assert.That(_manager.TotalJobs, Is.EqualTo(0));

        // The Left parcel on the uninterrupted continuous side opposite the branch road SURVIVES!
        Assert.That(_manager.Parcels.Any(p => p.Id == nearJunctionLeftParcel.Id), Is.True,
            "Left parcel on uninterrupted continuous side opposite branch road must survive!");
        Assert.That(_manager.TotalPopulation, Is.EqualTo(180));
    }

    [Test]
    public void OppositeSideEmptyParcelGeneration_AfterSplitWithZonedParcels_BothSubEdgesHaveZonableEmptySlots()
    {
        // Straight road of length 200 px
        var nodeA = _graph.CreateNode(new Vector2(0, 0));
        var nodeB = _graph.CreateNode(new Vector2(200, 0));
        _graph.AddCurvedRoadSegment(nodeA.Id, nodeB.Id, new CurveSegment(nodeA.WorldPosition, nodeB.WorldPosition));

        int origEdgeId = _graph.FindEdgeId(nodeA.Id, nodeB.Id);
        _manager.GenerateParcelsForEdge(_graph.Edges[origEdgeId], _graph);

        // Zone all parcels on the Right side
        int rightCount = _manager.ZoneEdgeSide(origEdgeId, ParcelSide.Right, ZoneType.Residential);
        Assert.That(rightCount, Is.GreaterThan(0));

        // Simulate mid-road bus stop insertion at midpoint (x = 100)
        var junction = _graph.SplitEdgeAtPoint(origEdgeId, new Vector2(100, 0));
        Assert.That(junction, Is.Not.Null);
        _manager.RefreshParcels(_graph);

        int newEdge1 = _graph.FindEdgeId(nodeA.Id, junction.Id);
        int newEdge2 = _graph.FindEdgeId(junction.Id, nodeB.Id);

        // Verify sub-edges exist
        Assert.That(newEdge1, Is.Not.EqualTo(-1));
        Assert.That(newEdge2, Is.Not.EqualTo(-1));

        // Verify that the unzoned Left side of the road has empty parcel slots on BOTH sub-edges
        var leftSub1 = _manager.GetParcelsForEdge(newEdge1, ParcelSide.Left);
        var leftSub2 = _manager.GetParcelsForEdge(newEdge2, ParcelSide.Left);

        Assert.That(leftSub1.Count, Is.GreaterThan(0), "Sub-edge 1 must have empty parcel slots on the unzoned Left side.");
        Assert.That(leftSub2.Count, Is.GreaterThan(0), "Sub-edge 2 must have empty parcel slots on the unzoned Left side.");

        foreach (var p in leftSub1)
        {
            Assert.That(p.ZoneType, Is.EqualTo(ZoneType.Empty));
        }
        foreach (var p in leftSub2)
        {
            Assert.That(p.ZoneType, Is.EqualTo(ZoneType.Empty));
        }

        // Verify player can zone the opposite-side slots
        bool zoneSub1 = _manager.ZoneParcel(leftSub1[0].Id, ZoneType.Commercial, jobs: 80);
        bool zoneSub2 = _manager.ZoneParcel(leftSub2[0].Id, ZoneType.Industrial, jobs: 90);

        Assert.That(zoneSub1, Is.True);
        Assert.That(zoneSub2, Is.True);
        Assert.That(_manager.TotalJobs, Is.EqualTo(170));
    }
}
