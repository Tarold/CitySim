using System;
using System.Collections.Generic;
using Godot;

namespace CitySim.Simulation;

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

    public IReadOnlyList<TrafficAnalysisZone> ActiveZones => _activeTazList;
    public bool IsDirty { get; set; } = true;

    public void MarkDirty() => IsDirty = true;

    public TrafficAnalysisZone GetTaz(int id)
    {
        if (id >= 0 && id < _activeTazList.Count)
            return _activeTazList[id];
        return null;
    }

    /// <summary>
    /// Full rebuild of all Traffic Analysis Zones in O(N + K*M) time.
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

        if (roadGraph == null || roadGraph.Nodes.Count == 0)
        {
            IsDirty = false;
            return;
        }

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

        IsDirty = false;
    }

    /// <summary>
    /// Determines whether transit travel between two TAZs is possible directly or via transfer.
    /// Returns: 0 = no connection, 1 = direct route, 2 = route with 1 transfer.
    /// </summary>
    public int GetTransitConnectionType(TrafficAnalysisZone origin, TrafficAnalysisZone dest)
    {
        if (origin.AccessibleTransitRoutes.Count == 0 || dest.AccessibleTransitRoutes.Count == 0)
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

    private static void AccumulateToTaz(
        TrafficAnalysisZone taz, Vector2 pos, ZoneType type, int pop, int jobs, int capacity)
    {
        int activityWeight = pop + jobs;
        if (activityWeight > 0)
        {
            taz.Centroid += pos * activityWeight;
        }

        taz.TotalPopulation += pop;
        taz.TotalCapacity += capacity;

        if (type == ZoneType.Commercial)
            taz.CommercialJobs += jobs;
        else if (type == ZoneType.Industrial)
            taz.IndustrialJobs += jobs;
    }

    private static void BindRoadConnectors(TrafficAnalysisZone taz, RoadGraph roadGraph)
    {
        const int maxConnectors = 3;
        Span<(int NodeId, float DistSq)> bestNodes = stackalloc (int, float)[maxConnectors];
        for (int i = 0; i < maxConnectors; i++)
            bestNodes[i] = (-1, float.MaxValue);

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

    private static void EvaluateTransitCatchment(
        TrafficAnalysisZone taz, TransitManager transit, RoadGraph roadGraph)
    {
        const float maxCatchmentMeters = 350.0f;
        float bestDist = float.MaxValue;
        float bestWait = 15.0f;

        foreach (var route in transit.Routes)
        {
            float routeWait = route.HeadwayMinutes * 0.5f;
            foreach (int stopNodeId in route.StopNodeIds)
            {
                var node = roadGraph.GetNode(stopNodeId);
                if (node == null) continue;

                float dist = taz.Centroid.DistanceTo(node.WorldPosition);
                if (dist <= maxCatchmentMeters)
                {
                    taz.AccessibleTransitRoutes.Add(route.Id);
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
