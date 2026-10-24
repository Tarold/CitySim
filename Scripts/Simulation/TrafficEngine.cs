using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;

namespace CitySim.Simulation;

public class TrafficEngine
{
    public void AssignFlows(ODMatrix od, RoadGraph graph, CityGrid grid = null, TransitManager transit = null)
    {
        graph.ClearVolumes();

        // 1. Dynamic continuous assignment
        if (od != null && od.DynamicTrips != null && od.DynamicTrips.Count > 0)
        {
            for (int t = 0; t < od.DynamicTrips.Count; t++)
            {
                var trip = od.DynamicTrips[t];

                // A. Assign Private Car Trips along road shortest paths
                if (trip.CarTrips > 0.001f)
                {
                    if (trip.OriginAccessNodeId != trip.DestAccessNodeId && trip.OriginAccessNodeId != -1 && trip.DestAccessNodeId != -1)
                    {
                        if (graph.PathCache.TryGetValue((trip.OriginAccessNodeId, trip.DestAccessNodeId), out var carPath) ||
                            (carPath = graph.GetShortestPath(trip.OriginAccessNodeId, trip.DestAccessNodeId)) != null)
                        {
                            for (int p = 0; p < carPath.Count; p++)
                            {
                                int eid = carPath[p];
                                if (eid >= 0 && eid < graph.Edges.Count)
                                    graph.Edges[eid].CurrentVolume += trip.CarTrips;
                            }
                        }
                    }
                    else if (trip.EdgePath != null && trip.EdgePath.Count > 0)
                    {
                        for (int p = 0; p < trip.EdgePath.Count; p++)
                        {
                            int eid = trip.EdgePath[p];
                            if (eid >= 0 && eid < graph.Edges.Count)
                                graph.Edges[eid].CurrentVolume += trip.CarTrips;
                        }
                    }
                }

                // B. Assign Pure Walk Trips along pedestrian shortest paths
                if (trip.WalkTrips > 0.001f)
                {
                    if (trip.OriginAccessNodeId != trip.DestAccessNodeId && trip.OriginAccessNodeId != -1 && trip.DestAccessNodeId != -1)
                    {
                        if (graph.WalkingPathCache.TryGetValue((trip.OriginAccessNodeId, trip.DestAccessNodeId), out var walkPath) ||
                            graph.PathCache.TryGetValue((trip.OriginAccessNodeId, trip.DestAccessNodeId), out walkPath) ||
                            (walkPath = graph.GetShortestWalkingPath(trip.OriginAccessNodeId, trip.DestAccessNodeId)) != null)
                        {
                            for (int p = 0; p < walkPath.Count; p++)
                            {
                                int eid = walkPath[p];
                                if (eid >= 0 && eid < graph.Edges.Count)
                                    graph.Edges[eid].PedestrianVolume += trip.WalkTrips;
                            }
                        }
                    }
                    else if (trip.EdgePath != null && trip.EdgePath.Count > 0)
                    {
                        for (int p = 0; p < trip.EdgePath.Count; p++)
                        {
                            int eid = trip.EdgePath[p];
                            if (eid >= 0 && eid < graph.Edges.Count)
                                graph.Edges[eid].PedestrianVolume += trip.WalkTrips;
                        }
                    }
                }

                // C. Assign Multimodal Transit Trips: Walking access and egress legs to sidewalks
                if (trip.TransitTrips > 0.001f)
                {
                    if (transit != null && transit.Stops.Count > 0)
                    {
                        int s1 = -1;
                        float bestDist1 = float.MaxValue;
                        int s2 = -1;
                        float bestDist2 = float.MaxValue;

                        foreach (var stop in transit.Stops.Values)
                        {
                            var sNode = graph.GetNode(stop.NodeId);
                            Vector2 stopPos = sNode?.WorldPosition ?? (grid != null ? grid.GetWorldCenter(stop.ZoneId) : Vector2.Zero);

                            float d1 = trip.OriginPos.DistanceTo(stopPos);
                            if (d1 < bestDist1) { bestDist1 = d1; s1 = stop.NodeId; }

                            float d2 = trip.DestPos.DistanceTo(stopPos);
                            if (d2 < bestDist2) { bestDist2 = d2; s2 = stop.NodeId; }
                        }

                        // Access walk leg: origin access node -> boarding stop s1
                        if (s1 != -1 && s1 != trip.OriginAccessNodeId && trip.OriginAccessNodeId != -1)
                        {
                            if (graph.WalkingPathCache.TryGetValue((trip.OriginAccessNodeId, s1), out var accessPath) ||
                                (accessPath = graph.GetShortestWalkingPath(trip.OriginAccessNodeId, s1)) != null)
                            {
                                for (int p = 0; p < accessPath.Count; p++)
                                {
                                    int eid = accessPath[p];
                                    if (eid >= 0 && eid < graph.Edges.Count)
                                        graph.Edges[eid].PedestrianVolume += trip.TransitTrips;
                                }
                            }
                        }

                        // Egress walk leg: alighting stop s2 -> destination access node
                        if (s2 != -1 && s2 != trip.DestAccessNodeId && trip.DestAccessNodeId != -1)
                        {
                            if (graph.WalkingPathCache.TryGetValue((s2, trip.DestAccessNodeId), out var egressPath) ||
                                (egressPath = graph.GetShortestWalkingPath(s2, trip.DestAccessNodeId)) != null)
                            {
                                for (int p = 0; p < egressPath.Count; p++)
                                {
                                    int eid = egressPath[p];
                                    if (eid >= 0 && eid < graph.Edges.Count)
                                        graph.Edges[eid].PedestrianVolume += trip.TransitTrips;
                                }
                            }
                        }
                    }
                    else if (trip.EdgePath != null && trip.EdgePath.Count > 0)
                    {
                        int eid0 = trip.EdgePath[0];
                        if (eid0 >= 0 && eid0 < graph.Edges.Count)
                            graph.Edges[eid0].PedestrianVolume += trip.TransitTrips;

                        if (trip.EdgePath.Count > 1)
                        {
                            int eidLast = trip.EdgePath[trip.EdgePath.Count - 1];
                            if (eidLast >= 0 && eidLast < graph.Edges.Count)
                                graph.Edges[eidLast].PedestrianVolume += trip.TransitTrips;
                        }
                    }
                }
            }
            return;
        }

        // 2. Legacy zone-matrix assignment fallback
        if (grid != null && od != null && od.CarTrips != null)
        {
            var activeIds = grid.ActiveZoneIds;
            int activeCount = activeIds.Count;

            for (int a = 0; a < activeCount; a++)
            {
                int i = activeIds[a];
                var zoneI = grid.GetZone(i);

                for (int b = 0; b < activeCount; b++)
                {
                    if (a == b) continue;
                    int j = activeIds[b];
                    var zoneJ = grid.GetZone(j);

                    // 1. Assign Private Car Trips along road shortest paths
                    float carVolume = od.CarTrips[i, j];
                    if (carVolume > 0.01f)
                    {
                        if (graph.PathCache.TryGetValue((i, j), out var carPath) || (carPath = graph.GetShortestPath(i, j)) != null)
                        {
                            for (int p = 0; p < carPath.Count; p++)
                            {
                                graph.Edges[carPath[p]].CurrentVolume += carVolume;
                            }
                        }
                    }

                    // 2. Assign Pure Walk Trips along pedestrian shortest paths
                    float walkVolume = od.WalkTrips != null ? od.WalkTrips[i, j] : 0f;
                    if (walkVolume > 0.01f)
                    {
                        if (graph.WalkingPathCache.TryGetValue((i, j), out var walkPath) || 
                            graph.PathCache.TryGetValue((i, j), out walkPath) || 
                            (walkPath = graph.GetShortestWalkingPath(i, j)) != null)
                        {
                            for (int p = 0; p < walkPath.Count; p++)
                            {
                                graph.Edges[walkPath[p]].PedestrianVolume += walkVolume;
                            }
                        }
                    }

                    // 3. Assign Multimodal Transit Trips: Walking access and egress legs to sidewalks
                    float transitVolume = od.TransitTrips[i, j];
                    if (transitVolume > 0.01f)
                    {
                        if (transit != null && transit.Stops.Count > 0)
                        {
                            int s1 = -1;
                            float bestDist1 = float.MaxValue;
                            int s2 = -1;
                            float bestDist2 = float.MaxValue;

                            foreach (var stop in transit.Stops.Values)
                            {
                                var sZone = grid.GetZone(stop.ZoneId);
                                if (sZone == null) continue;

                                int d1 = Mathf.Abs(zoneI.GridPos.X - sZone.GridPos.X) + Mathf.Abs(zoneI.GridPos.Y - sZone.GridPos.Y);
                                if (d1 < bestDist1) { bestDist1 = d1; s1 = stop.NodeId; }

                                int d2 = Mathf.Abs(zoneJ.GridPos.X - sZone.GridPos.X) + Mathf.Abs(zoneJ.GridPos.Y - sZone.GridPos.Y);
                                if (d2 < bestDist2) { bestDist2 = d2; s2 = stop.NodeId; }
                            }

                            // Access walk leg: origin zone i -> boarding stop s1
                            if (s1 != -1 && s1 != i)
                            {
                                if (graph.WalkingPathCache.TryGetValue((i, s1), out var accessPath) ||
                                    (accessPath = graph.GetShortestWalkingPath(i, s1)) != null)
                                {
                                    for (int p = 0; p < accessPath.Count; p++)
                                    {
                                        graph.Edges[accessPath[p]].PedestrianVolume += transitVolume;
                                    }
                                }
                            }

                            // Egress walk leg: alighting stop s2 -> destination zone j
                            if (s2 != -1 && s2 != j)
                            {
                                if (graph.WalkingPathCache.TryGetValue((s2, j), out var egressPath) ||
                                    (egressPath = graph.GetShortestWalkingPath(s2, j)) != null)
                                {
                                    for (int p = 0; p < egressPath.Count; p++)
                                    {
                                        graph.Edges[egressPath[p]].PedestrianVolume += transitVolume;
                                    }
                                }
                            }
                        }
                        else
                        {
                            // Fallback: assign access and egress walk to connecting ends of corridor
                            if (graph.WalkingPathCache.TryGetValue((i, j), out var fallbackPath) ||
                                graph.PathCache.TryGetValue((i, j), out fallbackPath))
                            {
                                if (fallbackPath.Count > 0)
                                {
                                    graph.Edges[fallbackPath[0]].PedestrianVolume += transitVolume;
                                    if (fallbackPath.Count > 1)
                                    {
                                        graph.Edges[fallbackPath[fallbackPath.Count - 1]].PedestrianVolume += transitVolume;
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    }

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
        int msaIterations = 4)
    {
        if (tazManager == null || matrix == null || roadGraph == null) return;

        roadGraph.ClearVolumes();

        var tazList = tazManager.ActiveZones;
        int k = tazList.Count;
        int edgeCount = roadGraph.Edges.Count;
        int maxNodeId = roadGraph.GetMaxNodeIndex();
        if (k == 0 || edgeCount == 0 || maxNodeId <= 0) return;

        EnsureMsaBuffersCapacity(k, edgeCount, maxNodeId);

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
                        walkEdges = roadGraph.GetShortestWalkingPath(u, v);
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
                                roadGraph.Edges[eid].PedestrianVolume += walkFlow;
                        }
                    }
                }
            }
        }

        if (_msaActiveOrigins.Count == 0)
        {
            matrix.UpdateAccessibilityScores(tazManager, roadGraph);
            return;
        }

        int iterations = Math.Clamp(msaIterations, 1, 8);

        // 4. MSA loop with O(V) Leaf-to-Root flow accumulation
        for (int iter = 1; iter <= iterations; iter++)
        {
            if (iter > 1)
            {
                // One compact copy of the graph with travel times under V^(n-1), shared by all origins
                _msaRouting.Rebuild(roadGraph);
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
                var edge = roadGraph.Edges[e];
                edge.CurrentVolume = keepWeight * edge.CurrentVolume + stepSize * _msaAuxCarVolumes[e];
            }
        }

        // 5. Final synchronization: update shortest trees, _travelTimes, and LUTI AccessibilityScores using equilibrium volumes V^(n)
        if (iterations > 1)
        {
            matrix.UpdateEquilibriumTreesAndTimes(tazManager, roadGraph);
        }
        matrix.UpdateAccessibilityScores(tazManager, roadGraph);
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
                roadGraph.Edges[edgeId].CurrentVolume += carPerEdge;
                roadGraph.Edges[edgeId].PedestrianVolume += pedPerEdge;
            }
        }
    }

    private void EnsureMsaBuffersCapacity(int k, int edgeCount, int maxNodeId)
    {
        if (_msaBaseInternalCarVolumes.Length < edgeCount)
        {
            int size = Math.Max(edgeCount * 2, 256);
            _msaBaseInternalCarVolumes = new float[size];
            _msaAuxCarVolumes = new float[size];
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
