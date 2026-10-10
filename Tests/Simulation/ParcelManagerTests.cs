using Godot;
using NUnit.Framework;
using CitySim.Simulation;
using System.Collections.Generic;

namespace CitySim.Tests.Simulation;

[TestFixture]
public class ParcelManagerTests
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
    public void GenerateParcelsForStraightEdge_CreatesParcelsOnBothSides()
    {
        var n1 = _graph.CreateNode(new Vector2(0, 0));
        var n2 = _graph.CreateNode(new Vector2(160, 0));
        var curve = new CurveSegment(n1.WorldPosition, n2.WorldPosition);
        _graph.AddCurvedRoadSegment(n1.Id, n2.Id, curve);

        int edgeId = _graph.FindEdgeId(n1.Id, n2.Id);
        var edge = _graph.Edges[edgeId];

        var parcels = _manager.GenerateParcelsForEdge(edge, _graph);

        Assert.That(parcels, Is.Not.Null);
        Assert.That(parcels.Count, Is.GreaterThan(0));

        var leftParcels = _manager.GetParcelsForEdge(edgeId, ParcelSide.Left);
        var rightParcels = _manager.GetParcelsForEdge(edgeId, ParcelSide.Right);

        Assert.That(leftParcels.Count, Is.GreaterThan(0));
        Assert.That(rightParcels.Count, Is.GreaterThan(0));
        Assert.That(leftParcels.Count, Is.EqualTo(rightParcels.Count));

        foreach (var p in parcels)
        {
            Assert.That(p.EdgeId, Is.EqualTo(edgeId));
            Assert.That(p.NormalizedT, Is.InRange(0f, 1f));
            Assert.That(p.Boundary, Is.Not.Null);
            Assert.That(p.Boundary.Length, Is.EqualTo(4));
            Assert.That(p.ZoneType, Is.EqualTo(ZoneType.Empty));
        }
    }

    [Test]
    public void GenerateParcelsForCurvedEdge_ComputesNormalAdaptedQuadrilaterals()
    {
        var n1 = _graph.CreateNode(new Vector2(0, 0));
        var n2 = _graph.CreateNode(new Vector2(200, 0));
        Vector2 apex = new Vector2(100, 60);
        var curve = CurveSegment.CreateFromThreePoints(n1.WorldPosition, apex, n2.WorldPosition);
        _graph.AddCurvedRoadSegment(n1.Id, n2.Id, curve);

        int edgeId = _graph.FindEdgeId(n1.Id, n2.Id);
        var edge = _graph.Edges[edgeId];

        var parcels = _manager.GenerateParcelsForEdge(edge, _graph);
        Assert.That(parcels.Count, Is.GreaterThan(0));

        foreach (var p in parcels)
        {
            Assert.That(p.Boundary.Length, Is.EqualTo(4));
            // Verify frontage width is in reasonable lot frontage range (32-48 px)
            Assert.That(p.FrontageWidth, Is.GreaterThan(20f));
            // Verify lot depth is in reasonable range (~48 px)
            Assert.That(p.LotDepth, Is.EqualTo(ParcelManager.DefaultLotDepth).Within(2.0f));
        }
    }

    [Test]
    public void BoundaryAndFacingAngle_OrientedTowardRoadCenterline()
    {
        // Straight road along +X: curve from (0, 0) to (120, 0). Tangent is (1, 0), Normal is (0, 1) (+Y direction)
        var n1 = _graph.CreateNode(new Vector2(0, 0));
        var n2 = _graph.CreateNode(new Vector2(120, 0));
        var curve = new CurveSegment(n1.WorldPosition, n2.WorldPosition);
        _graph.AddCurvedRoadSegment(n1.Id, n2.Id, curve);

        int edgeId = _graph.FindEdgeId(n1.Id, n2.Id);
        var edge = _graph.Edges[edgeId];
        _manager.GenerateParcelsForEdge(edge, _graph);

        var rightParcels = _manager.GetParcelsForEdge(edgeId, ParcelSide.Right);
        var leftParcels = _manager.GetParcelsForEdge(edgeId, ParcelSide.Left);

        // Right side parcels have positive Y coordinates
        foreach (var p in rightParcels)
        {
            Assert.That(p.Center.Y, Is.GreaterThan(0f));
            Assert.That(p.AccessPoint.Y, Is.EqualTo(0f).Within(0.01f));
            // Vector from Center to AccessPoint points along -Y => angle is -PI / 2
            Vector2 toRoad = (p.AccessPoint - p.Center).Normalized();
            Assert.That(toRoad.Y, Is.LessThan(-0.9f));
            Assert.That(p.FacingAngle, Is.EqualTo(-Mathf.Pi * 0.5f).Within(0.05f));
        }

        // Left side parcels have negative Y coordinates
        foreach (var p in leftParcels)
        {
            Assert.That(p.Center.Y, Is.LessThan(0f));
            Assert.That(p.AccessPoint.Y, Is.EqualTo(0f).Within(0.01f));
            // Vector from Center to AccessPoint points along +Y => angle is +PI / 2
            Vector2 toRoad = (p.AccessPoint - p.Center).Normalized();
            Assert.That(toRoad.Y, Is.GreaterThan(0.9f));
            Assert.That(p.FacingAngle, Is.EqualTo(Mathf.Pi * 0.5f).Within(0.05f));
        }
    }

    [Test]
    public void GetSideOfPoint_CorrectlyIdentifiesLeftAndRight()
    {
        var curve = new CurveSegment(new Vector2(0, 0), new Vector2(100, 0));

        // In Godot 2D (Y down), tangent is (1, 0), normal is (0, 1) (+Y is Right, -Y is Left)
        Vector2 rightPoint = new Vector2(50, 30);
        Vector2 leftPoint = new Vector2(50, -30);

        Assert.That(ParcelManager.GetSideOfPoint(curve, rightPoint), Is.EqualTo(ParcelSide.Right));
        Assert.That(ParcelManager.GetSideOfPoint(curve, leftPoint), Is.EqualTo(ParcelSide.Left));
    }

    [Test]
    public void ContainsPoint_IdentifiesPointsInsideAndOutsideQuadrilateral()
    {
        var n1 = _graph.CreateNode(new Vector2(0, 0));
        var n2 = _graph.CreateNode(new Vector2(120, 0));
        var curve = new CurveSegment(n1.WorldPosition, n2.WorldPosition);
        _graph.AddCurvedRoadSegment(n1.Id, n2.Id, curve);

        int edgeId = _graph.FindEdgeId(n1.Id, n2.Id);
        _manager.GenerateParcelsForEdge(_graph.Edges[edgeId], _graph);

        var parcel = _manager.Parcels[0];

        // Center must be inside
        Assert.That(parcel.ContainsPoint(parcel.Center), Is.True);
        // Distant point must be outside
        Assert.That(parcel.ContainsPoint(new Vector2(9999, 9999)), Is.False);
    }

    [Test]
    public void ZoningAndDezoning_InitializesCapacitiesAndClearsProperly()
    {
        var n1 = _graph.CreateNode(new Vector2(0, 0));
        var n2 = _graph.CreateNode(new Vector2(100, 0));
        _graph.AddCurvedRoadSegment(n1.Id, n2.Id, new CurveSegment(n1.WorldPosition, n2.WorldPosition));

        int edgeId = _graph.FindEdgeId(n1.Id, n2.Id);
        _manager.GenerateParcelsForEdge(_graph.Edges[edgeId], _graph);
        var parcel = _manager.Parcels[0];

        // 1. Zone Residential
        bool resResult = _manager.ZoneParcel(parcel.Id, ZoneType.Residential);
        Assert.That(resResult, Is.True);
        Assert.That(parcel.ZoneType, Is.EqualTo(ZoneType.Residential));
        Assert.That(parcel.ResidentialCap, Is.EqualTo(ParcelManager.DefaultResidentialCap));
        Assert.That(parcel.Population, Is.EqualTo(0));
        Assert.That(parcel.Jobs, Is.EqualTo(0));

        // 2. Zone Commercial
        bool comResult = _manager.ZoneParcel(parcel.Id, ZoneType.Commercial);
        Assert.That(comResult, Is.True);
        Assert.That(parcel.ZoneType, Is.EqualTo(ZoneType.Commercial));
        Assert.That(parcel.Jobs, Is.EqualTo(ParcelManager.DefaultCommercialJobs));
        Assert.That(parcel.CommercialCap, Is.EqualTo(ParcelManager.DefaultCommercialCap));
        Assert.That(parcel.ResidentialCap, Is.EqualTo(0));

        // 3. Zone Industrial
        bool indResult = _manager.ZoneParcel(parcel.Id, ZoneType.Industrial);
        Assert.That(indResult, Is.True);
        Assert.That(parcel.ZoneType, Is.EqualTo(ZoneType.Industrial));
        Assert.That(parcel.Jobs, Is.EqualTo(ParcelManager.DefaultIndustrialJobs));

        // 4. Dezone
        bool dezoneResult = _manager.DezoneParcel(parcel.Id);
        Assert.That(dezoneResult, Is.True);
        Assert.That(parcel.ZoneType, Is.EqualTo(ZoneType.Empty));
        Assert.That(parcel.ResidentialCap, Is.EqualTo(0));
        Assert.That(parcel.Jobs, Is.EqualTo(0));
        Assert.That(parcel.CommercialCap, Is.EqualTo(0));
    }

    [Test]
    public void ZoneEdgeSide_ZonesAllParcelsOnSpecificSide()
    {
        var n1 = _graph.CreateNode(new Vector2(0, 0));
        var n2 = _graph.CreateNode(new Vector2(160, 0));
        _graph.AddCurvedRoadSegment(n1.Id, n2.Id, new CurveSegment(n1.WorldPosition, n2.WorldPosition));

        int edgeId = _graph.FindEdgeId(n1.Id, n2.Id);
        _manager.GenerateParcelsForEdge(_graph.Edges[edgeId], _graph);

        int leftCount = _manager.GetParcelsForEdge(edgeId, ParcelSide.Left).Count;
        int zoned = _manager.ZoneEdgeSide(edgeId, ParcelSide.Left, ZoneType.Residential);

        Assert.That(zoned, Is.EqualTo(leftCount));

        // Verify Left side are Residential
        foreach (var p in _manager.GetParcelsForEdge(edgeId, ParcelSide.Left))
        {
            Assert.That(p.ZoneType, Is.EqualTo(ZoneType.Residential));
        }

        // Verify Right side remain Empty
        foreach (var p in _manager.GetParcelsForEdge(edgeId, ParcelSide.Right))
        {
            Assert.That(p.ZoneType, Is.EqualTo(ZoneType.Empty));
        }
    }

    [Test]
    public void Aggregates_TotalPopulationAndTotalJobs_CalculatedAccurately()
    {
        var n1 = _graph.CreateNode(new Vector2(0, 0));
        var n2 = _graph.CreateNode(new Vector2(150, 0));
        _graph.AddCurvedRoadSegment(n1.Id, n2.Id, new CurveSegment(n1.WorldPosition, n2.WorldPosition));

        int edgeId = _graph.FindEdgeId(n1.Id, n2.Id);
        _manager.GenerateParcelsForEdge(_graph.Edges[edgeId], _graph);

        var p1 = _manager.Parcels[0];
        var p2 = _manager.Parcels[1];

        _manager.ZoneParcel(p1.Id, ZoneType.Residential);
        p1.Population = 65;

        _manager.ZoneParcel(p2.Id, ZoneType.Commercial);
        // Commercial default jobs = 80

        Assert.That(_manager.TotalPopulation, Is.EqualTo(65));
        Assert.That(_manager.TotalJobs, Is.EqualTo(ParcelManager.DefaultCommercialJobs));
    }

    [Test]
    public void EdgeRemoval_CleansUpParcelsAndUpdatesAggregates()
    {
        var n1 = _graph.CreateNode(new Vector2(0, 0));
        var n2 = _graph.CreateNode(new Vector2(120, 0));
        _graph.AddCurvedRoadSegment(n1.Id, n2.Id, new CurveSegment(n1.WorldPosition, n2.WorldPosition));

        int edgeId = _graph.FindEdgeId(n1.Id, n2.Id);
        _manager.GenerateParcelsForEdge(_graph.Edges[edgeId], _graph);
        int initialCount = _manager.Parcels.Count;
        Assert.That(initialCount, Is.GreaterThan(0));

        // Zone one parcel and add population
        var p = _manager.Parcels[0];
        _manager.ZoneParcel(p.Id, ZoneType.Residential);
        p.Population = 50;
        Assert.That(_manager.TotalPopulation, Is.EqualTo(50));

        // Remove road segment
        _graph.RemoveRoadSegment(n1.Id, n2.Id);
        _manager.HandleEdgeRemoved(edgeId);

        Assert.That(_manager.Parcels.Count, Is.EqualTo(0));
        Assert.That(_manager.TotalPopulation, Is.EqualTo(0));
        Assert.That(_manager.GetParcelsForEdge(edgeId).Count, Is.EqualTo(0));
    }

    [Test]
    public void EdgeSplit_CleansUpOldParcelsAndGeneratesParcelsForNewEdges()
    {
        var n1 = _graph.CreateNode(new Vector2(0, 0));
        var n2 = _graph.CreateNode(new Vector2(200, 0));
        _graph.AddCurvedRoadSegment(n1.Id, n2.Id, new CurveSegment(n1.WorldPosition, n2.WorldPosition));

        int origEdgeId = _graph.FindEdgeId(n1.Id, n2.Id);
        _manager.GenerateParcelsForEdge(_graph.Edges[origEdgeId], _graph);
        int origParcelCount = _manager.Parcels.Count;
        Assert.That(origParcelCount, Is.GreaterThan(0));

        // Split edge at mid-point
        var junction = _graph.SplitEdge(origEdgeId, 0.5f);
        Assert.That(junction, Is.Not.Null);

        // Update ParcelManager
        _manager.HandleEdgeSplit(origEdgeId, _graph);

        // Old parcels for origEdgeId must no longer exist
        Assert.That(_manager.GetParcelsForEdge(origEdgeId).Count, Is.EqualTo(0));

        // New parcels should be generated along the split segments
        int newEdge1 = _graph.FindEdgeId(n1.Id, junction.Id);
        int newEdge2 = _graph.FindEdgeId(junction.Id, n2.Id);

        Assert.That(newEdge1, Is.Not.EqualTo(-1));
        Assert.That(newEdge2, Is.Not.EqualTo(-1));

        var parcels1 = _manager.GetParcelsForEdge(newEdge1);
        var parcels2 = _manager.GetParcelsForEdge(newEdge2);

        Assert.That(parcels1.Count, Is.GreaterThan(0));
        Assert.That(parcels2.Count, Is.GreaterThan(0));
    }

    [Test]
    public void QueryZoningTarget_FindsParcelAndDetectsSide()
    {
        var n1 = _graph.CreateNode(new Vector2(0, 0));
        var n2 = _graph.CreateNode(new Vector2(160, 0));
        _graph.AddCurvedRoadSegment(n1.Id, n2.Id, new CurveSegment(n1.WorldPosition, n2.WorldPosition));

        int edgeId = _graph.FindEdgeId(n1.Id, n2.Id);
        _manager.GenerateParcelsForEdge(_graph.Edges[edgeId], _graph);

        var firstRightParcel = _manager.GetParcelsForEdge(edgeId, ParcelSide.Right)[0];

        // Query point directly at parcel center
        var queryDirect = _manager.QueryZoningTarget(firstRightParcel.Center, _graph, selectFullSide: false);
        Assert.That(queryDirect.Parcel, Is.Not.Null);
        Assert.That(queryDirect.Parcel.Id, Is.EqualTo(firstRightParcel.Id));
        Assert.That(queryDirect.Side, Is.EqualTo(ParcelSide.Right));
        Assert.That(queryDirect.SideParcels.Count, Is.EqualTo(1));

        // Query with selectFullSide = true
        var queryFull = _manager.QueryZoningTarget(firstRightParcel.Center, _graph, selectFullSide: true);
        int rightCount = _manager.GetParcelsForEdge(edgeId, ParcelSide.Right).Count;
        Assert.That(queryFull.SideParcels.Count, Is.EqualTo(rightCount));
        Assert.That(queryFull.Side, Is.EqualTo(ParcelSide.Right));
    }

    [Test]
    public void EconomyIntegration_TaxRevenueCalculatesParcelPopulationAndJobs()
    {
        var economy = new EconomyManager(1000f);
        var grid = new CityGrid(5, 5, 64f);
        var tm = new TransitManager();

        var n1 = _graph.CreateNode(new Vector2(0, 0));
        var n2 = _graph.CreateNode(new Vector2(120, 0));
        _graph.AddCurvedRoadSegment(n1.Id, n2.Id, new CurveSegment(n1.WorldPosition, n2.WorldPosition));

        int edgeId = _graph.FindEdgeId(n1.Id, n2.Id);
        _manager.GenerateParcelsForEdge(_graph.Edges[edgeId], _graph);

        var pRes = _manager.Parcels[0];
        _manager.ZoneParcel(pRes.Id, ZoneType.Residential);
        pRes.Population = 100;

        var pCom = _manager.Parcels[1];
        _manager.ZoneParcel(pCom.Id, ZoneType.Commercial);
        // Jobs = 80

        float initialMoney = economy.Money;
        economy.ProcessFinancialTick(grid, _graph, tm, _manager);

        float expectedResTax = (100 * economy.TaxPerPopulation) / 24f;
        float expectedComTax = (80 * economy.TaxPerJob) / 24f;
        float expectedRoadMaint = (1 * EconomyManager.RoadMaintenancePerSegment) / 24f;

        float expectedDelta = expectedResTax + expectedComTax - expectedRoadMaint;
        Assert.That(economy.LastDelta, Is.EqualTo(expectedDelta).Within(0.01f));
        Assert.That(economy.Money, Is.EqualTo(initialMoney + expectedDelta).Within(0.01f));
    }

    [Test]
    public void GenerateParcels_ShortEdgeOrInvalid_ReturnsEmptyList()
    {
        // 1. Edge with length < 24 px
        var n1 = _graph.CreateNode(new Vector2(0, 0));
        var n2 = _graph.CreateNode(new Vector2(10, 0));
        _graph.AddCurvedRoadSegment(n1.Id, n2.Id, new CurveSegment(n1.WorldPosition, n2.WorldPosition));

        int shortEdgeId = _graph.FindEdgeId(n1.Id, n2.Id);
        var shortParcels = _manager.GenerateParcelsForEdge(_graph.Edges[shortEdgeId], _graph);
        Assert.That(shortParcels.Count, Is.EqualTo(0));

        // 2. Null or invalid edge
        Assert.That(_manager.GenerateParcelsForEdge(null).Count, Is.EqualTo(0));
        var invalidEdge = new RoadEdge { Id = -1, FromId = -1, ToId = -1 };
        Assert.That(_manager.GenerateParcelsForEdge(invalidEdge).Count, Is.EqualTo(0));
    }

    [Test]
    public void DezoneEdgeSide_ResetsOnlySpecifiedSide()
    {
        var n1 = _graph.CreateNode(new Vector2(0, 0));
        var n2 = _graph.CreateNode(new Vector2(160, 0));
        _graph.AddCurvedRoadSegment(n1.Id, n2.Id, new CurveSegment(n1.WorldPosition, n2.WorldPosition));

        int edgeId = _graph.FindEdgeId(n1.Id, n2.Id);
        _manager.GenerateParcelsForEdge(_graph.Edges[edgeId], _graph);

        _manager.ZoneEdgeSide(edgeId, ParcelSide.Left, ZoneType.Residential);
        _manager.ZoneEdgeSide(edgeId, ParcelSide.Right, ZoneType.Commercial);

        int dezoned = _manager.DezoneEdgeSide(edgeId, ParcelSide.Left);
        Assert.That(dezoned, Is.GreaterThan(0));

        foreach (var p in _manager.GetParcelsForEdge(edgeId, ParcelSide.Left))
        {
            Assert.That(p.ZoneType, Is.EqualTo(ZoneType.Empty));
        }

        foreach (var p in _manager.GetParcelsForEdge(edgeId, ParcelSide.Right))
        {
            Assert.That(p.ZoneType, Is.EqualTo(ZoneType.Commercial));
        }
    }

    [Test]
    public void FindClosestParcel_ReturnsNearestParcelOrNull()
    {
        var n1 = _graph.CreateNode(new Vector2(0, 0));
        var n2 = _graph.CreateNode(new Vector2(120, 0));
        _graph.AddCurvedRoadSegment(n1.Id, n2.Id, new CurveSegment(n1.WorldPosition, n2.WorldPosition));

        int edgeId = _graph.FindEdgeId(n1.Id, n2.Id);
        _manager.GenerateParcelsForEdge(_graph.Edges[edgeId], _graph);

        var first = _manager.Parcels[0];
        var found = _manager.FindClosestParcel(first.Center, 20f);
        Assert.That(found, Is.Not.Null);
        Assert.That(found.Id, Is.EqualTo(first.Id));

        var farAway = _manager.FindClosestParcel(new Vector2(9999, 9999), 30f);
        Assert.That(farAway, Is.Null);
    }

    [Test]
    public void QueryZoningTarget_ProximitySearchFallbackNearEdge()
    {
        var n1 = _graph.CreateNode(new Vector2(0, 0));
        var n2 = _graph.CreateNode(new Vector2(150, 0));
        _graph.AddCurvedRoadSegment(n1.Id, n2.Id, new CurveSegment(n1.WorldPosition, n2.WorldPosition));

        int edgeId = _graph.FindEdgeId(n1.Id, n2.Id);
        _manager.GenerateParcelsForEdge(_graph.Edges[edgeId], _graph);

        // Point near road on the Right side (+Y in Godot 2D), but just outside lot boundary
        Vector2 nearPt = new Vector2(50, ParcelManager.RoadOffset + ParcelManager.DefaultLotDepth + 5f);
        var target = _manager.QueryZoningTarget(nearPt, _graph, selectFullSide: false);

        Assert.That(target.Parcel, Is.Not.Null);
        Assert.That(target.Edge, Is.Not.Null);
        Assert.That(target.Side, Is.EqualTo(ParcelSide.Right));
    }

    [Test]
    public void SAT_PolygonsOverlap_AccuratelyDetectsOverlapsAndSeparations()
    {
        // 1. Two disjoint quadrilaterals
        var polyA = new Vector2[] { new Vector2(0, 0), new Vector2(40, 0), new Vector2(40, 48), new Vector2(0, 48) };
        var polyB = new Vector2[] { new Vector2(50, 0), new Vector2(90, 0), new Vector2(90, 48), new Vector2(50, 48) };
        Assert.That(ParcelManager.PolygonsOverlap(polyA, polyB), Is.False);

        // 2. Two overlapping quadrilaterals (overlap by 10 px)
        var polyC = new Vector2[] { new Vector2(30, 0), new Vector2(70, 0), new Vector2(70, 48), new Vector2(30, 48) };
        Assert.That(ParcelManager.PolygonsOverlap(polyA, polyC), Is.True);

        // 3. Two adjacent quadrilaterals sharing an edge (x = 40)
        var polyD = new Vector2[] { new Vector2(40, 0), new Vector2(80, 0), new Vector2(80, 48), new Vector2(40, 48) };
        Assert.That(ParcelManager.PolygonsOverlap(polyA, polyD), Is.False, "Adjacent lots sharing a boundary line must not be flagged as overlapping");
    }

    [Test]
    public void ParcelManager_CornerIntersectionParcels_RejectsCollidingLots()
    {
        // Road 1: Along +X from (0, 0) to (200, 0)
        var nJunc = _graph.CreateNode(new Vector2(0, 0));
        var nEast = _graph.CreateNode(new Vector2(200, 0));
        _graph.AddCurvedRoadSegment(nJunc.Id, nEast.Id, new CurveSegment(nJunc.WorldPosition, nEast.WorldPosition));

        // Road 2: Along +Y from (0, 0) to (0, 200) meeting Road 1 at nJunc (0, 0)
        var nSouth = _graph.CreateNode(new Vector2(0, 200));
        _graph.AddCurvedRoadSegment(nJunc.Id, nSouth.Id, new CurveSegment(nJunc.WorldPosition, nSouth.WorldPosition));

        _manager.RefreshParcels(_graph);

        // Verify that no two parcels across the entire network overlap
        var parcels = _manager.Parcels;
        Assert.That(parcels.Count, Is.GreaterThan(0));

        for (int i = 0; i < parcels.Count; i++)
        {
            for (int j = i + 1; j < parcels.Count; j++)
            {
                bool overlaps = ParcelManager.ParcelsOverlap(parcels[i], parcels[j]);
                Assert.That(overlaps, Is.False, $"Parcels #{parcels[i].Id} and #{parcels[j].Id} must not collide or overlap at intersection corner");
            }
        }
    }
    [Test]
    public void Degree2MidRoadStop_PreservesZonedParcelsOnBothSidesWithoutJunctionMarginDeletion()
    {
        var n1 = _graph.CreateNode(new Vector2(0, 0));
        var n2 = _graph.CreateNode(new Vector2(400, 0));
        _graph.AddCurvedRoadSegment(n1.Id, n2.Id, new CurveSegment(n1.WorldPosition, n2.WorldPosition));

        int edgeId = _graph.FindEdgeId(n1.Id, n2.Id);
        _manager.GenerateParcelsForEdge(_graph.Edges[edgeId], _graph);

        var rightParcels = _manager.GetParcelsForEdge(edgeId, ParcelSide.Right);
        var leftParcels = _manager.GetParcelsForEdge(edgeId, ParcelSide.Left);

        // Find parcels near midpoint (distance to x=200 is < JunctionMargin 44px) on BOTH sides
        var nearMidRight = rightParcels.First(p => Mathf.Abs(p.AccessPoint.X - 200f) < ParcelManager.JunctionMargin);
        var nearMidLeft = leftParcels.First(p => Mathf.Abs(p.AccessPoint.X - 200f) < ParcelManager.JunctionMargin);

        // Also zone far parcels on both sides
        var farRight = rightParcels.OrderBy(p => p.NormalizedT).First();
        var farLeft = leftParcels.OrderByDescending(p => p.NormalizedT).First();

        _manager.ZoneParcel(nearMidRight.Id, ZoneType.Commercial, jobs: 80);
        _manager.ZoneParcel(nearMidLeft.Id, ZoneType.Residential, population: 120);
        _manager.ZoneParcel(farRight.Id, ZoneType.Residential, population: 100);
        _manager.ZoneParcel(farLeft.Id, ZoneType.Commercial, jobs: 60);

        int expectedPop = 220;
        int expectedJobs = 140;

        Assert.That(_manager.TotalPopulation, Is.EqualTo(expectedPop));
        Assert.That(_manager.TotalJobs, Is.EqualTo(expectedJobs));

        // Split edge at mid-road point (t = 0.5 at x = 200) simulating mid-road transit stop insertion
        var junction = _graph.SplitEdgeAtPoint(edgeId, new Vector2(200, 0));
        Assert.That(junction, Is.Not.Null);
        Assert.That(_graph.GetNodeDegree(junction.Id), Is.EqualTo(2), "Bus stop on continuous road must be a degree-2 node.");

        // Synchronize parcels
        _manager.RefreshParcels(_graph);

        // All 4 zoned parcels on both Left and Right sides must survive without being wiped by JunctionMargin
        Assert.That(_manager.Parcels.Any(p => p.Id == nearMidRight.Id), Is.True, "Mid-road Right parcel within JunctionMargin must be preserved at degree-2 stop.");
        Assert.That(_manager.Parcels.Any(p => p.Id == nearMidLeft.Id), Is.True, "Mid-road Left parcel within JunctionMargin must be preserved at degree-2 stop.");
        Assert.That(_manager.Parcels.Any(p => p.Id == farRight.Id), Is.True, "Far Right parcel must be preserved.");
        Assert.That(_manager.Parcels.Any(p => p.Id == farLeft.Id), Is.True, "Far Left parcel must be preserved.");

        Assert.That(_manager.ActiveParcels.Count(), Is.EqualTo(4));
        Assert.That(_manager.TotalPopulation, Is.EqualTo(expectedPop));
        Assert.That(_manager.TotalJobs, Is.EqualTo(expectedJobs));

        // Verify side and zone properties are retained
        Assert.That(nearMidRight.Side, Is.EqualTo(ParcelSide.Right));
        Assert.That(nearMidRight.ZoneType, Is.EqualTo(ZoneType.Commercial));
        Assert.That(nearMidRight.Jobs, Is.EqualTo(80));

        Assert.That(nearMidLeft.Side, Is.EqualTo(ParcelSide.Left));
        Assert.That(nearMidLeft.ZoneType, Is.EqualTo(ZoneType.Residential));
        Assert.That(nearMidLeft.Population, Is.EqualTo(120));
    }

    [Test]
    public void RefreshParcels_AfterEdgeSplit_PreservesMigratedParcelsDuringReverseEdgeCleanup()
    {
        var n1 = _graph.CreateNode(new Vector2(0, 0));
        var n2 = _graph.CreateNode(new Vector2(300, 0));
        _graph.AddCurvedRoadSegment(n1.Id, n2.Id, new CurveSegment(n1.WorldPosition, n2.WorldPosition));

        int origEdgeId = _graph.FindEdgeId(n1.Id, n2.Id);
        int origRevId = _graph.FindEdgeId(n2.Id, n1.Id);
        _manager.GenerateParcelsForEdge(_graph.Edges[origEdgeId], _graph);

        var parcels = _manager.GetParcelsForEdge(origEdgeId);
        Assert.That(parcels.Count, Is.GreaterThan(0));

        // Zone several parcels along the edge
        _manager.ZoneParcel(parcels[0].Id, ZoneType.Residential, population: 110);
        _manager.ZoneParcel(parcels[1].Id, ZoneType.Commercial, jobs: 75);

        // Split the edge via reverse edge ID to ensure reverse edge handling works seamlessly
        var junction = _graph.SplitEdge(origRevId, 0.4f);
        Assert.That(junction, Is.Not.Null);

        // Calling RefreshParcels should never delete migrated parcels during cleanup of old disconnected edge IDs
        _manager.RefreshParcels(_graph);
        Assert.That(_manager.TotalPopulation, Is.EqualTo(110));
        Assert.That(_manager.TotalJobs, Is.EqualTo(75));
        Assert.That(_manager.ActiveParcels.Count(), Is.EqualTo(2));

        // Multiple repeated calls to RefreshParcels must be idempotent and non-destructive
        _manager.RefreshParcels(_graph);
        _manager.RefreshParcels(_graph);
        Assert.That(_manager.TotalPopulation, Is.EqualTo(110));
        Assert.That(_manager.TotalJobs, Is.EqualTo(75));
        Assert.That(_manager.ActiveParcels.Count(), Is.EqualTo(2));
    }

    [Test]
    public void ModeTransitionAndInspectClear_RetainsAllParcelsInActiveList()
    {
        var n1 = _graph.CreateNode(new Vector2(0, 0));
        var n2 = _graph.CreateNode(new Vector2(400, 0));
        _graph.AddCurvedRoadSegment(n1.Id, n2.Id, new CurveSegment(n1.WorldPosition, n2.WorldPosition));

        int edgeId = _graph.FindEdgeId(n1.Id, n2.Id);
        _manager.GenerateParcelsForEdge(_graph.Edges[edgeId], _graph);

        var allParcels = _manager.GetParcelsForEdge(edgeId);
        for (int i = 0; i < allParcels.Count; i++)
        {
            _manager.ZoneParcel(allParcels[i].Id, ZoneType.Residential, population: 50);
        }

        int initialZonedCount = allParcels.Count;
        int expectedTotalPop = initialZonedCount * 50;
        Assert.That(_manager.TotalPopulation, Is.EqualTo(expectedTotalPop));

        // Simulate transit stop insertion splitting the road
        _graph.SplitEdgeAtPoint(edgeId, new Vector2(200, 0));
        _manager.RefreshParcels(_graph);

        // Verify all zoned parcels remain present in Parcels and ActiveParcels
        Assert.That(_manager.ActiveParcels.Count(), Is.EqualTo(initialZonedCount));
        Assert.That(_manager.TotalPopulation, Is.EqualTo(expectedTotalPop));

        foreach (var p in _manager.ActiveParcels)
        {
            Assert.That(p.ZoneType, Is.EqualTo(ZoneType.Residential));
            Assert.That(p.Population, Is.EqualTo(50));
        }
    }

    [Test]
    public void NoDeadZonesAroundDegree2TransitStops_EmptyParcelsGeneratedNearStop()
    {
        // Straight road of length 200 px
        var n1 = _graph.CreateNode(new Vector2(0, 0));
        var n2 = _graph.CreateNode(new Vector2(200, 0));
        _graph.AddCurvedRoadSegment(n1.Id, n2.Id, new CurveSegment(n1.WorldPosition, n2.WorldPosition));

        int origEdgeId = _graph.FindEdgeId(n1.Id, n2.Id);
        _manager.GenerateParcelsForEdge(_graph.Edges[origEdgeId], _graph);

        // Split edge at midpoint (100, 0) simulating a bus stop insertion
        var junction = _graph.SplitEdgeAtPoint(origEdgeId, new Vector2(100, 0));
        Assert.That(junction, Is.Not.Null);
        _manager.RefreshParcels(_graph);

        int subEdge1 = _graph.FindEdgeId(n1.Id, junction.Id);
        int subEdge2 = _graph.FindEdgeId(junction.Id, n2.Id);

        // Get parcels for both sub-edges
        var parcels1 = _manager.GetParcelsForEdge(subEdge1);
        var parcels2 = _manager.GetParcelsForEdge(subEdge2);

        Assert.That(parcels1.Count, Is.GreaterThan(0));
        Assert.That(parcels2.Count, Is.GreaterThan(0));

        // On sub-edge 1 (from 0 to 100), the parcel closest to the junction at x=100
        // must be right up near the stop (distance to 100 is far less than 44 px, within 25 px)
        var closestSub1 = parcels1.OrderBy(p => Mathf.Abs(p.AccessPoint.X - 100f)).First();
        float distToStop1 = Mathf.Abs(closestSub1.AccessPoint.X - 100f);
        Assert.That(distToStop1, Is.LessThan(30f), "Parcels on sub-edge 1 must be generated close to the degree-2 stop without a 44px dead zone.");

        // On sub-edge 2 (from 100 to 200), the closest parcel
        var closestSub2 = parcels2.OrderBy(p => Mathf.Abs(p.AccessPoint.X - 100f)).First();
        float distToStop2 = Mathf.Abs(closestSub2.AccessPoint.X - 100f);
        Assert.That(distToStop2, Is.LessThan(30f), "Parcels on sub-edge 2 must be generated close to the degree-2 stop without a 44px dead zone.");

        // Both Left and Right sides near the stop must have empty parcels
        var rightNear = _manager.GetParcelsForEdge(subEdge1, ParcelSide.Right).Any(p => Mathf.Abs(p.AccessPoint.X - 100f) < 30f);
        var leftNear = _manager.GetParcelsForEdge(subEdge1, ParcelSide.Left).Any(p => Mathf.Abs(p.AccessPoint.X - 100f) < 30f);
        Assert.That(rightNear, Is.True, "Empty parcel must exist near degree-2 stop on Right side.");
        Assert.That(leftNear, Is.True, "Empty parcel must exist near degree-2 stop on Left side.");
    }

    [Test]
    public void TJunction_UninterruptedContinuousSide_ParcelsFullyPlacedAndZonable()
    {
        // Continuous horizontal road from (0, 0) to (200, 0)
        var nWest = _graph.CreateNode(new Vector2(0, 0));
        var nJunc = _graph.CreateNode(new Vector2(100, 0));
        var nEast = _graph.CreateNode(new Vector2(200, 0));
        _graph.AddCurvedRoadSegment(nWest.Id, nJunc.Id, new CurveSegment(nWest.WorldPosition, nJunc.WorldPosition));
        _graph.AddCurvedRoadSegment(nJunc.Id, nEast.Id, new CurveSegment(nJunc.WorldPosition, nEast.WorldPosition));

        // Incoming branch road from junction (100, 0) heading South to (100, 100) (+Y in Godot 2D = Right side)
        var nSouth = _graph.CreateNode(new Vector2(100, 100));
        _graph.AddCurvedRoadSegment(nJunc.Id, nSouth.Id, new CurveSegment(nJunc.WorldPosition, nSouth.WorldPosition));

        _manager.RefreshParcels(_graph);

        int edgeWest = _graph.FindEdgeId(nWest.Id, nJunc.Id);
        int edgeEast = _graph.FindEdgeId(nJunc.Id, nEast.Id);

        // On the uninterrupted continuous side (Left side, -Y direction):
        // Parcels must be placed continuously with no gap
        var leftWest = _manager.GetParcelsForEdge(edgeWest, ParcelSide.Left);
        var leftEast = _manager.GetParcelsForEdge(edgeEast, ParcelSide.Left);

        Assert.That(leftWest.Count, Is.GreaterThan(0));
        Assert.That(leftEast.Count, Is.GreaterThan(0));

        // Parcels on continuous side are placed right up near x = 100
        var closestLeftWest = leftWest.OrderBy(p => Mathf.Abs(p.AccessPoint.X - 100f)).First();
        var closestLeftEast = leftEast.OrderBy(p => Mathf.Abs(p.AccessPoint.X - 100f)).First();
        Assert.That(Mathf.Abs(closestLeftWest.AccessPoint.X - 100f), Is.LessThan(30f));
        Assert.That(Mathf.Abs(closestLeftEast.AccessPoint.X - 100f), Is.LessThan(30f));

        // Left-side parcels are fully zonable
        bool zonedWest = _manager.ZoneParcel(closestLeftWest.Id, ZoneType.Residential, population: 100);
        bool zonedEast = _manager.ZoneParcel(closestLeftEast.Id, ZoneType.Commercial, jobs: 80);
        Assert.That(zonedWest, Is.True);
        Assert.That(zonedEast, Is.True);
        Assert.That(_manager.TotalPopulation, Is.EqualTo(100));
        Assert.That(_manager.TotalJobs, Is.EqualTo(80));

        // On the branch side (Right side, +Y direction where road South exists):
        // No parcel should physically collide with or overlap the South road pavement
        int branchEdge = _graph.FindEdgeId(nJunc.Id, nSouth.Id);
        var southRoad = _graph.Edges[branchEdge];
        foreach (var p in _manager.Parcels)
        {
            Assert.That(ParcelManager.DoesParcelCollideWithRoad(p, southRoad), Is.False,
                $"Parcel #{p.Id} must not collide with branch road pavement.");
        }
    }

    [Test]
    public void PhysicalCollisionRejection_RejectsCollidingCandidate_AcceptsNonColliding()
    {
        // Continuous road from (0, 0) to (200, 0)
        var nWest = _graph.CreateNode(new Vector2(0, 0));
        var nJunc = _graph.CreateNode(new Vector2(100, 0));
        _graph.AddCurvedRoadSegment(nWest.Id, nJunc.Id, new CurveSegment(nWest.WorldPosition, nJunc.WorldPosition));

        // Cross road from (100, 0) to (100, 100) (+Y direction)
        var nSouth = _graph.CreateNode(new Vector2(100, 100));
        _graph.AddCurvedRoadSegment(nJunc.Id, nSouth.Id, new CurveSegment(nJunc.WorldPosition, nSouth.WorldPosition));

        int crossEdgeId = _graph.FindEdgeId(nJunc.Id, nSouth.Id);
        var crossEdge = _graph.Edges[crossEdgeId];

        // Construct candidate A: on Right side (+Y) close to x = 100, overlapping the cross-road pavement
        // Corners: x in [80, 98], y in [8, 56]
        var collidingCandidate = new RoadsideParcel
        {
            Boundary = new Vector2[]
            {
                new Vector2(80, 8),
                new Vector2(98, 8),
                new Vector2(98, 56),
                new Vector2(80, 56)
            }
        };

        // Construct candidate B: on Left side (-Y) near x = 100 (continuous uninterrupted side)
        // Corners: x in [80, 98], y in [-56, -8]
        var nonCollidingLeft = new RoadsideParcel
        {
            Boundary = new Vector2[]
            {
                new Vector2(80, -8),
                new Vector2(98, -8),
                new Vector2(98, -56),
                new Vector2(80, -56)
            }
        };

        // Construct candidate C: on Right side (+Y) far from x = 100
        // Corners: x in [10, 48], y in [8, 56]
        var nonCollidingFar = new RoadsideParcel
        {
            Boundary = new Vector2[]
            {
                new Vector2(10, 8),
                new Vector2(48, 8),
                new Vector2(48, 56),
                new Vector2(10, 56)
            }
        };

        // Verify collision evaluations:
        Assert.That(ParcelManager.DoesParcelCollideWithRoad(collidingCandidate, crossEdge), Is.True,
            "Candidate intersecting cross-road pavement must be detected as colliding.");

        Assert.That(ParcelManager.DoesParcelCollideWithRoad(nonCollidingLeft, crossEdge), Is.False,
            "Candidate on uninterrupted opposite side must not collide with cross-road.");

        Assert.That(ParcelManager.DoesParcelCollideWithRoad(nonCollidingFar, crossEdge), Is.False,
            "Candidate far from cross-road must not collide with cross-road.");
    }
}

