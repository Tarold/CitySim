using Godot;
using NUnit.Framework;
using System.Collections.Generic;
using CitySim.Simulation;
using CitySim.Tests.Simulation;
using CitySim.Rendering;
using CitySim.UI;

namespace CitySim.Tests;

[TestFixture]
public class ZoningAndExpansionTests
{
    private CityGrid _grid;
    private RoadGraph _roadGraph;

    [SetUp]
    public void Setup()
    {
        _grid = new CityGrid(10, 10, 64f);
        _roadGraph = new RoadGraph();
    }

    [Test]
    public void ZoneCell_EmptyToResidential_UpdatesStateAndDemographics()
    {
        int x = 2;
        int y = 3;
        int id = _grid.GetZoneId(x, y);

        Assert.That(_grid.Zones[id].Type, Is.EqualTo(ZoneType.Empty));
        Assert.That(_grid.ActiveZoneIds.Contains(id), Is.False);

        bool success = _grid.ZoneCell(x, y, ZoneType.Residential);

        Assert.That(success, Is.True);
        Assert.That(_grid.Zones[id].Type, Is.EqualTo(ZoneType.Residential));
        Assert.That(_grid.Zones[id].ResidentialCap, Is.EqualTo(CityGrid.DefaultResidentialPopulation));
        Assert.That(_grid.Zones[id].Population, Is.EqualTo(0));
        Assert.That(_grid.Zones[id].Jobs, Is.EqualTo(0));
        Assert.That(_grid.Zones[id].CommercialCap, Is.EqualTo(0));
        Assert.That(_grid.ActiveZoneIds.Contains(id), Is.True);
        Assert.That(_grid.TotalPopulation(), Is.EqualTo(0));
    }

    [Test]
    public void ZoneCell_EmptyToCommercial_UpdatesJobsAndCapacities()
    {
        int x = 4;
        int y = 5;
        int id = _grid.GetZoneId(x, y);

        bool success = _grid.ZoneCell(x, y, ZoneType.Commercial);

        Assert.That(success, Is.True);
        Assert.That(_grid.Zones[id].Type, Is.EqualTo(ZoneType.Commercial));
        Assert.That(_grid.Zones[id].Population, Is.EqualTo(0));
        Assert.That(_grid.Zones[id].Jobs, Is.EqualTo(CityGrid.DefaultCommercialJobs));
        Assert.That(_grid.Zones[id].CommercialCap, Is.EqualTo(CityGrid.DefaultCommercialCapacity));
        Assert.That(_grid.ActiveZoneIds.Contains(id), Is.True);
        Assert.That(_grid.TotalJobs(), Is.EqualTo(CityGrid.DefaultCommercialJobs));
    }

    [Test]
    public void ZoneCell_EmptyToIndustrial_UpdatesJobs()
    {
        int x = 6;
        int y = 7;
        int id = _grid.GetZoneId(x, y);

        bool success = _grid.ZoneCell(x, y, ZoneType.Industrial);

        Assert.That(success, Is.True);
        Assert.That(_grid.Zones[id].Type, Is.EqualTo(ZoneType.Industrial));
        Assert.That(_grid.Zones[id].Population, Is.EqualTo(0));
        Assert.That(_grid.Zones[id].Jobs, Is.EqualTo(CityGrid.DefaultIndustrialJobs));
        Assert.That(_grid.Zones[id].CommercialCap, Is.EqualTo(0));
        Assert.That(_grid.ActiveZoneIds.Contains(id), Is.True);
        Assert.That(_grid.TotalJobs(), Is.EqualTo(CityGrid.DefaultIndustrialJobs));
    }

    [Test]
    public void DezoneCell_ActiveZone_ResetsStateAndMetrics()
    {
        int x = 3;
        int y = 3;
        int id = _grid.GetZoneId(x, y);

        _grid.ZoneCell(x, y, ZoneType.Residential); int _zId = _grid.GetZoneId(x, y); _grid.Zones[_zId].Population = _grid.Zones[_zId].ResidentialCap;
        _grid.Zones[id].Population = _grid.Zones[id].ResidentialCap; // Simulate
        Assert.That(_grid.ActiveZoneIds.Contains(id), Is.True);
        Assert.That(_grid.TotalPopulation(), Is.GreaterThan(0));

        bool dezoneSuccess = _grid.DezoneCell(x, y);

        Assert.That(dezoneSuccess, Is.True);
        Assert.That(_grid.Zones[id].Type, Is.EqualTo(ZoneType.Empty));
        Assert.That(_grid.Zones[id].Population, Is.EqualTo(0));
        Assert.That(_grid.Zones[id].Jobs, Is.EqualTo(0));
        Assert.That(_grid.Zones[id].CommercialCap, Is.EqualTo(0));
        Assert.That(_grid.ActiveZoneIds.Contains(id), Is.False);
        Assert.That(_grid.TotalPopulation(), Is.EqualTo(0));
    }

    [Test]
    public void Rezone_ResidentialToCommercial_UpdatesMetricsAccurately()
    {
        int x = 2;
        int y = 2;
        int id = _grid.GetZoneId(x, y);

        _grid.ZoneCell(x, y, ZoneType.Residential); int _zId = _grid.GetZoneId(x, y); _grid.Zones[_zId].Population = _grid.Zones[_zId].ResidentialCap;
        Assert.That(_grid.TotalPopulation(), Is.EqualTo(CityGrid.DefaultResidentialPopulation));
        Assert.That(_grid.TotalJobs(), Is.EqualTo(0));

        // Rezone to commercial
        bool success = _grid.ZoneCell(x, y, ZoneType.Commercial);

        Assert.That(success, Is.True);
        Assert.That(_grid.Zones[id].Type, Is.EqualTo(ZoneType.Commercial));
        Assert.That(_grid.Zones[id].Population, Is.EqualTo(0));
        Assert.That(_grid.Zones[id].Jobs, Is.EqualTo(CityGrid.DefaultCommercialJobs));
        Assert.That(_grid.Zones[id].CommercialCap, Is.EqualTo(CityGrid.DefaultCommercialCapacity));
        Assert.That(_grid.TotalPopulation(), Is.EqualTo(0));
        Assert.That(_grid.TotalJobs(), Is.EqualTo(CityGrid.DefaultCommercialJobs));

        // ActiveZoneIds must contain the ID exactly once
        int occurrences = 0;
        foreach (int zid in _grid.ActiveZoneIds)
        {
            if (zid == id) occurrences++;
        }
        Assert.That(occurrences, Is.EqualTo(1));
    }

    [Test]
    public void RoadGraph_EnsureNode_CreatesNodeForNewZone()
    {
        int zoneId = _grid.GetZoneId(1, 1);
        Vector2 worldPos = _grid.GetWorldCenter(zoneId);

        Assert.That(_roadGraph.GetNode(zoneId), Is.Null);

        var node = _roadGraph.EnsureNode(zoneId, worldPos);

        Assert.That(node, Is.Not.Null);
        Assert.That(node.Id, Is.EqualTo(zoneId));
        Assert.That(node.WorldPosition, Is.EqualTo(worldPos));
        Assert.That(_roadGraph.NodeMap.ContainsKey(zoneId), Is.True);
        Assert.That(_roadGraph.AdjacencyEdges.ContainsKey(zoneId), Is.True);

        // Calling EnsureNode again on existing node returns same node
        var existingNode = _roadGraph.EnsureNode(zoneId, worldPos);
        Assert.That(existingNode, Is.SameAs(node));
    }

    [Test]
    public void RoadGraph_DetachAndRemoveNode_RemovesAllConnectedRoadsAndNode()
    {
        int z1 = _grid.GetZoneId(1, 1);
        int z2 = _grid.GetZoneId(1, 2);
        int z3 = _grid.GetZoneId(2, 1);

        _roadGraph.EnsureNode(z1, _grid.GetWorldCenter(z1));
        _roadGraph.EnsureNode(z2, _grid.GetWorldCenter(z2));
        _roadGraph.EnsureNode(z3, _grid.GetWorldCenter(z3));

        _roadGraph.AddRoadSegment(z1, z2);
        _roadGraph.AddRoadSegment(z1, z3);

        Assert.That(_roadGraph.HasEdge(z1, z2), Is.True);
        Assert.That(_roadGraph.HasEdge(z2, z1), Is.True);
        Assert.That(_roadGraph.HasEdge(z1, z3), Is.True);
        Assert.That(_roadGraph.HasEdge(z3, z1), Is.True);

        // Detach z1
        bool removed = _roadGraph.DetachAndRemoveNode(z1);

        Assert.That(removed, Is.True);
        Assert.That(_roadGraph.GetNode(z1), Is.Null);
        Assert.That(_roadGraph.NodeMap.ContainsKey(z1), Is.False);
        Assert.That(_roadGraph.AdjacencyEdges.ContainsKey(z1), Is.False);

        // All edges to and from z1 must be dismantled
        Assert.That(_roadGraph.HasEdge(z1, z2), Is.False);
        Assert.That(_roadGraph.HasEdge(z2, z1), Is.False);
        Assert.That(_roadGraph.HasEdge(z1, z3), Is.False);
        Assert.That(_roadGraph.HasEdge(z3, z1), Is.False);

        // Neighbors z2 and z3 must remain valid in graph
        Assert.That(_roadGraph.GetNode(z2), Is.Not.Null);
        Assert.That(_roadGraph.GetNode(z3), Is.Not.Null);
        Assert.That(_roadGraph.AdjacencyEdges[z2].Count, Is.EqualTo(0));
        Assert.That(_roadGraph.AdjacencyEdges[z3].Count, Is.EqualTo(0));
    }

    [Test]
    public void Zoning_BoundsChecking_SafeHandling()
    {
        Assert.That(_grid.ZoneCell(-1, 0, ZoneType.Residential), Is.False);
        Assert.That(_grid.ZoneCell(100, 100, ZoneType.Commercial), Is.False);
        Assert.That(_grid.ZoneCell(9999, ZoneType.Industrial), Is.False);

        Assert.That(_grid.DezoneCell(-1, -1), Is.False);
        Assert.That(_grid.DezoneCell(50, 50), Is.False);
        Assert.That(_grid.DezoneCell(9999), Is.False);

        Assert.That(_roadGraph.DetachAndRemoveNode(9999), Is.False);
    }

    [Test]
    public void InteractionMode_ZoningModes_EnumAndToolPreview()
    {
        Assert.That(ToolPreviewRenderer.IsZoningMode(InteractionMode.ZoneResidential), Is.True);
        Assert.That(ToolPreviewRenderer.IsZoningMode(InteractionMode.ZoneCommercial), Is.True);
        Assert.That(ToolPreviewRenderer.IsZoningMode(InteractionMode.ZoneIndustrial), Is.True);
        Assert.That(ToolPreviewRenderer.IsZoningMode(InteractionMode.Dezone), Is.True);

        Assert.That(ToolPreviewRenderer.IsZoningMode(InteractionMode.Inspect), Is.False);
        Assert.That(ToolPreviewRenderer.IsZoningMode(InteractionMode.BuildRoad), Is.False);
        Assert.That(ToolPreviewRenderer.IsZoningMode(InteractionMode.Demolish), Is.False);
    }

    [Test]
    public void CarTrafficManager_HandlesDetachedDezonedEdgesSafely()
    {
        int z1 = _grid.GetZoneId(1, 1);
        int z2 = _grid.GetZoneId(1, 2);

        _roadGraph.EnsureNode(z1, _grid.GetWorldCenter(z1));
        _roadGraph.EnsureNode(z2, _grid.GetWorldCenter(z2));
        _roadGraph.AddRoadSegment(z1, z2);

        var carMgr = new CarTrafficManager();
        carMgr.Initialize(_roadGraph);

        int edgeId = _roadGraph.FindEdgeId(z1, z2);
        Assert.That(edgeId, Is.Not.EqualTo(-1));
        _roadGraph.Edges[edgeId].CurrentVolume = 10f;
        if (carMgr.Cars.Count == 0)
        {
            carMgr.Cars.Add(new VisualCar { EdgeId = edgeId });
        }
        else
        {
            carMgr.Cars[0].EdgeId = edgeId;
        }

        // Detach node
        _roadGraph.DetachAndRemoveNode(z1);

        // Handle invalidated edges should safely clean up or respawn car
        Assert.DoesNotThrow(() => carMgr.HandleInvalidatedEdges(_roadGraph));

        var lights = new TrafficLightManager();
        lights.BuildIntersections(_roadGraph);
        Assert.DoesNotThrow(() => carMgr.Update(0.1f, 1.0f, _roadGraph, lights));
    }

    [Test]
    public void RoadGraph_DetachIsolatedNode_ReturnsTrueAndRemovesNode()
    {
        int zId = _grid.GetZoneId(0, 0);
        _roadGraph.EnsureNode(zId, _grid.GetWorldCenter(zId));

        Assert.That(_roadGraph.GetNode(zId), Is.Not.Null);

        bool removed = _roadGraph.DetachAndRemoveNode(zId);

        Assert.That(removed, Is.True);
        Assert.That(_roadGraph.GetNode(zId), Is.Null);
        Assert.That(_roadGraph.NodeMap.ContainsKey(zId), Is.False);
        Assert.That(_roadGraph.AdjacencyEdges.ContainsKey(zId), Is.False);
    }

    [Test]
    public void RoadGraph_GetNodeDegree_AccuratelyReportsDegreeAndExcludesDisconnectedEdges()
    {
        int z1 = _grid.GetZoneId(1, 1);
        int z2 = _grid.GetZoneId(1, 2);

        _roadGraph.EnsureNode(z1, _grid.GetWorldCenter(z1));
        _roadGraph.EnsureNode(z2, _grid.GetWorldCenter(z2));

        // Isolated nodes have degree 0
        Assert.That(_roadGraph.GetNodeDegree(z1), Is.EqualTo(0));
        Assert.That(_roadGraph.GetNodeDegree(z2), Is.EqualTo(0));

        // Add connecting road
        _roadGraph.AddRoadSegment(z1, z2);
        Assert.That(_roadGraph.GetNodeDegree(z1), Is.EqualTo(1));
        Assert.That(_roadGraph.GetNodeDegree(z2), Is.EqualTo(1));

        // Remove road segment: degree should revert to 0
        _roadGraph.RemoveRoadSegment(z1, z2);
        Assert.That(_roadGraph.GetNodeDegree(z1), Is.EqualTo(0));
        Assert.That(_roadGraph.GetNodeDegree(z2), Is.EqualTo(0));
    }

    [Test]
    public void RoadGraph_MultiConnectionAndSplit_DegreesAreAccurate()
    {
        int z1 = _grid.GetZoneId(2, 2);
        int z2 = _grid.GetZoneId(2, 3);
        int z3 = _grid.GetZoneId(3, 2);

        _roadGraph.EnsureNode(z1, _grid.GetWorldCenter(z1));
        _roadGraph.EnsureNode(z2, _grid.GetWorldCenter(z2));
        _roadGraph.EnsureNode(z3, _grid.GetWorldCenter(z3));

        _roadGraph.AddRoadSegment(z1, z2);
        _roadGraph.AddRoadSegment(z1, z3);

        Assert.That(_roadGraph.GetNodeDegree(z1), Is.EqualTo(2));
        Assert.That(_roadGraph.GetNodeDegree(z2), Is.EqualTo(1));
        Assert.That(_roadGraph.GetNodeDegree(z3), Is.EqualTo(1));

        // Detach and remove z1
        bool removed = _roadGraph.DetachAndRemoveNode(z1);
        Assert.That(removed, Is.True);
        Assert.That(_roadGraph.GetNode(z1), Is.Null);
        Assert.That(_roadGraph.GetNodeDegree(z1), Is.EqualTo(0));
        // z2 and z3 now have degree 0
        Assert.That(_roadGraph.GetNodeDegree(z2), Is.EqualTo(0));
        Assert.That(_roadGraph.GetNodeDegree(z3), Is.EqualTo(0));
    }

    /// <summary>
    /// Residential cell (1,y) and commercial cell (6,y) in different 320 px TAZ buckets, joined by a road
    /// corridor through the cells in between.
    /// </summary>
    private (int ResidentialId, int CommercialId) BuildCommuteCorridor(int y)
    {
        _grid.ZoneCell(1, y, ZoneType.Residential);
        int rId = _grid.GetZoneId(1, y);
        _grid.Zones[rId].Population = _grid.Zones[rId].ResidentialCap;
        _grid.ZoneCell(6, y, ZoneType.Commercial);
        int cId = _grid.GetZoneId(6, y);

        for (int x = 1; x <= 6; x++)
        {
            int id = _grid.GetZoneId(x, y);
            _roadGraph.EnsureNode(id, _grid.GetWorldCenter(id));
            if (x > 1) _roadGraph.AddRoadSegment(_grid.GetZoneId(x - 1, y), id);
        }
        return (rId, cId);
    }

    private static float CarTripsBetween(TazManager taz, OptimizedODMatrix od, Vector2I from, Vector2I to)
    {
        int i = taz.GetTazIdForZoneTile(from);
        int j = taz.GetTazIdForZoneTile(to);
        return i < 0 || j < 0 ? 0f : od.GetCarTrips(i, j);
    }

    [Test]
    public void MacroModel_NewlyZonedResidentialAndCommercial_GeneratesCommuteTrips()
    {
        var (rId, cId) = BuildCommuteCorridor(1);

        var (taz, od, _) = MacroTestPipeline.Run(_grid, _roadGraph, assignFlows: false);

        Assert.That(od.TotalTrips, Is.GreaterThan(0f));
        Assert.That(CarTripsBetween(taz, od, _grid.GetZone(rId).GridPos, _grid.GetZone(cId).GridPos), Is.GreaterThan(0f),
            "Morning commuters drive from the residential TAZ to the commercial TAZ");
    }

    [Test]
    public void MacroModel_AfterDezoningResidential_RemovesItsTrips()
    {
        var (rId, cId) = BuildCommuteCorridor(1);
        var (_, before, _) = MacroTestPipeline.Run(_grid, _roadGraph, assignFlows: false);

        _grid.DezoneCell(rId);
        var (taz, after, _) = MacroTestPipeline.Run(_grid, _roadGraph, assignFlows: false);

        Assert.That(taz.GetTazIdForZoneTile(_grid.GetZone(rId).GridPos), Is.EqualTo(-1), "Empty cell no longer forms a TAZ");
        Assert.That(after.TotalTrips, Is.LessThan(before.TotalTrips));
        Assert.That(after.TotalCarTrips, Is.LessThan(before.TotalCarTrips));
    }

    [Test]
    public void MacroModel_AfterExpansion_AssignsVolumeToCorridor()
    {
        var (rId, _) = BuildCommuteCorridor(2);

        MacroTestPipeline.Run(_grid, _roadGraph);

        int firstLeg = _roadGraph.FindEdgeId(rId, _grid.GetZoneId(2, 2));
        Assert.That(firstLeg, Is.Not.EqualTo(-1));
        Assert.That(_roadGraph.Edges[firstLeg].CurrentVolume, Is.GreaterThan(0f));
    }

    [Test]
    public void MacroModel_RezoneCommercialToIndustrial_KeepsCommuteDemand()
    {
        var (rId, cId) = BuildCommuteCorridor(3);
        MacroTestPipeline.Run(_grid, _roadGraph, assignFlows: false);

        _grid.ZoneCell(cId, ZoneType.Industrial);
        var (taz, od, _) = MacroTestPipeline.Run(_grid, _roadGraph, assignFlows: false);

        Assert.That(_grid.GetZone(cId).Type, Is.EqualTo(ZoneType.Industrial));
        Assert.That(CarTripsBetween(taz, od, _grid.GetZone(rId).GridPos, _grid.GetZone(cId).GridPos), Is.GreaterThan(0f));
    }
}
