using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CitySim.Simulation;

/// <summary>
/// Car travel times ("skim") between the primary road nodes of all TAZs, produced by one round of
/// shortest-path searches. It is the only routing result kept between rounds and between macro cycles:
/// shortest-path trees are computed, consumed and discarded per origin, so memory is O(P²) for P access
/// nodes instead of O(P·N) for cached trees over N road nodes.
/// </summary>
public sealed class TravelTimeSkim
{
    [ThreadStatic]
    private static ShortestPathTreeBuffer _tlsTree;

    private int[] _nodeIds = Array.Empty<int>();
    private int[] _indexByNode = Array.Empty<int>();
    private float[] _times = Array.Empty<float>();

    /// <summary>Number of access nodes (rows and columns).</summary>
    public int NodeCount { get; private set; }

    /// <summary><see cref="RoadGraph.TopologyVersion"/> the skim was computed for; -1 when empty.</summary>
    public int TopologyVersion { get; private set; } = -1;

    /// <summary>The graph the skim was computed for.</summary>
    public RoadGraph Graph { get; private set; }

    /// <summary>Sorted, distinct primary road nodes of the active TAZs.</summary>
    public static int[] CollectAccessNodes(TazManager tazManager)
    {
        var nodes = new SortedSet<int>();
        var zones = tazManager.ActiveZones;
        for (int i = 0; i < zones.Count; i++)
        {
            int nodeId = zones[i].PrimaryRoadNodeId;
            if (nodeId >= 0) nodes.Add(nodeId);
        }
        var result = new int[nodes.Count];
        nodes.CopyTo(result);
        return result;
    }

    /// <summary>
    /// True when the skim was computed on <paramref name="graph"/> at its current topology and contains
    /// every TAZ primary node, so it can replace a fresh search round.
    /// </summary>
    public bool Covers(TazManager tazManager, RoadGraph graph)
    {
        if (graph == null || !ReferenceEquals(graph, Graph) || graph.TopologyVersion != TopologyVersion) return false;

        var zones = tazManager.ActiveZones;
        for (int i = 0; i < zones.Count; i++)
        {
            int nodeId = zones[i].PrimaryRoadNodeId;
            if (nodeId >= 0 && IndexOf(nodeId) < 0) return false;
        }
        return true;
    }

    /// <summary>Row/column index of an access node, or -1.</summary>
    public int IndexOf(int nodeId) =>
        nodeId >= 0 && nodeId < _indexByNode.Length ? _indexByNode[nodeId] : -1;

    /// <summary>Road travel time between two access nodes (float.MaxValue when unknown or unreachable).</summary>
    public float GetRoadTime(int fromNodeId, int toNodeId)
    {
        int row = IndexOf(fromNodeId);
        int col = IndexOf(toNodeId);
        return row < 0 || col < 0 ? float.MaxValue : _times[row * NodeCount + col];
    }

    /// <summary>Road travel time by row/column index.</summary>
    public float GetRoadTimeByIndex(int row, int col) => _times[row * NodeCount + col];

    /// <summary>Access node of a row/column.</summary>
    public int GetNodeId(int index) => _nodeIds[index];

    /// <summary>
    /// Prepares storage for <paramref name="accessNodes"/> (sorted, distinct); rows are then filled by
    /// <see cref="WriteRow"/> (thread-safe for distinct rows).
    /// </summary>
    public void Reset(int[] accessNodes, RoadGraph graph, int nodeCapacity)
    {
        Graph = graph;
        TopologyVersion = graph.TopologyVersion;
        NodeCount = accessNodes.Length;
        _nodeIds = accessNodes;

        if (_indexByNode.Length < nodeCapacity)
        {
            _indexByNode = new int[Math.Max(nodeCapacity, 16)];
        }
        Array.Fill(_indexByNode, -1);
        for (int i = 0; i < accessNodes.Length; i++)
        {
            if (accessNodes[i] < _indexByNode.Length) _indexByNode[accessNodes[i]] = i;
        }

        int required = NodeCount * NodeCount;
        if (_times.Length < required)
        {
            _times = new float[required];
        }
    }

    /// <summary>Copies the distances of one shortest-path tree into the row of its origin.</summary>
    public void WriteRow(int row, float[] distances)
    {
        int offset = row * NodeCount;
        for (int col = 0; col < NodeCount; col++)
        {
            int nodeId = _nodeIds[col];
            _times[offset + col] = nodeId < distances.Length ? distances[nodeId] : float.MaxValue;
        }
    }

    /// <summary>
    /// Fills the skim with one parallel search round on <paramref name="routing"/> (built from
    /// <paramref name="graph"/>), without keeping any tree.
    /// </summary>
    public void Compute(RoadGraph graph, RoutingSnapshot routing, int[] accessNodes)
    {
        Reset(accessNodes, graph, routing.NodeCapacity);
        Parallel.For(0, accessNodes.Length, row =>
        {
            var tree = _tlsTree ??= new ShortestPathTreeBuffer();
            routing.ComputeShortestPathTree(accessNodes[row], tree);
            WriteRow(row, tree.Distances);
        });
    }
}
