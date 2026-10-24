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
    private float[] _msaNodeFlowAccumulator = Array.Empty<float>();

    // Flow state of the running assignment, published to RoadEdge.CurrentVolume/PedestrianVolume at the end
    private float[] _carVolumes = Array.Empty<float>();
    private float[] _pedestrianVolumes = Array.Empty<float>();
    private int _pendingEdgeCount;

    /// <summary>
    /// True after a deferred assignment (<c>deferPublish: true</c>) until <see cref="PublishPendingVolumes"/>.
    /// </summary>
    public bool HasPendingVolumes { get; private set; }
    private readonly List<int> _msaActiveOrigins = new(256);
    private readonly RoutingSnapshot _msaRouting = new RoutingSnapshot();
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
    /// Equilibrium traffic assignment (User Equilibrium) using Method of Successive Averages (MSA)
    /// with O(V) leaf-to-root flow accumulation on predecessor trees and zero allocations per cycle.
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

        EnsureMsaBuffersCapacity(k, edgeCount, maxNodeId);
        Array.Clear(_carVolumes, 0, edgeCount);
        Array.Clear(_pedestrianVolumes, 0, edgeCount);

        if (k == 0 || edgeCount == 0 || maxNodeId <= 0)
        {
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

        if (_msaActiveOrigins.Count == 0)
        {
            CommitVolumes(roadGraph, edgeCount, deferPublish);
            if (!deferPublish) matrix.UpdateAccessibilityScores(tazManager, roadGraph);
            return;
        }

        int iterations = Math.Clamp(msaIterations, 1, 8);

        // 4. MSA loop with O(V) Leaf-to-Root flow accumulation
        for (int iter = 1; iter <= iterations; iter++)
        {
            if (iter > 1)
            {
                // One compact copy of the graph with travel times under V^(n-1), shared by all origins
                _msaRouting.Rebuild(roadGraph, _carVolumes);
                Parallel.For(0, _msaActiveOrigins.Count, idx =>
                {
                    int u = _msaActiveOrigins[idx];
                    var buffer = matrix.CachedTreeBuffers.GetOrAdd(u, _ => new ShortestPathTreeBuffer());
                    _msaRouting.ComputeShortestPathTree(u, buffer);
                });
            }

            // Copy base internal traffic to auxiliary flow vector Y^(n)
            Array.Copy(_msaBaseInternalCarVolumes, 0, _msaAuxCarVolumes, 0, edgeCount);

            // Accumulate flows leaf-to-root for each active origin u in O(V) time
            for (int idx = 0; idx < _msaActiveOrigins.Count; idx++)
            {
                int u = _msaActiveOrigins[idx];
                if (!matrix.CachedTreeBuffers.TryGetValue(u, out var tree) || tree.VisitedCount == 0)
                    continue;

                var destList = _msaDemandsByOrigin[u];
                Array.Clear(_msaNodeFlowAccumulator, 0, maxNodeId);

                // Seed target nodes with demand flows
                for (int d = 0; d < destList.Count; d++)
                {
                    var (destNodeId, flow) = destList[d];
                    if (destNodeId >= 0 && destNodeId < maxNodeId)
                    {
                        _msaNodeFlowAccumulator[destNodeId] += flow;
                    }
                }

                // Traverse nodes in reverse topological order (from farthest leaves back to root u)
                int[] visitedOrder = tree.VisitedNodesOrder;
                int[] parentEdges = tree.ParentEdgeIds;
                int[] parentNodes = tree.ParentNodeIds;

                for (int vIdx = tree.VisitedCount - 1; vIdx >= 0; vIdx--)
                {
                    int curr = visitedOrder[vIdx];
                    if (curr == u) continue;

                    float accumulatedFlow = _msaNodeFlowAccumulator[curr];
                    if (accumulatedFlow <= 1e-4f) continue;

                    int edgeId = parentEdges[curr];
                    int parentNode = parentNodes[curr];

                    if (edgeId >= 0 && edgeId < edgeCount && parentNode >= 0 && parentNode < maxNodeId)
                    {
                        _msaAuxCarVolumes[edgeId] += accumulatedFlow;
                        _msaNodeFlowAccumulator[parentNode] += accumulatedFlow;
                    }
                }
            }

            // MSA blending formula: V^(n) = (1 - 1/n) * V^(n-1) + (1/n) * Y^(n)
            float stepSize = 1.0f / iter;
            float keepWeight = 1.0f - stepSize;

            for (int e = 0; e < edgeCount; e++)
            {
                _carVolumes[e] = keepWeight * _carVolumes[e] + stepSize * _msaAuxCarVolumes[e];
            }
        }

        CommitVolumes(roadGraph, edgeCount, deferPublish);

        // 5. Final synchronization: update shortest trees, _travelTimes, and LUTI AccessibilityScores using equilibrium volumes V^(n)
        if (iterations > 1)
        {
            matrix.UpdateEquilibriumTreesAndTimes(tazManager, roadGraph, deferPublish ? _carVolumes : null);
        }
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
                _carVolumes[edgeId] += carPerEdge;
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

    private void EnsureMsaBuffersCapacity(int k, int edgeCount, int maxNodeId)
    {
        if (_msaBaseInternalCarVolumes.Length < edgeCount)
        {
            int size = Math.Max(edgeCount * 2, 256);
            _msaBaseInternalCarVolumes = new float[size];
            _msaAuxCarVolumes = new float[size];
            _carVolumes = new float[size];
            _pedestrianVolumes = new float[size];
        }
        if (_msaTransitBoardings.Length < k)
        {
            int size = Math.Max(k * 2, 128);
            _msaTransitBoardings = new float[size];
            _msaTransitAlightings = new float[size];
        }
        if (_msaNodeFlowAccumulator.Length < maxNodeId)
        {
            _msaNodeFlowAccumulator = new float[Math.Max(maxNodeId * 2, 256)];
        }
    }
}
