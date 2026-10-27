using System;
using System.Collections.Generic;
using Godot;

namespace CitySim.Simulation;

/// <summary>
/// Manages spatial clustering of CityGrid zones and RoadsideParcels into Traffic Analysis Zones (TAZ),
/// road network centroid connectors, and public transit stop catchments.
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

    private readonly Dictionary<int, List<TransitRoute>> _stopNodeToRoutes = new();
    private readonly List<int> _nodeQueryBuffer = new(64);

    // Number of grid zones + parcels that contributed to TAZ buckets at the last full rebuild
    private int _contributingEntityCount = 0;

    internal TransitManager LastTransitManager { get; private set; }

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
        LastTransitManager = transitManager;
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
                AccumulateTotals(taz, zone);
            }
            foreach (var parcel in taz.Parcels)
            {
                AccumulateTotals(taz, parcel);
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
        LastTransitManager = transitManager;
        _spatialBuckets.Clear();
        _activeTazList.Clear();
        _parcelToTazId.Clear();
        _zoneTileToTazId.Clear();
        _stopNodeToRoutes.Clear();
        _contributingEntityCount = CountContributingEntities(cityGrid, parcelManager);

        if (roadGraph == null || roadGraph.Nodes.Count == 0)
        {
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
                if (!LandUse.ContributesToTaz(zone)) continue;

                Vector2 worldPos = cityGrid.GetWorldCenter(zone.Id);
                var taz = GetOrCreateBucket(worldPos);
                taz.Zones.Add(zone);
                AccumulateToTaz(taz, worldPos, zone);
            }
        }

        // 2. Aggregate continuous RoadsideParcels
        if (parcelManager != null)
        {
            foreach (var parcel in parcelManager.ActiveParcels)
            {
                if (!LandUse.ContributesToTaz(parcel)) continue;

                Vector2 worldPos = parcel.Center;
                var taz = GetOrCreateBucket(worldPos);
                taz.Parcels.Add(parcel);
                AccumulateToTaz(taz, worldPos, parcel);
            }
        }

        // 3. Index transit stops to serving routes for O(1) catchment lookup
        if (transitManager != null)
        {
            BuildStopToRoutesIndex(transitManager);
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
                if (LandUse.ContributesToTaz(cityGrid.GetZone(zoneId))) count++;
            }
        }
        if (parcelManager != null)
        {
            var parcels = parcelManager.Parcels;
            for (int i = 0; i < parcels.Count; i++)
            {
                if (LandUse.ContributesToTaz(parcels[i])) count++;
            }
        }
        return count;
    }

    private static void AccumulateToTaz(TrafficAnalysisZone taz, Vector2 pos, ILandUseUnit unit)
    {
        int activityWeight = unit.Population + unit.Jobs;
        if (activityWeight > 0)
        {
            taz.Centroid += pos * activityWeight;
        }

        AccumulateTotals(taz, unit);
    }

    private static void AccumulateTotals(TrafficAnalysisZone taz, ILandUseUnit unit)
    {
        ZoneType type = unit.LandUseType;
        int pop = unit.Population;
        int jobs = unit.Jobs;
        int capacity = LandUse.TotalCapacity(unit);

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
                taz.TransitStops.Add(new TazStopAccess(stopNodeId, dist));
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

        if (taz.TransitStops.Count > 1)
        {
            taz.TransitStops.Sort(static (a, b) =>
            {
                int cmp = a.DistanceMeters.CompareTo(b.DistanceMeters);
                return cmp != 0 ? cmp : a.StopNodeId.CompareTo(b.StopNodeId);
            });
        }

        if (taz.AccessibleTransitRoutes.Count > 0)
        {
            taz.AvgTransitAccessDistance = bestDist;
            taz.BestTransitWaitTime = bestWait;
        }
    }

    private void BuildStopToRoutesIndex(TransitManager transit)
    {
        foreach (var route in transit.Routes)
        {
            foreach (int stopId in route.StopNodeIds)
            {
                if (!_stopNodeToRoutes.TryGetValue(stopId, out var list))
                {
                    list = new List<TransitRoute>(4);
                    _stopNodeToRoutes[stopId] = list;
                }
                list.Add(route);
            }
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
