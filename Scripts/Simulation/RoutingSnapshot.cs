using System;
using System.Collections.Generic;

namespace CitySim.Simulation;

/// <summary>
/// Compact, read-only copy of the road graph for a round of many shortest-path searches (macro pipeline):
/// CSR adjacency in the same order as <see cref="RoadGraph.AdjacencyEdges"/> and car travel times evaluated
/// once per round. Searches run on flat arrays only (no dictionaries, no per-edge object access, no BPR
/// <c>Pow</c> per relaxation) and are thread-safe, so a round can run in <c>Parallel.For</c>.
/// Results are identical to <see cref="RoadGraph.ComputeDijkstraIntoBuffer"/> on the graph state at
/// <see cref="Rebuild"/> time.
/// </summary>
public sealed class RoutingSnapshot
{
    [ThreadStatic]
    private static PriorityQueue<int, float> _tlsPriorityQueue;

    private int[] _rowStart = Array.Empty<int>();
    private int[] _adjEdge = Array.Empty<int>();
    private int[] _adjTarget = Array.Empty<int>();
    private float[] _adjCost = Array.Empty<float>();
    private bool[] _nodeExists = Array.Empty<bool>();

    /// <summary>Exclusive upper bound of node ids (size of per-node search arrays).</summary>
    public int NodeCapacity { get; private set; }

    /// <summary>Number of directed adjacency entries copied.</summary>
    public int AdjacencyCount { get; private set; }

    /// <summary>
    /// Copies topology and car travel times from <paramref name="graph"/>, reusing arrays. Travel times use
    /// <paramref name="carVolumes"/> (indexed by edge id) when given, otherwise the published
    /// <see cref="RoadEdge.CurrentVolume"/>. Call before each search round; later graph changes are not seen
    /// until the next rebuild.
    /// </summary>
    public void Rebuild(RoadGraph graph, float[] carVolumes = null)
    {
        int maxId = graph.GetMaxNodeIndex();
        NodeCapacity = maxId;

        if (_rowStart.Length < maxId + 1)
        {
            _rowStart = new int[Math.Max((maxId + 1) * 2, 64)];
            _nodeExists = new bool[_rowStart.Length];
        }

        var edges = graph.Edges;
        int count = 0;
        for (int u = 0; u < maxId; u++)
        {
            _rowStart[u] = count;
            _nodeExists[u] = graph.NodeMap.ContainsKey(u);

            if (!graph.AdjacencyEdges.TryGetValue(u, out var edgeIds)) continue;

            EnsureAdjacencyCapacity(count + edgeIds.Count);
            for (int i = 0; i < edgeIds.Count; i++)
            {
                int eid = edgeIds[i];
                if (eid < 0 || eid >= edges.Count) continue;
                var edge = edges[eid];

                int v = edge.ToId;
                if (v < 0 || v >= maxId || edge.FromId == -1) continue;

                _adjEdge[count] = eid;
                _adjTarget[count] = v;
                _adjCost[count] = carVolumes == null
                    ? edge.GetTravelTime()
                    : RoadEdge.ComputeTravelTime(edge.Length, edge.FreeFlowSpeed, carVolumes[eid], edge.Capacity);
                count++;
            }
        }
        _rowStart[maxId] = count;
        AdjacencyCount = count;
    }

    /// <summary>
    /// Fills <paramref name="buffer"/> with the shortest-path tree from <paramref name="sourceNodeId"/>
    /// (distances, predecessor edges/nodes, settle order). Unknown sources leave every node unreachable.
    /// </summary>
    public void ComputeShortestPathTree(int sourceNodeId, ShortestPathTreeBuffer buffer)
    {
        int maxId = NodeCapacity;
        buffer.EnsureCapacity(maxId);
        Array.Fill(buffer.Distances, float.MaxValue, 0, maxId);
        Array.Fill(buffer.ParentEdgeIds, -1, 0, maxId);
        Array.Fill(buffer.ParentNodeIds, -1, 0, maxId);
        buffer.VisitedCount = 0;

        if (sourceNodeId < 0 || sourceNodeId >= maxId || !_nodeExists[sourceNodeId])
            return;

        var pq = _tlsPriorityQueue ??= new PriorityQueue<int, float>(256);
        pq.Clear();

        float[] dist = buffer.Distances;
        int[] parentEdge = buffer.ParentEdgeIds;
        int[] parentNode = buffer.ParentNodeIds;
        int[] visited = buffer.VisitedNodesOrder;
        int visitedCount = 0;

        dist[sourceNodeId] = 0f;
        pq.Enqueue(sourceNodeId, 0f);

        while (pq.TryDequeue(out int u, out float d))
        {
            if (d > dist[u]) continue;
            visited[visitedCount++] = u;

            int end = _rowStart[u + 1];
            for (int a = _rowStart[u]; a < end; a++)
            {
                int v = _adjTarget[a];
                float newDist = d + _adjCost[a];
                if (newDist < dist[v])
                {
                    dist[v] = newDist;
                    parentEdge[v] = _adjEdge[a];
                    parentNode[v] = u;
                    pq.Enqueue(v, newDist);
                }
            }
        }

        buffer.VisitedCount = visitedCount;
    }

    private void EnsureAdjacencyCapacity(int required)
    {
        if (_adjEdge.Length >= required) return;

        int size = Math.Max(required * 2, 256);
        Array.Resize(ref _adjEdge, size);
        Array.Resize(ref _adjTarget, size);
        Array.Resize(ref _adjCost, size);
    }
}
