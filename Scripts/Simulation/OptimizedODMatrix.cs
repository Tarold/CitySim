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
            _rowScoreSums = new float[(int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(k, 16))];
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

        float targetHourlyDemand = 1.5f * totalCityPop * diurnalMultiplier;
        if (targetHourlyDemand <= 0.001f) return;

        // 1 & 2. Compute shortest trees into CachedTreeBuffers and populate _travelTimes
        UpdateEquilibriumTreesAndTimes(tazManager, roadGraph);

        // 3. Gravity Model Trip Distribution
        Array.Clear(_rowScoreSums, 0, k);

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

                float impedance = 1.0f / MathF.Pow(tCar + 2.0f, 1.25f);
                float score = production * attraction * impedance;
                _rawScores[rowOffset + j] = score;
                localSum += score;
            }

            _rowScoreSums[i] = localSum;
        });

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
                int transitConnType = tazManager.GetTransitConnectionType(origin, dest);
                if (transitConnType > 0)
                {
                    float accessWalkTime = (origin.AvgTransitAccessDistance + dest.AvgTransitAccessDistance) / walkSpeedMetersPerMin;
                    float waitTime = origin.BestTransitWaitTime;
                    float transferPenalty = transitConnType == 2 ? 8.0f : 0.0f;
                    cTransit = ticketFare + 0.8f * accessWalkTime + 1.2f * waitTime + transferPenalty + (tCar * 1.15f);
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
}
