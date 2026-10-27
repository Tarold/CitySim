using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;

namespace CitySim.Simulation;

public class TrafficEngine
{
    public float GetMaxVolume(RoadGraph graph)
    {
        float maxVol = 0f;
        for (int i = 0; i < graph.Edges.Count; i++)
        {
            var edge = graph.Edges[i];
            if (edge.FromId == -1 || edge.ToId == -1) continue;
            float v = edge.CurrentVolume;
            if (v > maxVol) maxVol = v;
        }
        return maxVol;
    }

    public float GetMaxPedestrianVolume(RoadGraph graph)
    {
        float maxVol = 0f;
        for (int i = 0; i < graph.Edges.Count; i++)
        {
            var edge = graph.Edges[i];
            if (edge.FromId == -1 || edge.ToId == -1) continue;
            float v = edge.PedestrianVolume;
            if (v > maxVol) maxVol = v;
        }
        return maxVol;
    }

    public float GetAverageCongestion(RoadGraph graph)
    {
        float totalRatio = 0f;
        int count = 0;
        for (int i = 0; i < graph.Edges.Count; i++)
        {
            var edge = graph.Edges[i];
            if (edge.FromId == -1 || edge.ToId == -1) continue;
            if (edge.CurrentVolume > 0f)
            {
                totalRatio += edge.GetCongestionRatio();
                count++;
            }
        }
        return count > 0 ? (totalRatio / count) : 0f;
    }

    public float GetAveragePedestrianCongestion(RoadGraph graph)
    {
        float totalRatio = 0f;
        int count = 0;
        for (int i = 0; i < graph.Edges.Count; i++)
        {
            var edge = graph.Edges[i];
            if (edge.FromId == -1 || edge.ToId == -1) continue;
            if (edge.PedestrianVolume > 0f)
            {
                totalRatio += edge.GetPedestrianCongestionRatio();
                count++;
            }
        }
        return count > 0 ? (totalRatio / count) : 0f;
    }

    // Cached buffers for TrafficEngine (Zero allocations during simulation ticks)
    private float[] _msaBaseInternalCarVolumes = Array.Empty<float>();
    private float[] _msaAuxCarVolumes = Array.Empty<float>();
    private float[] _msaTransitBoardings = Array.Empty<float>();
    private float[] _msaTransitAlightings = Array.Empty<float>();

    // Flow state of the running assignment, published to RoadEdge.CurrentVolume/PedestrianVolume at the end
    private float[] _carVolumes = Array.Empty<float>();
    private float[] _pedestrianVolumes = Array.Empty<float>();
    private int _pendingEdgeCount;

    /// <summary>
    /// True after a deferred assignment (<c>deferPublish: true</c>) until <see cref="PublishPendingVolumes"/>.
    /// </summary>
    public bool HasPendingVolumes { get; private set; }

    internal float[] PendingCarVolumes => _carVolumes;
    private readonly List<int> _msaActiveOrigins = new(256);
    private readonly RoutingSnapshot _msaRouting = new RoutingSnapshot();

    /// <summary>MSA iterations per macro cycle once a warm equilibrium exists (day-to-day adjustment).</summary>
    public const int WarmStartIterationsPerCycle = 1;

    /// <summary>
    /// Lower bound of the MSA step size in warm cycles, so that demand changes during the day reach the road
    /// volumes within a few cycles instead of being averaged away.
    /// </summary>
    public const float MinWarmStepSize = 0.25f;

    /// <summary>
    /// Fixed number of origin chunks per search round. Each chunk accumulates its origins' flows in its own
    /// array and the chunks are summed in index order, so volumes do not depend on thread scheduling or core count.
    /// </summary>
    public const int AccumulationChunks = 16;

    /// <summary>Edges summed per parallel task when merging chunk volumes.</summary>
    private const int MergeBlockSize = 4096;

    /// <summary>Flow below which a node's accumulated demand is not propagated further.</summary>
    private const float MinPropagatedFlow = 1e-4f;

    [ThreadStatic]
    private static ShortestPathTreeBuffer _tlsTree;

    [ThreadStatic]
    private static float[] _tlsNodeFlow;

    private float[][] _chunkVolumes = Array.Empty<float[]>();
    private int _iterationCount;
    private RoadGraph _warmGraph;
    private int _warmTopologyVersion = -1;
    private int _warmEdgeCount = -1;

    /// <summary>
    /// Continue the previous equilibrium across calls (game): when the road topology is unchanged, each call
    /// runs <see cref="WarmStartIterationsPerCycle"/> MSA iterations starting from the last volumes. False
    /// (default) starts every call from scratch with the requested number of iterations.
    /// </summary>
    public bool WarmStart { get; set; }

    /// <summary>
    /// Car travel times between TAZ access nodes from the last search round; feed it to the next
    /// <see cref="OptimizedODMatrix.Recalculate"/> to skip its own search round.
    /// </summary>
    public TravelTimeSkim Skim { get; } = new TravelTimeSkim();

    /// <summary>MSA iterations accumulated in the current equilibrium (reset on cold start).</summary>
    public int EquilibriumIterations => _iterationCount;

    /// <summary>True when the previous call's volumes are a valid starting point for <paramref name="graph"/>.</summary>
    public bool HasWarmEquilibrium(RoadGraph graph) =>
        _iterationCount > 0 &&
        ReferenceEquals(_warmGraph, graph) &&
        graph.TopologyVersion == _warmTopologyVersion &&
        graph.Edges.Count == _warmEdgeCount;
    private readonly Dictionary<int, List<(int DestNodeId, float Flow)>> _msaDemandsByOrigin = new(256);

    /// <summary>
    /// Default entry point (backward-compatible with existing callers and tests).
    /// Executes Method of Successive Averages (MSA) with 4 iterations by default.
    /// </summary>
    public void AssignFlowsFromTaz(
        TazManager tazManager,
        OptimizedODMatrix matrix,
        RoadGraph roadGraph)
    {
        AssignFlowsFromTazMsa(tazManager, matrix, roadGraph, msaIterations: 4);
    }

    /// <summary>
    /// Equilibrium traffic assignment (User Equilibrium) using the Method of Successive Averages (MSA) with
    /// O(V) leaf-to-root flow accumulation. Each search round streams over the TAZ access nodes: a tree is
    /// computed, writes its <see cref="Skim"/> row, adds its origin's flows to a per-chunk volume array and is
    /// discarded, so no trees are kept. With <see cref="WarmStart"/> the equilibrium continues across calls.
    /// </summary>
    public void AssignFlowsFromTazMsa(
        TazManager tazManager,
        OptimizedODMatrix matrix,
        RoadGraph roadGraph,
        int msaIterations = 4,
        bool deferPublish = false)
    {
        if (tazManager == null || matrix == null || roadGraph == null) return;

        var tazList = tazManager.ActiveZones;
        int k = tazList.Count;
        int edgeCount = roadGraph.Edges.Count;
        int maxNodeId = roadGraph.GetMaxNodeIndex();

        bool warm = WarmStart && HasWarmEquilibrium(roadGraph);
        if (EnsureMsaBuffersCapacity(k, edgeCount, maxNodeId))
        {
            warm = false; // car volume arrays were reallocated
        }
        if (!warm)
        {
            _iterationCount = 0;
        }
        Array.Clear(_pedestrianVolumes, 0, edgeCount);

        if (k == 0 || edgeCount == 0 || maxNodeId <= 0)
        {
            Array.Clear(_carVolumes, 0, edgeCount);
            _iterationCount = 0;
            CommitVolumes(roadGraph, edgeCount, deferPublish);
            return;
        }

        Array.Clear(_msaBaseInternalCarVolumes, 0, edgeCount);
        Array.Clear(_msaTransitBoardings, 0, k);
        Array.Clear(_msaTransitAlightings, 0, k);

        // 1. Aggregate transit access and egress pedestrian trips per TAZ
        for (int i = 0; i < k; i++)
        {
            for (int j = 0; j < k; j++)
            {
                float t = matrix.GetTransitTrips(i, j);
                if (t <= 0f) continue;
                _msaTransitBoardings[i] += t;
                _msaTransitAlightings[j] += t;
            }
        }

        // 2. Assign internal trips (i == j) and transit boarding/alighting pedestrian flows
        for (int i = 0; i < k; i++)
        {
            AddLocalTazFlow(
                tazList[i],
                matrix.GetCarTrips(i, i),
                matrix.GetWalkTrips(i, i) + _msaTransitBoardings[i] + _msaTransitAlightings[i],
                roadGraph,
                edgeCount);
        }

        // 3. Group inter-TAZ demand by origin node (Origin-Based Grouping)
        _msaActiveOrigins.Clear();
        foreach (var list in _msaDemandsByOrigin.Values) list.Clear();

        for (int i = 0; i < k; i++)
        {
            var origTaz = tazList[i];
            int u = origTaz.PrimaryRoadNodeId;
            if (u < 0) continue;

            for (int j = 0; j < k; j++)
            {
                if (i == j) continue;

                var destTaz = tazList[j];
                int v = destTaz.PrimaryRoadNodeId;
                if (v < 0) continue;

                float carFlow = matrix.GetCarTrips(i, j);
                float walkFlow = matrix.GetWalkTrips(i, j);

                // Preserve local flow when adjacent TAZs (i != j) share the same primary node (u == v)
                if (u == v)
                {
                    if (carFlow >= 0.05f || walkFlow >= 0.05f)
                    {
                        AddLocalTazFlow(origTaz, carFlow * 0.5f, walkFlow * 0.5f, roadGraph, edgeCount);
                        AddLocalTazFlow(destTaz, carFlow * 0.5f, walkFlow * 0.5f, roadGraph, edgeCount);
                    }
                    continue;
                }

                if (carFlow >= 0.05f)
                {
                    if (!_msaDemandsByOrigin.TryGetValue(u, out var destList))
                    {
                        destList = new List<(int, float)>(32);
                        _msaDemandsByOrigin[u] = destList;
                    }
                    if (destList.Count == 0) _msaActiveOrigins.Add(u);
                    destList.Add((v, carFlow));
                }

                // Inter-TAZ pedestrians use native walking path routing with caching
                if (walkFlow >= 0.05f && origTaz.Centroid.DistanceTo(destTaz.Centroid) <= 600f)
                {
                    if (!roadGraph.WalkingPathCache.TryGetValue((u, v), out var walkEdges))
                    {
                        walkEdges = roadGraph.GetShortestWalkingPath(u, v, _pedestrianVolumes);
                        if (walkEdges != null)
                        {
                            roadGraph.WalkingPathCache[(u, v)] = walkEdges;
                        }
                    }

                    if (walkEdges != null)
                    {
                        for (int w = 0; w < walkEdges.Count; w++)
                        {
                            int eid = walkEdges[w];
                            if (eid >= 0 && eid < edgeCount)
                                _pedestrianVolumes[eid] += walkFlow;
                        }
                    }
                }
            }
        }

        int[] accessNodes = TravelTimeSkim.CollectAccessNodes(tazManager);

        if (_msaActiveOrigins.Count == 0)
        {
            // Only local traffic: no equilibrium to iterate, but refresh the skim for the next cycle
            Array.Copy(_msaBaseInternalCarVolumes, _carVolumes, edgeCount);
            _iterationCount = 0;
            _msaRouting.Rebuild(roadGraph, _carVolumes);
            Skim.Compute(roadGraph, _msaRouting, accessNodes);
            FinishAssignment(tazManager, matrix, roadGraph, edgeCount, deferPublish);
            return;
        }

        int iterations = warm ? WarmStartIterationsPerCycle : Math.Clamp(msaIterations, 1, 8);

        // 4. MSA loop: V^(n) = (1 - a) * V^(n-1) + a * Y^(n), a = 1/n (floored at MinWarmStepSize when warm)
        for (int iter = 0; iter < iterations; iter++)
        {
            // Cold start routes the first all-or-nothing round on the published volumes (previous cycle)
            bool usePublishedVolumes = !warm && iter == 0;
            _msaRouting.Rebuild(roadGraph, usePublishedVolumes ? null : _carVolumes);
            RunAssignmentRound(roadGraph, accessNodes, edgeCount);

            _iterationCount++;
            float stepSize = 1.0f / _iterationCount;
            if (warm) stepSize = Math.Max(stepSize, MinWarmStepSize);
            float keepWeight = 1.0f - stepSize;

            for (int e = 0; e < edgeCount; e++)
            {
                _carVolumes[e] = keepWeight * _carVolumes[e] + stepSize * _msaAuxCarVolumes[e];
            }
        }

        FinishAssignment(tazManager, matrix, roadGraph, edgeCount, deferPublish);
    }

    /// <summary>
    /// One streaming search round: for every access node compute the shortest-path tree on the current
    /// <see cref="_msaRouting"/>, write its skim row and accumulate its origin's demand into the chunk's
    /// volume array; then sum the chunks in index order into <see cref="_msaAuxCarVolumes"/> (Y^(n)).
    /// </summary>
    private void RunAssignmentRound(RoadGraph roadGraph, int[] accessNodes, int edgeCount)
    {
        int nodeCapacity = _msaRouting.NodeCapacity;
        int originCount = accessNodes.Length;
        Skim.Reset(accessNodes, roadGraph, nodeCapacity);

        Parallel.For(0, AccumulationChunks, chunk =>
        {
            var volumes = _chunkVolumes[chunk];
            var tree = _tlsTree ??= new ShortestPathTreeBuffer();
            var nodeFlow = _tlsNodeFlow;
            if (nodeFlow == null || nodeFlow.Length < nodeCapacity)
            {
                nodeFlow = _tlsNodeFlow = new float[Math.Max(nodeCapacity * 2, 256)];
            }

            int start = (int)((long)originCount * chunk / AccumulationChunks);
            int end = (int)((long)originCount * (chunk + 1) / AccumulationChunks);
            for (int row = start; row < end; row++)
            {
                int origin = accessNodes[row];
                _msaRouting.ComputeShortestPathTree(origin, tree);
                Skim.WriteRow(row, tree.Distances);

                if (_msaDemandsByOrigin.TryGetValue(origin, out var destList) && destList.Count > 0)
                {
                    AccumulateOriginFlows(origin, destList, tree, nodeFlow, volumes, edgeCount, nodeCapacity);
                }
            }
        });

        int blocks = (edgeCount + MergeBlockSize - 1) / MergeBlockSize;
        Parallel.For(0, blocks, block =>
        {
            int start = block * MergeBlockSize;
            int end = Math.Min(start + MergeBlockSize, edgeCount);
            for (int e = start; e < end; e++)
            {
                float sum = _msaBaseInternalCarVolumes[e];
                for (int chunk = 0; chunk < AccumulationChunks; chunk++)
                {
                    float[] volumes = _chunkVolumes[chunk];
                    sum += volumes[e];
                    volumes[e] = 0f;
                }
                _msaAuxCarVolumes[e] = sum;
            }
        });
    }

    /// <summary>
    /// Leaf-to-root accumulation of one origin's demand along its shortest-path tree. Uses and resets the
    /// touched entries of <paramref name="nodeFlow"/> so the scratch array stays zero between origins.
    /// </summary>
    private static void AccumulateOriginFlows(
        int origin,
        List<(int DestNodeId, float Flow)> destList,
        ShortestPathTreeBuffer tree,
        float[] nodeFlow,
        float[] volumes,
        int edgeCount,
        int nodeCapacity)
    {
        for (int d = 0; d < destList.Count; d++)
        {
            var (destNodeId, flow) = destList[d];
            if (destNodeId >= 0 && destNodeId < nodeCapacity)
            {
                nodeFlow[destNodeId] += flow;
            }
        }

        // Traverse nodes in reverse settle order (from farthest leaves back to the origin)
        int[] visitedOrder = tree.VisitedNodesOrder;
        int[] parentEdges = tree.ParentEdgeIds;
        int[] parentNodes = tree.ParentNodeIds;

        for (int vIdx = tree.VisitedCount - 1; vIdx >= 0; vIdx--)
        {
            int curr = visitedOrder[vIdx];
            if (curr == origin) continue;

            float accumulatedFlow = nodeFlow[curr];
            nodeFlow[curr] = 0f;
            if (accumulatedFlow <= MinPropagatedFlow) continue;

            int edgeId = parentEdges[curr];
            int parentNode = parentNodes[curr];
            if (edgeId >= 0 && edgeId < edgeCount && parentNode >= 0 && parentNode < nodeCapacity)
            {
                volumes[edgeId] += accumulatedFlow;
                nodeFlow[parentNode] += accumulatedFlow;
            }
        }

        // Reset the root and unreachable destinations (not in the settle order)
        nodeFlow[origin] = 0f;
        for (int d = 0; d < destList.Count; d++)
        {
            int destNodeId = destList[d].DestNodeId;
            if (destNodeId >= 0 && destNodeId < nodeCapacity) nodeFlow[destNodeId] = 0f;
        }
    }

    /// <summary>Publishes or defers the volumes, stores the warm state and refreshes OD travel times.</summary>
    private void FinishAssignment(TazManager tazManager, OptimizedODMatrix matrix, RoadGraph roadGraph, int edgeCount, bool deferPublish)
    {
        CommitVolumes(roadGraph, edgeCount, deferPublish);

        _warmGraph = roadGraph;
        _warmTopologyVersion = roadGraph.TopologyVersion;
        _warmEdgeCount = edgeCount;

        // Travel times of the last round become the OD matrix's times (accessibility, analytics)
        matrix.ApplySkim(tazManager, Skim);

        // Accessibility reads congestion from the published edge volumes; deferred callers run it after publishing
        if (!deferPublish)
        {
            matrix.UpdateAccessibilityScores(tazManager, roadGraph);
        }
    }

    /// <summary>
    /// Writes the volumes of the last deferred assignment to the road edges. Main thread only.
    /// </summary>
    public void PublishPendingVolumes(RoadGraph roadGraph)
    {
        if (!HasPendingVolumes || roadGraph == null) return;

        PublishVolumes(roadGraph, Math.Min(_pendingEdgeCount, roadGraph.Edges.Count));
        HasPendingVolumes = false;
    }

    private void CommitVolumes(RoadGraph roadGraph, int edgeCount, bool deferPublish)
    {
        if (deferPublish)
        {
            _pendingEdgeCount = edgeCount;
            HasPendingVolumes = true;
        }
        else
        {
            PublishVolumes(roadGraph, edgeCount);
        }
    }

    private void AddLocalTazFlow(
        TrafficAnalysisZone taz,
        float carFlow,
        float walkFlow,
        RoadGraph roadGraph,
        int edgeCount)
    {
        var internalEdges = taz.InternalEdgeIds.Count > 0
            ? taz.InternalEdgeIds
            : (roadGraph.AdjacencyEdges.TryGetValue(taz.PrimaryRoadNodeId, out var inc) ? inc : null);

        if (internalEdges == null || internalEdges.Count == 0) return;

        float carPerEdge = carFlow / internalEdges.Count;
        float pedPerEdge = walkFlow / internalEdges.Count;

        for (int i = 0; i < internalEdges.Count; i++)
        {
            int edgeId = internalEdges[i];
            if (edgeId >= 0 && edgeId < edgeCount)
            {
                _msaBaseInternalCarVolumes[edgeId] += carPerEdge;
                _pedestrianVolumes[edgeId] += pedPerEdge;
            }
        }
    }

    /// <summary>Copies the assignment's flow arrays into the road edges (single publish point).</summary>
    private void PublishVolumes(RoadGraph roadGraph, int edgeCount)
    {
        var edges = roadGraph.Edges;
        for (int e = 0; e < edgeCount; e++)
        {
            edges[e].CurrentVolume = _carVolumes[e];
            edges[e].PedestrianVolume = _pedestrianVolumes[e];
        }
    }

    /// <summary>Grows the per-cycle buffers; returns true when the car volume array was reallocated (warm state lost).</summary>
    private bool EnsureMsaBuffersCapacity(int k, int edgeCount, int maxNodeId)
    {
        bool reallocated = false;
        if (_msaBaseInternalCarVolumes.Length < edgeCount)
        {
            int size = Math.Max(edgeCount * 2, 256);
            _msaBaseInternalCarVolumes = new float[size];
            _msaAuxCarVolumes = new float[size];
            _carVolumes = new float[size];
            _pedestrianVolumes = new float[size];
            reallocated = true;
        }
        if (_chunkVolumes.Length != AccumulationChunks || _chunkVolumes[0].Length < edgeCount)
        {
            int size = Math.Max(edgeCount * 2, 256);
            _chunkVolumes = new float[AccumulationChunks][];
            for (int c = 0; c < AccumulationChunks; c++) _chunkVolumes[c] = new float[size];
        }
        if (_msaTransitBoardings.Length < k)
        {
            int size = Math.Max(k * 2, 128);
            _msaTransitBoardings = new float[size];
            _msaTransitAlightings = new float[size];
        }
        return reallocated;
    }
}
