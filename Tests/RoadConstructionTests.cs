using Godot;
using NUnit.Framework;
using System.Collections.Generic;
using CitySim.Simulation;

namespace CitySim.Tests;

[TestFixture]
public class RoadConstructionTests
{
    private CityGrid _grid;
    private RoadGraph _roadGraph;

    [SetUp]
    public void Setup()
    {
        _grid = new CityGrid(5, 5, 64f);
        // Create 4 active non-empty zones in a 2x2 square: (1,1), (1,2), (2,1), (2,2)
        for (int x = 1; x <= 2; x++)
        {
            for (int y = 1; y <= 2; y++)
            {
                int id = _grid.GetZoneId(x, y);
                _grid.Zones[id].Type = ZoneType.Residential;
                _grid.Zones[id].Population = 1000;
                _grid.ActiveZoneIds.Add(id);
            }
        }

        _roadGraph = new RoadGraph();
        _roadGraph.BuildFromGrid(_grid);
    }

    [Test]
    public void AddRoadSegment_ValidNodes_CreatesBidirectionalEdges()
    {
        // Pick two nodes that don't have an edge between them initially (e.g. by clearing edges first)
        var graph = new RoadGraph();
        int z1 = _grid.GetZoneId(1, 1);
        int z2 = _grid.GetZoneId(1, 2);

        graph.Nodes.Add(new RoadNode { Id = z1, ZoneId = z1, WorldPosition = _grid.GetWorldCenter(z1) });
        graph.NodeMap[z1] = graph.Nodes[0];
        graph.Nodes.Add(new RoadNode { Id = z2, ZoneId = z2, WorldPosition = _grid.GetWorldCenter(z2) });
        graph.NodeMap[z2] = graph.Nodes[1];

        Assert.That(graph.HasEdge(z1, z2), Is.False);
        Assert.That(graph.HasEdge(z2, z1), Is.False);

        bool success = graph.AddRoadSegment(z1, z2, 1500f);

        Assert.That(success, Is.True);
        Assert.That(graph.HasEdge(z1, z2), Is.True);
        Assert.That(graph.HasEdge(z2, z1), Is.True);

        int forwardEid = graph.FindEdgeId(z1, z2);
        int reverseEid = graph.FindEdgeId(z2, z1);

        Assert.That(forwardEid, Is.Not.EqualTo(-1));
        Assert.That(reverseEid, Is.Not.EqualTo(-1));
        Assert.That(forwardEid, Is.Not.EqualTo(reverseEid));

        var fwd = graph.Edges[forwardEid];
        var rev = graph.Edges[reverseEid];

        Assert.That(fwd.FromId, Is.EqualTo(z1));
        Assert.That(fwd.ToId, Is.EqualTo(z2));
        Assert.That(fwd.Capacity, Is.EqualTo(1500f));
        Assert.That(fwd.Length, Is.EqualTo(64f).Within(0.01f));

        Assert.That(rev.FromId, Is.EqualTo(z2));
        Assert.That(rev.ToId, Is.EqualTo(z1));
        Assert.That(rev.Capacity, Is.EqualTo(1500f));
    }

    [Test]
    public void AddRoadSegment_Duplicate_ReturnsFalse()
    {
        int z1 = _grid.GetZoneId(1, 1);
        int z2 = _grid.GetZoneId(1, 2);

        Assert.That(_roadGraph.HasEdge(z1, z2), Is.True);

        bool addedAgain = _roadGraph.AddRoadSegment(z1, z2);
        Assert.That(addedAgain, Is.False);
    }

    [Test]
    public void AddRoadSegment_UnknownNode_ReturnsFalse()
    {
        int validNode = _grid.GetZoneId(1, 1);
        int nonExistentNode = 999;

        bool success = _roadGraph.AddRoadSegment(validNode, nonExistentNode);
        Assert.That(success, Is.False);
    }

    [Test]
    public void RemoveRoadSegment_ExistingConnection_RemovesBothDirections()
    {
        int z1 = _grid.GetZoneId(1, 1);
        int z2 = _grid.GetZoneId(1, 2);

        Assert.That(_roadGraph.HasEdge(z1, z2), Is.True);
        Assert.That(_roadGraph.HasEdge(z2, z1), Is.True);

        int fwdId = _roadGraph.FindEdgeId(z1, z2);
        int revId = _roadGraph.FindEdgeId(z2, z1);

        bool removed = _roadGraph.RemoveRoadSegment(z1, z2);

        Assert.That(removed, Is.True);
        Assert.That(_roadGraph.HasEdge(z1, z2), Is.False);
        Assert.That(_roadGraph.HasEdge(z2, z1), Is.False);

        // Edges should be marked disconnected with FromId = -1 and ToId = -1
        Assert.That(_roadGraph.Edges[fwdId].FromId, Is.EqualTo(-1));
        Assert.That(_roadGraph.Edges[fwdId].ToId, Is.EqualTo(-1));
        Assert.That(_roadGraph.Edges[revId].FromId, Is.EqualTo(-1));
        Assert.That(_roadGraph.Edges[revId].ToId, Is.EqualTo(-1));
    }

    [Test]
    public void RemoveRoadSegment_NonExistentConnection_ReturnsFalse()
    {
        int z1 = _grid.GetZoneId(1, 1);
        int zDiagonal = _grid.GetZoneId(2, 2); // Diagonal zones have no direct edge

        Assert.That(_roadGraph.HasEdge(z1, zDiagonal), Is.False);

        bool removed = _roadGraph.RemoveRoadSegment(z1, zDiagonal);
        Assert.That(removed, Is.False);
    }

    [Test]
    public void RebuildAfterTopologyChange_RecalculatesDistanceAndPathCache()
    {
        // Setup a 3-node linear graph: A (1,1) -> B (1,2) -> C (1,3)
        var grid = new CityGrid(5, 5, 64f);
        int a = grid.GetZoneId(1, 1);
        int b = grid.GetZoneId(1, 2);
        int c = grid.GetZoneId(1, 3);

        grid.Zones[a].Type = ZoneType.Residential;
        grid.Zones[b].Type = ZoneType.Residential;
        grid.Zones[c].Type = ZoneType.Commercial;

        var graph = new RoadGraph();
        graph.BuildFromGrid(grid);

        var distances = graph.RebuildAfterTopologyChange(grid.ZoneCount);

        // Distance from A to C should be travel time along A->B->C
        float initialDist = distances[a, c];
        Assert.That(initialDist, Is.LessThan(float.MaxValue));
        Assert.That(graph.PathCache.ContainsKey((a, c)), Is.True);
        Assert.That(graph.PathCache[(a, c)].Count, Is.EqualTo(2)); // two edges

        // Demolish segment B-C: C becomes disconnected from A
        graph.RemoveRoadSegment(b, c);
        var newDistances = graph.RebuildAfterTopologyChange(grid.ZoneCount);

        Assert.That(newDistances[a, c], Is.EqualTo(float.MaxValue));
        Assert.That(graph.PathCache.ContainsKey((a, c)), Is.False);

        // Reconnect directly with a new road segment A-C
        bool added = graph.AddRoadSegment(a, c);
        Assert.That(added, Is.True);

        var reconnectedDistances = graph.RebuildAfterTopologyChange(grid.ZoneCount);
        Assert.That(reconnectedDistances[a, c], Is.LessThan(float.MaxValue));
        Assert.That(graph.PathCache.ContainsKey((a, c)), Is.True);
        Assert.That(graph.PathCache[(a, c)].Count, Is.EqualTo(1)); // direct 1 edge shortcut!
    }

    [Test]
    public void CarTrafficManager_HandleInvalidatedEdges_SafelyRespawnsVehicles()
    {
        int z1 = _grid.GetZoneId(1, 1);
        int z2 = _grid.GetZoneId(1, 2);

        var carMgr = new CarTrafficManager();
        carMgr.Initialize(_roadGraph);

        // Find the edge between z1 and z2
        int fwdId = _roadGraph.FindEdgeId(z1, z2);
        Assert.That(fwdId, Is.Not.EqualTo(-1));

        // Place a car explicitly on this edge
        carMgr.Cars[0].EdgeId = fwdId;

        // Demolish the segment
        _roadGraph.RemoveRoadSegment(z1, z2);

        // Call HandleInvalidatedEdges
        Assert.DoesNotThrow(() => carMgr.HandleInvalidatedEdges(_roadGraph));

        // Car 0 must no longer reference the demolished edge
        Assert.That(carMgr.Cars[0].EdgeId, Is.Not.EqualTo(fwdId));
        if (carMgr.Cars[0].EdgeId != -1)
        {
            var newEdge = _roadGraph.Edges[carMgr.Cars[0].EdgeId];
            Assert.That(newEdge.FromId, Is.Not.EqualTo(-1));
            Assert.That(newEdge.ToId, Is.Not.EqualTo(-1));
        }

        // Updating car manager must not throw exceptions
        var lights = new TrafficLightManager();
        lights.BuildIntersections(_roadGraph);
        Assert.DoesNotThrow(() => carMgr.Update(0.1f, 1.0f, _roadGraph, lights));
    }

    [Test]
    public void TrafficLightManager_BuildIntersections_UpdatesOnTopologyChange()
    {
        // Create a 3x3 grid with a central node at (1,1) having 4 neighbors: (0,1), (2,1), (1,0), (1,2)
        var grid = new CityGrid(3, 3, 64f);
        for (int x = 0; x < 3; x++)
        {
            for (int y = 0; y < 3; y++)
            {
                int id = grid.GetZoneId(x, y);
                grid.Zones[id].Type = ZoneType.Residential;
            }
        }

        var graph = new RoadGraph();
        graph.BuildFromGrid(grid);

        int center = grid.GetZoneId(1, 1);
        int top = grid.GetZoneId(1, 0);
        int bottom = grid.GetZoneId(1, 2);
        int left = grid.GetZoneId(0, 1);
        int right = grid.GetZoneId(2, 1);

        var lights = new TrafficLightManager();
        lights.BuildIntersections(graph);

        // Center should have a traffic light because it's a 4-way intersection (both H and V crossings)
        Assert.That(lights.Intersections.ContainsKey(center), Is.True);

        // Remove top and bottom connections: now center only has left and right (straight 2-way road)
        graph.RemoveRoadSegment(center, top);
        graph.RemoveRoadSegment(center, bottom);

        lights.BuildIntersections(graph);

        // Center should no longer have a traffic light
        Assert.That(lights.Intersections.ContainsKey(center), Is.False);
    }

    [Test]
    public void ManhattanDistance_AdjacentCells_IsOne()
    {
        var z1 = _grid.GetZone(1, 1);
        var z2 = _grid.GetZone(1, 2);
        var z3 = _grid.GetZone(2, 2);

        int dist12 = Mathf.Abs(z1.GridPos.X - z2.GridPos.X) + Mathf.Abs(z1.GridPos.Y - z2.GridPos.Y);
        int dist13 = Mathf.Abs(z1.GridPos.X - z3.GridPos.X) + Mathf.Abs(z1.GridPos.Y - z3.GridPos.Y);

        Assert.That(dist12, Is.EqualTo(1)); // adjacent
        Assert.That(dist13, Is.EqualTo(2)); // diagonal, non-adjacent
    }

    [Test]
    public void RemoveRoadSegment_UnknownNodes_ReturnsFalseSafely()
    {
        bool result = _roadGraph.RemoveRoadSegment(9999, 8888);
        Assert.That(result, Is.False);
    }

    [Test]
    public void DemolishAndReconstruct_RestoresConnectivityProperly()
    {
        int z1 = _grid.GetZoneId(1, 1);
        int z2 = _grid.GetZoneId(1, 2);

        Assert.That(_roadGraph.HasEdge(z1, z2), Is.True);

        // Demolish
        bool removed = _roadGraph.RemoveRoadSegment(z1, z2);
        Assert.That(removed, Is.True);
        Assert.That(_roadGraph.HasEdge(z1, z2), Is.False);

        // Reconstruct
        bool added = _roadGraph.AddRoadSegment(z1, z2);
        Assert.That(added, Is.True);
        Assert.That(_roadGraph.HasEdge(z1, z2), Is.True);
        Assert.That(_roadGraph.HasEdge(z2, z1), Is.True);

        var distances = _roadGraph.RebuildAfterTopologyChange(_grid.ZoneCount);
        Assert.That(distances[z1, z2], Is.LessThan(float.MaxValue));
        Assert.That(_roadGraph.PathCache.ContainsKey((z1, z2)), Is.True);
    }

    [Test]
    public void CarTrafficManager_AllEdgesDemolished_DoesNotThrow()
    {
        // Demolish all edges in graph
        for (int i = 0; i < _roadGraph.Edges.Count; i++)
        {
            var edge = _roadGraph.Edges[i];
            if (edge.FromId != -1 && edge.ToId != -1)
            {
                _roadGraph.RemoveRoadSegment(edge.FromId, edge.ToId);
            }
        }

        var carMgr = new CarTrafficManager();
        carMgr.Initialize(_roadGraph);

        Assert.DoesNotThrow(() => carMgr.HandleInvalidatedEdges(_roadGraph));

        var lights = new TrafficLightManager();
        lights.BuildIntersections(_roadGraph);
        Assert.DoesNotThrow(() => carMgr.Update(0.1f, 1.0f, _roadGraph, lights));
    }
}
