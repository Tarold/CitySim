using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;

namespace CitySim.Simulation;

/// <summary>
/// Streaming macroscopic public transit engine operating on <see cref="TransitNetworkSnapshot"/>.
/// Computes multi-transfer shortest-path trees on the expanded stop-and-line graph, fills <see cref="TransitSkim"/>,
/// accumulates line-segment passenger loads, route boardings and stop arrival rates in deterministic chunks,
/// models segment crowding above <see cref="TransitNetworkSnapshot.CrowdingOccupancyThreshold"/> (80% capacity)
/// via MSA equilibrium, and publishes results to <see cref="TransitManager"/> on the main thread in O(routes + stops).
/// </summary>
public sealed class TransitEngine
{
    /// <summary>Weight of waiting time (minutes) in transit generalized cost.</summary>
    public const float WaitTimeWeight = 1.2f;

    /// <summary>Weight of walking transfer time (minutes) in transit generalized cost.</summary>
    public const float WalkTimeWeight = 0.8f;

    /// <summary>Generalized cost penalty (minutes) added for each line-to-line transfer.</summary>
    public const float TransferPenaltyMinutes = 8.0f;

    /// <summary>
    /// Fixed number of origin TAZ chunks per search round. Each chunk accumulates into private arrays and
    /// chunks are merged in index order (0..15) so results are deterministic regardless of thread scheduling.
    /// </summary>
    public const int AccumulationChunks = 16;

    /// <summary>Lower bound on the MSA blending step size during warm cycles.</summary>
    public const float MinWarmStepSize = 0.25f;

    private const float MinPropagatedFlow = 1e-4f;

    private sealed class SearchBuffer
    {
        public readonly PriorityQueue<int, float> Queue = new(128);
        public float[] Dist = Array.Empty<float>();
        public float[] Ivt = Array.Empty<float>();
        public float[] Wait = Array.Empty<float>();
        public float[] Crowding = Array.Empty<float>();
        public sbyte[] Transfers = Array.Empty<sbyte>();
        public int[] ParentState = Array.Empty<int>();
        public int[] ParentSeg = Array.Empty<int>();
        public int[] Visited = Array.Empty<int>();
        public float[] StateFlow = Array.Empty<float>();

        public void EnsureCapacity(int stateCount)
        {
            if (Dist.Length >= stateCount) return;
            int size = Math.Max(stateCount * 2, 128);
            Dist = new float[size];
            Ivt = new float[size];
            Wait = new float[size];
            Crowding = new float[size];
            Transfers = new sbyte[size];
            ParentState = new int[size];
            ParentSeg = new int[size];
            Visited = new int[size];
            StateFlow = new float[size];
        }
    }

    [ThreadStatic]
    private static SearchBuffer _tlsBuffer;

    // CSR bindings of TAZs (0..k-1) to (routeIndex, stopIndex, fwdState, bwdState) in the current snapshot
    private int[] _tazAccessStart = Array.Empty<int>();
    private int[] _tazAccessRoute = Array.Empty<int>();
    private int[] _tazAccessStop = Array.Empty<int>();
    private int[] _tazAccessFwdState = Array.Empty<int>();
    private int[] _tazAccessBwdState = Array.Empty<int>();
    private int[] _routeSeenStamp = Array.Empty<int>();

    // Per-chunk private accumulators
    private float[][] _chunkSegmentVolumes = Array.Empty<float[]>();
    private float[][] _chunkRouteDemand = Array.Empty<float[]>();
    private float[][] _chunkStopArrivals = Array.Empty<float[]>();

    // Merged assignment results (ready to publish on the main thread)
    private float[] _segmentVolumes = Array.Empty<float>();
    private float[] _auxSegmentVolumes = Array.Empty<float>();
    private float[] _routeHourlyDemand = Array.Empty<float>();
    private float[] _auxRouteHourlyDemand = Array.Empty<float>();
    private float[] _routeMaxSegmentVolume = Array.Empty<float>();
    private float[] _stopArrivalRates = Array.Empty<float>();
    private float[] _auxStopArrivalRates = Array.Empty<float>();

    private int _pendingRouteCount;
    private int _pendingStopCount;
    private int _pendingSegmentCount;
    private int[] _pendingStopNodeIds = Array.Empty<int>();

    // Warm equilibrium tracking across cycles
    private int _iterationCount;
    private int _warmTopologyVersion = -1;
    private int _warmRouteCount = -1;
    private int _warmStopCount = -1;
    private int _warmSegmentCount = -1;

    /// <summary>Continue MSA segment crowding equilibrium across cycles when transit topology is unchanged.</summary>
    public bool WarmStart { get; set; }

    /// <summary>Transit travel-time skim from the latest search round.</summary>
    public TransitSkim Skim { get; } = new TransitSkim();

    /// <summary>True when passenger demand arrays have been computed and are waiting for <see cref="PublishPendingDemand"/>.</summary>
    public bool HasPendingDemand { get; private set; }

    /// <summary>Maximum segment volume-to-capacity ratio observed in the last assignment.</summary>
    public float MaxSegmentLoadRatio { get; private set; }

    /// <summary>Passenger load (passengers/hour) on each directed stop-to-stop segment from the last assignment.</summary>
    public ReadOnlySpan<float> SegmentVolumes => _segmentVolumes.AsSpan(0, _pendingSegmentCount);

    /// <summary>Hourly passenger boardings on each route from the last assignment.</summary>
    public ReadOnlySpan<float> RouteHourlyDemand => _routeHourlyDemand.AsSpan(0, _pendingRouteCount);

    /// <summary>Peak segment passenger volume on each route from the last assignment.</summary>
    public ReadOnlySpan<float> RouteMaxSegmentVolume => _routeMaxSegmentVolume.AsSpan(0, _pendingRouteCount);

    /// <summary>Hourly passenger arrival rate at each stop from the last assignment.</summary>
    public ReadOnlySpan<float> StopArrivalRates => _stopArrivalRates.AsSpan(0, _pendingStopCount);

    public bool HasWarmEquilibrium(TransitNetworkSnapshot snapshot) =>
        snapshot != null &&
        _iterationCount > 0 &&
        snapshot.TopologyVersion == _warmTopologyVersion &&
        snapshot.RouteCount == _warmRouteCount &&
        snapshot.StopCount == _warmStopCount &&
        snapshot.SegmentCount == _warmSegmentCount;

    /// <summary>
    /// Computes <see cref="Skim"/> on <paramref name="snapshot"/> without accumulating passenger flows
    /// (used on cold start before the first OD calculation).
    /// </summary>
    public void ComputeSkim(TazManager tazManager, TransitNetworkSnapshot snapshot)
    {
        if (tazManager == null || snapshot == null) return;

        int k = tazManager.ActiveZones.Count;
        Skim.Reset(k, snapshot);
        if (k == 0 || snapshot.RouteCount == 0 || snapshot.StopCount == 0 || snapshot.SegmentCount == 0)
            return;

        PrepareTazAccess(tazManager, snapshot);
        RunStreamingRound(k, snapshot, matrix: null, accumulateFlows: false);
    }

    /// <summary>
    /// Runs streaming transit passenger assignment and updates <see cref="Skim"/> (including crowding penalties).
    /// When any line segment exceeds <see cref="TransitNetworkSnapshot.CrowdingOccupancyThreshold"/> (80% capacity)
    /// during a cold start, runs an additional MSA equilibrium iteration so overcrowded lines divert passengers
    /// to relief routes and record their crowding penalty in <see cref="Skim"/>.
    /// </summary>
    public void AssignFlows(
        TazManager tazManager,
        OptimizedODMatrix matrix,
        TransitNetworkSnapshot snapshot,
        RoadGraph graph = null,
        TransitManager transit = null,
        int coldMsaIterations = 2,
        bool deferPublish = false)
    {
        if (tazManager == null || matrix == null || snapshot == null) return;

        int k = tazManager.ActiveZones.Count;
        int routeCount = snapshot.RouteCount;
        int stopCount = snapshot.StopCount;
        int segCount = snapshot.SegmentCount;

        bool warm = WarmStart && HasWarmEquilibrium(snapshot);
        if (EnsureBuffersCapacity(routeCount, stopCount, segCount))
        {
            warm = false;
        }
        if (!warm)
        {
            _iterationCount = 0;
        }

        Skim.Reset(k, snapshot);
        _pendingRouteCount = routeCount;
        _pendingStopCount = stopCount;
        _pendingSegmentCount = segCount;
        if (_pendingStopNodeIds.Length < stopCount)
        {
            _pendingStopNodeIds = new int[Math.Max(stopCount * 2, 64)];
        }
        snapshot.StopNodeIds.CopyTo(_pendingStopNodeIds.AsSpan(0, stopCount));

        if (k == 0 || routeCount == 0 || stopCount == 0 || segCount == 0 || matrix.TotalTransitTrips <= 1e-4f)
        {
            Array.Clear(_segmentVolumes, 0, segCount);
            Array.Clear(_routeHourlyDemand, 0, routeCount);
            Array.Clear(_routeMaxSegmentVolume, 0, routeCount);
            Array.Clear(_stopArrivalRates, 0, stopCount);
            MaxSegmentLoadRatio = 0f;
            _iterationCount = 0;

            if (k > 0 && routeCount > 0 && stopCount > 0 && segCount > 0)
            {
                snapshot.ResetPerceivedTimes();
                PrepareTazAccess(tazManager, snapshot);
                RunStreamingRound(k, snapshot, matrix: null, accumulateFlows: false);
            }

            CommitOrPublish(transit, graph, deferPublish);
            return;
        }

        PrepareTazAccess(tazManager, snapshot);

        if (warm)
        {
            // Warm start: evaluate crowding from previous cycle's segment loads and run 1 MSA step
            snapshot.UpdateCrowdingPerceivedTimes(_segmentVolumes.AsSpan(0, segCount));
            RunStreamingRound(k, snapshot, matrix, accumulateFlows: true);

            _iterationCount++;
            float step = MathF.Max(1.0f / _iterationCount, MinWarmStepSize);
            BlendFlows(step, routeCount, stopCount, segCount);
            MaxSegmentLoadRatio = snapshot.UpdateCrowdingPerceivedTimes(_segmentVolumes.AsSpan(0, segCount));
        }
        else
        {
            // Cold start: round 0 on uncrowded base travel times
            snapshot.ResetPerceivedTimes();
            RunStreamingRound(k, snapshot, matrix, accumulateFlows: true);
            _iterationCount = 1;
            CopyAuxToCurrent(routeCount, stopCount, segCount);
            MaxSegmentLoadRatio = snapshot.UpdateCrowdingPerceivedTimes(_segmentVolumes.AsSpan(0, segCount));

            // If any segment is overcrowded (> 80%), run remaining MSA iterations to redistribute flow and update Skim
            int maxIters = Math.Clamp(coldMsaIterations, 1, 6);
            for (int iter = 1; iter < maxIters && MaxSegmentLoadRatio > TransitNetworkSnapshot.CrowdingOccupancyThreshold; iter++)
            {
                RunStreamingRound(k, snapshot, matrix, accumulateFlows: true);
                _iterationCount++;
                float step = 1.0f / _iterationCount;
                BlendFlows(step, routeCount, stopCount, segCount);
                MaxSegmentLoadRatio = snapshot.UpdateCrowdingPerceivedTimes(_segmentVolumes.AsSpan(0, segCount));
            }
        }

        ComputeRouteMaxSegmentVolumes(snapshot, routeCount, segCount);

        _warmTopologyVersion = snapshot.TopologyVersion;
        _warmRouteCount = routeCount;
        _warmStopCount = stopCount;
        _warmSegmentCount = segCount;

        CommitOrPublish(transit, graph, deferPublish);
    }

    /// <summary>
    /// Publishes the computed route demands, daily financials, occupancies and stop arrival rates to
    /// <paramref name="transit"/> on the main thread in O(routes + stops) time.
    /// </summary>
    public void PublishPendingDemand(TransitManager transit, RoadGraph graph = null)
    {
        if (!HasPendingDemand || transit == null) return;

        if (graph != null)
        {
            transit.UpdateAllRouteTravelTimes(graph);
        }

        transit.ApplyPublishedDemand(
            _routeHourlyDemand,
            _routeMaxSegmentVolume,
            _pendingRouteCount,
            _pendingStopNodeIds,
            _stopArrivalRates,
            _pendingStopCount);

        HasPendingDemand = false;
    }

    private void CommitOrPublish(TransitManager transit, RoadGraph graph, bool deferPublish)
    {
        HasPendingDemand = true;
        if (!deferPublish && transit != null)
        {
            PublishPendingDemand(transit, graph);
        }
    }

    private void PrepareTazAccess(TazManager tazManager, TransitNetworkSnapshot snapshot)
    {
        var tazList = tazManager.ActiveZones;
        int k = tazList.Count;
        int routeCount = snapshot.RouteCount;

        if (_tazAccessStart.Length < k + 1)
        {
            _tazAccessStart = new int[Math.Max((k + 1) * 2, 64)];
        }
        if (_routeSeenStamp.Length < routeCount)
        {
            _routeSeenStamp = new int[Math.Max(routeCount * 2, 32)];
        }
        Array.Clear(_routeSeenStamp, 0, routeCount);

        int[] stopRouteRowStart = snapshot.RawStopRouteRowStart;
        int[] stopRouteIndex = snapshot.RawStopRouteIndex;
        int[] stopRouteFwd = snapshot.RawStopRouteFwdState;
        int[] stopRouteBwd = snapshot.RawStopRouteBwdState;

        int cursor = 0;
        for (int i = 0; i < k; i++)
        {
            _tazAccessStart[i] = cursor;
            var taz = tazList[i];
            var stops = taz.TransitStops;
            if (stops.Count == 0) continue;

            int stamp = i + 1;
            for (int s = 0; s < stops.Count; s++)
            {
                int stopIdx = snapshot.GetStopIndex(stops[s].StopNodeId);
                if (stopIdx < 0) continue;

                int rEnd = stopRouteRowStart[stopIdx + 1];
                int rStart = stopRouteRowStart[stopIdx];
                EnsureTazAccessCapacity(cursor + (rEnd - rStart));

                for (int e = rStart; e < rEnd; e++)
                {
                    int r = stopRouteIndex[e];
                    if (_routeSeenStamp[r] == stamp) continue;
                    _routeSeenStamp[r] = stamp;

                    _tazAccessRoute[cursor] = r;
                    _tazAccessStop[cursor] = stopIdx;
                    _tazAccessFwdState[cursor] = stopRouteFwd[e];
                    _tazAccessBwdState[cursor] = stopRouteBwd[e];
                    cursor++;
                }
            }
        }
        _tazAccessStart[k] = cursor;
    }

    private void RunStreamingRound(
        int k,
        TransitNetworkSnapshot snapshot,
        OptimizedODMatrix matrix,
        bool accumulateFlows)
    {
        int stopCount = snapshot.StopCount;
        int routeCount = snapshot.RouteCount;
        int segCount = snapshot.SegmentCount;
        int stateCount = snapshot.StateCount;

        float[] routeWaitMinutes = snapshot.RawRouteWaitMinutes;
        int[] stateStopIndex = snapshot.RawStateStopIndex;
        int[] stateRouteIndex = snapshot.RawStateRouteIndex;
        int[] stateNextState = snapshot.RawStateNextState;
        int[] stateNextSeg = snapshot.RawStateNextSeg;
        float[] segBaseIvt = snapshot.RawSegBaseIvt;
        float[] segPerceivedIvt = snapshot.RawSegPerceivedIvt;
        int[] stopBoardRowStart = snapshot.RawStopBoardRowStart;
        int[] stopBoardState = snapshot.RawStopBoardState;
        int[] stopWalkRowStart = snapshot.RawStopWalkRowStart;
        int[] stopWalkTargetStop = snapshot.RawStopWalkTargetStop;
        float[] stopWalkMinutes = snapshot.RawStopWalkMinutes;

        Parallel.For(0, AccumulationChunks, chunk =>
        {
            int startTaz = (int)((long)k * chunk / AccumulationChunks);
            int endTaz = (int)((long)k * (chunk + 1) / AccumulationChunks);
            if (startTaz >= endTaz) return;

            var buf = _tlsBuffer ??= new SearchBuffer();
            buf.EnsureCapacity(stateCount);

            float[] dist = buf.Dist;
            float[] ivt = buf.Ivt;
            float[] waitArr = buf.Wait;
            float[] crowding = buf.Crowding;
            sbyte[] transfers = buf.Transfers;
            int[] parentState = buf.ParentState;
            int[] parentSeg = buf.ParentSeg;
            int[] visited = buf.Visited;
            float[] stateFlow = buf.StateFlow;
            var pq = buf.Queue;

            float[] chunkSegVols = accumulateFlows ? _chunkSegmentVolumes[chunk] : null;
            float[] chunkRouteDem = accumulateFlows ? _chunkRouteDemand[chunk] : null;
            float[] chunkStopArr = accumulateFlows ? _chunkStopArrivals[chunk] : null;

            for (int i = startTaz; i < endTaz; i++)
            {
                int accessStart = _tazAccessStart[i];
                int accessEnd = _tazAccessStart[i + 1];
                if (accessStart == accessEnd)
                {
                    Skim.ClearRow(i);
                    continue;
                }

                Array.Fill(dist, float.MaxValue, 0, stateCount);
                pq.Clear();
                int visitedCount = 0;

                // 1. Enqueue initial boardings at origin TAZ i's closest stop for each accessible route
                for (int a = accessStart; a < accessEnd; a++)
                {
                    int r = _tazAccessRoute[a];
                    float w = routeWaitMinutes[r];
                    float initCost = WaitTimeWeight * w;

                    int fwd = _tazAccessFwdState[a];
                    if (fwd >= 0 && initCost < dist[fwd])
                    {
                        dist[fwd] = initCost;
                        ivt[fwd] = 0f;
                        waitArr[fwd] = w;
                        crowding[fwd] = 0f;
                        transfers[fwd] = 0;
                        parentState[fwd] = -1;
                        parentSeg[fwd] = -1;
                        pq.Enqueue(fwd, initCost);
                    }

                    int bwd = _tazAccessBwdState[a];
                    if (bwd >= 0 && bwd != fwd && initCost < dist[bwd])
                    {
                        dist[bwd] = initCost;
                        ivt[bwd] = 0f;
                        waitArr[bwd] = w;
                        crowding[bwd] = 0f;
                        transfers[bwd] = 0;
                        parentState[bwd] = -1;
                        parentSeg[bwd] = -1;
                        pq.Enqueue(bwd, initCost);
                    }
                }

                // 2. Dijkstra search over the expanded stop-and-line network
                while (pq.TryDequeue(out int u, out float d))
                {
                    if (d > dist[u]) continue;
                    visited[visitedCount++] = u;

                    if (u >= stopCount)
                    {
                        // u is an on-board state: ride next segment along the same line
                        int nextState = stateNextState[u];
                        if (nextState >= 0)
                        {
                            int seg = stateNextSeg[u];
                            float segPerceived = segPerceivedIvt[seg];
                            float newDist = d + segPerceived;
                            if (newDist < dist[nextState])
                            {
                                float segBase = segBaseIvt[seg];
                                dist[nextState] = newDist;
                                ivt[nextState] = ivt[u] + segBase;
                                waitArr[nextState] = waitArr[u];
                                crowding[nextState] = crowding[u] + MathF.Max(0f, segPerceived - segBase);
                                transfers[nextState] = transfers[u];
                                parentState[nextState] = u;
                                parentSeg[nextState] = seg;
                                pq.Enqueue(nextState, newDist);
                            }
                        }

                        // Alight to stop hub (only after riding at least 1 segment on this route)
                        if (parentState[u] >= stopCount)
                        {
                            int stopHub = stateStopIndex[u];
                            if (d < dist[stopHub])
                            {
                                dist[stopHub] = d;
                                ivt[stopHub] = ivt[u];
                                waitArr[stopHub] = waitArr[u];
                                crowding[stopHub] = crowding[u];
                                transfers[stopHub] = transfers[u];
                                parentState[stopHub] = u;
                                parentSeg[stopHub] = -1;
                                pq.Enqueue(stopHub, d);
                            }
                        }
                    }
                    else
                    {
                        // u is a stop hub state (0..stopCount-1)
                        int alightedState = parentState[u];
                        int prevBoardState = alightedState >= stopCount
                            ? alightedState
                            : (alightedState >= 0 ? parentState[alightedState] : -1);
                        int prevRoute = prevBoardState >= stopCount ? stateRouteIndex[prevBoardState] : -1;

                        // Transfer-board onto another route at stop hub u
                        int bEnd = stopBoardRowStart[u + 1];
                        for (int b = stopBoardRowStart[u]; b < bEnd; b++)
                        {
                            int boardState = stopBoardState[b];
                            int r = stateRouteIndex[boardState];
                            if (r == prevRoute) continue;

                            float w = routeWaitMinutes[r];
                            float newDist = d + TransferPenaltyMinutes + WaitTimeWeight * w;
                            if (newDist < dist[boardState])
                            {
                                dist[boardState] = newDist;
                                ivt[boardState] = ivt[u];
                                waitArr[boardState] = waitArr[u] + w;
                                crowding[boardState] = crowding[u];
                                transfers[boardState] = (sbyte)Math.Min(126, transfers[u] + 1);
                                parentState[boardState] = u;
                                parentSeg[boardState] = -1;
                                pq.Enqueue(boardState, newDist);
                            }
                        }

                        // Walking transfer to a nearby stop s' (only immediately after alighting from a bus)
                        if (alightedState >= stopCount)
                        {
                            int wEnd = stopWalkRowStart[u + 1];
                            for (int w = stopWalkRowStart[u]; w < wEnd; w++)
                            {
                                int targetStop = stopWalkTargetStop[w];
                                float walkMin = stopWalkMinutes[w];
                                float newDist = d + WalkTimeWeight * walkMin;
                                if (newDist < dist[targetStop])
                                {
                                    dist[targetStop] = newDist;
                                    ivt[targetStop] = ivt[u] + walkMin;
                                    waitArr[targetStop] = waitArr[u];
                                    crowding[targetStop] = crowding[u];
                                    transfers[targetStop] = transfers[u];
                                    parentState[targetStop] = u; // < stopCount prevents multi-hop walking chains
                                    parentSeg[targetStop] = -1;
                                    pq.Enqueue(targetStop, newDist);
                                }
                            }
                        }
                    }
                }

                // 3. Evaluate destinations j = 0..k-1: write Skim row and seed leaf state flows
                Skim.SetUnconnected(i, i);
                bool hasOriginFlow = false;

                for (int j = 0; j < k; j++)
                {
                    if (j == i) continue;
                    int dStart = _tazAccessStart[j];
                    int dEnd = _tazAccessStart[j + 1];
                    if (dStart == dEnd)
                    {
                        Skim.SetUnconnected(i, j);
                        continue;
                    }

                    int bestState = -1;
                    float bestCost = float.MaxValue;
                    float bestIvt = 0f;
                    float bestWait = 0f;
                    float bestCrowd = 0f;
                    int bestTrans = -1;

                    for (int a = dStart; a < dEnd; a++)
                    {
                        int fwd = _tazAccessFwdState[a];
                        if (fwd >= 0)
                        {
                            EvaluateCandidateState(
                                fwd, stopCount, dist, ivt, waitArr, crowding, transfers, parentState,
                                ref bestState, ref bestCost, ref bestIvt, ref bestWait, ref bestCrowd, ref bestTrans);
                        }

                        int bwd = _tazAccessBwdState[a];
                        if (bwd >= 0 && bwd != fwd)
                        {
                            EvaluateCandidateState(
                                bwd, stopCount, dist, ivt, waitArr, crowding, transfers, parentState,
                                ref bestState, ref bestCost, ref bestIvt, ref bestWait, ref bestCrowd, ref bestTrans);
                        }
                    }

                    if (bestState >= 0)
                    {
                        Skim.SetConnection(i, j, bestIvt, bestWait, bestTrans, bestCrowd);
                        if (accumulateFlows)
                        {
                            float trips = matrix.GetTransitTrips(i, j);
                            if (trips > MinPropagatedFlow)
                            {
                                stateFlow[bestState] += trips;
                                hasOriginFlow = true;
                            }
                        }
                    }
                    else
                    {
                        Skim.SetUnconnected(i, j);
                    }
                }

                // 4. Leaf-to-root passenger flow accumulation in reverse settle order
                if (accumulateFlows && hasOriginFlow)
                {
                    for (int vIdx = visitedCount - 1; vIdx >= 0; vIdx--)
                    {
                        int curr = visited[vIdx];
                        float flow = stateFlow[curr];
                        if (flow <= 0f) continue;
                        stateFlow[curr] = 0f;

                        int p = parentState[curr];
                        if (curr >= stopCount)
                        {
                            if (p >= stopCount)
                            {
                                int seg = parentSeg[curr];
                                if (seg >= 0) chunkSegVols[seg] += flow;
                                stateFlow[p] += flow;
                            }
                            else
                            {
                                int r = stateRouteIndex[curr];
                                int s = stateStopIndex[curr];
                                chunkRouteDem[r] += flow;
                                chunkStopArr[s] += flow;
                                if (p >= 0) stateFlow[p] += flow;
                            }
                        }
                        else if (p >= 0)
                        {
                            stateFlow[p] += flow;
                        }
                    }
                }
            }
        });

        if (!accumulateFlows) return;

        // Deterministic chunk merge (0..AccumulationChunks-1) into auxiliary flow arrays
        for (int seg = 0; seg < segCount; seg++)
        {
            float sum = 0f;
            for (int c = 0; c < AccumulationChunks; c++)
            {
                sum += _chunkSegmentVolumes[c][seg];
                _chunkSegmentVolumes[c][seg] = 0f;
            }
            _auxSegmentVolumes[seg] = sum;
        }

        for (int r = 0; r < routeCount; r++)
        {
            float sum = 0f;
            for (int c = 0; c < AccumulationChunks; c++)
            {
                sum += _chunkRouteDemand[c][r];
                _chunkRouteDemand[c][r] = 0f;
            }
            _auxRouteHourlyDemand[r] = sum;
        }

        for (int s = 0; s < stopCount; s++)
        {
            float sum = 0f;
            for (int c = 0; c < AccumulationChunks; c++)
            {
                sum += _chunkStopArrivals[c][s];
                _chunkStopArrivals[c][s] = 0f;
            }
            _auxStopArrivalRates[s] = sum;
        }
    }

    private static void EvaluateCandidateState(
        int cand,
        int stopCount,
        float[] dist,
        float[] ivt,
        float[] waitArr,
        float[] crowding,
        sbyte[] transfers,
        int[] parentState,
        ref int bestState,
        ref float bestCost,
        ref float bestIvt,
        ref float bestWait,
        ref float bestCrowd,
        ref int bestTrans)
    {
        float d = dist[cand];
        if (d >= float.MaxValue * 0.5f) return;

        int p = parentState[cand];
        // A valid arrival state either rode at least 1 segment (p >= stopCount) or is a same-stop direct entry (p == -1)
        if (p >= 0 && p < stopCount) return;

        float candIvt;
        float candCrowd;
        float candCost;
        if (p == -1)
        {
            candIvt = 0.5f;
            candCrowd = 0f;
            candCost = d + 0.5f;
        }
        else
        {
            candIvt = MathF.Max(0.5f, ivt[cand]);
            candCrowd = crowding[cand];
            candCost = d;
        }

        if (candCost < bestCost)
        {
            bestCost = candCost;
            bestState = cand;
            bestIvt = candIvt;
            bestWait = waitArr[cand];
            bestCrowd = candCrowd;
            bestTrans = transfers[cand];
        }
    }

    private void CopyAuxToCurrent(int routeCount, int stopCount, int segCount)
    {
        Array.Copy(_auxSegmentVolumes, _segmentVolumes, segCount);
        Array.Copy(_auxRouteHourlyDemand, _routeHourlyDemand, routeCount);
        Array.Copy(_auxStopArrivalRates, _stopArrivalRates, stopCount);
    }

    private void BlendFlows(float stepSize, int routeCount, int stopCount, int segCount)
    {
        float keep = 1.0f - stepSize;
        for (int seg = 0; seg < segCount; seg++)
        {
            _segmentVolumes[seg] = keep * _segmentVolumes[seg] + stepSize * _auxSegmentVolumes[seg];
        }
        for (int r = 0; r < routeCount; r++)
        {
            _routeHourlyDemand[r] = keep * _routeHourlyDemand[r] + stepSize * _auxRouteHourlyDemand[r];
        }
        for (int s = 0; s < stopCount; s++)
        {
            _stopArrivalRates[s] = keep * _stopArrivalRates[s] + stepSize * _auxStopArrivalRates[s];
        }
    }

    private void ComputeRouteMaxSegmentVolumes(TransitNetworkSnapshot snapshot, int routeCount, int segCount)
    {
        Array.Clear(_routeMaxSegmentVolume, 0, routeCount);
        int[] segRoutes = snapshot.RawSegRouteIndex;
        for (int seg = 0; seg < segCount; seg++)
        {
            int r = segRoutes[seg];
            float vol = _segmentVolumes[seg];
            if (vol > _routeMaxSegmentVolume[r])
            {
                _routeMaxSegmentVolume[r] = vol;
            }
        }
    }

    private void EnsureTazAccessCapacity(int required)
    {
        if (_tazAccessRoute.Length >= required) return;
        int size = Math.Max(required * 2, 128);
        Array.Resize(ref _tazAccessRoute, size);
        Array.Resize(ref _tazAccessStop, size);
        Array.Resize(ref _tazAccessFwdState, size);
        Array.Resize(ref _tazAccessBwdState, size);
    }

    private bool EnsureBuffersCapacity(int routeCount, int stopCount, int segCount)
    {
        bool reallocated = false;

        if (_segmentVolumes.Length < segCount)
        {
            int size = Math.Max(segCount * 2, 128);
            _segmentVolumes = new float[size];
            _auxSegmentVolumes = new float[size];
            reallocated = true;
        }
        if (_routeHourlyDemand.Length < routeCount)
        {
            int size = Math.Max(routeCount * 2, 32);
            _routeHourlyDemand = new float[size];
            _auxRouteHourlyDemand = new float[size];
            _routeMaxSegmentVolume = new float[size];
            reallocated = true;
        }
        if (_stopArrivalRates.Length < stopCount)
        {
            int size = Math.Max(stopCount * 2, 64);
            _stopArrivalRates = new float[size];
            _auxStopArrivalRates = new float[size];
            reallocated = true;
        }

        if (_chunkSegmentVolumes.Length != AccumulationChunks || _chunkSegmentVolumes[0].Length < segCount)
        {
            int size = Math.Max(segCount * 2, 128);
            _chunkSegmentVolumes = new float[AccumulationChunks][];
            for (int c = 0; c < AccumulationChunks; c++) _chunkSegmentVolumes[c] = new float[size];
        }
        if (_chunkRouteDemand.Length != AccumulationChunks || _chunkRouteDemand[0].Length < routeCount)
        {
            int size = Math.Max(routeCount * 2, 32);
            _chunkRouteDemand = new float[AccumulationChunks][];
            for (int c = 0; c < AccumulationChunks; c++) _chunkRouteDemand[c] = new float[size];
        }
        if (_chunkStopArrivals.Length != AccumulationChunks || _chunkStopArrivals[0].Length < stopCount)
        {
            int size = Math.Max(stopCount * 2, 64);
            _chunkStopArrivals = new float[AccumulationChunks][];
            for (int c = 0; c < AccumulationChunks; c++) _chunkStopArrivals[c] = new float[size];
        }

        return reallocated;
    }
}
