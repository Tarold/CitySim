using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;

namespace CitySim.Simulation;

/// <summary>
/// High-performance 4-step macroscopic Origin-Destination matrix computed between Traffic Analysis Zones (TAZ).
/// Uses flat continuous single-dimensional float arrays for cache locality and zero GC pressure,
/// multi-threaded parallel computation, reusable shortest path tree buffers, and equilibrium travel time updates.
/// </summary>
public sealed class OptimizedODMatrix
{
    private int _tazCount;

    private float[] _rawScores = Array.Empty<float>();
    private float[] _carTrips = Array.Empty<float>();
    private float[] _transitTrips = Array.Empty<float>();
    private float[] _walkTrips = Array.Empty<float>();
    private float[] _travelTimes = Array.Empty<float>();
    private float[] _rowScoreSums = Array.Empty<float>();
    private float[] _hansenPotentials = Array.Empty<float>();
    private float[] _accessibilityScores = Array.Empty<float>();

    /// <summary>
    /// Reusable pool of shortest path tree buffers (Zero-Allocation for MSA iterations).
    /// </summary>
    public ConcurrentDictionary<int, ShortestPathTreeBuffer> CachedTreeBuffers { get; } = new();

    /// <summary>
    /// Backward-compatible representation for external queries and tests.
    /// References the same arrays inside CachedTreeBuffers without memory copies.
    /// </summary>
    public ConcurrentDictionary<int, (float[] Distances, int[] ParentEdgeIds, int[] ParentNodeIds)> CachedShortestTrees { get; } = new();

    private readonly HashSet<int> _uniqueOriginNodes = new();
    private int[] _uniqueOriginNodesArray = Array.Empty<int>();

    public int TazCount => _tazCount;
    public float CurrentHour { get; set; }
    public float TotalTrips { get; private set; }
    public float TotalCarTrips { get; private set; }
    public float TotalTransitTrips { get; private set; }
    public float TotalWalkTrips { get; private set; }

    public float GetCarTrips(int originTaz, int destTaz) =>
        (originTaz >= 0 && originTaz < _tazCount && destTaz >= 0 && destTaz < _tazCount)
            ? _carTrips[originTaz * _tazCount + destTaz] : 0f;

    public float GetTransitTrips(int originTaz, int destTaz) =>
        (originTaz >= 0 && originTaz < _tazCount && destTaz >= 0 && destTaz < _tazCount)
            ? _transitTrips[originTaz * _tazCount + destTaz] : 0f;

    public float GetWalkTrips(int originTaz, int destTaz) =>
        (originTaz >= 0 && originTaz < _tazCount && destTaz >= 0 && destTaz < _tazCount)
            ? _walkTrips[originTaz * _tazCount + destTaz] : 0f;

    public float GetTravelTime(int originTaz, int destTaz) =>
        (originTaz >= 0 && originTaz < _tazCount && destTaz >= 0 && destTaz < _tazCount)
            ? _travelTimes[originTaz * _tazCount + destTaz] : float.MaxValue;

    public float GetRowScoreSum(int originTaz) =>
        (originTaz >= 0 && originTaz < _tazCount) ? _rowScoreSums[originTaz] : 0f;

    public float GetAccessibilityScore(int originTaz) =>
        (originTaz >= 0 && originTaz < _tazCount) ? _accessibilityScores[originTaz] : 0f;

    private void EnsureCapacity(int k)
    {
        _tazCount = k;
        int required = k * k;
        if (_rawScores.Length < required)
        {
            int allocSize = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(required, 16));
            _rawScores = new float[allocSize];
            _carTrips = new float[allocSize];
            _transitTrips = new float[allocSize];
            _walkTrips = new float[allocSize];
            _travelTimes = new float[allocSize];
        }
        if (_rowScoreSums.Length < k)
        {
            int rowAlloc = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(k, 16));
            _rowScoreSums = new float[rowAlloc];
            _hansenPotentials = new float[rowAlloc];
            _accessibilityScores = new float[rowAlloc];
        }
    }

    /// <summary>
    /// Updates shortest path trees in CachedTreeBuffers and populates travel times in _travelTimes.
    /// Called at the start of Recalculate() and after final MSA iterations.
    /// </summary>
    public void UpdateEquilibriumTreesAndTimes(TazManager tazManager, RoadGraph roadGraph)
    {
        if (tazManager == null || roadGraph == null) return;

        var tazList = tazManager.ActiveZones;
        int k = tazList.Count;
        if (k == 0) return;

        EnsureCapacity(k);

        _uniqueOriginNodes.Clear();
        for (int i = 0; i < k; i++)
        {
            int nodeId = tazList[i].PrimaryRoadNodeId;
            if (nodeId >= 0) _uniqueOriginNodes.Add(nodeId);
        }

        int uniqueCount = _uniqueOriginNodes.Count;
        if (_uniqueOriginNodesArray.Length < uniqueCount)
        {
            _uniqueOriginNodesArray = new int[Math.Max(uniqueCount * 2, 64)];
        }
        _uniqueOriginNodes.CopyTo(_uniqueOriginNodesArray);

        Parallel.For(0, uniqueCount, idx =>
        {
            int nodeId = _uniqueOriginNodesArray[idx];
            var buffer = CachedTreeBuffers.GetOrAdd(nodeId, _ => new ShortestPathTreeBuffer());
            roadGraph.ComputeDijkstraIntoBuffer(nodeId, buffer);

            // Backward compatibility without extra array allocation
            CachedShortestTrees[nodeId] = (buffer.Distances, buffer.ParentEdgeIds, buffer.ParentNodeIds);
        });

        Parallel.For(0, k, i =>
        {
            var origin = tazList[i];
            int rowOffset = i * k;

            if (!CachedTreeBuffers.TryGetValue(origin.PrimaryRoadNodeId, out var tree))
            {
                for (int j = 0; j < k; j++) _travelTimes[rowOffset + j] = float.MaxValue;
                return;
            }

            float[] distMap = tree.Distances;
            float origAccess = origin.Connectors.Count > 0 ? origin.Connectors[0].AccessTime : 0.5f;

            for (int j = 0; j < k; j++)
            {
                if (i == j)
                {
                    _travelTimes[rowOffset + j] = 1.8f;
                    continue;
                }

                var dest = tazList[j];
                float roadTime = (dest.PrimaryRoadNodeId >= 0 && dest.PrimaryRoadNodeId < distMap.Length)
                    ? distMap[dest.PrimaryRoadNodeId]
                    : float.MaxValue;

                if (roadTime >= float.MaxValue * 0.5f)
                {
                    _travelTimes[rowOffset + j] = float.MaxValue;
                    continue;
                }

                float destAccess = dest.Connectors.Count > 0 ? dest.Connectors[0].AccessTime : 0.5f;
                _travelTimes[rowOffset + j] = roadTime + origAccess + destAccess;
            }
        });
    }

    /// <summary>
    /// Full parallel recalculation of travel demand, shortest trees, gravity distribution, and 3-mode split.
    /// </summary>
    public void Recalculate(
        TazManager tazManager,
        RoadGraph roadGraph,
        float currentHour,
        float ticketFare = 12.0f)
    {
        CurrentHour = currentHour;
        TotalTrips = 0f;
        TotalCarTrips = 0f;
        TotalTransitTrips = 0f;
        TotalWalkTrips = 0f;

        if (tazManager == null || roadGraph == null) return;

        var tazList = tazManager.ActiveZones;
        int k = tazList.Count;
        if (k == 0)
        {
            _tazCount = 0;
            return;
        }

        EnsureCapacity(k);

        int required = k * k;
        Array.Clear(_rawScores, 0, required);
        Array.Clear(_carTrips, 0, required);
        Array.Clear(_transitTrips, 0, required);
        Array.Clear(_walkTrips, 0, required);

        float diurnalMultiplier = ODMatrix.GetDemandMultiplier(currentHour);
        float morningBias = ODMatrix.GetDirectionalBias(currentHour);

        int totalCityPop = 0;
        for (int i = 0; i < k; i++)
            totalCityPop += tazList[i].TotalPopulation;

        // 1 & 2. Compute shortest trees into CachedTreeBuffers and populate _travelTimes
        UpdateEquilibriumTreesAndTimes(tazManager, roadGraph);

        // 3. Gravity Model Trip Distribution & Hansen Accessibility Index
        Array.Clear(_rowScoreSums, 0, k);
        Array.Clear(_hansenPotentials, 0, k);
        Array.Clear(_accessibilityScores, 0, k);

        Parallel.For(0, k, i =>
        {
            var origin = tazList[i];
            float pop = origin.TotalPopulation > 0 ? origin.TotalPopulation : origin.TotalCapacity * 0.05f;
            float jobs = origin.TotalJobs > 0 ? origin.TotalJobs : origin.TotalCapacity * 0.05f;

            float production = pop * morningBias + jobs * (1.0f - morningBias);
            int rowOffset = i * k;
            if (production <= 0.01f) return;

            float localSum = 0f;
            for (int j = 0; j < k; j++)
            {
                float tCar = _travelTimes[rowOffset + j];
                if (tCar >= float.MaxValue * 0.5f)
                {
                    _rawScores[rowOffset + j] = 0f;
                    continue;
                }

                var dest = tazList[j];
                float destPop = dest.TotalPopulation > 0 ? dest.TotalPopulation : dest.TotalCapacity * 0.05f;
                float destJobs = dest.TotalJobs > 0 ? dest.TotalJobs : dest.TotalCapacity * 0.05f;

                float attraction = destJobs * morningBias + destPop * (1.0f - morningBias);
                if (attraction <= 0.01f)
                {
                    _rawScores[rowOffset + j] = 0f;
                    continue;
                }

                float effectiveTime = tCar;
                var connForGravity = tazManager.GetTransitConnectionInfo(origin, dest, tCar);
                if (connForGravity.IsConnected)
                {
                    float transitEquivTime = connForGravity.InVehicleTime + connForGravity.WaitTime * 0.5f;
                    effectiveTime = MathF.Min(tCar, transitEquivTime);
                }

                float impedance = 1.0f / MathF.Pow(effectiveTime + 2.0f, 1.25f);
                float score = production * attraction * impedance;
                _rawScores[rowOffset + j] = score;
                localSum += score;
            }

            _rowScoreSums[i] = localSum;
        });

        // Compute normalized Hansen AccessibilityScore (0.0..2.0) and congestion metrics for all TAZs
        UpdateAccessibilityScores(tazManager, roadGraph, morningBias);

        float targetHourlyDemand = 1.5f * totalCityPop * diurnalMultiplier;
        if (targetHourlyDemand <= 0.001f) return;

        float globalScoreSum = 0f;
        for (int i = 0; i < k; i++) globalScoreSum += _rowScoreSums[i];
        if (globalScoreSum <= 1e-5f) return;

        float normFactor = targetHourlyDemand / globalScoreSum;

        // 4. 3-Mode Multinomial Logit Choice Model (Walk / Car / Transit)
        const float lambda = 0.08f;
        const float walkSpeedMetersPerMin = 80.0f; // ~4.8 km/h

        float accTotalTrips = 0f;
        float accTotalCar = 0f;
        float accTotalTransit = 0f;
        float accTotalWalk = 0f;
        object lockObj = new object();

        Parallel.For(0, k, () => (trips: 0f, car: 0f, transit: 0f, walk: 0f), (i, state, localAcc) =>
        {
            var origin = tazList[i];
            int rowOffset = i * k;

            for (int j = 0; j < k; j++)
            {
                int idx = rowOffset + j;
                float totalTrips = _rawScores[idx] * normFactor;
                if (totalTrips < 0.01f) continue;

                var dest = tazList[j];
                float netDist = i == j
                    ? tazManager.CellSize * 0.35f
                    : origin.Centroid.DistanceTo(dest.Centroid) * 1.25f;

                float tWalk = netDist / walkSpeedMetersPerMin;
                float tCar = _travelTimes[idx];

                // Walking generalized cost: steep exponential penalty starting after 5 minutes (~400m)
                float cWalk = netDist < 400f
                    ? tWalk * 0.8f
                    : tWalk * 0.9f + 1.2f * MathF.Pow(MathF.Max(0f, tWalk - 5.0f), 1.5f);

                float cCar = (tCar * 1.4f) + 22.0f;

                float cTransit = 500.0f;
                var transitConn = tazManager.GetTransitConnectionInfo(origin, dest, tCar);
                if (transitConn.IsConnected)
                {
                    float accessWalkTime = (origin.AvgTransitAccessDistance + dest.AvgTransitAccessDistance) / walkSpeedMetersPerMin;
                    float waitTime = transitConn.WaitTime;
                    float transferPenalty = transitConn.Transfers * 8.0f;
                    cTransit = ticketFare + 0.8f * accessWalkTime + 1.2f * waitTime + transferPenalty + transitConn.InVehicleTime;
                }

                float uWalk = -lambda * cWalk;
                float uCar = -lambda * cCar;
                float uTransit = -lambda * cTransit;

                float uMax = MathF.Max(uWalk, MathF.Max(uCar, uTransit));
                float expWalk = MathF.Exp(uWalk - uMax);
                float expCar = MathF.Exp(uCar - uMax);
                float expTransit = MathF.Exp(uTransit - uMax);
                float expSum = expWalk + expCar + expTransit;

                float pWalk = expWalk / expSum;
                float pCar = expCar / expSum;

                float wTrips = totalTrips * pWalk;
                float cTrips = totalTrips * pCar;
                float tTrips = totalTrips - wTrips - cTrips;
                if (tTrips < 0f) tTrips = 0f;

                _walkTrips[idx] = wTrips;
                _carTrips[idx] = cTrips;
                _transitTrips[idx] = tTrips;

                localAcc.trips += totalTrips;
                localAcc.car += cTrips;
                localAcc.transit += tTrips;
                localAcc.walk += wTrips;
            }

            return localAcc;
        },
        localAcc =>
        {
            lock (lockObj)
            {
                accTotalTrips += localAcc.trips;
                accTotalCar += localAcc.car;
                accTotalTransit += localAcc.transit;
                accTotalWalk += localAcc.walk;
            }
        });

        TotalTrips = accTotalTrips;
        TotalCarTrips = accTotalCar;
        TotalTransitTrips = accTotalTransit;
        TotalWalkTrips = accTotalWalk;
    }

    /// <summary>
    /// Computes the Hansen Accessibility Index from the gravity model denominator (_rowScoreSums)
    /// and multimodal travel impedances, normalizes AccessibilityScore into [0.0, 2.0] for each TAZ,
    /// and propagates accessibility and congestion ratios (R_congestion) to child zones and parcels.
    /// </summary>
    public void UpdateAccessibilityScores(TazManager tazManager, RoadGraph roadGraph, float morningBias = -1f)
    {
        if (tazManager == null || roadGraph == null) return;

        var tazList = tazManager.ActiveZones;
        int k = tazList.Count;
        if (k == 0) return;

        EnsureCapacity(k);

        if (morningBias < 0f)
        {
            morningBias = ODMatrix.GetDirectionalBias(CurrentHour);
        }

        float totalCityOpportunity = 0f;
        for (int j = 0; j < k; j++)
        {
            var d = tazList[j];
            totalCityOpportunity += MathF.Max(1f, d.TotalJobs + 0.5f * d.TotalPopulation + 0.05f * d.TotalCapacity);
        }
        if (totalCityOpportunity <= 1e-4f) totalCityOpportunity = 1f;

        for (int i = 0; i < k; i++)
        {
            var origin = tazList[i];

            // 1. Evaluate local road congestion (R_congestion) in and around this TAZ
            var (avgCong, maxCong, hasBusLane) = EvaluateTazCongestion(origin, roadGraph);
            origin.AverageCongestion = avgCong;
            origin.MaxCongestion = maxCong;

            float pop = origin.TotalPopulation > 0 ? origin.TotalPopulation : origin.TotalCapacity * 0.05f;
            float jobs = origin.TotalJobs > 0 ? origin.TotalJobs : origin.TotalCapacity * 0.05f;
            float production = MathF.Max(1.0f, pop * morningBias + jobs * (1.0f - morningBias));

            int rowOffset = i * k;
            float rowGravitySum = 0f;
            float hansenOpportunitySum = 0f;
            int reachableDestinations = 0;

            for (int j = 0; j < k; j++)
            {
                float tCar = _travelTimes[rowOffset + j];
                if (tCar >= float.MaxValue * 0.5f) continue;

                var dest = tazList[j];
                float destPop = dest.TotalPopulation > 0 ? dest.TotalPopulation : dest.TotalCapacity * 0.05f;
                float destJobs = dest.TotalJobs > 0 ? dest.TotalJobs : dest.TotalCapacity * 0.05f;
                float attraction = destJobs * morningBias + destPop * (1.0f - morningBias);
                float opportunity = MathF.Max(1f, dest.TotalJobs + 0.5f * dest.TotalPopulation + 0.05f * dest.TotalCapacity);

                float effectiveTime = tCar;
                float carImpedance = 1.0f / MathF.Pow(tCar + 2.0f, 1.25f);
                float multimodalImpedance = carImpedance;

                var conn = tazManager.GetTransitConnectionInfo(origin, dest, tCar);
                if (conn.IsConnected)
                {
                    float transitEquivTime = conn.InVehicleTime + conn.WaitTime * 0.5f;
                    effectiveTime = MathF.Min(tCar, transitEquivTime);
                    float transitImpedance = 1.0f / MathF.Pow(transitEquivTime + 2.0f, 1.25f);
                    multimodalImpedance = MathF.Max(carImpedance, transitImpedance) + 0.45f * MathF.Min(carImpedance, transitImpedance);
                }

                float gravityImpedance = 1.0f / MathF.Pow(effectiveTime + 2.0f, 1.25f);
                if (attraction > 0.01f)
                {
                    rowGravitySum += production * attraction * gravityImpedance;
                }

                hansenOpportunitySum += opportunity * multimodalImpedance;
                reachableDestinations++;
            }

            if (_rowScoreSums[i] <= 0f && rowGravitySum > 0f)
            {
                _rowScoreSums[i] = rowGravitySum;
            }

            // Classic Hansen Accessibility derived from gravity denominator (_rowScoreSums[i] / production)
            // combined with time-invariant multimodal opportunity potential
            float gravityDenominator = _rowScoreSums[i] > 0f ? (_rowScoreSums[i] / production) : 0f;
            float baseHansen = hansenOpportunitySum > 0f
                ? (0.5f * hansenOpportunitySum + 0.5f * (gravityDenominator > 0f ? gravityDenominator : hansenOpportunitySum))
                : 0f;

            // Transit hub bonus: districts near transit routes (and route intersections) enjoy higher LUTI accessibility
            float transitHubBonus = 1.0f + 0.18f * Math.Min(origin.AccessibleTransitRoutes.Count, 4);
            if (hasBusLane && origin.AccessibleTransitRoutes.Count > 0)
            {
                transitHubBonus += 0.15f;
            }

            // Local congestion penalty when gridlocked (R_congestion > 1.0, severe above 1.5)
            float effectiveCong = MathF.Max(avgCong, maxCong * 0.85f);
            float congestionFactor = 1.0f;
            if (effectiveCong > 1.0f && !(hasBusLane && origin.AccessibleTransitRoutes.Count > 0))
            {
                congestionFactor = 1.0f / (1.0f + 0.85f * (effectiveCong - 1.0f));
            }

            origin.RawAccessibility = _rowScoreSums[i] > 0f ? _rowScoreSums[i] : baseHansen;
            _hansenPotentials[i] = reachableDestinations > 0
                ? (baseHansen * transitHubBonus * congestionFactor)
                : 0f;
        }

        // Normalize Hansen potentials into [0.0, 2.0] combining city-relative and absolute impedance benchmarks
        float sumPotentials = 0f;
        int connectedCount = 0;
        for (int i = 0; i < k; i++)
        {
            if (_hansenPotentials[i] > 1e-5f)
            {
                sumPotentials += _hansenPotentials[i];
                connectedCount++;
            }
        }

        float meanPotential = connectedCount > 0 ? (sumPotentials / connectedCount) : 0f;
        // Reference impedance for an 8-minute uncongested urban trip: (8 + 2)^(-1.25) = 0.056234
        const float referenceUrbanImpedance = 0.056234f;

        for (int i = 0; i < k; i++)
        {
            var taz = tazList[i];
            float pot = _hansenPotentials[i];
            if (pot <= 1e-5f || meanPotential <= 1e-5f)
            {
                taz.AccessibilityScore = 0.0f;
                _accessibilityScores[i] = 0.0f;
            }
            else
            {
                float relScore = pot / meanPotential;
                float normImpedance = pot / totalCityOpportunity;
                float absScore = normImpedance / referenceUrbanImpedance;

                float combined = 0.50f * relScore + 0.50f * absScore;
                float clamped = Math.Clamp(combined, 0.0f, 2.0f);
                taz.AccessibilityScore = clamped;
                _accessibilityScores[i] = clamped;
            }

            // Propagate LUTI AccessibilityScore and LocalCongestion to parcels and grid zones in this TAZ
            for (int p = 0; p < taz.Parcels.Count; p++)
            {
                var parcel = taz.Parcels[p];
                if (parcel == null) continue;
                parcel.AccessibilityScore = taz.AccessibilityScore;
                float edgeCong = GetParcelEdgeCongestion(parcel, roadGraph);
                parcel.LocalCongestion = MathF.Max(edgeCong, taz.AverageCongestion);
            }

            for (int z = 0; z < taz.Zones.Count; z++)
            {
                var zone = taz.Zones[z];
                if (zone == null) continue;
                zone.AccessibilityScore = taz.AccessibilityScore;
                float nodeCong = GetNodeCongestion(zone.Id, roadGraph);
                zone.LocalCongestion = MathF.Max(nodeCong, taz.AverageCongestion);
            }
        }
    }

    private static (float avgCong, float maxCong, bool hasBusLane) EvaluateTazCongestion(TrafficAnalysisZone taz, RoadGraph graph)
    {
        if (taz == null || graph == null) return (0f, 0f, false);

        float sumRatio = 0f;
        float maxRatio = 0f;
        int edgeCount = 0;
        bool hasBusLane = false;

        void InspectEdge(int edgeId)
        {
            var e = graph.GetEdge(edgeId);
            if (e == null || e.FromId == -1 || e.ToId == -1) return;
            if (e.HasBusLane) hasBusLane = true;
            float r = e.GetCongestionRatio();
            sumRatio += r;
            if (r > maxRatio) maxRatio = r;
            edgeCount++;
        }

        for (int i = 0; i < taz.InternalEdgeIds.Count; i++)
        {
            InspectEdge(taz.InternalEdgeIds[i]);
        }

        if (taz.PrimaryRoadNodeId >= 0 && graph.AdjacencyEdges.TryGetValue(taz.PrimaryRoadNodeId, out var primaryEdges))
        {
            for (int i = 0; i < primaryEdges.Count; i++)
            {
                int eid = primaryEdges[i];
                if (!taz.InternalEdgeIds.Contains(eid))
                {
                    InspectEdge(eid);
                }
            }
        }

        for (int c = 0; c < taz.Connectors.Count; c++)
        {
            int cNodeId = taz.Connectors[c].RoadNodeId;
            if (cNodeId >= 0 && cNodeId != taz.PrimaryRoadNodeId && graph.AdjacencyEdges.TryGetValue(cNodeId, out var connEdges))
            {
                for (int i = 0; i < connEdges.Count; i++)
                {
                    int eid = connEdges[i];
                    if (!taz.InternalEdgeIds.Contains(eid))
                    {
                        InspectEdge(eid);
                    }
                }
            }
        }

        float avg = edgeCount > 0 ? (sumRatio / edgeCount) : 0f;
        return (avg, maxRatio, hasBusLane);
    }

    public static float GetParcelEdgeCongestion(RoadsideParcel parcel, RoadGraph graph)
    {
        if (parcel == null || graph == null) return 0f;
        float maxCong = 0f;
        var edge = graph.GetEdge(parcel.EdgeId);
        if (edge != null && edge.FromId != -1 && edge.ToId != -1)
        {
            maxCong = MathF.Max(maxCong, edge.GetCongestionRatio());
            int revId = graph.GetReverseEdgeId(parcel.EdgeId);
            var revEdge = graph.GetEdge(revId);
            if (revEdge != null && revEdge.FromId != -1 && revEdge.ToId != -1)
            {
                maxCong = MathF.Max(maxCong, revEdge.GetCongestionRatio());
            }
        }
        return maxCong;
    }

    public static float GetNodeCongestion(int nodeId, RoadGraph graph)
    {
        if (nodeId < 0 || graph == null || !graph.AdjacencyEdges.TryGetValue(nodeId, out var edges) || edges.Count == 0)
            return 0f;

        float sum = 0f;
        float max = 0f;
        int count = 0;
        for (int i = 0; i < edges.Count; i++)
        {
            var e = graph.GetEdge(edges[i]);
            if (e == null || e.FromId == -1 || e.ToId == -1) continue;
            float r = e.GetCongestionRatio();
            sum += r;
            if (r > max) max = r;
            count++;
        }
        return count > 0 ? MathF.Max(sum / count, max * 0.85f) : 0f;
    }
}
