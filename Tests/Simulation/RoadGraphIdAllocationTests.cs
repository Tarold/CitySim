using Godot;
using NUnit.Framework;
using System.Linq;
using CitySim.Simulation;

namespace CitySim.Tests.Simulation;

[TestFixture]
public class RoadGraphIdAllocationTests
{
    private static int ScanNextNodeId(RoadGraph graph) => graph.NodeMap.Count == 0 ? 0 : graph.NodeMap.Keys.Max() + 1;

    [Test]
    public void CreateNode_Sequence_MatchesLargestIdPlusOne()
    {
        var graph = new RoadGraph();
        for (int i = 0; i < 50; i++)
        {
            int expected = ScanNextNodeId(graph);
            Assert.That(graph.CreateNode(new Vector2(i, 0)).Id, Is.EqualTo(expected));
        }
    }

    [Test]
    public void CreateNode_AfterRemovingLargestNode_ReusesScanSemantics()
    {
        var graph = new RoadGraph();
        for (int i = 0; i < 5; i++) graph.CreateNode(new Vector2(i, 0));
        graph.DetachAndRemoveNode(4);

        Assert.That(graph.CreateNode(new Vector2(9, 9)).Id, Is.EqualTo(4));
    }

    [Test]
    public void CreateNode_AfterDirectNodeMapEdit_SkipsPastForeignId()
    {
        var graph = new RoadGraph();
        graph.CreateNode(Vector2.Zero);
        graph.NodeMap[40] = new RoadNode { Id = 40 };

        Assert.That(graph.CreateNode(Vector2.One).Id, Is.EqualTo(41));
    }

    [Test]
    public void EnsureNode_ThenCreateNode_AllocatesAboveZoneIds()
    {
        var graph = new RoadGraph();
        graph.EnsureNode(100, Vector2.Zero);

        Assert.That(graph.CreateNode(Vector2.One).Id, Is.EqualTo(101));
    }

    [Test]
    public void AddCurvedRoadSegment_EdgeIdsMatchListIndices()
    {
        var graph = new RoadGraph();
        var nodes = Enumerable.Range(0, 10).Select(i => graph.CreateNode(new Vector2(i * 50, 0))).ToList();
        for (int i = 0; i < nodes.Count - 1; i++)
        {
            graph.AddCurvedRoadSegment(nodes[i].Id, nodes[i + 1].Id, null);
        }

        for (int e = 0; e < graph.Edges.Count; e++)
        {
            Assert.That(graph.Edges[e].Id, Is.EqualTo(e));
        }

        graph.Edges.Clear();
        graph.AdjacencyEdges.Clear();
        graph.AddCurvedRoadSegment(nodes[0].Id, nodes[1].Id, null);
        Assert.That(graph.Edges[0].Id, Is.EqualTo(0), "Cache is invalidated when the list is cleared");
    }
}
