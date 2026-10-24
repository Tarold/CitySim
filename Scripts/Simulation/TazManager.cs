using System;
using System.Collections.Generic;
using Godot;

namespace CitySim.Simulation;

/// <summary>
/// Represents precomputed public transit connection metrics between an origin and destination TAZ.
/// </summary>
public readonly struct TransitConnectionInfo
{
    public readonly int ConnectionType; // 0 = No connection, 1 = Direct route, 2 = 1-Transfer route
    public readonly float InVehicleTime;
    public readonly float WaitTime;
    public readonly int Transfers;

    public bool IsConnected => ConnectionType > 0;

    public TransitConnectionInfo(int connectionType, float inVehicleTime, float waitTime, int transfers)
    {
        ConnectionType = connectionType;
        InVehicleTime = inVehicleTime;
        WaitTime = waitTime;
        Transfers = transfers;
    }
}

/// <summary>
/// Manages spatial clustering of CityGrid zones and RoadsideParcels into Traffic Analysis Zones (TAZ),
/// road network centroid connectors, and public transit transfer routes.
/// </summary>
public sealed class TazManager
{
    /// <summary>
    /// Side length of spatial macro-cells in world units (meters/pixels).
    /// Typically 300-350 units yielding 50-350 active TAZ clusters across a metropolis.
    /// </summary>
    public float CellSize { get; set; } = 320.0f;

    private readonly Dictionary<Vector2I, TrafficAnalysisZone> _spatialBuckets = new();
    private readonly List<TrafficAnalysisZone> _activeTazList = new();
    private readonly Dictionary<int, int> _parcelToTazId = new();
    private readonly Dictionary<Vector2I, int> _zoneTileToTazId = new();

    // Route-to-Route adjacency for public transit transfers (routeId -> set of routes sharing at least one stop)
    private readonly Dictionary<int, HashSet<int>> _routeTransferMap = new();
    private readonly Dictionary<int, List<TransitRoute>> _stopNodeToRoutes = new();
    private readonly List<int> _nodeQueryBuffer = new(64);

    // Cached flat K*K array of real transit connection travel times and wait times
    private TransitConnectionInfo[] _transitConnectionsCache = Array.Empty<TransitConnectionInfo>();
    private int _cachedTazCount = 0;

    // Number of grid zones + parcels that contributed to TAZ buckets at the last full rebuild
    private int _contributingEntityCount = 0;

    public IReadOnlyList<TrafficAnalysisZone> ActiveZones => _activeTazList;
    public bool IsDirty { get; set; } = true;

    public void MarkDirty() => IsDirty = true;

    /// <summary>
    /// Brings the TAZ set up to date for a macro recalculation. When nothing structural changed since the
    /// last <see cref="Rebuild"/> (no <see cref="MarkDirty"/>, same set of contributing zones and parcels),
    /// only population, job and capacity totals are re-summed; clustering, centroids, road connectors,
    /// transit catchments and internal edges are kept. Otherwise performs a full <see cref="Rebuild"/>.
    /// </summary>
    /// <returns>True when a full rebuild was performed.</returns>
    public bool RefreshOrRebuild(
        CityGrid cityGrid,
        ParcelManager parcelManager,
        RoadGraph roadGraph,
        TransitManager transitManager)
    {
        if (TryRefreshAggregates(cityGrid, parcelManager)) return false;

        Rebuild(cityGrid, parcelManager, roadGraph, transitManager);
        return true;
    }

    /// <summary>
    /// Re-sums population, jobs and capacity of every active TAZ from its member zones and parcels.
    /// Fails (returns false) when the TAZ set is dirty or the set of contributing entities changed.
    /// </summary>
    public bool TryRefreshAggregates(CityGrid cityGrid, ParcelManager parcelManager)
    {
        if (IsDirty || CountContributingEntities(cityGrid, parcelManager) != _contributingEntityCount)
            return false;

        for (int i = 0; i < _activeTazList.Count; i++)
        {
            var taz = _activeTazList[i];
            taz.TotalPopulation = 0;
            taz.CommercialJobs = 0;
            taz.IndustrialJobs = 0;
            taz.TotalCapacity = 0;

            foreach (var zone in taz.Zones)
            {
                AccumulateTotals(taz, zone.Type, zone.Population, zone.Jobs, zone.ResidentialCap + zone.CommercialCap);
            }
            foreach (var parcel in taz.Parcels)
            {
                AccumulateTotals(taz, parcel.ZoneType, parcel.Population, parcel.Jobs, parcel.ResidentialCap + parcel.CommercialCap);
            }
        }

        return true;
    }

    public TrafficAnalysisZone GetTaz(int id)
    {
        if (id >= 0 && id < _activeTazList.Count)
            return _activeTazList[id];
        return null;
    }

    /// <summary>
    /// Full rebuild of all Traffic Analysis Zones in O(N + K) time using <see cref="SpatialGridIndex"/>.
    /// Triggered when zoning or road topology changes.
    /// </summary>
    public void Rebuild(
        CityGrid cityGrid,
        ParcelManager parcelManager,
        RoadGraph roadGraph,
        TransitManager transitManager)
    {
        _spatialBuckets.Clear();
        _activeTazList.Clear();
        _parcelToTazId.Clear();
        _zoneTileToTazId.Clear();
        _routeTransferMap.Clear();
        _stopNodeToRoutes.Clear();
        _contributingEntityCount = CountContributingEntities(cityGrid, parcelManager);

        if (roadGraph == null || roadGraph.Nodes.Count == 0)
        {
            _cachedTazCount = 0;
            IsDirty = false;
            return;
        }

        roadGraph.EnsureSpatialIndexSynced();

        // 1. Aggregate discrete CityGrid cells
        if (cityGrid != null)
        {
            foreach (int zoneId in cityGrid.ActiveZoneIds)
            {
                var zone = cityGrid.GetZone(zoneId);
                if (zone == null || zone.Type == ZoneType.Empty) continue;

                int cap = zone.ResidentialCap + zone.CommercialCap;
                if (zone.Population <= 0 && zone.Jobs <= 0 && cap <= 0) continue;

                Vector2 worldPos = cityGrid.GetWorldCenter(zone.Id);
                var taz = GetOrCreateBucket(worldPos);
                taz.Zones.Add(zone);
                AccumulateToTaz(taz, worldPos, zone.Type, zone.Population, zone.Jobs, cap);
            }
        }

        // 2. Aggregate continuous RoadsideParcels
        if (parcelManager != null)
        {
            foreach (var parcel in parcelManager.ActiveParcels)
            {
                if (parcel == null || parcel.ZoneType == ZoneType.Empty) continue;

                int cap = parcel.ResidentialCap + parcel.CommercialCap;
                if (parcel.Population <= 0 && parcel.Jobs <= 0 && cap <= 0) continue;

                Vector2 worldPos = parcel.Center;
                var taz = GetOrCreateBucket(worldPos);
                taz.Parcels.Add(parcel);
                AccumulateToTaz(taz, worldPos, parcel.ZoneType, parcel.Population, parcel.Jobs, cap);
            }
        }

        // 3. Build transit route transfer connectivity map
        if (transitManager != null)
        {
            BuildRouteTransferTable(transitManager);
        }

        // 4. Finalize weighted centroids and bind road connectors
        int nextId = 0;
        foreach (var kvp in _spatialBuckets)
        {
            var taz = kvp.Value;
            int activeWeight = taz.TotalPopulation + taz.TotalJobs;

            if (activeWeight > 0)
            {
                taz.Centroid /= activeWeight;
            }
            else if (taz.Zones.Count + taz.Parcels.Count > 0)
            {
                // Cold-start fallback for newly zoned empty parcels with capacity
                Vector2 sum = Vector2.Zero;
                if (cityGrid != null)
                {
                    foreach (var z in taz.Zones) sum += cityGrid.GetWorldCenter(z.Id);
                }
                foreach (var p in taz.Parcels) sum += p.Center;
                taz.Centroid = sum / (taz.Zones.Count + taz.Parcels.Count);
            }
            else
            {
                continue;
            }

            BindRoadConnectors(taz, roadGraph);
            if (taz.PrimaryRoadNodeId == -1) continue;

            taz.Id = nextId++;
            foreach (var z in taz.Zones) _zoneTileToTazId[z.GridPos] = taz.Id;
            foreach (var p in taz.Parcels) _parcelToTazId[p.Id] = taz.Id;

            if (transitManager != null)
            {
                EvaluateTransitCatchment(taz, transitManager, roadGraph);
            }

            _activeTazList.Add(taz);
        }

        // 5. Assign internal edges for local neighbourhood and pedestrian flow
        AssignInternalEdgesToBuckets(roadGraph);

        // 6. Precompute exact in-vehicle transit travel times between all TAZ pairs
        UpdateTransitConnectionMatrix(transitManager, roadGraph);

        IsDirty = false;
    }

    /// <summary>
    /// Updates the cached K*K matrix of real public transit in-vehicle times and wait times
    /// using current route prefix sums (reflecting BPR road congestion and dedicated bus lanes).
    /// </summary>
    public void UpdateTransitConnectionMatrix(TransitManager transit, RoadGraph roadGraph)
    {
        int k = _activeTazList.Count;
        _cachedTazCount = k;
        if (k == 0) return;

        int required = k * k;
        if (_transitConnectionsCache.Length < required)
        {
            int allocSize = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(required, 16));
            _transitConnectionsCache = new TransitConnectionInfo[allocSize];
        }
        Array.Clear(_transitConnectionsCache, 0, required);

        if (transit == null || transit.Routes.Count == 0 || roadGraph == null)
            return;

        transit.UpdateAllRouteTravelTimes(roadGraph);

        for (int i = 0; i < k; i++)
        {
            var origin = _activeTazList[i];
            if (origin.AccessibleTransitRoutes.Count == 0) continue;

            int rowOffset = i * k;
            for (int j = 0; j < k; j++)
            {
                if (i == j) continue;
                var dest = _activeTazList[j];
                if (dest.AccessibleTransitRoutes.Count == 0) continue;

                // 1. Check direct shared routes
                float bestDirectCost = float.MaxValue;
                float bestDirectIvt = 0f;
                float bestDirectWait = 0f;
                bool foundDirect = false;

                foreach (int routeId in origin.AccessibleTransitRoutes)
                {
                    if (!dest.AccessibleTransitRoutes.Contains(routeId)) continue;

                    var route = FindRouteById(transit, routeId);
                    if (route == null) continue;

                    int sOrig = FindClosestStopOnRoute(origin, route, roadGraph);
                    int sDest = FindClosestStopOnRoute(dest, route, roadGraph);
                    if (sOrig == -1 || sDest == -1) continue;

                    float ivt = route.GetInVehicleTimeBetweenStops(sOrig, sDest, roadGraph);
                    float wait = route.HeadwayMinutes * 0.5f;
                    float totalCost = ivt + 1.2f * wait;

                    if (totalCost < bestDirectCost)
                    {
                        bestDirectCost = totalCost;
                        bestDirectIvt = ivt;
                        bestDirectWait = wait;
                        foundDirect = true;
                    }
                }

                if (foundDirect)
                {
                    _transitConnectionsCache[rowOffset + j] = new TransitConnectionInfo(
                        connectionType: 1,
                        inVehicleTime: bestDirectIvt,
                        waitTime: bestDirectWait,
                        transfers: 0);
                    continue;
                }

                // 2. Check 1-transfer route chains
                float bestTransferCost = float.MaxValue;
                float bestTransferIvt = 0f;
                float bestTransferWait = 0f;
                bool foundTransfer = false;

                foreach (int r1Id in origin.AccessibleTransitRoutes)
                {
                    if (!_routeTransferMap.TryGetValue(r1Id, out var connectedRoutes)) continue;
                    var r1 = FindRouteById(transit, r1Id);
                    if (r1 == null) continue;

                    int sOrig = FindClosestStopOnRoute(origin, r1, roadGraph);
                    if (sOrig == -1) continue;

                    foreach (int r2Id in dest.AccessibleTransitRoutes)
                    {
                        if (r1Id == r2Id || !connectedRoutes.Contains(r2Id)) continue;
                        var r2 = FindRouteById(transit, r2Id);
                        if (r2 == null) continue;

                        int sTransfer = FindBestTransferStop(r1, r2, origin.Centroid, dest.Centroid, roadGraph);
                        int sDest = FindClosestStopOnRoute(dest, r2, roadGraph);
                        if (sTransfer == -1 || sDest == -1) continue;

                        float ivt1 = r1.GetInVehicleTimeBetweenStops(sOrig, sTransfer, roadGraph);
                        float ivt2 = r2.GetInVehicleTimeBetweenStops(sTransfer, sDest, roadGraph);
                        float totalIvt = ivt1 + ivt2;
                        float totalWait = (r1.HeadwayMinutes + r2.HeadwayMinutes) * 0.5f;
                        float totalCost = totalIvt + 1.2f * totalWait + 8.0f;

                        if (totalCost < bestTransferCost)
                        {
                            bestTransferCost = totalCost;
                            bestTransferIvt = totalIvt;
                            bestTransferWait = totalWait;
                            foundTransfer = true;
                        }
                    }
                }

                if (foundTransfer)
                {
                    _transitConnectionsCache[rowOffset + j] = new TransitConnectionInfo(
                        connectionType: 2,
                        inVehicleTime: bestTransferIvt,
                        waitTime: bestTransferWait,
                        transfers: 1);
                }
            }
        }
    }

    /// <summary>
    /// Returns the precomputed real transit connection info (in-vehicle time, wait time, transfers) between two TAZ IDs in O(1) time.
    /// Falls back to topological connection evaluation if the cache was not populated with a TransitManager.
    /// </summary>
    public TransitConnectionInfo GetTransitConnectionInfo(TrafficAnalysisZone origin, TrafficAnalysisZone dest, float fallbackCarTime = 10f)
    {
        if (origin == null || dest == null || origin.Id == dest.Id)
            return default;

        if (_cachedTazCount > 0 &&
            origin.Id >= 0 && origin.Id < _cachedTazCount &&
            dest.Id >= 0 && dest.Id < _cachedTazCount)
        {
            var cached = _transitConnectionsCache[origin.Id * _cachedTazCount + dest.Id];
            if (cached.IsConnected)
                return cached;
        }

        // Fallback if routes were added to AccessibleTransitRoutes manually in unit tests without full TransitManager
        int connType = GetTransitConnectionType(origin, dest);
        if (connType > 0)
        {
            return new TransitConnectionInfo(
                connectionType: connType,
                inVehicleTime: fallbackCarTime * 1.15f,
                waitTime: origin.BestTransitWaitTime,
                transfers: connType == 2 ? 1 : 0);
        }

        return default;
    }

    /// <summary>
    /// Determines whether transit travel between two TAZs is possible directly or via transfer.
    /// Returns: 0 = no connection, 1 = direct route, 2 = route with 1 transfer.
    /// </summary>
    public int GetTransitConnectionType(TrafficAnalysisZone origin, TrafficAnalysisZone dest)
    {
        if (origin == null || dest == null || origin.Id == dest.Id ||
            origin.AccessibleTransitRoutes.Count == 0 || dest.AccessibleTransitRoutes.Count == 0)
            return 0;

        // Check for direct shared route
        if (origin.AccessibleTransitRoutes.Overlaps(dest.AccessibleTransitRoutes))
            return 1;

        // Check for single transfer between intersecting routes
        foreach (int r1 in origin.AccessibleTransitRoutes)
        {
            if (_routeTransferMap.TryGetValue(r1, out var connectedRoutes) &&
                connectedRoutes.Overlaps(dest.AccessibleTransitRoutes))
            {
                return 2;
            }
        }

        return 0;
    }

    /// <summary>
    /// Populates the provided list with route IDs that directly serve both origin and destination TAZs.
    /// </summary>
    public void GetDirectRouteIds(TrafficAnalysisZone origin, TrafficAnalysisZone dest, List<int> results)
    {
        results.Clear();
        if (origin == null || dest == null ||
            origin.AccessibleTransitRoutes.Count == 0 || dest.AccessibleTransitRoutes.Count == 0)
            return;

        foreach (int routeId in origin.AccessibleTransitRoutes)
        {
            if (dest.AccessibleTransitRoutes.Contains(routeId))
            {
                results.Add(routeId);
            }
        }
    }

    /// <summary>
    /// Populates the provided list with 1-transfer route chains (FirstRouteId, SecondRouteId, TransferStopNodeId)
    /// connecting origin TAZ to destination TAZ via a shared stop.
    /// </summary>
    public void GetTransferRoutes(
        TrafficAnalysisZone origin,
        TrafficAnalysisZone dest,
        TransitManager transit,
        RoadGraph graph,
        List<(int FirstRouteId, int SecondRouteId, int TransferStopNodeId)> results)
    {
        results.Clear();
        if (origin == null || dest == null || transit == null ||
            origin.AccessibleTransitRoutes.Count == 0 || dest.AccessibleTransitRoutes.Count == 0)
            return;

        foreach (int r1Id in origin.AccessibleTransitRoutes)
        {
            if (!_routeTransferMap.TryGetValue(r1Id, out var connectedRoutes)) continue;

            var r1 = FindRouteById(transit, r1Id);
            if (r1 == null) continue;

            foreach (int r2Id in dest.AccessibleTransitRoutes)
            {
                if (r1Id == r2Id || !connectedRoutes.Contains(r2Id)) continue;

                var r2 = FindRouteById(transit, r2Id);
                if (r2 == null) continue;

                int transferStop = FindBestTransferStop(r1, r2, origin.Centroid, dest.Centroid, graph);
                if (transferStop != -1)
                {
                    results.Add((r1Id, r2Id, transferStop));
                }
            }
        }
    }

    /// <summary>
    /// Finds the stop node ID on the given route closest to the TAZ centroid.
    /// Returns -1 if the route has no stops.
    /// </summary>
    public int FindClosestStopOnRoute(TrafficAnalysisZone taz, TransitRoute route, RoadGraph graph)
    {
        if (taz == null || route == null || route.StopNodeIds.Count == 0)
            return -1;

        int bestStopId = -1;
        float bestDistSq = float.MaxValue;

        foreach (int stopNodeId in route.StopNodeIds)
        {
            var node = graph?.GetNode(stopNodeId);
            if (node == null)
            {
                if (bestStopId == -1) bestStopId = stopNodeId;
                continue;
            }

            float dSq = taz.Centroid.DistanceSquaredTo(node.WorldPosition);
            if (dSq < bestDistSq)
            {
                bestDistSq = dSq;
                bestStopId = stopNodeId;
            }
        }

        return bestStopId;
    }

    private static int FindBestTransferStop(
        TransitRoute r1,
        TransitRoute r2,
        Vector2 originCentroid,
        Vector2 destCentroid,
        RoadGraph graph)
    {
        int bestStopId = -1;
        float bestDist = float.MaxValue;

        foreach (int stopId in r1.StopNodeIds)
        {
            if (!r2.StopNodeIds.Contains(stopId)) continue;

            var node = graph?.GetNode(stopId);
            if (node == null)
            {
                if (bestStopId == -1) bestStopId = stopId;
                continue;
            }

            float d = originCentroid.DistanceTo(node.WorldPosition) + destCentroid.DistanceTo(node.WorldPosition);
            if (d < bestDist)
            {
                bestDist = d;
                bestStopId = stopId;
            }
        }

        return bestStopId;
    }

    private static TransitRoute FindRouteById(TransitManager transit, int routeId)
    {
        for (int i = 0; i < transit.Routes.Count; i++)
        {
            if (transit.Routes[i].Id == routeId)
                return transit.Routes[i];
        }
        return null;
    }

    private TrafficAnalysisZone GetOrCreateBucket(Vector2 worldPos)
    {
        var coord = new Vector2I(
            Mathf.FloorToInt(worldPos.X / CellSize),
            Mathf.FloorToInt(worldPos.Y / CellSize)
        );

        if (!_spatialBuckets.TryGetValue(coord, out var taz))
        {
            taz = new TrafficAnalysisZone { GridCoord = coord, Centroid = Vector2.Zero };
            _spatialBuckets[coord] = taz;
        }
        return taz;
    }

    /// <summary>Zones and parcels that would be aggregated into a TAZ bucket (non-empty with activity or capacity).</summary>
    private static int CountContributingEntities(CityGrid cityGrid, ParcelManager parcelManager)
    {
        int count = 0;
        if (cityGrid != null)
        {
            foreach (int zoneId in cityGrid.ActiveZoneIds)
            {
                var zone = cityGrid.GetZone(zoneId);
                if (zone == null || zone.Type == ZoneType.Empty) continue;
                if (zone.Population > 0 || zone.Jobs > 0 || zone.ResidentialCap + zone.CommercialCap > 0) count++;
            }
        }
        if (parcelManager != null)
        {
            var parcels = parcelManager.Parcels;
            for (int i = 0; i < parcels.Count; i++)
            {
                var parcel = parcels[i];
                if (parcel == null || parcel.ZoneType == ZoneType.Empty) continue;
                if (parcel.Population > 0 || parcel.Jobs > 0 || parcel.ResidentialCap + parcel.CommercialCap > 0) count++;
            }
        }
        return count;
    }

    private static void AccumulateToTaz(
        TrafficAnalysisZone taz, Vector2 pos, ZoneType type, int pop, int jobs, int capacity)
    {
        int activityWeight = pop + jobs;
        if (activityWeight > 0)
        {
            taz.Centroid += pos * activityWeight;
        }

        AccumulateTotals(taz, type, pop, jobs, capacity);
    }

    private static void AccumulateTotals(TrafficAnalysisZone taz, ZoneType type, int pop, int jobs, int capacity)
    {
        taz.TotalPopulation += pop;
        taz.TotalCapacity += capacity;

        if (type == ZoneType.Commercial)
            taz.CommercialJobs += jobs;
        else if (type == ZoneType.Industrial)
            taz.IndustrialJobs += jobs;
    }

    private void BindRoadConnectors(TrafficAnalysisZone taz, RoadGraph roadGraph)
    {
        const int maxConnectors = 3;
        Span<(int NodeId, float DistSq)> bestNodes = stackalloc (int, float)[maxConnectors];
        for (int i = 0; i < maxConnectors; i++)
            bestNodes[i] = (-1, float.MaxValue);

        // 1. Primary O(1) lookup in 3x3 neighboring SpatialGridIndex cells (384x384m)
        roadGraph.SpatialIndex.QueryNodesInCellNeighborhood(taz.Centroid, cellRadius: 1, _nodeQueryBuffer);

        // 2. If fewer than maxConnectors found in immediate 3x3 cells, expand to 450m secondary connector radius
        if (_nodeQueryBuffer.Count < maxConnectors)
        {
            roadGraph.SpatialIndex.QueryNodesInRadius(taz.Centroid, 450.0f, _nodeQueryBuffer);
        }

        if (_nodeQueryBuffer.Count > 0)
        {
            for (int n = 0; n < _nodeQueryBuffer.Count; n++)
            {
                var node = roadGraph.GetNode(_nodeQueryBuffer[n]);
                if (node == null) continue;

                float dSq = taz.Centroid.DistanceSquaredTo(node.WorldPosition);
                for (int i = 0; i < maxConnectors; i++)
                {
                    if (dSq < bestNodes[i].DistSq)
                    {
                        for (int j = maxConnectors - 1; j > i; j--)
                            bestNodes[j] = bestNodes[j - 1];
                        bestNodes[i] = (node.Id, dSq);
                        break;
                    }
                }
            }
        }
        else
        {
            // Fallback for isolated synthetic test topologies where nearest road node is > 450m away
            foreach (var node in roadGraph.Nodes)
            {
                float dSq = taz.Centroid.DistanceSquaredTo(node.WorldPosition);
                for (int i = 0; i < maxConnectors; i++)
                {
                    if (dSq < bestNodes[i].DistSq)
                    {
                        for (int j = maxConnectors - 1; j > i; j--)
                            bestNodes[j] = bestNodes[j - 1];
                        bestNodes[i] = (node.Id, dSq);
                        break;
                    }
                }
            }
        }

        if (bestNodes[0].NodeId == -1) return;
        taz.PrimaryRoadNodeId = bestNodes[0].NodeId;

        float weightSum = 0f;
        Span<float> rawWeights = stackalloc float[maxConnectors];
        Span<float> accessTimes = stackalloc float[maxConnectors];

        for (int i = 0; i < maxConnectors; i++)
        {
            if (bestNodes[i].NodeId == -1) continue;
            float dist = Mathf.Sqrt(bestNodes[i].DistSq);

            if (i > 0 && dist > 450f)
            {
                bestNodes[i] = (-1, float.MaxValue);
                continue;
            }

            rawWeights[i] = 1.0f / (dist + 25.0f);
            accessTimes[i] = dist / 250.0f;
            weightSum += rawWeights[i];
        }

        for (int i = 0; i < maxConnectors; i++)
        {
            if (bestNodes[i].NodeId == -1) continue;
            taz.Connectors.Add(new TazConnector(
                bestNodes[i].NodeId,
                rawWeights[i] / weightSum,
                accessTimes[i]
            ));
        }
    }

    private void EvaluateTransitCatchment(
        TrafficAnalysisZone taz, TransitManager transit, RoadGraph roadGraph)
    {
        if (transit.Routes.Count == 0 || _stopNodeToRoutes.Count == 0)
            return;

        const float maxCatchmentMeters = 350.0f;
        float bestDist = float.MaxValue;
        float bestWait = 15.0f;

        // Query SpatialGridIndex neighborhood around TAZ centroid (radius 350m / surrounding cells)
        roadGraph.SpatialIndex.QueryNodesInRadius(taz.Centroid, maxCatchmentMeters, _nodeQueryBuffer);

        for (int i = 0; i < _nodeQueryBuffer.Count; i++)
        {
            int stopNodeId = _nodeQueryBuffer[i];
            if (!_stopNodeToRoutes.TryGetValue(stopNodeId, out var routesAtStop))
                continue;

            var node = roadGraph.GetNode(stopNodeId);
            if (node == null) continue;

            float dist = taz.Centroid.DistanceTo(node.WorldPosition);
            if (dist <= maxCatchmentMeters)
            {
                for (int r = 0; r < routesAtStop.Count; r++)
                {
                    var route = routesAtStop[r];
                    taz.AccessibleTransitRoutes.Add(route.Id);
                    float routeWait = route.HeadwayMinutes * 0.5f;
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestWait = routeWait;
                    }
                }
            }
        }

        if (taz.AccessibleTransitRoutes.Count > 0)
        {
            taz.AvgTransitAccessDistance = bestDist;
            taz.BestTransitWaitTime = bestWait;
        }
    }

    private void BuildRouteTransferTable(TransitManager transit)
    {
        foreach (var r1 in transit.Routes)
        {
            foreach (int stopId in r1.StopNodeIds)
            {
                if (!_stopNodeToRoutes.TryGetValue(stopId, out var list))
                {
                    list = new List<TransitRoute>(4);
                    _stopNodeToRoutes[stopId] = list;
                }
                list.Add(r1);
            }

            var set = new HashSet<int>();
            foreach (var r2 in transit.Routes)
            {
                if (r1.Id == r2.Id) continue;
                if (r1.StopNodeIds.Overlaps(r2.StopNodeIds))
                {
                    set.Add(r2.Id);
                }
            }
            _routeTransferMap[r1.Id] = set;
        }
    }

    private void AssignInternalEdgesToBuckets(RoadGraph roadGraph)
    {
        foreach (var edge in roadGraph.Edges)
        {
            var fromNode = roadGraph.GetNode(edge.FromId);
            var toNode = roadGraph.GetNode(edge.ToId);
            if (fromNode == null || toNode == null) continue;

            Vector2 midPoint = (fromNode.WorldPosition + toNode.WorldPosition) * 0.5f;
            var coord = new Vector2I(
                Mathf.FloorToInt(midPoint.X / CellSize),
                Mathf.FloorToInt(midPoint.Y / CellSize)
            );

            if (_spatialBuckets.TryGetValue(coord, out var taz) && taz.PrimaryRoadNodeId != -1)
            {
                taz.InternalEdgeIds.Add(edge.Id);
            }
        }
    }

    public int GetTazIdForParcel(int parcelId) =>
        _parcelToTazId.TryGetValue(parcelId, out int id) ? id : -1;

    public int GetTazIdForZoneTile(Vector2I tile) =>
        _zoneTileToTazId.TryGetValue(tile, out int id) ? id : -1;
}
