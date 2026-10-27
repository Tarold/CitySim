using System;
using System.Collections.Generic;
using Godot;

namespace CitySim.Simulation;

/// <summary>
/// Read-only snapshot of the public transit stop-and-line network for a macro recalculation.
/// Built on the main thread from the active <see cref="TransitRoute"/> list and <see cref="RoadGraph"/>:
/// maps stop road nodes to dense indices, expands each route into directed stop-to-stop segments (forward and
/// backward for lines, circular for loops) with precomputed bus travel times and hourly passenger capacities,
/// and indexes walking transfers between nearby stops via <see cref="SpatialGridIndex"/>.
/// Worker threads read the flat CSR arrays and update segment travel times from unpublished car volume arrays
/// without touching <see cref="TransitRoute"/> or <see cref="TransitStop"/>.
/// </summary>
public sealed class TransitNetworkSnapshot
{
    /// <summary>Maximum walking distance (meters) between two distinct stops for a pedestrian transfer.</summary>
    public const float MaxTransferWalkMeters = 160.0f;

    /// <summary>Walking speed (meters/minute, ~4.8 km/h) used for stop access and walking transfers.</summary>
    public const float WalkSpeedMetersPerMin = 80.0f;

    /// <summary>Passengers per transit vehicle used to compute hourly line capacity.</summary>
    public const float VehicleCapacityPassengers = 75.0f;

    /// <summary>Segment volume-to-capacity ratio above which crowding increases perceived in-vehicle time.</summary>
    public const float CrowdingOccupancyThreshold = 0.80f;

    /// <summary>Fallback road-edge travel time (minutes) when a path pair has no road edge.</summary>
    private const float FallbackEdgeMinutes = 1.28f;

    // Stops (dense index s in 0..StopCount-1)
    private int[] _stopNodeIds = Array.Empty<int>();
    private Vector2[] _stopPositions = Array.Empty<Vector2>();
    private int[] _nodeToStopIndex = Array.Empty<int>();
    private int _indexedNodeCapacity;

    // Routes (dense index r in 0..RouteCount-1)
    private int[] _routeIds = Array.Empty<int>();
    private float[] _routeHeadways = Array.Empty<float>();
    private float[] _routeWaitMinutes = Array.Empty<float>();
    private float[] _routeHourlyCapacities = Array.Empty<float>();
    private float[] _routeTicketPrices = Array.Empty<float>();
    private int[] _routeFleetSizes = Array.Empty<int>();

    // Expanded graph states: 0..StopCount-1 are stop hub states; StopCount..StateCount-1 are on-board states
    private int[] _stateStopIndex = Array.Empty<int>();
    private int[] _stateRouteIndex = Array.Empty<int>();
    private int[] _stateNextState = Array.Empty<int>();
    private int[] _stateNextSeg = Array.Empty<int>();

    // Directed stop-to-stop line segments (seg in 0..SegmentCount-1)
    private int[] _segRouteIndex = Array.Empty<int>();
    private int[] _segFromStop = Array.Empty<int>();
    private int[] _segToStop = Array.Empty<int>();
    private float[] _segDwellMinutes = Array.Empty<float>();
    private float[] _segBaseIvt = Array.Empty<float>();
    private float[] _segPerceivedIvt = Array.Empty<float>();
    private int[] _segEdgeStart = Array.Empty<int>();
    private int[] _segEdgeIds = Array.Empty<int>();
    private int[] _segFallbackEdgeIds = Array.Empty<int>();

    // CSR of boarding transitions from stop hub s (0..StopCount-1) to departing on-board states at s
    private int[] _stopBoardRowStart = Array.Empty<int>();
    private int[] _stopBoardState = Array.Empty<int>();

    // CSR of (routeIndex, fwdState, bwdState) serving stop s (0..StopCount-1), for TAZ catchment binding
    private int[] _stopRouteRowStart = Array.Empty<int>();
    private int[] _stopRouteIndex = Array.Empty<int>();
    private int[] _stopRouteFwdState = Array.Empty<int>();
    private int[] _stopRouteBwdState = Array.Empty<int>();

    // CSR of walking transfers between nearby stops s -> s' within MaxTransferWalkMeters
    private int[] _stopWalkRowStart = Array.Empty<int>();
    private int[] _stopWalkTargetStop = Array.Empty<int>();
    private float[] _stopWalkMinutes = Array.Empty<float>();

    // Scratch buffers reused during Rebuild
    private readonly List<(int StopIdx, int PathIdx)> _routeStopsScratch = new(64);
    private readonly List<int> _spatialQueryScratch = new(64);
    private List<int>[] _stopBoardLists = Array.Empty<List<int>>();
    private List<(int RouteIdx, int FwdState, int BwdState)>[] _stopRouteLists = Array.Empty<List<(int, int, int)>>();

    /// <summary>Monotonically increasing snapshot version incremented on every <see cref="Rebuild"/>.</summary>
    public int Version { get; private set; }

    /// <summary><see cref="RoadGraph.TopologyVersion"/> at the time of <see cref="Rebuild"/>.</summary>
    public int TopologyVersion { get; private set; } = -1;

    /// <summary>Number of unique transit stops in the network.</summary>
    public int StopCount { get; private set; }

    /// <summary>Number of transit routes in the network.</summary>
    public int RouteCount { get; private set; }

    /// <summary>Number of directed stop-to-stop line segments across all routes.</summary>
    public int SegmentCount { get; private set; }

    /// <summary>Total number of search states (<see cref="StopCount"/> stop hubs + on-board route-stop states).</summary>
    public int StateCount { get; private set; }

    public ReadOnlySpan<int> StopNodeIds => _stopNodeIds.AsSpan(0, StopCount);
    public ReadOnlySpan<int> RouteIds => _routeIds.AsSpan(0, RouteCount);
    public ReadOnlySpan<float> RouteWaitMinutes => _routeWaitMinutes.AsSpan(0, RouteCount);
    public ReadOnlySpan<float> RouteHourlyCapacities => _routeHourlyCapacities.AsSpan(0, RouteCount);

    internal int[] RawStopNodeIds => _stopNodeIds;
    internal float[] RawRouteWaitMinutes => _routeWaitMinutes;
    internal float[] RawRouteHourlyCapacities => _routeHourlyCapacities;
    internal int[] RawStateStopIndex => _stateStopIndex;
    internal int[] RawStateRouteIndex => _stateRouteIndex;
    internal int[] RawStateNextState => _stateNextState;
    internal int[] RawStateNextSeg => _stateNextSeg;
    internal int[] RawSegRouteIndex => _segRouteIndex;
    internal float[] RawSegBaseIvt => _segBaseIvt;
    internal float[] RawSegPerceivedIvt => _segPerceivedIvt;
    internal int[] RawStopBoardRowStart => _stopBoardRowStart;
    internal int[] RawStopBoardState => _stopBoardState;
    internal int[] RawStopRouteRowStart => _stopRouteRowStart;
    internal int[] RawStopRouteIndex => _stopRouteIndex;
    internal int[] RawStopRouteFwdState => _stopRouteFwdState;
    internal int[] RawStopRouteBwdState => _stopRouteBwdState;
    internal int[] RawStopWalkRowStart => _stopWalkRowStart;
    internal int[] RawStopWalkTargetStop => _stopWalkTargetStop;
    internal float[] RawStopWalkMinutes => _stopWalkMinutes;

    /// <summary>Dense stop index for <paramref name="roadNodeId"/>, or -1 if not an active transit stop.</summary>
    public int GetStopIndex(int roadNodeId) =>
        roadNodeId >= 0 && roadNodeId < _indexedNodeCapacity ? _nodeToStopIndex[roadNodeId] : -1;

    /// <summary>Road node ID of dense stop <paramref name="stopIndex"/>.</summary>
    public int GetStopNodeId(int stopIndex) => _stopNodeIds[stopIndex];

    /// <summary>Route ID of dense route <paramref name="routeIndex"/>.</summary>
    public int GetRouteId(int routeIndex) => _routeIds[routeIndex];

    /// <summary>Hourly passenger capacity per direction of dense route <paramref name="routeIndex"/>.</summary>
    public float GetRouteHourlyCapacity(int routeIndex) => _routeHourlyCapacities[routeIndex];

    /// <summary>Dense route index that owns directed segment <paramref name="segmentIndex"/>.</summary>
    public int GetSegmentRouteIndex(int segmentIndex) => _segRouteIndex[segmentIndex];

    /// <summary>Base (uncrowded) in-vehicle travel time in minutes of directed segment <paramref name="segmentIndex"/>.</summary>
    public float GetSegmentBaseIvt(int segmentIndex) => _segBaseIvt[segmentIndex];

    /// <summary>Perceived in-vehicle travel time in minutes (including crowding penalty) of <paramref name="segmentIndex"/>.</summary>
    public float GetSegmentPerceivedIvt(int segmentIndex) => _segPerceivedIvt[segmentIndex];

    /// <summary>
    /// Perceived time multiplier on a line segment at <paramref name="loadRatio"/> (volume / hourly capacity).
    /// Equal to 1.0 up to <see cref="CrowdingOccupancyThreshold"/> (80%), then grows quadratically up to 3.5x.
    /// </summary>
    public static float ComputeCrowdingMultiplier(float loadRatio)
    {
        if (loadRatio <= CrowdingOccupancyThreshold) return 1.0f;
        float excess = loadRatio - CrowdingOccupancyThreshold;
        return 1.0f + MathF.Min(2.5f, 0.8f * excess + 1.6f * excess * excess);
    }

    /// <summary>
    /// Captures the current transit routes, stops, segments, walking transfers and bus travel times.
    /// When <paramref name="updateRoutePrefixSums"/> is true (main thread), also refreshes
    /// <see cref="TransitRoute.UpdatePrefixTravelTimes"/> on each route.
    /// </summary>
    public void Rebuild(
        TransitManager transit,
        RoadGraph graph,
        float[] carVolumes = null,
        bool updateRoutePrefixSums = true)
    {
        Version++;

        if (transit == null || graph == null || transit.Routes.Count == 0)
        {
            TopologyVersion = graph?.TopologyVersion ?? -1;
            StopCount = 0;
            RouteCount = 0;
            SegmentCount = 0;
            StateCount = 0;
            _indexedNodeCapacity = 0;
            return;
        }

        TopologyVersion = graph.TopologyVersion;
        graph.EnsureSpatialIndexSynced();

        if (updateRoutePrefixSums && carVolumes == null)
        {
            transit.UpdateAllRouteTravelTimes(graph);
        }

        int maxNodeId = graph.GetMaxNodeIndex();
        EnsureNodeLookupCapacity(maxNodeId);
        Array.Fill(_nodeToStopIndex, -1, 0, maxNodeId);
        _indexedNodeCapacity = maxNodeId;

        var routes = transit.Routes;
        int routeCount = routes.Count;
        RouteCount = routeCount;
        EnsureRouteCapacity(routeCount);

        // 1. Assign dense stop indices for all stops referenced by routes (and TransitManager.Stops)
        int stopCount = 0;
        for (int r = 0; r < routeCount; r++)
        {
            var route = routes[r];
            var path = route.PathNodeIds;
            for (int k = 0; k < path.Count; k++)
            {
                int nodeId = path[k];
                if (nodeId >= 0 && nodeId < maxNodeId && route.StopNodeIds.Contains(nodeId) && _nodeToStopIndex[nodeId] < 0)
                {
                    RegisterDenseStop(ref stopCount, nodeId, graph);
                }
            }
            foreach (int nodeId in route.StopNodeIds)
            {
                if (nodeId >= 0 && nodeId < maxNodeId && _nodeToStopIndex[nodeId] < 0)
                {
                    RegisterDenseStop(ref stopCount, nodeId, graph);
                }
            }
        }
        foreach (int nodeId in transit.Stops.Keys)
        {
            if (nodeId >= 0 && nodeId < maxNodeId && _nodeToStopIndex[nodeId] < 0)
            {
                RegisterDenseStop(ref stopCount, nodeId, graph);
            }
        }

        StopCount = stopCount;
        EnsureStopScratchLists(stopCount);

        // 2. Expand routes into on-board states and directed stop-to-stop segments
        int stateCount = stopCount;
        int segCount = 0;
        int segEdgeCursor = 0;

        for (int r = 0; r < routeCount; r++)
        {
            var route = routes[r];
            float headway = MathF.Max(1.0f, route.HeadwayMinutes);
            _routeIds[r] = route.Id;
            _routeHeadways[r] = headway;
            _routeWaitMinutes[r] = headway * 0.5f;
            _routeHourlyCapacities[r] = (60.0f / headway) * VehicleCapacityPassengers;
            _routeTicketPrices[r] = route.TicketPrice;
            _routeFleetSizes[r] = route.FleetSize;

            _routeStopsScratch.Clear();
            var path = route.PathNodeIds;
            int pathLen = path.Count;
            for (int k = 0; k < pathLen; k++)
            {
                int nodeId = path[k];
                if (!route.StopNodeIds.Contains(nodeId)) continue;
                int sIdx = GetStopIndex(nodeId);
                if (sIdx < 0) continue;

                // Keep first occurrence along PathNodeIds (matching TransitRoute.NodeToFirstPathIndex)
                bool alreadyAdded = false;
                for (int prev = 0; prev < _routeStopsScratch.Count; prev++)
                {
                    if (_routeStopsScratch[prev].StopIdx == sIdx)
                    {
                        alreadyAdded = true;
                        break;
                    }
                }
                if (!alreadyAdded)
                {
                    _routeStopsScratch.Add((sIdx, k));
                }
            }

            int m = _routeStopsScratch.Count;
            if (m < 2) continue;

            bool isLoop = route.IsLoop;
            int newStates = isLoop ? m : 2 * m;
            int newSegs = isLoop ? m : 2 * (m - 1);
            EnsureStateCapacity(stateCount + newStates);
            EnsureSegmentCapacity(segCount + newSegs);

            int fwdBase = stateCount;
            stateCount += m;
            int bwdBase = isLoop ? -1 : stateCount;
            if (!isLoop) stateCount += m;

            for (int a = 0; a < m; a++)
            {
                int sIdx = _routeStopsScratch[a].StopIdx;
                int fwdState = fwdBase + a;
                _stateStopIndex[fwdState] = sIdx;
                _stateRouteIndex[fwdState] = r;
                _stateNextState[fwdState] = -1;
                _stateNextSeg[fwdState] = -1;

                int bwdState = -1;
                if (!isLoop)
                {
                    bwdState = bwdBase + a;
                    _stateStopIndex[bwdState] = sIdx;
                    _stateRouteIndex[bwdState] = r;
                    _stateNextState[bwdState] = -1;
                    _stateNextSeg[bwdState] = -1;
                }

                _stopRouteLists[sIdx].Add((r, fwdState, bwdState));
            }

            // Forward segments: S_a -> S_{a+1}
            for (int a = 0; a < m - 1; a++)
            {
                var (sFrom, kFrom) = _routeStopsScratch[a];
                var (sTo, kTo) = _routeStopsScratch[a + 1];
                int fromState = fwdBase + a;
                int toState = fwdBase + a + 1;
                int seg = segCount++;

                _segRouteIndex[seg] = r;
                _segFromStop[seg] = sFrom;
                _segToStop[seg] = sTo;
                _segEdgeStart[seg] = segEdgeCursor;

                float dwell = 0f;
                EnsureSegmentEdgeCapacity(segEdgeCursor + (kTo - kFrom));
                for (int k = kFrom; k < kTo; k++)
                {
                    int u = path[k];
                    int v = path[k + 1];
                    int fwdId = graph.FindEdgeId(u, v);
                    _segEdgeIds[segEdgeCursor] = fwdId;
                    _segFallbackEdgeIds[segEdgeCursor] = fwdId;
                    segEdgeCursor++;
                    if (route.StopNodeIds.Contains(v)) dwell += TransitRoute.StopDwellPenaltyMinutes;
                }
                _segDwellMinutes[seg] = dwell;

                _stateNextState[fromState] = toState;
                _stateNextSeg[fromState] = seg;
                _stopBoardLists[sFrom].Add(fromState);
            }

            if (isLoop)
            {
                // Loop-closing segment: S_{m-1} -> S_0 (wraps past end of PathNodeIds and back to k_0)
                var (sLast, kLast) = _routeStopsScratch[m - 1];
                var (sFirst, kFirst) = _routeStopsScratch[0];
                int fromState = fwdBase + m - 1;
                int toState = fwdBase;
                int seg = segCount++;

                _segRouteIndex[seg] = r;
                _segFromStop[seg] = sLast;
                _segToStop[seg] = sFirst;
                _segEdgeStart[seg] = segEdgeCursor;

                float dwell = 0f;
                int steps = (pathLen - 1 - kLast) + 1 + kFirst;
                EnsureSegmentEdgeCapacity(segEdgeCursor + steps);

                for (int k = kLast; k < pathLen - 1; k++)
                {
                    int u = path[k];
                    int v = path[k + 1];
                    int fwdId = graph.FindEdgeId(u, v);
                    _segEdgeIds[segEdgeCursor] = fwdId;
                    _segFallbackEdgeIds[segEdgeCursor] = fwdId;
                    segEdgeCursor++;
                    if (route.StopNodeIds.Contains(v)) dwell += TransitRoute.StopDwellPenaltyMinutes;
                }
                {
                    int u = path[pathLen - 1];
                    int v = path[0];
                    int closeId = graph.FindEdgeId(u, v);
                    _segEdgeIds[segEdgeCursor] = closeId;
                    _segFallbackEdgeIds[segEdgeCursor] = closeId;
                    segEdgeCursor++;
                    if (route.StopNodeIds.Contains(v)) dwell += TransitRoute.StopDwellPenaltyMinutes;
                }
                for (int k = 0; k < kFirst; k++)
                {
                    int u = path[k];
                    int v = path[k + 1];
                    int fwdId = graph.FindEdgeId(u, v);
                    _segEdgeIds[segEdgeCursor] = fwdId;
                    _segFallbackEdgeIds[segEdgeCursor] = fwdId;
                    segEdgeCursor++;
                    if (route.StopNodeIds.Contains(v)) dwell += TransitRoute.StopDwellPenaltyMinutes;
                }
                _segDwellMinutes[seg] = dwell;

                _stateNextState[fromState] = toState;
                _stateNextSeg[fromState] = seg;
                _stopBoardLists[sLast].Add(fromState);
            }
            else
            {
                // Backward segments: S_{a+1} -> S_a
                for (int a = m - 1; a >= 1; a--)
                {
                    var (sFrom, kFrom) = _routeStopsScratch[a];
                    var (sTo, kTo) = _routeStopsScratch[a - 1];
                    int fromState = bwdBase + a;
                    int toState = bwdBase + a - 1;
                    int seg = segCount++;

                    _segRouteIndex[seg] = r;
                    _segFromStop[seg] = sFrom;
                    _segToStop[seg] = sTo;
                    _segEdgeStart[seg] = segEdgeCursor;

                    float dwell = 0f;
                    EnsureSegmentEdgeCapacity(segEdgeCursor + (kFrom - kTo));
                    for (int k = kTo; k < kFrom; k++)
                    {
                        int u = path[k];
                        int v = path[k + 1];
                        int fwdId = graph.FindEdgeId(u, v);
                        int bwdId = graph.FindEdgeId(v, u);
                        _segEdgeIds[segEdgeCursor] = bwdId;
                        _segFallbackEdgeIds[segEdgeCursor] = fwdId;
                        segEdgeCursor++;
                        if (route.StopNodeIds.Contains(u)) dwell += TransitRoute.StopDwellPenaltyMinutes;
                    }
                    _segDwellMinutes[seg] = dwell;

                    _stateNextState[fromState] = toState;
                    _stateNextSeg[fromState] = seg;
                    _stopBoardLists[sFrom].Add(fromState);
                }
            }
        }

        _segEdgeStart[segCount] = segEdgeCursor;
        StateCount = stateCount;
        SegmentCount = segCount;

        // 3. Flatten stop boarding and stop-route CSR tables
        FlattenStopTables(stopCount);

        // 4. Build walking transfers between stops within MaxTransferWalkMeters via SpatialGridIndex
        BuildWalkingTransfers(graph, stopCount);

        // 5. Compute segment travel times from road graph (and carVolumes if provided)
        UpdateTravelTimes(graph, carVolumes);
    }

    /// <summary>
    /// Recomputes base and perceived in-vehicle travel times for all line segments from <paramref name="graph"/>.
    /// Uses <paramref name="carVolumes"/> when given (worker thread after MSA), otherwise the published
    /// <see cref="RoadEdge.CurrentVolume"/>. Does not touch <see cref="TransitRoute"/>.
    /// </summary>
    public void UpdateTravelTimes(RoadGraph graph, float[] carVolumes = null)
    {
        if (graph == null || SegmentCount == 0) return;

        var edges = graph.Edges;
        int edgeCount = edges.Count;

        for (int seg = 0; seg < SegmentCount; seg++)
        {
            float ivt = _segDwellMinutes[seg];
            int end = _segEdgeStart[seg + 1];
            for (int e = _segEdgeStart[seg]; e < end; e++)
            {
                int eid = _segEdgeIds[e];
                if (eid < 0 || eid >= edgeCount)
                {
                    eid = _segFallbackEdgeIds[e];
                }

                if (eid >= 0 && eid < edgeCount)
                {
                    var edge = edges[eid];
                    float vol = carVolumes != null && eid < carVolumes.Length ? carVolumes[eid] : edge.CurrentVolume;
                    ivt += RoadEdge.ComputeBusTravelTime(edge.Length, edge.FreeFlowSpeed, vol, edge.Capacity, edge.HasBusLane);
                }
                else
                {
                    ivt += FallbackEdgeMinutes;
                }
            }

            ivt = MathF.Max(0.1f, ivt);
            _segBaseIvt[seg] = ivt;
            _segPerceivedIvt[seg] = ivt;
        }
    }

    /// <summary>Resets perceived segment travel times to uncrowded base travel times.</summary>
    public void ResetPerceivedTimes()
    {
        Array.Copy(_segBaseIvt, _segPerceivedIvt, SegmentCount);
    }

    /// <summary>
    /// Updates perceived segment travel times from <paramref name="segmentVolumes"/> (passengers/hour) using
    /// <see cref="ComputeCrowdingMultiplier"/>. Returns the maximum segment volume-to-capacity ratio observed.
    /// </summary>
    public float UpdateCrowdingPerceivedTimes(ReadOnlySpan<float> segmentVolumes)
    {
        float maxRatio = 0f;
        for (int seg = 0; seg < SegmentCount; seg++)
        {
            int r = _segRouteIndex[seg];
            float cap = MathF.Max(1.0f, _routeHourlyCapacities[r]);
            float ratio = segmentVolumes[seg] / cap;
            if (ratio > maxRatio) maxRatio = ratio;
            _segPerceivedIvt[seg] = _segBaseIvt[seg] * ComputeCrowdingMultiplier(ratio);
        }
        return maxRatio;
    }

    private void RegisterDenseStop(ref int stopCount, int nodeId, RoadGraph graph)
    {
        EnsureStopCapacity(stopCount + 1);
        int sIdx = stopCount++;
        _stopNodeIds[sIdx] = nodeId;
        _stopPositions[sIdx] = graph.GetNode(nodeId)?.WorldPosition ?? Vector2.Zero;
        _nodeToStopIndex[nodeId] = sIdx;
    }

    private void FlattenStopTables(int stopCount)
    {
        if (_stopBoardRowStart.Length < stopCount + 1)
        {
            int size = Math.Max((stopCount + 1) * 2, 64);
            _stopBoardRowStart = new int[size];
            _stopRouteRowStart = new int[size];
        }

        int totalBoards = 0;
        int totalRouteEntries = 0;
        for (int s = 0; s < stopCount; s++)
        {
            totalBoards += _stopBoardLists[s].Count;
            totalRouteEntries += _stopRouteLists[s].Count;
        }

        if (_stopBoardState.Length < totalBoards)
        {
            _stopBoardState = new int[Math.Max(totalBoards * 2, 64)];
        }
        if (_stopRouteIndex.Length < totalRouteEntries)
        {
            int size = Math.Max(totalRouteEntries * 2, 64);
            _stopRouteIndex = new int[size];
            _stopRouteFwdState = new int[size];
            _stopRouteBwdState = new int[size];
        }

        int bCursor = 0;
        int rCursor = 0;
        for (int s = 0; s < stopCount; s++)
        {
            _stopBoardRowStart[s] = bCursor;
            var bList = _stopBoardLists[s];
            for (int i = 0; i < bList.Count; i++)
            {
                _stopBoardState[bCursor++] = bList[i];
            }

            _stopRouteRowStart[s] = rCursor;
            var rList = _stopRouteLists[s];
            for (int i = 0; i < rList.Count; i++)
            {
                var entry = rList[i];
                _stopRouteIndex[rCursor] = entry.RouteIdx;
                _stopRouteFwdState[rCursor] = entry.FwdState;
                _stopRouteBwdState[rCursor] = entry.BwdState;
                rCursor++;
            }
        }
        _stopBoardRowStart[stopCount] = bCursor;
        _stopRouteRowStart[stopCount] = rCursor;
    }

    private void BuildWalkingTransfers(RoadGraph graph, int stopCount)
    {
        if (_stopWalkRowStart.Length < stopCount + 1)
        {
            _stopWalkRowStart = new int[Math.Max((stopCount + 1) * 2, 64)];
        }

        int walkCursor = 0;
        for (int s = 0; s < stopCount; s++)
        {
            _stopWalkRowStart[s] = walkCursor;
            Vector2 pos = _stopPositions[s];
            graph.SpatialIndex.QueryNodesInRadius(pos, MaxTransferWalkMeters, _spatialQueryScratch);

            EnsureWalkCapacity(walkCursor + _spatialQueryScratch.Count);
            for (int i = 0; i < _spatialQueryScratch.Count; i++)
            {
                int otherNodeId = _spatialQueryScratch[i];
                int otherStop = GetStopIndex(otherNodeId);
                if (otherStop < 0 || otherStop == s) continue;

                float dist = pos.DistanceTo(_stopPositions[otherStop]);
                if (dist <= MaxTransferWalkMeters)
                {
                    _stopWalkTargetStop[walkCursor] = otherStop;
                    _stopWalkMinutes[walkCursor] = MathF.Max(0.05f, dist / WalkSpeedMetersPerMin);
                    walkCursor++;
                }
            }
        }
        _stopWalkRowStart[stopCount] = walkCursor;
    }

    private void EnsureNodeLookupCapacity(int maxNodeId)
    {
        if (_nodeToStopIndex.Length >= maxNodeId) return;
        _nodeToStopIndex = new int[Math.Max(maxNodeId * 2, 64)];
    }

    private void EnsureStopCapacity(int required)
    {
        if (_stopNodeIds.Length >= required) return;
        int size = Math.Max(required * 2, 64);
        Array.Resize(ref _stopNodeIds, size);
        Array.Resize(ref _stopPositions, size);
    }

    private void EnsureStopScratchLists(int stopCount)
    {
        if (_stopBoardLists.Length < stopCount)
        {
            int oldLen = _stopBoardLists.Length;
            int size = Math.Max(stopCount * 2, 64);
            Array.Resize(ref _stopBoardLists, size);
            Array.Resize(ref _stopRouteLists, size);
            for (int i = oldLen; i < size; i++)
            {
                _stopBoardLists[i] = new List<int>(4);
                _stopRouteLists[i] = new List<(int, int, int)>(4);
            }
        }
        for (int i = 0; i < stopCount; i++)
        {
            _stopBoardLists[i].Clear();
            _stopRouteLists[i].Clear();
        }
    }

    private void EnsureRouteCapacity(int required)
    {
        if (_routeIds.Length >= required) return;
        int size = Math.Max(required * 2, 32);
        Array.Resize(ref _routeIds, size);
        Array.Resize(ref _routeHeadways, size);
        Array.Resize(ref _routeWaitMinutes, size);
        Array.Resize(ref _routeHourlyCapacities, size);
        Array.Resize(ref _routeTicketPrices, size);
        Array.Resize(ref _routeFleetSizes, size);
    }

    private void EnsureStateCapacity(int required)
    {
        if (_stateStopIndex.Length >= required) return;
        int size = Math.Max(required * 2, 128);
        Array.Resize(ref _stateStopIndex, size);
        Array.Resize(ref _stateRouteIndex, size);
        Array.Resize(ref _stateNextState, size);
        Array.Resize(ref _stateNextSeg, size);
    }

    private void EnsureSegmentCapacity(int required)
    {
        if (_segRouteIndex.Length >= required && _segEdgeStart.Length >= required + 1) return;
        int size = Math.Max(required * 2, 128);
        Array.Resize(ref _segRouteIndex, size);
        Array.Resize(ref _segFromStop, size);
        Array.Resize(ref _segToStop, size);
        Array.Resize(ref _segDwellMinutes, size);
        Array.Resize(ref _segBaseIvt, size);
        Array.Resize(ref _segPerceivedIvt, size);
        Array.Resize(ref _segEdgeStart, size + 1);
    }

    private void EnsureSegmentEdgeCapacity(int required)
    {
        if (_segEdgeIds.Length >= required) return;
        int size = Math.Max(required * 2, 256);
        Array.Resize(ref _segEdgeIds, size);
        Array.Resize(ref _segFallbackEdgeIds, size);
    }

    private void EnsureWalkCapacity(int required)
    {
        if (_stopWalkTargetStop.Length >= required) return;
        int size = Math.Max(required * 2, 64);
        Array.Resize(ref _stopWalkTargetStop, size);
        Array.Resize(ref _stopWalkMinutes, size);
    }
}
