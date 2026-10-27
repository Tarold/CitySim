using System;
using System.Collections.Generic;
using Godot;
using NUnit.Framework;
using CitySim.Simulation;

namespace CitySim.Tests.Simulation;

[TestFixture]
public class CompactRoutingGraphTests
{
    [Test]
    public void CompactRoutingGraph_LinearChain_ContractsDegree2NodesIntoSuperedge()
    {
        var graph = new RoadGraph();
        int a = graph.CreateNode(new Vector2(0, 0)).Id;
        int n1 = graph.CreateNode(new Vector2(10, 0)).Id;
        int n2 = graph.CreateNode(new Vector2(20, 0)).Id;
        int b = graph.CreateNode(new Vector2(30, 0)).Id;

        graph.AddCurvedRoadSegment(a, n1, null, 1000f);
        graph.AddCurvedRoadSegment(n1, n2, null, 1000f);
        graph.AddCurvedRoadSegment(n2, b, null, 1000f);

        var protectedNodes = new[] { a, b };
        var snapshot = new RoutingSnapshot();
        snapshot.Rebuild(graph, null, protectedNodes, contractChains: true);

        Assert.That(snapshot.IsContracted, Is.True);
        Assert.That(snapshot.CompactNodeCount, Is.EqualTo(2), "Only A and B should be compact nodes");
        Assert.That(snapshot.GetDenseIndex(a), Is.GreaterThanOrEqualTo(0));
        Assert.That(snapshot.GetDenseIndex(b), Is.GreaterThanOrEqualTo(0));
        Assert.That(snapshot.GetDenseIndex(n1), Is.EqualTo(-1), "N1 should be contracted");
        Assert.That(snapshot.GetDenseIndex(n2), Is.EqualTo(-1), "N2 should be contracted");

        // Directed superedges: one A -> B and one B -> A
        Assert.That(snapshot.AdjacencyCount, Is.EqualTo(2));

        var tree = new ShortestPathTreeBuffer();
        snapshot.ComputeShortestPathTree(a, tree);

        float expectedDist = 0f;
        expectedDist += graph.GetEdge(graph.FindEdgeId(a, n1)).GetTravelTime();
        expectedDist += graph.GetEdge(graph.FindEdgeId(n1, n2)).GetTravelTime();
        expectedDist += graph.GetEdge(graph.FindEdgeId(n2, b)).GetTravelTime();

        Assert.That(tree.Distances[b], Is.EqualTo(expectedDist).Within(1e-5f));
    }

    [Test]
    public void CompactRoutingGraph_ProtectedIntermediateNode_IsNotContracted()
    {
        var graph = new RoadGraph();
        int a = graph.CreateNode(new Vector2(0, 0)).Id;
        int n1 = graph.CreateNode(new Vector2(10, 0)).Id;
        int n2 = graph.CreateNode(new Vector2(20, 0)).Id;
        int b = graph.CreateNode(new Vector2(30, 0)).Id;

        graph.AddCurvedRoadSegment(a, n1, null, 1000f);
        graph.AddCurvedRoadSegment(n1, n2, null, 1000f);
        graph.AddCurvedRoadSegment(n2, b, null, 1000f);

        // Protect N1 (e.g. transit stop or access node)
        var protectedNodes = new[] { a, n1, b };
        var snapshot = new RoutingSnapshot();
        snapshot.Rebuild(graph, null, protectedNodes, contractChains: true);

        Assert.That(snapshot.CompactNodeCount, Is.EqualTo(3), "A, N1, and B should be compact nodes");
        Assert.That(snapshot.GetDenseIndex(n1), Is.GreaterThanOrEqualTo(0), "N1 must not be contracted");
        Assert.That(snapshot.GetDenseIndex(n2), Is.EqualTo(-1), "N2 should still be contracted");
    }

    [Test]
    public void CompactRoutingGraph_JunctionAndDeadEnd_AreNeverContracted()
    {
        var graph = new RoadGraph();
        int j = graph.CreateNode(new Vector2(0, 0)).Id;
        int d1 = graph.CreateNode(new Vector2(-10, 0)).Id;
        int d2 = graph.CreateNode(new Vector2(10, 0)).Id;
        int d3 = graph.CreateNode(new Vector2(0, 10)).Id;

        graph.AddCurvedRoadSegment(j, d1, null, 1000f);
        graph.AddCurvedRoadSegment(j, d2, null, 1000f);
        graph.AddCurvedRoadSegment(j, d3, null, 1000f);

        // Only D1 and D2 are protected; J has degree 3, D3 has degree 1
        var protectedNodes = new[] { d1, d2 };
        var snapshot = new RoutingSnapshot();
        snapshot.Rebuild(graph, null, protectedNodes, contractChains: true);

        Assert.That(snapshot.GetDenseIndex(j), Is.GreaterThanOrEqualTo(0), "Degree-3 junction must not be contracted");
        Assert.That(snapshot.GetDenseIndex(d3), Is.GreaterThanOrEqualTo(0), "Dead-end must not be contracted");
        Assert.That(snapshot.CompactNodeCount, Is.EqualTo(4));
    }

    [Test]
    public void CompactRoutingGraph_FlowAccumulation_UnfoldsOntoAllChainEdges()
    {
        var graph = new RoadGraph();
        int a = graph.CreateNode(new Vector2(0, 0)).Id;
        int n1 = graph.CreateNode(new Vector2(10, 0)).Id;
        int n2 = graph.CreateNode(new Vector2(20, 0)).Id;
        int b = graph.CreateNode(new Vector2(30, 0)).Id;

        graph.AddCurvedRoadSegment(a, n1, null, 1000f);
        graph.AddCurvedRoadSegment(n1, n2, null, 1000f);
        graph.AddCurvedRoadSegment(n2, b, null, 1000f);

        int eAN1 = graph.FindEdgeId(a, n1);
        int eN1N2 = graph.FindEdgeId(n1, n2);
        int eN2B = graph.FindEdgeId(n2, b);
        int eBA = graph.FindEdgeId(b, n2);

        var snapshot = new RoutingSnapshot();
        snapshot.Rebuild(graph, null, new[] { a, b }, contractChains: true);

        var tree = new ShortestPathTreeBuffer();
        snapshot.ComputeShortestPathTree(a, tree);

        var nodeFlow = new float[snapshot.NodeCapacity];
        var volumes = new float[graph.Edges.Count];
        var demands = new List<(int, float)> { (b, 75.0f) };

        snapshot.AccumulateOriginFlows(a, demands, tree, nodeFlow, volumes, graph.Edges.Count);

        Assert.That(volumes[eAN1], Is.EqualTo(75.0f).Within(1e-5f));
        Assert.That(volumes[eN1N2], Is.EqualTo(75.0f).Within(1e-5f));
        Assert.That(volumes[eN2B], Is.EqualTo(75.0f).Within(1e-5f));
        Assert.That(volumes[eBA], Is.EqualTo(0.0f).Within(1e-5f), "Opposite direction edge should have 0 flow");

        // Verify direct accumulation without ShortestPathTreeBuffer produces identical volumes
        var directVolumes = new float[graph.Edges.Count];
        snapshot.AccumulateOriginFlows(a, demands, directVolumes, graph.Edges.Count);
        Assert.That(directVolumes[eAN1], Is.EqualTo(75.0f).Within(1e-5f));
        Assert.That(directVolumes[eN1N2], Is.EqualTo(75.0f).Within(1e-5f));
        Assert.That(directVolumes[eN2B], Is.EqualTo(75.0f).Within(1e-5f));
        Assert.That(directVolumes[eBA], Is.EqualTo(0.0f).Within(1e-5f));
    }

    [Test]
    public void CompactRoutingGraph_MetropolisSmall_MatchesFullGraphEquilibrium()
    {
        using var world = SimulationWorld.CreateMetropolis(MetropolisOptions.Small);
        world.RecalculateMacroModel();
        var graph = world.RoadGraph;
        var taz = world.Taz;

        int[] accessNodes = TravelTimeSkim.CollectAccessNodes(taz);
        var protectedNodes = TravelTimeSkim.CollectProtectedNodes(taz);

        int totalNodes = graph.Nodes.Count;
        int edgeCount = graph.Edges.Count;

        var currentCarVolumes = new float[edgeCount];
        for (int e = 0; e < edgeCount; e++) currentCarVolumes[e] = graph.Edges[e].CurrentVolume;

        // 1. Uncontracted routing snapshot
        var uncontractedSnapshot = new RoutingSnapshot();
        uncontractedSnapshot.Rebuild(graph, currentCarVolumes, protectedNodes, contractChains: false);
        Assert.That(uncontractedSnapshot.IsContracted, Is.False);
        Assert.That(uncontractedSnapshot.CompactNodeCount, Is.EqualTo(graph.NodeMap.Count));

        var uncontractedSkim = new TravelTimeSkim();
        uncontractedSkim.Compute(graph, uncontractedSnapshot, accessNodes);

        // 2. Compact routing snapshot
        var compactSnapshot = new RoutingSnapshot();
        compactSnapshot.Rebuild(graph, currentCarVolumes, protectedNodes, contractChains: true);
        Assert.That(compactSnapshot.IsContracted, Is.True);

        // Verify that a substantial fraction of intermediate curve nodes were collapsed
        Assert.That(compactSnapshot.CompactNodeCount, Is.LessThan(totalNodes * 0.70),
            $"Compact nodes ({compactSnapshot.CompactNodeCount}) should be < 70% of total ({totalNodes})");

        var compactSkim = new TravelTimeSkim();
        compactSkim.Compute(graph, compactSnapshot, accessNodes);

        // 3. Compare travel times between all access nodes
        for (int r = 0; r < accessNodes.Length; r++)
        {
            int u = accessNodes[r];
            for (int c = 0; c < accessNodes.Length; c++)
            {
                int v = accessNodes[c];
                float tUncontracted = uncontractedSkim.GetRoadTime(u, v);
                float tCompact = compactSkim.GetRoadTime(u, v);

                if (tUncontracted < float.MaxValue * 0.5f)
                {
                    Assert.That(tCompact, Is.EqualTo(tUncontracted).Within(Math.Max(1e-4f * tUncontracted, 1e-4f)),
                        $"Skim mismatch from {u} to {v}: compact={tCompact}, uncontracted={tUncontracted}");
                }
            }
        }

        // 4. Compare flow accumulation along trees
        var uncontractedTree = new ShortestPathTreeBuffer();
        var compactTree = new ShortestPathTreeBuffer();

        var uncontractedVolumes = new float[edgeCount];
        var compactVolumes = new float[edgeCount];

        var nodeFlowUncontracted = new float[uncontractedSnapshot.NodeCapacity];
        var nodeFlowCompact = new float[compactSnapshot.NodeCapacity];

        // Create synthetic demands from 5 random access nodes to 5 other access nodes
        for (int i = 0; i < Math.Min(10, accessNodes.Length); i++)
        {
            int origin = accessNodes[i];
            var demands = new List<(int, float)>();
            for (int j = 0; j < accessNodes.Length; j += 3)
            {
                if (i != j) demands.Add((accessNodes[j], 10.0f + (i * 3 + j) % 17));
            }

            uncontractedSnapshot.ComputeShortestPathTree(origin, uncontractedTree);
            compactSnapshot.ComputeShortestPathTree(origin, compactTree);

            uncontractedSnapshot.AccumulateOriginFlows(origin, demands, uncontractedTree, nodeFlowUncontracted, uncontractedVolumes, edgeCount);
            compactSnapshot.AccumulateOriginFlows(origin, demands, compactTree, nodeFlowCompact, compactVolumes, edgeCount);
        }

        float totalUncontracted = 0f, totalCompact = 0f;
        for (int e = 0; e < edgeCount; e++)
        {
            totalUncontracted += uncontractedVolumes[e];
            totalCompact += compactVolumes[e];
        }

        // Total flow propagated across the road network is conserved
        Assert.That(totalCompact, Is.GreaterThan(0f));
        Assert.That(totalCompact, Is.EqualTo(totalUncontracted).Within(1e-4f * totalUncontracted),
            $"Total volume mismatch: compact={totalCompact}, uncontracted={totalUncontracted}");
    }
}
