using Godot;
using NUnit.Framework;
using System.Collections.Generic;
using CitySim.Simulation;
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
        Assert.That(_grid.Zones[id].Population, Is.EqualTo(CityGrid.DefaultResidentialPopulation));
        Assert.That(_grid.Zones[id].Jobs, Is.EqualTo(0));
        Assert.That(_grid.Zones[id].CommercialCap, Is.EqualTo(0));
        Assert.That(_grid.ActiveZoneIds.Contains(id), Is.True);
        Assert.That(_grid.TotalPopulation(), Is.EqualTo(CityGrid.DefaultResidentialPopulation));
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

        _grid.ZoneCell(x, y, ZoneType.Residential);
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

        _grid.ZoneCell(x, y, ZoneType.Residential);
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
    public void ODMatrix_Recalculate_WithNewlyZonedResidentialAndCommercial_GeneratesCommuteTrips()
    {
        int rId = _grid.GetZoneId(1, 1);
        int cId = _grid.GetZoneId(1, 2);

        _grid.ZoneCell(1, 1, ZoneType.Residential);
        _grid.ZoneCell(1, 2, ZoneType.Commercial);

        _roadGraph.EnsureNode(rId, _grid.GetWorldCenter(rId));
        _roadGraph.EnsureNode(cId, _grid.GetWorldCenter(cId));
        _roadGraph.AddRoadSegment(rId, cId);

        var distances = _roadGraph.RebuildAfterTopologyChange(_grid.ZoneCount);
        Assert.That(distances[rId, cId], Is.LessThan(float.MaxValue));

        var odMatrix = new ODMatrix(_grid.ZoneCount);
        odMatrix.Recalculate(8.0f, _grid, distances, hasTransit: false); // Morning rush

        // Tripmaking should occur from Residential to Commercial
        Assert.That(odMatrix.Trips[rId, cId], Is.GreaterThan(0f));
        Assert.That(odMatrix.TotalTrips, Is.GreaterThan(0f));
        Assert.That(odMatrix.CarTrips[rId, cId], Is.GreaterThan(0f));
    }

    [Test]
    public void ODMatrix_Recalculate_AfterDezoning_CleansUpTrips()
    {
        int rId = _grid.GetZoneId(1, 1);
        int cId = _grid.GetZoneId(1, 2);

        _grid.ZoneCell(1, 1, ZoneType.Residential);
        _grid.ZoneCell(1, 2, ZoneType.Commercial);

        _roadGraph.EnsureNode(rId, _grid.GetWorldCenter(rId));
        _roadGraph.EnsureNode(cId, _grid.GetWorldCenter(cId));
        _roadGraph.AddRoadSegment(rId, cId);

        var distances = _roadGraph.RebuildAfterTopologyChange(_grid.ZoneCount);
        var odMatrix = new ODMatrix(_grid.ZoneCount);
        odMatrix.Recalculate(8.0f, _grid, distances, hasTransit: false);

        Assert.That(odMatrix.TotalTrips, Is.GreaterThan(0f));

        // Now dezone residential
        _grid.DezoneCell(1, 1);
        _roadGraph.DetachAndRemoveNode(rId);
        distances = _roadGraph.RebuildAfterTopologyChange(_grid.ZoneCount);

        odMatrix.Recalculate(8.0f, _grid, distances, hasTransit: false);

        Assert.That(odMatrix.Trips[rId, cId], Is.EqualTo(0f));
        Assert.That(odMatrix.TotalTrips, Is.EqualTo(0f));
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
    public void TrafficEngine_AssignFlows_AfterExpansion_PopulatesVolumes()
    {
        int rId = _grid.GetZoneId(2, 2);
        int cId = _grid.GetZoneId(2, 3);

        _grid.ZoneCell(2, 2, ZoneType.Residential);
        _grid.ZoneCell(2, 3, ZoneType.Commercial);

        _roadGraph.EnsureNode(rId, _grid.GetWorldCenter(rId));
        _roadGraph.EnsureNode(cId, _grid.GetWorldCenter(cId));
        _roadGraph.AddRoadSegment(rId, cId);

        var distances = _roadGraph.RebuildAfterTopologyChange(_grid.ZoneCount);
        var odMatrix = new ODMatrix(_grid.ZoneCount);
        odMatrix.Recalculate(8.0f, _grid, distances, hasTransit: false);

        var trafficEngine = new TrafficEngine();
        trafficEngine.AssignFlows(odMatrix, _roadGraph, _grid);

        int edgeId = _roadGraph.FindEdgeId(rId, cId);
        Assert.That(edgeId, Is.Not.EqualTo(-1));
        Assert.That(_roadGraph.Edges[edgeId].CurrentVolume, Is.GreaterThan(0f));

        float maxVol = trafficEngine.GetMaxVolume(_roadGraph);
        Assert.That(maxVol, Is.GreaterThan(0f));

        float avgCong = trafficEngine.GetAverageCongestion(_roadGraph);
        Assert.That(avgCong, Is.GreaterThan(0f));
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
        carMgr.Cars[0].EdgeId = edgeId;

        // Detach node
        _roadGraph.DetachAndRemoveNode(z1);

        // Handle invalidated edges should safely clean up or respawn car
        Assert.DoesNotThrow(() => carMgr.HandleInvalidatedEdges(_roadGraph));

        var lights = new TrafficLightManager();
        lights.BuildIntersections(_roadGraph);
        Assert.DoesNotThrow(() => carMgr.Update(0.1f, 1.0f, _roadGraph, lights));
    }
}
