using System;
using System.Collections.Generic;

namespace CitySim.Simulation;

/// <summary>
/// Compact, read-only copy of the road graph for macro shortest-path search rounds:
/// contracts bidirectional degree-2 chains (intermediate curve shape nodes) into directed superedges,
/// indexes compact nodes with dense indices 0..CompactNodeCount-1, and evaluates travel times once per round.
/// Searches run on flat CSR arrays in thread-local memory with zero heap allocations during the round.
/// </summary>
public sealed class RoutingSnapshot
{
    [ThreadStatic]
    private static PriorityQueue<int, float> _tlsPriorityQueue;

    [ThreadStatic]
    private static float[] _tlsDenseDist;

    [ThreadStatic]
    private static int[] _tlsDenseParentSuperEdge;

    [ThreadStatic]
    private static int[] _tlsDenseParentDenseNode;

    [ThreadStatic]
    private static int[] _tlsDenseVisited;

    [ThreadStatic]
    private static float[] _tlsDenseNodeFlow;

    [ThreadStatic]
    private static int _tlsDenseVisitedCount;

    // Topology version tracking
    private RoadGraph _lastGraph;
    private int _lastTopologyVersion = -1;
    private bool _topologyBuilt;
    private readonly HashSet<int> _protectedSet = new();

    // Compact nodes (dense indices 0..CompactNodeCount-1)
    private int[] _originalToDense = Array.Empty<int>();
    private int[] _denseToOriginal = Array.Empty<int>();

    // CSR adjacency on compact graph (indexed by dense node index u in 0..CompactNodeCount-1)
    private int[] _rowStart = Array.Empty<int>();
    private int[] _adjTargetDense = Array.Empty<int>();
    private float[] _adjCost = Array.Empty<float>();

    // Superedges to constituent original edges mapping (CSR)
    // For directed superedge a in 0..AdjacencyCount-1:
    // original edges are _superEdgeEdges[_superEdgeEdgesStart[a].._superEdgeEdgesStart[a + 1]]
    private int[] _superEdgeEdgesStart = Array.Empty<int>();
    private int[] _superEdgeEdges = Array.Empty<int>();
    private int _superEdgeEdgesTotal;

    // Reusable buffers for topology rebuilding
    private int[] _inDegree = Array.Empty<int>();
    private int[] _outDegree = Array.Empty<int>();
    private bool[] _isCollapsible = Array.Empty<bool>();
    private readonly List<int> _tempSuperEdge = new(32);
    private int[] _accessDenseIndices = Array.Empty<int>();

    /// <summary>Exclusive upper bound of original node ids.</summary>
    public int NodeCapacity { get; private set; }

    /// <summary>Number of nodes in the compact graph (junctions, dead-ends, protected access nodes).</summary>
    public int CompactNodeCount { get; private set; }

    /// <summary>Number of directed superedges (CSR adjacency count) in the compact graph.</summary>
    public int AdjacencyCount { get; private set; }

    /// <summary>True when degree-2 chains were contracted into superedges.</summary>
    public bool IsContracted { get; private set; }

    /// <summary>Returns the dense compact index for an original node ID, or -1 if not in the compact graph.</summary>
    public int GetDenseIndex(int originalNodeId) =>
        originalNodeId >= 0 && originalNodeId < _originalToDense.Length ? _originalToDense[originalNodeId] : -1;

    /// <summary>Returns the original node ID for a dense compact index.</summary>
    public int GetOriginalNodeId(int denseIndex) =>
        denseIndex >= 0 && denseIndex < CompactNodeCount ? _denseToOriginal[denseIndex] : -1;

    /// <summary>
    /// Prepares the snapshot for a search round. When topology or protected nodes are unchanged,
    /// only superedge travel times are updated in O(E) without rebuilding compact graph structure.
    /// </summary>
    public void Rebuild(
        RoadGraph graph,
        float[] carVolumes = null,
        IEnumerable<int> protectedNodes = null,
        bool contractChains = true)
    {
        if (graph == null) return;

        bool protectedChanged = UpdateProtectedSet(protectedNodes);
        bool topologyDirty = !_topologyBuilt ||
                             !ReferenceEquals(_lastGraph, graph) ||
                             _lastTopologyVersion != graph.TopologyVersion ||
                             protectedChanged;

        if (topologyDirty)
        {
            RebuildTopology(graph, contractChains);
            _lastGraph = graph;
            _lastTopologyVersion = graph.TopologyVersion;
            _topologyBuilt = true;
        }

        UpdateCosts(graph, carVolumes);
    }

    /// <summary>
    /// Recomputes travel times of all compact superedges in O(E). Called each MSA round.
    /// </summary>
    public void UpdateCosts(RoadGraph graph, float[] carVolumes = null)
    {
        var edges = graph.Edges;
        for (int a = 0; a < AdjacencyCount; a++)
        {
            float sumCost = 0f;
            int start = _superEdgeEdgesStart[a];
            int end = _superEdgeEdgesStart[a + 1];
            for (int i = start; i < end; i++)
            {
                int eid = _superEdgeEdges[i];
                if (eid < 0 || eid >= edges.Count) continue;
                var edge = edges[eid];
                sumCost += carVolumes == null
                    ? edge.GetTravelTime()
                    : RoadEdge.ComputeTravelTime(edge.Length, edge.FreeFlowSpeed, carVolumes[eid], edge.Capacity);
            }
            _adjCost[a] = sumCost;
        }
    }

    /// <summary>Prepares dense indices for a list of access nodes so skim rows can be written directly without full distance array fills.</summary>
    public void PrepareAccessNodes(int[] accessNodes)
    {
        if (accessNodes == null) return;
        if (_accessDenseIndices.Length < accessNodes.Length)
        {
            _accessDenseIndices = new int[accessNodes.Length];
        }
        for (int i = 0; i < accessNodes.Length; i++)
        {
            int orig = accessNodes[i];
            _accessDenseIndices[i] = orig >= 0 && orig < _originalToDense.Length ? _originalToDense[orig] : -1;
        }
    }

    /// <summary>Writes the travel times from the last search into <paramref name="skim"/> row directly from dense distances.</summary>
    public void WriteSkimRow(TravelTimeSkim skim, int row)
    {
        skim.WriteRowFromDense(row, _tlsDenseDist, _accessDenseIndices);
    }

    /// <summary>
    /// Computes the shortest-path tree from <paramref name="sourceNodeId"/> on the compact graph.
    /// When <paramref name="buffer"/> is provided, it is populated with visited nodes, parent nodes and edges.
    /// When <paramref name="populateAllDistances"/> is true, distances in <paramref name="buffer"/> are populated for original node IDs.
    /// When <paramref name="buffer"/> is null (streaming into skim or direct flow accumulation), buffer writes are completely skipped.
    /// </summary>
    public void ComputeShortestPathTree(int sourceNodeId, ShortestPathTreeBuffer buffer = null, bool populateAllDistances = true)
    {
        if (buffer != null)
        {
            int maxId = NodeCapacity;
            buffer.EnsureCapacity(maxId);
            if (populateAllDistances)
            {
                Array.Fill(buffer.Distances, float.MaxValue, 0, maxId);
            }
            buffer.VisitedCount = 0;
        }

        _tlsDenseVisitedCount = 0;

        if (sourceNodeId < 0 || sourceNodeId >= _originalToDense.Length)
            return;

        int sourceDense = _originalToDense[sourceNodeId];
        if (sourceDense < 0)
            return;

        int compactCount = CompactNodeCount;
        EnsureDenseBuffers(compactCount);

        var pq = _tlsPriorityQueue ??= new PriorityQueue<int, float>(1024);
        pq.Clear();

        float[] denseDist = _tlsDenseDist;
        int[] denseParentSuperEdge = _tlsDenseParentSuperEdge;
        int[] denseParentDenseNode = _tlsDenseParentDenseNode;
        int[] denseVisited = _tlsDenseVisited;

        Array.Fill(denseDist, float.MaxValue, 0, compactCount);

        denseDist[sourceDense] = 0f;
        denseParentSuperEdge[sourceDense] = -1;
        denseParentDenseNode[sourceDense] = -1;
        pq.Enqueue(sourceDense, 0f);

        int visitedCount = 0;
        while (pq.TryDequeue(out int uDense, out float d))
        {
            if (d > denseDist[uDense]) continue;
            denseVisited[visitedCount++] = uDense;

            int end = _rowStart[uDense + 1];
            for (int a = _rowStart[uDense]; a < end; a++)
            {
                int vDense = _adjTargetDense[a];
                float newDist = d + _adjCost[a];
                if (newDist < denseDist[vDense])
                {
                    denseDist[vDense] = newDist;
                    denseParentSuperEdge[vDense] = a;
                    denseParentDenseNode[vDense] = uDense;
                    pq.Enqueue(vDense, newDist);
                }
            }
        }

        _tlsDenseVisitedCount = visitedCount;

        if (buffer != null)
        {
            // Populate caller's buffer
            buffer.VisitedCount = visitedCount;
            for (int i = 0; i < visitedCount; i++)
            {
                int uDense = denseVisited[i];
                int uOrig = _denseToOriginal[uDense];
                buffer.VisitedNodesOrder[i] = uOrig;

                int parentDense = denseParentDenseNode[uDense];
                buffer.ParentNodeIds[uOrig] = parentDense >= 0 ? _denseToOriginal[parentDense] : -1;

                int superEdge = denseParentSuperEdge[uDense];
                if (!IsContracted && superEdge >= 0)
                {
                    // In uncontracted mode, report original edge id for direct parity with RoadGraph
                    int start = _superEdgeEdgesStart[superEdge];
                    buffer.ParentEdgeIds[uOrig] = _superEdgeEdges[start];
                }
                else
                {
                    buffer.ParentEdgeIds[uOrig] = superEdge;
                }

                if (populateAllDistances)
                {
                    buffer.Distances[uOrig] = denseDist[uDense];
                }
            }
        }
    }

    /// <summary>
    /// Accumulates demand from an origin search by reading current thread's dense search state
    /// and unfolding superedges directly into constituent original road edges.
    /// Runs thread-safely per chunk without buffer translation overhead.
    /// </summary>
    public void AccumulateOriginFlows(
        int origin,
        List<(int DestNodeId, float Flow)> destList,
        float[] volumes,
        int edgeCount)
    {
        if (destList == null || destList.Count == 0) return;
        int compactCount = CompactNodeCount;
        EnsureDenseBuffers(compactCount);

        float[] denseNodeFlow = _tlsDenseNodeFlow;
        int origToDenseLen = _originalToDense.Length;

        for (int d = 0; d < destList.Count; d++)
        {
            var (destNodeId, flow) = destList[d];
            if (destNodeId >= 0 && destNodeId < origToDenseLen)
            {
                int destDense = _originalToDense[destNodeId];
                if (destDense >= 0 && destDense < compactCount)
                {
                    denseNodeFlow[destDense] += flow;
                }
            }
        }

        int[] denseVisited = _tlsDenseVisited;
        int[] parentSuperEdges = _tlsDenseParentSuperEdge;
        int[] parentDenseNodes = _tlsDenseParentDenseNode;
        int visitedCount = _tlsDenseVisitedCount;

        int sourceDense = origin >= 0 && origin < origToDenseLen ? _originalToDense[origin] : -1;

        for (int vIdx = visitedCount - 1; vIdx >= 0; vIdx--)
        {
            int currDense = denseVisited[vIdx];
            if (currDense == sourceDense) continue;

            float accumulatedFlow = denseNodeFlow[currDense];
            denseNodeFlow[currDense] = 0f;
            if (accumulatedFlow <= 1e-4f) continue;

            int superEdgeId = parentSuperEdges[currDense];
            int parentDense = parentDenseNodes[currDense];
            if (superEdgeId >= 0 && parentDense >= 0)
            {
                if (IsContracted)
                {
                    int start = _superEdgeEdgesStart[superEdgeId];
                    int end = _superEdgeEdgesStart[superEdgeId + 1];
                    for (int i = start; i < end; i++)
                    {
                        int eid = _superEdgeEdges[i];
                        if (eid >= 0 && eid < edgeCount) volumes[eid] += accumulatedFlow;
                    }
                }
                else
                {
                    int eid = superEdgeId;
                    if (eid >= 0 && eid < edgeCount) volumes[eid] += accumulatedFlow;
                }

                denseNodeFlow[parentDense] += accumulatedFlow;
            }
        }

        if (sourceDense >= 0 && sourceDense < compactCount)
        {
            denseNodeFlow[sourceDense] = 0f;
        }

        for (int d = 0; d < destList.Count; d++)
        {
            int destNodeId = destList[d].DestNodeId;
            if (destNodeId >= 0 && destNodeId < origToDenseLen)
            {
                int destDense = _originalToDense[destNodeId];
                if (destDense >= 0 && destDense < compactCount)
                {
                    denseNodeFlow[destDense] = 0f;
                }
            }
        }
    }

    /// <summary>
    /// Accumulates demand from an origin tree by unfolding superedges into constituent original road edges.
    /// Runs thread-safely per chunk.
    /// </summary>
    public void AccumulateOriginFlows(
        int origin,
        List<(int DestNodeId, float Flow)> destList,
        ShortestPathTreeBuffer tree,
        float[] nodeFlow,
        float[] volumes,
        int edgeCount)
    {
        int nodeCapacity = NodeCapacity;
        for (int d = 0; d < destList.Count; d++)
        {
            var (destNodeId, flow) = destList[d];
            if (destNodeId >= 0 && destNodeId < nodeCapacity)
            {
                nodeFlow[destNodeId] += flow;
            }
        }

        int[] visitedOrder = tree.VisitedNodesOrder;
        int[] parentEdges = tree.ParentEdgeIds;
        int[] parentNodes = tree.ParentNodeIds;

        for (int vIdx = tree.VisitedCount - 1; vIdx >= 0; vIdx--)
        {
            int curr = visitedOrder[vIdx];
            if (curr == origin) continue;

            float accumulatedFlow = nodeFlow[curr];
            nodeFlow[curr] = 0f;
            if (accumulatedFlow <= 1e-4f) continue;

            int superEdgeId = parentEdges[curr];
            int parentNode = parentNodes[curr];
            if (superEdgeId >= 0 && parentNode >= 0 && parentNode < nodeCapacity)
            {
                if (IsContracted)
                {
                    int start = _superEdgeEdgesStart[superEdgeId];
                    int end = _superEdgeEdgesStart[superEdgeId + 1];
                    for (int i = start; i < end; i++)
                    {
                        int eid = _superEdgeEdges[i];
                        if (eid >= 0 && eid < edgeCount) volumes[eid] += accumulatedFlow;
                    }
                }
                else
                {
                    int eid = superEdgeId;
                    if (eid >= 0 && eid < edgeCount) volumes[eid] += accumulatedFlow;
                }

                nodeFlow[parentNode] += accumulatedFlow;
            }
        }

        nodeFlow[origin] = 0f;
        for (int d = 0; d < destList.Count; d++)
        {
            int destNodeId = destList[d].DestNodeId;
            if (destNodeId >= 0 && destNodeId < nodeCapacity) nodeFlow[destNodeId] = 0f;
        }
    }

    private void RebuildTopology(RoadGraph graph, bool contractChains)
    {
        int maxId = graph.GetMaxNodeIndex();
        NodeCapacity = maxId;

        EnsureCapacityForNodes(maxId);
        Array.Clear(_inDegree, 0, maxId);
        Array.Clear(_outDegree, 0, maxId);
        Array.Clear(_isCollapsible, 0, maxId);

        var edges = graph.Edges;
        for (int eid = 0; eid < edges.Count; eid++)
        {
            var edge = edges[eid];
            if (edge.FromId == -1 || edge.ToId == -1) continue;
            if (edge.FromId >= maxId || edge.ToId >= maxId) continue;
            _outDegree[edge.FromId]++;
            _inDegree[edge.ToId]++;
        }

        // Identify collapsible degree-2 chain nodes
        // Only contract if contractChains is true AND protected nodes were specified
        bool canContract = contractChains && _protectedSet.Count > 0;
        IsContracted = canContract;

        if (canContract)
        {
            foreach (var kvp in graph.NodeMap)
            {
                int u = kvp.Key;
                if (u < 0 || u >= maxId) continue;
                if (_protectedSet.Contains(u)) continue;

                if (_inDegree[u] != 2 || _outDegree[u] != 2) continue;
                if (!graph.AdjacencyEdges.TryGetValue(u, out var adj) || adj.Count < 2) continue;

                // Find the two active outgoing edges
                int e1 = -1, e2 = -1;
                for (int i = 0; i < adj.Count; i++)
                {
                    int eid = adj[i];
                    if (eid < 0 || eid >= edges.Count) continue;
                    var edge = edges[eid];
                    if (edge.FromId == -1 || edge.ToId == -1) continue;
                    if (e1 == -1) e1 = eid;
                    else if (e2 == -1) e2 = eid;
                    else { e1 = -1; break; } // More than 2 active edges
                }
                if (e1 == -1 || e2 == -1) continue;

                int v1 = edges[e1].ToId;
                int v2 = edges[e2].ToId;
                if (v1 == u || v2 == u || v1 == v2) continue;

                // Verify both edges are bidirectional pairs
                if (!graph.HasEdge(v1, u) || !graph.HasEdge(v2, u)) continue;

                _isCollapsible[u] = true;
            }
        }

        // Assign dense IDs to all compact (non-collapsible) nodes
        Array.Fill(_originalToDense, -1, 0, maxId);
        int denseIndex = 0;
        foreach (var kvp in graph.NodeMap)
        {
            int u = kvp.Key;
            if (u < 0 || u >= maxId) continue;
            if (_isCollapsible[u]) continue;

            _originalToDense[u] = denseIndex;
            _denseToOriginal[denseIndex] = u;
            denseIndex++;
        }
        CompactNodeCount = denseIndex;

        // Build compact CSR adjacency
        if (_rowStart.Length < CompactNodeCount + 1)
        {
            _rowStart = new int[Math.Max((CompactNodeCount + 1) * 2, 64)];
        }

        int adjacencyCount = 0;
        _superEdgeEdgesTotal = 0;
        EnsureAdjacencyCapacity(1);

        for (int uDense = 0; uDense < CompactNodeCount; uDense++)
        {
            int uOrig = _denseToOriginal[uDense];
            _rowStart[uDense] = adjacencyCount;

            if (!graph.AdjacencyEdges.TryGetValue(uOrig, out var adj)) continue;

            for (int i = 0; i < adj.Count; i++)
            {
                int eid = adj[i];
                if (eid < 0 || eid >= edges.Count) continue;
                var edge = edges[eid];
                if (edge.FromId == -1 || edge.ToId == -1) continue;

                int curr = edge.ToId;
                int prev = uOrig;

                _tempSuperEdge.Clear();
                _tempSuperEdge.Add(eid);

                int hops = 0;
                while (curr >= 0 && curr < maxId && _isCollapsible[curr] && hops++ < maxId)
                {
                    if (!graph.AdjacencyEdges.TryGetValue(curr, out var currAdj)) break;

                    int nextEdge = -1;
                    int nextNode = -1;
                    for (int j = 0; j < currAdj.Count; j++)
                    {
                        int ceid = currAdj[j];
                        if (ceid < 0 || ceid >= edges.Count) continue;
                        var cedge = edges[ceid];
                        if (cedge.FromId == -1 || cedge.ToId == -1) continue;
                        if (cedge.ToId != prev)
                        {
                            nextEdge = ceid;
                            nextNode = cedge.ToId;
                            break;
                        }
                    }

                    if (nextEdge == -1 || nextNode == -1) break;

                    _tempSuperEdge.Add(nextEdge);
                    prev = curr;
                    curr = nextNode;
                }

                if (curr < 0 || curr >= maxId) continue;
                int vDense = _originalToDense[curr];
                if (vDense < 0) continue; // Chain reached unmapped node

                EnsureAdjacencyCapacity(adjacencyCount + 1);
                _adjTargetDense[adjacencyCount] = vDense;

                EnsureSuperEdgeCapacity(_superEdgeEdgesTotal + _tempSuperEdge.Count);
                _superEdgeEdgesStart[adjacencyCount] = _superEdgeEdgesTotal;
                for (int eIdx = 0; eIdx < _tempSuperEdge.Count; eIdx++)
                {
                    _superEdgeEdges[_superEdgeEdgesTotal++] = _tempSuperEdge[eIdx];
                }

                adjacencyCount++;
            }
        }

        _rowStart[CompactNodeCount] = adjacencyCount;
        EnsureAdjacencyCapacity(adjacencyCount + 1);
        EnsureSuperEdgeCapacity(_superEdgeEdgesTotal);
        _superEdgeEdgesStart[adjacencyCount] = _superEdgeEdgesTotal;
        AdjacencyCount = adjacencyCount;
    }

    private bool UpdateProtectedSet(IEnumerable<int> protectedNodes)
    {
        if (protectedNodes == null)
        {
            if (_protectedSet.Count == 0) return false;
            _protectedSet.Clear();
            return true;
        }

        int oldCount = _protectedSet.Count;
        int newCount = 0;
        bool changed = false;

        var tempSet = new HashSet<int>();
        foreach (int node in protectedNodes)
        {
            if (node >= 0)
            {
                tempSet.Add(node);
                newCount++;
            }
        }

        if (tempSet.Count != _protectedSet.Count)
        {
            changed = true;
        }
        else
        {
            foreach (int node in tempSet)
            {
                if (!_protectedSet.Contains(node))
                {
                    changed = true;
                    break;
                }
            }
        }

        if (changed)
        {
            _protectedSet.Clear();
            foreach (int node in tempSet) _protectedSet.Add(node);
        }

        return changed;
    }

    private void EnsureCapacityForNodes(int maxId)
    {
        if (_originalToDense.Length < maxId)
        {
            int size = Math.Max(maxId * 2, 64);
            _originalToDense = new int[size];
            _denseToOriginal = new int[size];
            _inDegree = new int[size];
            _outDegree = new int[size];
            _isCollapsible = new bool[size];
        }
    }

    private void EnsureAdjacencyCapacity(int required)
    {
        if (_adjTargetDense.Length >= required && _superEdgeEdgesStart.Length >= required + 1) return;
        int size = Math.Max(required * 2, 256);
        Array.Resize(ref _adjTargetDense, size);
        Array.Resize(ref _adjCost, size);
        Array.Resize(ref _superEdgeEdgesStart, size + 1);
    }

    private void EnsureSuperEdgeCapacity(int required)
    {
        if (_superEdgeEdges.Length >= required) return;
        int size = Math.Max(required * 2, 256);
        Array.Resize(ref _superEdgeEdges, size);
    }

    private static void EnsureDenseBuffers(int capacity)
    {
        if (_tlsDenseDist == null || _tlsDenseDist.Length < capacity)
        {
            int size = Math.Max(capacity * 2, 256);
            _tlsDenseDist = new float[size];
            _tlsDenseParentSuperEdge = new int[size];
            _tlsDenseParentDenseNode = new int[size];
            _tlsDenseVisited = new int[size];
            _tlsDenseNodeFlow = new float[size];
        }
    }
}
