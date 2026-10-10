using Godot;
using NUnit.Framework;
using CitySim.Simulation;
using System.Collections.Generic;
using System.Linq;

namespace CitySim.Tests.Simulation;

[TestFixture]
public class DynamicODTests
{
    private RoadGraph _graph;
    private ParcelManager _parcelManager;

    [SetUp]
    public void Setup()
    {
        _graph = new RoadGraph();
        _parcelManager = new ParcelManager();
        _parcelManager.Initialize(_graph);
    }

    [Test]
    public void ParcelOD_TripGeneration_ScalesWithPopulationAndRushHourCurve()
    {
        // 1. Set up curved road with residential origin parcel and commercial destination parcel
        var n1 = _graph.CreateNode(new Vector2(0, 0));
        var n2 = _graph.CreateNode(new Vector2(300, 0));
        var curve = CurveSegment.CreateFromThreePoints(n1.WorldPosition, new Vector2(150, 40), n2.WorldPosition);
        _graph.AddCurvedRoadSegment(n1.Id, n2.Id, curve);
        _graph.BuildPathCache(10);

        int edgeId = _graph.FindEdgeId(n1.Id, n2.Id);
        var edge = _graph.Edges[edgeId];
        var parcels = _parcelManager.GenerateParcelsForEdge(edge, _graph);

        Assert.That(parcels.Count, Is.GreaterThanOrEqualTo(2));
        var resParcel = parcels[0];
        var comParcel = parcels[parcels.Count - 1];

        _parcelManager.ZoneParcel(resParcel.Id, ZoneType.Residential, population: 100);
        _parcelManager.ZoneParcel(comParcel.Id, ZoneType.Commercial, jobs: 100);

        var od = new DynamicODMatrix(0);

        // Recalculate at morning peak rush (8:00 AM)
        od.Recalculate(8.0f, null, null, false, parcelManager: _parcelManager, roadGraph: _graph);
        float peakTrips = od.TotalTrips;
        float peakDemandMult = ODMatrix.GetDemandMultiplier(8.0f);

        // Recalculate at quiet night (2:00 AM)
        od.Recalculate(2.0f, null, null, false, parcelManager: _parcelManager, roadGraph: _graph);
        float nightTrips = od.TotalTrips;
        float nightDemandMult = ODMatrix.GetDemandMultiplier(2.0f);

        Assert.That(peakTrips, Is.GreaterThan(0f));
        Assert.That(nightTrips, Is.GreaterThan(0f));
        Assert.That(peakTrips, Is.GreaterThan(nightTrips * 10f), "Peak rush trips must be dramatically higher than night trips");

        // Verify scaling ratio matches demand multiplier ratio closely
        float expectedRatio = peakDemandMult / nightDemandMult;
        float actualRatio = peakTrips / nightTrips;
        Assert.That(actualRatio, Is.EqualTo(expectedRatio).Within(0.05f));

        // Verify doubling population scales trips proportionally
        _parcelManager.ZoneParcel(resParcel.Id, ZoneType.Residential, population: 200);
        od.Recalculate(8.0f, null, null, false, parcelManager: _parcelManager, roadGraph: _graph);
        float doubledTrips = od.TotalTrips;
        Assert.That(doubledTrips, Is.EqualTo(peakTrips * 2f).Within(1.0f));
    }

    [Test]
    public void ParcelOD_DirectionalBias_InboundMorningOutboundEvening()
    {
        var n1 = _graph.CreateNode(new Vector2(0, 0));
        var n2 = _graph.CreateNode(new Vector2(250, 0));
        var curve = new CurveSegment(n1.WorldPosition, n2.WorldPosition);
        _graph.AddCurvedRoadSegment(n1.Id, n2.Id, curve);
        _graph.BuildPathCache(10);

        int edgeId = _graph.FindEdgeId(n1.Id, n2.Id);
        var edge = _graph.Edges[edgeId];
        var parcels = _parcelManager.GenerateParcelsForEdge(edge, _graph);

        var resParcel = parcels[0];
        var comParcel = parcels[parcels.Count - 1];

        _parcelManager.ZoneParcel(resParcel.Id, ZoneType.Residential, population: 150);
        _parcelManager.ZoneParcel(comParcel.Id, ZoneType.Commercial, jobs: 150);

        var od = new DynamicODMatrix(0);

        // Morning Peak: 7:30 AM (Inbound rush, Home -> Work)
        od.Recalculate(7.5f, null, null, false, parcelManager: _parcelManager, roadGraph: _graph);
        var morningTrips = od.DynamicTrips;
        Assert.That(morningTrips.Count, Is.GreaterThan(0));

        var mTrip = morningTrips.FirstOrDefault(t => t.OriginParcelId == resParcel.Id && t.DestParcelId == comParcel.Id);
        Assert.That(mTrip, Is.Not.Null);
        Assert.That(mTrip.TotalTrips, Is.GreaterThan(0f));

        // Evening Peak: 17:00 PM (Outbound rush, Work -> Home)
        od.Recalculate(17.0f, null, null, false, parcelManager: _parcelManager, roadGraph: _graph);
        var eveningTrips = od.DynamicTrips;
        Assert.That(eveningTrips.Count, Is.GreaterThan(0));

        var eTrip = eveningTrips.FirstOrDefault(t => t.OriginParcelId == comParcel.Id && t.DestParcelId == resParcel.Id);
        Assert.That(eTrip, Is.Not.Null);
        Assert.That(eTrip.TotalTrips, Is.GreaterThan(0f));
    }

    [Test]
    public void ParcelOD_GravityDecay_OverCurvedRoadNetworkDistances()
    {
        // Setup node sequence: N1 (0,0) -> N2 (200, 0) -> N3 (800, 0)
        var n1 = _graph.CreateNode(new Vector2(0, 0));
        var n2 = _graph.CreateNode(new Vector2(200, 0));
        var n3 = _graph.CreateNode(new Vector2(800, 0));

        // Curved segment 1: Length ~220
        var curve1 = CurveSegment.CreateFromThreePoints(n1.WorldPosition, new Vector2(100, 50), n2.WorldPosition);
        _graph.AddCurvedRoadSegment(n1.Id, n2.Id, curve1);

        // Curved segment 2: Length ~650
        var curve2 = CurveSegment.CreateFromThreePoints(n2.WorldPosition, new Vector2(500, 100), n3.WorldPosition);
        _graph.AddCurvedRoadSegment(n2.Id, n3.Id, curve2);

        _graph.BuildPathCache(10);

        int e1 = _graph.FindEdgeId(n1.Id, n2.Id);
        int e2 = _graph.FindEdgeId(n2.Id, n3.Id);

        var pList1 = _parcelManager.GenerateParcelsForEdge(_graph.Edges[e1], _graph);
        var pList2 = _parcelManager.GenerateParcelsForEdge(_graph.Edges[e2], _graph);

        var originParcel = pList1[0];       // Residential near N1
        var nearDestParcel = pList1.Last(); // Commercial near N2 (short network distance)
        var farDestParcel = pList2.Last();  // Commercial near N3 (long network distance)

        _parcelManager.ZoneParcel(originParcel.Id, ZoneType.Residential, population: 300);
        _parcelManager.ZoneParcel(nearDestParcel.Id, ZoneType.Commercial, jobs: 100);
        _parcelManager.ZoneParcel(farDestParcel.Id, ZoneType.Commercial, jobs: 100);

        var od = new DynamicODMatrix(0);
        od.Recalculate(8.0f, null, null, false, parcelManager: _parcelManager, roadGraph: _graph);

        var nearTrip = od.DynamicTrips.FirstOrDefault(t => t.OriginParcelId == originParcel.Id && t.DestParcelId == nearDestParcel.Id);
        var farTrip = od.DynamicTrips.FirstOrDefault(t => t.OriginParcelId == originParcel.Id && t.DestParcelId == farDestParcel.Id);

        Assert.That(nearTrip, Is.Not.Null);
        Assert.That(farTrip, Is.Not.Null);
        Assert.That(nearTrip.NetworkDistance, Is.LessThan(farTrip.NetworkDistance));
        Assert.That(nearTrip.TotalTrips, Is.GreaterThan(farTrip.TotalTrips), "Closer workplace must receive more trips due to gravity impedance decay");
    }

    [Test]
    public void ParcelOD_ModeSplit_WalkingDominatesShortTrips_CarDominatesLongTrips()
    {
        // Short road edge: 120 px
        var n1 = _graph.CreateNode(new Vector2(0, 0));
        var n2 = _graph.CreateNode(new Vector2(120, 0));
        _graph.AddCurvedRoadSegment(n1.Id, n2.Id, new CurveSegment(n1.WorldPosition, n2.WorldPosition));

        // Long road edge: 900 px
        var n3 = _graph.CreateNode(new Vector2(1020, 0));
        _graph.AddCurvedRoadSegment(n2.Id, n3.Id, new CurveSegment(n2.WorldPosition, n3.WorldPosition));

        _graph.BuildPathCache(10);

        int eShort = _graph.FindEdgeId(n1.Id, n2.Id);
        int eLong = _graph.FindEdgeId(n2.Id, n3.Id);

        var shortParcels = _parcelManager.GenerateParcelsForEdge(_graph.Edges[eShort], _graph);
        var longParcels = _parcelManager.GenerateParcelsForEdge(_graph.Edges[eLong], _graph);

        var resParcel = shortParcels[0];
        var nearComParcel = shortParcels.Last();
        var farComParcel = longParcels.Last();

        _parcelManager.ZoneParcel(resParcel.Id, ZoneType.Residential, population: 200);
        _parcelManager.ZoneParcel(nearComParcel.Id, ZoneType.Commercial, jobs: 100);
        _parcelManager.ZoneParcel(farComParcel.Id, ZoneType.Commercial, jobs: 100);

        var od = new DynamicODMatrix(0);
        od.Recalculate(8.0f, null, null, false, parcelManager: _parcelManager, roadGraph: _graph);

        var nearTrip = od.DynamicTrips.FirstOrDefault(t => t.OriginParcelId == resParcel.Id && t.DestParcelId == nearComParcel.Id);
        var farTrip = od.DynamicTrips.FirstOrDefault(t => t.OriginParcelId == resParcel.Id && t.DestParcelId == farComParcel.Id);

        Assert.That(nearTrip, Is.Not.Null);
        Assert.That(farTrip, Is.Not.Null);

        // On short trip (< 120m), walking share should dominate over car
        float nearWalkShare = nearTrip.WalkTrips / nearTrip.TotalTrips;
        float nearCarShare = nearTrip.CarTrips / nearTrip.TotalTrips;
        Assert.That(nearWalkShare, Is.GreaterThan(nearCarShare), "Walking must dominate on short distances (< 400m)");

        // On far trip (~1000m), car share should dominate overwhelmingly
        float farWalkShare = farTrip.WalkTrips / farTrip.TotalTrips;
        float farCarShare = farTrip.CarTrips / farTrip.TotalTrips;
        Assert.That(farCarShare, Is.GreaterThan(farWalkShare), "Car must dominate on long trips");
        Assert.That(farCarShare, Is.GreaterThan(0.85f), "Car share must exceed 85% on long trips");
    }

    [Test]
    public void ParcelOD_ModeSplit_TransitShareWithProximity()
    {
        var n1 = _graph.CreateNode(new Vector2(0, 0));
        var n2 = _graph.CreateNode(new Vector2(600, 0));
        _graph.AddCurvedRoadSegment(n1.Id, n2.Id, new CurveSegment(n1.WorldPosition, n2.WorldPosition));
        _graph.BuildPathCache(10);

        int edgeId = _graph.FindEdgeId(n1.Id, n2.Id);
        var parcels = _parcelManager.GenerateParcelsForEdge(_graph.Edges[edgeId], _graph);

        var resParcel = parcels[0];
        var comParcel = parcels.Last();

        _parcelManager.ZoneParcel(resParcel.Id, ZoneType.Residential, population: 300);
        _parcelManager.ZoneParcel(comParcel.Id, ZoneType.Commercial, jobs: 300);

        // Transit manager with stops right next to n1 and n2
        var transit = new TransitManager();
        var route = new TransitRoute { Id = 1, Name = "Express", TicketPrice = 10f };
        route.PathNodeIds.AddRange(new[] { n1.Id, n2.Id });
        route.StopNodeIds.Add(n1.Id);
        route.StopNodeIds.Add(n2.Id);
        transit.Routes.Add(route);

        transit.Stops[n1.Id] = new TransitStop { NodeId = n1.Id, Name = "Stop 1" };
        transit.Stops[n2.Id] = new TransitStop { NodeId = n2.Id, Name = "Stop 2" };

        var od = new DynamicODMatrix(0);
        od.Recalculate(8.0f, null, null, hasTransit: true, transitManager: transit, parcelManager: _parcelManager, roadGraph: _graph);

        var trip = od.DynamicTrips.FirstOrDefault(t => t.OriginParcelId == resParcel.Id && t.DestParcelId == comParcel.Id);
        Assert.That(trip, Is.Not.Null);
        Assert.That(trip.TransitTrips, Is.GreaterThan(0f), "Transit trips must be generated when stops are proximate");

        float transitShare = trip.TransitTrips / trip.TotalTrips;
        Assert.That(transitShare, Is.GreaterThan(0.10f), "Transit share must be significant when active stops connect origin and destination");
    }

    [Test]
    public void TrafficAssignment_ContinuousAssignmentToCurvedEdgesAndBPR()
    {
        var n1 = _graph.CreateNode(new Vector2(0, 0));
        var n2 = _graph.CreateNode(new Vector2(400, 0));
        var curve = CurveSegment.CreateFromThreePoints(n1.WorldPosition, new Vector2(200, 80), n2.WorldPosition);
        _graph.AddCurvedRoadSegment(n1.Id, n2.Id, curve, capacity: 500f);
        _graph.BuildPathCache(10);

        int eFwd = _graph.FindEdgeId(n1.Id, n2.Id);
        int eRev = _graph.FindEdgeId(n2.Id, n1.Id);

        var parcels = _parcelManager.GenerateParcelsForEdge(_graph.Edges[eFwd], _graph);
        var resParcel = parcels[0];
        var comParcel = parcels.Last();

        _parcelManager.ZoneParcel(resParcel.Id, ZoneType.Residential, population: 500);
        _parcelManager.ZoneParcel(comParcel.Id, ZoneType.Commercial, jobs: 400);

        var od = new DynamicODMatrix(0);
        od.Recalculate(8.0f, null, null, false, parcelManager: _parcelManager, roadGraph: _graph);

        var trafficEngine = new TrafficEngine();
        trafficEngine.AssignFlows(od, _graph);

        float edgeVol = _graph.Edges[eFwd].CurrentVolume;
        float pedVol = _graph.Edges[eFwd].PedestrianVolume;

        Assert.That(edgeVol, Is.GreaterThan(0f), "Car volume must be assigned to the curved road edge");
        Assert.That(pedVol, Is.GreaterThan(0f), "Pedestrian volume must be assigned to the sidewalk edge");

        // BPR congestion function verification
        float freeFlowTravelTime = _graph.Edges[eFwd].Length / _graph.Edges[eFwd].FreeFlowSpeed;
        float congestedTravelTime = _graph.Edges[eFwd].GetTravelTime();
        float congestionRatio = _graph.Edges[eFwd].GetCongestionRatio();

        Assert.That(congestedTravelTime, Is.GreaterThan(freeFlowTravelTime), "Travel time under volume must exceed free flow travel time");
        Assert.That(congestionRatio, Is.EqualTo(edgeVol / 500f).Within(0.001f), "Congestion ratio must match volume / capacity");
        Assert.That(trafficEngine.GetMaxVolume(_graph), Is.EqualTo(edgeVol));
        Assert.That(trafficEngine.GetAverageCongestion(_graph), Is.GreaterThan(0f));
    }

    [Test]
    public void ParcelOD_TripConservationAndTotalsAggregation()
    {
        var n1 = _graph.CreateNode(new Vector2(0, 0));
        var n2 = _graph.CreateNode(new Vector2(300, 0));
        _graph.AddCurvedRoadSegment(n1.Id, n2.Id, new CurveSegment(n1.WorldPosition, n2.WorldPosition));
        _graph.BuildPathCache(10);

        int edgeId = _graph.FindEdgeId(n1.Id, n2.Id);
        var parcels = _parcelManager.GenerateParcelsForEdge(_graph.Edges[edgeId], _graph);

        _parcelManager.ZoneParcel(parcels[0].Id, ZoneType.Residential, population: 200);
        _parcelManager.ZoneParcel(parcels[1].Id, ZoneType.Commercial, jobs: 100);
        _parcelManager.ZoneParcel(parcels[2].Id, ZoneType.Industrial, jobs: 100);

        var od = new DynamicODMatrix(0);
        od.Recalculate(8.0f, null, null, false, parcelManager: _parcelManager, roadGraph: _graph);

        Assert.That(od.DynamicTrips.Count, Is.GreaterThan(0));

        float sumTrips = 0f;
        float sumCar = 0f;
        float sumTransit = 0f;
        float sumWalk = 0f;

        foreach (var trip in od.DynamicTrips)
        {
            // Exact per-trip conservation
            float tripSum = trip.CarTrips + trip.TransitTrips + trip.WalkTrips;
            Assert.That(tripSum, Is.EqualTo(trip.TotalTrips).Within(0.001f));

            sumTrips += trip.TotalTrips;
            sumCar += trip.CarTrips;
            sumTransit += trip.TransitTrips;
            sumWalk += trip.WalkTrips;
        }

        // Exact matrix totals aggregation
        Assert.That(od.TotalTrips, Is.EqualTo(sumTrips).Within(0.01f));
        Assert.That(od.TotalCarTrips, Is.EqualTo(sumCar).Within(0.01f));
        Assert.That(od.TotalTransitTrips, Is.EqualTo(sumTransit).Within(0.01f));
        Assert.That(od.TotalWalkTrips, Is.EqualTo(sumWalk).Within(0.01f));

        float totalModeSum = od.TotalCarTrips + od.TotalTransitTrips + od.TotalWalkTrips;
        Assert.That(totalModeSum, Is.EqualTo(od.TotalTrips).Within(0.01f));
    }

    [Test]
    public void HybridCity_SupportsBothParcelsAndGridZones()
    {
        // 1. Grid with Residential zone
        var grid = new CityGrid(5, 5, 64f);
        grid.ZoneCell(0, 0, ZoneType.Residential, population: 500);
        int zoneId = grid.GetZoneId(0, 0);
        grid.GetZone(zoneId).Population = 500;

        // 2. Road graph containing the zone node and a curved road with commercial parcel
        var zoneNode = _graph.CreateNode(grid.GetWorldCenter(zoneId), zoneId);
        var farNode = _graph.CreateNode(new Vector2(400, 100));
        _graph.AddCurvedRoadSegment(zoneNode.Id, farNode.Id, new CurveSegment(zoneNode.WorldPosition, farNode.WorldPosition));
        _graph.BuildPathCache(10);

        int edgeId = _graph.FindEdgeId(zoneNode.Id, farNode.Id);
        var parcels = _parcelManager.GenerateParcelsForEdge(_graph.Edges[edgeId], _graph);
        var comParcel = parcels.Last();
        _parcelManager.ZoneParcel(comParcel.Id, ZoneType.Commercial, jobs: 200);

        var od = new DynamicODMatrix(grid.ZoneCount);
        od.Recalculate(8.0f, grid, null, false, parcelManager: _parcelManager, roadGraph: _graph);

        Assert.That(od.DynamicTrips.Count, Is.GreaterThan(0));
        var hybridTrip = od.DynamicTrips.FirstOrDefault(t => t.OriginZoneId == zoneId && t.DestParcelId == comParcel.Id);

        Assert.That(hybridTrip, Is.Not.Null, "Trip from grid residential zone to curved roadside commercial parcel must be generated");
        Assert.That(hybridTrip.TotalTrips, Is.GreaterThan(0f));
        Assert.That(od.TotalTrips, Is.GreaterThan(0f));
    }

    [Test]
    public void CommuteAnalytics_ParcelCommuteAndTargets_ComputedAccurately()
    {
        var n1 = _graph.CreateNode(new Vector2(0, 0));
        var n2 = _graph.CreateNode(new Vector2(300, 0));
        _graph.AddCurvedRoadSegment(n1.Id, n2.Id, new CurveSegment(n1.WorldPosition, n2.WorldPosition));
        _graph.BuildPathCache(10);

        int edgeId = _graph.FindEdgeId(n1.Id, n2.Id);
        var parcels = _parcelManager.GenerateParcelsForEdge(_graph.Edges[edgeId], _graph);

        var resParcel = parcels[0];
        var comParcel = parcels[1];
        var indParcel = parcels.Last();

        _parcelManager.ZoneParcel(resParcel.Id, ZoneType.Residential, population: 300);
        _parcelManager.ZoneParcel(comParcel.Id, ZoneType.Commercial, jobs: 150);
        _parcelManager.ZoneParcel(indParcel.Id, ZoneType.Industrial, jobs: 100);

        var od = new DynamicODMatrix(0);
        od.Recalculate(8.0f, null, null, false, parcelManager: _parcelManager, roadGraph: _graph);

        // Residential parcel commute analytics
        var resCommute = CommuteAnalytics.GetResidentialCommuteForParcel(resParcel, od, _parcelManager);
        Assert.That(resCommute.totalTrips, Is.GreaterThan(0f));
        Assert.That(resCommute.destinations.Count, Is.GreaterThan(0));
        Assert.That(resCommute.comTrips + resCommute.indTrips, Is.EqualTo(resCommute.totalTrips).Within(0.01f));
        Assert.That(resCommute.carPct + resCommute.transitPct + resCommute.walkPct, Is.EqualTo(100f).Within(0.1f));

        // Commercial parcel incoming commute analytics
        var comCommute = CommuteAnalytics.GetIncomingCommuteForParcel(comParcel, od, _parcelManager);
        Assert.That(comCommute.totalTrips, Is.GreaterThan(0f));
        Assert.That(comCommute.origins.Count, Is.GreaterThan(0));

        // Commute corridor targets for parcel
        var targets = CommuteAnalytics.ComputeCommuteTargetsForParcel(resParcel.Id, _parcelManager, od, _graph);
        Assert.That(targets, Is.Not.Null);
        Assert.That(targets.Count, Is.GreaterThan(0));
        Assert.That(targets[0].EdgePath, Is.Not.Null);
        Assert.That(targets[0].Volume, Is.GreaterThan(0f));
    }
}
