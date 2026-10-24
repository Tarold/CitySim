using NUnit.Framework;
using System;
using System.Linq;
using CitySim.Simulation;
using CitySim.Tests.Performance;

namespace CitySim.Tests.Simulation;

[TestFixture]
public class ShortestPathCoreTests
{
    private const int GridSize = 20;

    private RoadGraph _graph;
    private CityGrid _grid;

    [SetUp]
    public void Setup()
    {
        (_grid, _graph, _, _) = MacroPipelineBenchmark.BuildSyntheticCity(GridSize);
        // Uneven congestion so shortest paths are not just Manhattan ties
        for (int i = 0; i < _graph.Edges.Count; i++)
        {
            _graph.Edges[i].CurrentVolume = _graph.Edges[i].Capacity * ((i * 37) % 13) / 10f;
            _graph.Edges[i].PedestrianVolume = _graph.Edges[i].PedestrianCapacity * ((i * 11) % 7) / 5f;
        }
    }

    private int Node(int x, int y) => _grid.GetZoneId(x, y);

    [Test]
    public void AllDrivingVariants_SameSource_AgreeOnDistances()
    {
        int source = Node(3, 4);
        var buffer = new ShortestPathTreeBuffer();
        _graph.ComputeDijkstraIntoBuffer(source, buffer);
        var plain = _graph.Dijkstra(source);
        var (withPred, parentEdges, _) = _graph.DijkstraWithPredecessors(source);

        for (int v = 0; v < plain.Length; v++)
        {
            Assert.That(withPred[v], Is.EqualTo(plain[v]), $"node {v}");
            Assert.That(buffer.Distances[v], Is.EqualTo(plain[v]), $"node {v}");
            Assert.That(buffer.ParentEdgeIds[v], Is.EqualTo(parentEdges[v]), $"node {v}");
        }
        Assert.That(buffer.VisitedCount, Is.EqualTo(_graph.Nodes.Count), "Connected grid: every node settled");
    }

    [Test]
    public void GetShortestPath_SumOfEdgeCostsEqualsTreeDistance()
    {
        int source = Node(1, 1);
        int target = Node(17, 15);
        var dist = _graph.Dijkstra(source);

        var path = _graph.GetShortestPath(source, target);

        Assert.That(path, Is.Not.Null);
        Assert.That(_graph.Edges[path[0]].FromId, Is.EqualTo(source));
        Assert.That(_graph.Edges[path[^1]].ToId, Is.EqualTo(target));
        float sum = 0f;
        foreach (int eid in path) sum += _graph.Edges[eid].GetTravelTime();
        Assert.That(sum, Is.EqualTo(dist[target]).Within(1e-3f));
    }

    [Test]
    public void GetShortestWalkingPath_UsesWalkingCosts()
    {
        int source = Node(2, 2);
        int target = Node(12, 9);
        var walkDist = _graph.DijkstraWalking(source);

        var path = _graph.GetShortestWalkingPath(source, target);

        float sum = 0f;
        foreach (int eid in path) sum += _graph.Edges[eid].GetWalkingTravelTime();
        Assert.That(sum, Is.EqualTo(walkDist[target]).Within(1e-3f));
    }

    [Test]
    public void GetShortestPath_SameNode_ReturnsEmptyPath()
    {
        Assert.That(_graph.GetShortestPath(Node(5, 5), Node(5, 5)), Is.Empty);
    }

    [Test]
    public void GetShortestPath_UnknownNodes_ReturnNull()
    {
        Assert.That(_graph.GetShortestPath(-1, Node(5, 5)), Is.Null);
        Assert.That(_graph.GetShortestWalkingPath(Node(5, 5), int.MaxValue), Is.Null);
    }

    [Test]
    public void ComputeDijkstraIntoBuffer_UnknownSource_LeavesEverythingUnreachable()
    {
        var buffer = new ShortestPathTreeBuffer();

        _graph.ComputeDijkstraIntoBuffer(int.MaxValue, buffer);

        Assert.That(buffer.VisitedCount, Is.EqualTo(0));
        Assert.That(buffer.Distances.Take(_graph.Nodes.Count).All(d => d == float.MaxValue), Is.True);
    }

    [Test]
    public void Search_DemolishedEdgeLeftInAdjacency_IsSkippedInsteadOfThrowing()
    {
        int a = Node(4, 4);
        int b = Node(5, 4);
        int eid = _graph.FindEdgeId(a, b);
        // Simulate a stale adjacency entry pointing at a demolished edge
        _graph.Edges[eid].FromId = -1;
        _graph.Edges[eid].ToId = -1;

        Assert.DoesNotThrow(() => _graph.Dijkstra(a));
        Assert.DoesNotThrow(() => _graph.DijkstraWalking(a));
    }

    [Test]
    public void GetShortestPath_RepeatedQueries_DoNotAllocatePerNodeArrays()
    {
        int source = Node(1, 1);
        int target = Node(18, 18);
        _graph.GetShortestPath(source, target); // warm up the pooled per-thread buffers

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++) _graph.GetShortestPath(source, target);
        long perQuery = (GC.GetAllocatedBytesForCurrentThread() - before) / 50;

        // Old implementation allocated two node-sized arrays per query (400 nodes * 8 bytes) plus the queue
        Assert.That(perQuery, Is.LessThan(_graph.Nodes.Count * 4), $"{perQuery} bytes per query");
    }

    [Test]
    public void RoutingSnapshot_EveryOrigin_MatchesRoadGraphTree()
    {
        var snapshot = new RoutingSnapshot();
        snapshot.Rebuild(_graph);
        var expected = new ShortestPathTreeBuffer();
        var actual = new ShortestPathTreeBuffer();

        foreach (var node in _graph.Nodes.Where((_, i) => i % 7 == 0))
        {
            _graph.ComputeDijkstraIntoBuffer(node.Id, expected);
            snapshot.ComputeShortestPathTree(node.Id, actual);

            Assert.That(actual.VisitedCount, Is.EqualTo(expected.VisitedCount));
            for (int v = 0; v < snapshot.NodeCapacity; v++)
            {
                Assert.That(actual.Distances[v], Is.EqualTo(expected.Distances[v]), $"source {node.Id}, node {v}");
                Assert.That(actual.ParentEdgeIds[v], Is.EqualTo(expected.ParentEdgeIds[v]), $"source {node.Id}, node {v}");
            }
            for (int k = 0; k < expected.VisitedCount; k++)
            {
                Assert.That(actual.VisitedNodesOrder[k], Is.EqualTo(expected.VisitedNodesOrder[k]));
            }
        }
    }

    [Test]
    public void RoutingSnapshot_GraphChangedAfterRebuild_KeepsOldCostsUntilNextRebuild()
    {
        var snapshot = new RoutingSnapshot();
        snapshot.Rebuild(_graph);
        int source = Node(0, 0);
        var before = new ShortestPathTreeBuffer();
        snapshot.ComputeShortestPathTree(source, before);
        float distBefore = before.Distances[Node(10, 0)];

        foreach (var edge in _graph.Edges) edge.CurrentVolume = edge.Capacity * 3f;
        var stale = new ShortestPathTreeBuffer();
        snapshot.ComputeShortestPathTree(source, stale);
        snapshot.Rebuild(_graph);
        var fresh = new ShortestPathTreeBuffer();
        snapshot.ComputeShortestPathTree(source, fresh);

        Assert.That(stale.Distances[Node(10, 0)], Is.EqualTo(distBefore));
        Assert.That(fresh.Distances[Node(10, 0)], Is.GreaterThan(distBefore));
    }

    [Test]
    public void RoutingSnapshot_UnknownSource_LeavesEverythingUnreachable()
    {
        var snapshot = new RoutingSnapshot();
        snapshot.Rebuild(_graph);
        var buffer = new ShortestPathTreeBuffer();

        snapshot.ComputeShortestPathTree(-5, buffer);

        Assert.That(buffer.VisitedCount, Is.EqualTo(0));
    }

    [Test]
    public void ComputeTravelTime_MatchesInstanceMethods()
    {
        foreach (var edge in _graph.Edges.Take(50))
        {
            Assert.That(RoadEdge.ComputeTravelTime(edge.Length, edge.FreeFlowSpeed, edge.CurrentVolume, edge.Capacity), Is.EqualTo(edge.GetTravelTime()));
            Assert.That(RoadEdge.ComputeWalkingTravelTime(edge.Length, edge.WalkingSpeed, edge.PedestrianVolume, edge.PedestrianCapacity), Is.EqualTo(edge.GetWalkingTravelTime()));
        }
    }

    [Test]
    public void RoutingSnapshot_ExternalVolumes_EquivalentToPublishedVolumes()
    {
        var volumes = _graph.Edges.Select(e => e.CurrentVolume).ToArray();
        var fromEdges = new RoutingSnapshot();
        var fromArray = new RoutingSnapshot();
        fromEdges.Rebuild(_graph);
        foreach (var e in _graph.Edges) e.CurrentVolume = 0f; // published state differs from the array
        fromArray.Rebuild(_graph, volumes);

        var a = new ShortestPathTreeBuffer();
        var b = new ShortestPathTreeBuffer();
        fromEdges.ComputeShortestPathTree(Node(2, 3), a);
        fromArray.ComputeShortestPathTree(Node(2, 3), b);

        for (int v = 0; v < fromEdges.NodeCapacity; v++)
        {
            Assert.That(b.Distances[v], Is.EqualTo(a.Distances[v]));
        }
    }

    [Test]
    public void GetShortestWalkingPath_ExternalVolumes_EquivalentToPublishedVolumes()
    {
        var pedestrianVolumes = _graph.Edges.Select(e => e.PedestrianVolume).ToArray();
        var expected = _graph.GetShortestWalkingPath(Node(1, 2), Node(14, 11));
        foreach (var e in _graph.Edges) e.PedestrianVolume = 0f;

        var actual = _graph.GetShortestWalkingPath(Node(1, 2), Node(14, 11), pedestrianVolumes);

        Assert.That(actual, Is.EqualTo(expected));
    }
}
