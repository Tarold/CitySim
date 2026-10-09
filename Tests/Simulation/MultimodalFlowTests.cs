using Godot;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using CitySim.Simulation;

namespace CitySim.Tests.Simulation;

[TestFixture]
public class MultimodalFlowTests
{
    private CityGrid _grid;
    private RoadGraph _roadGraph;

    [SetUp]
    public void Setup()
    {
        _grid = new CityGrid(5, 5, 64f);
        _roadGraph = new RoadGraph();

        // Build a 3-node horizontal road: (1, 2) -> (2, 2) -> (3, 2)
        for (int x = 1; x <= 3; x++)
        {
            _grid.ZoneCell(x, 2, ZoneType.Residential);
            _roadGraph.EnsureNode(_grid.GetZoneId(x, 2), _grid.GetWorldCenter(_grid.GetZoneId(x, 2)));
        }

        _roadGraph.AddRoadSegment(_grid.GetZoneId(1, 2), _grid.GetZoneId(2, 2));
        _roadGraph.AddRoadSegment(_grid.GetZoneId(2, 2), _grid.GetZoneId(3, 2));
        _roadGraph.RebuildAfterTopologyChange(_grid.ZoneCount);
    }

    [Test]
    public void RoadEdge_WalkingTravelTime_IncreasesWithPedestrianVolume()
    {
        var edge = _roadGraph.Edges[0];
        edge.PedestrianVolume = 0f;
        float freeWalkTime = edge.GetWalkingTravelTime();
        Assert.That(freeWalkTime, Is.GreaterThan(0f));
        Assert.That(edge.GetPedestrianCongestionRatio(), Is.EqualTo(0f));

        // Under congestion (e.g. at pedestrian capacity 1800)
        edge.PedestrianVolume = edge.PedestrianCapacity;
        float congestedWalkTime = edge.GetWalkingTravelTime();
        Assert.That(congestedWalkTime, Is.GreaterThan(freeWalkTime));
        Assert.That(edge.GetPedestrianCongestionRatio(), Is.EqualTo(1.0f).Within(0.001f));

        // Over capacity (overcrowded sidewalks)
        edge.PedestrianVolume = edge.PedestrianCapacity * 2f;
        float overcrowdedWalkTime = edge.GetWalkingTravelTime();
        Assert.That(overcrowdedWalkTime, Is.GreaterThan(congestedWalkTime));
        Assert.That(edge.GetPedestrianCongestionRatio(), Is.EqualTo(2.0f).Within(0.001f));
    }

    [Test]
    public void RoadGraph_WalkingDijkstraAndPath_MatchesExpectedTopology()
    {
        int z1 = _grid.GetZoneId(1, 2);
        int z2 = _grid.GetZoneId(2, 2);
        int z3 = _grid.GetZoneId(3, 2);

        var nodePath = _roadGraph.GetShortestWalkingNodePath(z1, z3);
        Assert.That(nodePath, Is.Not.Null);
        Assert.That(nodePath.Count, Is.EqualTo(3));
        Assert.That(nodePath[0], Is.EqualTo(z1));
        Assert.That(nodePath[1], Is.EqualTo(z2));
        Assert.That(nodePath[2], Is.EqualTo(z3));

        // Same node
        var sameNodePath = _roadGraph.GetShortestWalkingNodePath(z1, z1);
        Assert.That(sameNodePath, Is.Not.Null);
        Assert.That(sameNodePath.Count, Is.EqualTo(1));
        Assert.That(sameNodePath[0], Is.EqualTo(z1));

        // Disconnected node
        _grid.ZoneCell(0, 0, ZoneType.Commercial);
        int disconnectedId = _grid.GetZoneId(0, 0);
        _roadGraph.EnsureNode(disconnectedId, _grid.GetWorldCenter(disconnectedId));

        var disconnectedPath = _roadGraph.GetShortestWalkingNodePath(z1, disconnectedId);
        Assert.That(disconnectedPath, Is.Null);
    }

    [Test]
    public void RoadGraph_WalkingPathCache_PopulatedAndInvalidatedOnDemolition()
    {
        int z1 = _grid.GetZoneId(1, 2);
        int z3 = _grid.GetZoneId(3, 2);

        _roadGraph.BuildPathCache(_grid.ZoneCount);
        Assert.That(_roadGraph.WalkingPathCache.ContainsKey((z1, z3)), Is.True);
        Assert.That(_roadGraph.WalkingPathCache.ContainsKey((z3, z1)), Is.True);

        // Demolish the segment between (2, 2) and (3, 2)
        int z2 = _grid.GetZoneId(2, 2);
        bool removed = _roadGraph.RemoveRoadSegment(z2, z3);
        Assert.That(removed, Is.True);

        // Path between z1 and z3 must be invalidated from cache
        Assert.That(_roadGraph.WalkingPathCache.ContainsKey((z1, z3)), Is.False);
        Assert.That(_roadGraph.WalkingPathCache.ContainsKey((z3, z1)), Is.False);
    }

    [Test]
    public void RoadGraph_WalkingPathCache_InvalidatedOnNodeDetach()
    {
        int z1 = _grid.GetZoneId(1, 2);
        int z2 = _grid.GetZoneId(2, 2);
        int z3 = _grid.GetZoneId(3, 2);

        _roadGraph.BuildPathCache(_grid.ZoneCount);
        Assert.That(_roadGraph.WalkingPathCache.ContainsKey((z1, z3)), Is.True);

        // Detach central node z2
        bool detached = _roadGraph.DetachAndRemoveNode(z2);
        Assert.That(detached, Is.True);

        // Walking path cache entries referencing z2 or crossing z2 must be invalidated
        Assert.That(_roadGraph.WalkingPathCache.ContainsKey((z1, z2)), Is.False);
        Assert.That(_roadGraph.WalkingPathCache.ContainsKey((z2, z3)), Is.False);
        Assert.That(_roadGraph.WalkingPathCache.ContainsKey((z1, z3)), Is.False);
    }

    [Test]
    public void TrafficEngine_AssignFlows_AssignsTransitAccessAndEgressLegsToPedestrianVolume()
    {
        int zOrigin = _grid.GetZoneId(1, 2);
        int zTransitStop = _grid.GetZoneId(2, 2);
        int zDest = _grid.GetZoneId(3, 2);

        _grid.GetZone(zOrigin).Population = 1000;
        _grid.GetZone(zDest).Type = ZoneType.Commercial;
        _grid.GetZone(zDest).Jobs = 1000;

        var transitMgr = new TransitManager();
        var stopNodes = new HashSet<int> { zTransitStop };
        transitMgr.CreateRoute("Transit Line 1", new List<int> { zTransitStop, zDest }, stopNodes, Colors.SkyBlue, false, fleetSize: 1);

        var od = new ODMatrix(_grid.ZoneCount);
        od.TransitTrips[zOrigin, zDest] = 45f;
        od.Trips[zOrigin, zDest] = 45f;

        var engine = new TrafficEngine();
        engine.AssignFlows(od, _roadGraph, _grid, transitMgr);

        // Access walk leg is from origin (1, 2) to transit stop (2, 2)
        int accessEdgeId = _roadGraph.FindEdgeId(zOrigin, zTransitStop);
        Assert.That(accessEdgeId, Is.Not.EqualTo(-1));
        Assert.That(_roadGraph.Edges[accessEdgeId].PedestrianVolume, Is.GreaterThanOrEqualTo(45f));

        // Egress walk leg is from transit stop (2, 2) to destination (3, 2)
        int egressEdgeId = _roadGraph.FindEdgeId(zTransitStop, zDest);
        Assert.That(egressEdgeId, Is.Not.EqualTo(-1));
        Assert.That(_roadGraph.Edges[egressEdgeId].PedestrianVolume, Is.GreaterThanOrEqualTo(45f));

        // Private car volume on these edges should be 0 because all trips were transit
        Assert.That(_roadGraph.Edges[accessEdgeId].CurrentVolume, Is.EqualTo(0f));
        Assert.That(_roadGraph.Edges[egressEdgeId].CurrentVolume, Is.EqualTo(0f));
    }

    [Test]
    public void TrafficEngine_PedestrianCongestion_CalculatesMetricsAccurately()
    {
        var engine = new TrafficEngine();
        _roadGraph.ClearVolumes();

        Assert.That(engine.GetMaxPedestrianVolume(_roadGraph), Is.EqualTo(0f));
        Assert.That(engine.GetAveragePedestrianCongestion(_roadGraph), Is.EqualTo(0f));

        var edge0 = _roadGraph.Edges[0];
        var edge1 = _roadGraph.Edges[1];

        edge0.PedestrianVolume = 900f; // 900 / 1800 = 0.5 congestion ratio
        edge1.PedestrianVolume = 1800f; // 1800 / 1800 = 1.0 congestion ratio

        Assert.That(engine.GetMaxPedestrianVolume(_roadGraph), Is.EqualTo(1800f));
        
        // Average congestion across active edges = (0.5 + 1.0) / 2 = 0.75
        float avg = engine.GetAveragePedestrianCongestion(_roadGraph);
        Assert.That(avg, Is.EqualTo(0.75f).Within(0.01f));
    }

    [Test]
    public void CommuteAnalytics_ModeSplit_ExtremeAndEdgeCasesSumTo100Percent()
    {
        // All zero
        var (c0, t0, w0) = CommuteAnalytics.CalculateModeSplit(0f, 0f, 0f);
        Assert.That(c0 + t0 + w0, Is.EqualTo(100f).Within(0.001f));

        // 100% walk
        var (cW, tW, wW) = CommuteAnalytics.CalculateModeSplit(0f, 0f, 500f);
        Assert.That(cW, Is.EqualTo(0f).Within(0.001f));
        Assert.That(tW, Is.EqualTo(0f).Within(0.001f));
        Assert.That(wW, Is.EqualTo(100f).Within(0.001f));
        Assert.That(cW + tW + wW, Is.EqualTo(100f).Within(0.001f));

        // 100% transit
        var (cT, tT, wT) = CommuteAnalytics.CalculateModeSplit(0f, 500f, 0f);
        Assert.That(cT, Is.EqualTo(0f).Within(0.001f));
        Assert.That(tT, Is.EqualTo(100f).Within(0.001f));
        Assert.That(wT, Is.EqualTo(0f).Within(0.001f));
        Assert.That(cT + tT + wT, Is.EqualTo(100f).Within(0.001f));

        // 100% car
        var (cC, tC, wC) = CommuteAnalytics.CalculateModeSplit(500f, 0f, 0f);
        Assert.That(cC, Is.EqualTo(100f).Within(0.001f));
        Assert.That(tC, Is.EqualTo(0f).Within(0.001f));
        Assert.That(wC, Is.EqualTo(0f).Within(0.001f));
        Assert.That(cC + tC + wC, Is.EqualTo(100f).Within(0.001f));
    }
}
