using Godot;
using System;
using System.Collections.Generic;

namespace CitySim.Simulation;

/// <summary>
/// Represents a non-tile dynamic continuous origin-destination trip between roadside parcels and/or zones.
/// </summary>
public class DynamicTrip
{
    /// <summary>Origin legacy grid zone ID, or -1 if roadside parcel.</summary>
    public int OriginZoneId { get; set; } = -1;

    /// <summary>Origin roadside parcel ID, or -1 if grid zone.</summary>
    public int OriginParcelId { get; set; } = -1;

    /// <summary>Nearest road graph access node ID for the origin.</summary>
    public int OriginAccessNodeId { get; set; } = -1;

    /// <summary>2D world position of the origin.</summary>
    public Vector2 OriginPos { get; set; }

    /// <summary>Zoning type at the trip origin.</summary>
    public ZoneType OriginType { get; set; }

    /// <summary>Destination legacy grid zone ID, or -1 if roadside parcel.</summary>
    public int DestZoneId { get; set; } = -1;

    /// <summary>Destination roadside parcel ID, or -1 if grid zone.</summary>
    public int DestParcelId { get; set; } = -1;

    /// <summary>Nearest road graph access node ID for the destination.</summary>
    public int DestAccessNodeId { get; set; } = -1;

    /// <summary>2D world position of the destination.</summary>
    public Vector2 DestPos { get; set; }

    /// <summary>Zoning type at the trip destination.</summary>
    public ZoneType DestType { get; set; }

    /// <summary>Total hourly trips generated for this OD pair.</summary>
    public float TotalTrips { get; set; }

    /// <summary>Hourly private car trips assigned to the road network.</summary>
    public float CarTrips { get; set; }

    /// <summary>Hourly public transit trips.</summary>
    public float TransitTrips { get; set; }

    /// <summary>Hourly pure pedestrian trips assigned to sidewalks.</summary>
    public float WalkTrips { get; set; }

    /// <summary>Actual road network distance along traversed curved edges in pixels/meters.</summary>
    public float NetworkDistance { get; set; }

    /// <summary>Free-flow or congested car travel time in minutes.</summary>
    public float TravelTime { get; set; }

    /// <summary>Traversed curved road edge IDs along the shortest path.</summary>
    public List<int> EdgePath { get; set; } = new List<int>();

    /// <summary>True if this trip originates from a continuous roadside parcel.</summary>
    public bool IsOriginParcel => OriginParcelId >= 0;

    /// <summary>True if this trip terminates at a continuous roadside parcel.</summary>
    public bool IsDestParcel => DestParcelId >= 0;
}

/// <summary>
/// Modernized OD travel demand engine supporting continuous roadside ribbon parcels,
/// legacy grid zones, Bézier curved road graph distances, and a 3-mode split model.
/// </summary>
public class ODMatrix
{
    public int ZoneCount;
    public float[,] Trips;
    public float[,] CarTrips;
    public float[,] TransitTrips;
    public float[,] WalkTrips;
    public float TotalTrips;
    public float TotalCarTrips;
    public float TotalTransitTrips;
    public float TotalWalkTrips;
    public float CurrentHour;

    /// <summary>List of all active dynamic continuous trips generated during the last recalculation.</summary>
    public List<DynamicTrip> DynamicTrips { get; } = new List<DynamicTrip>();

    /// <summary>Fast lookup of dynamic trips grouped by origin parcel ID.</summary>
    public Dictionary<int, List<DynamicTrip>> TripsByOriginParcel { get; } = new Dictionary<int, List<DynamicTrip>>();

    /// <summary>Fast lookup of dynamic trips grouped by destination parcel ID.</summary>
    public Dictionary<int, List<DynamicTrip>> TripsByDestParcel { get; } = new Dictionary<int, List<DynamicTrip>>();

    /// <summary>Fast lookup of dynamic trips grouped by origin zone ID.</summary>
    public Dictionary<int, List<DynamicTrip>> TripsByOriginZone { get; } = new Dictionary<int, List<DynamicTrip>>();

    /// <summary>Fast lookup of dynamic trips grouped by destination zone ID.</summary>
    public Dictionary<int, List<DynamicTrip>> TripsByDestZone { get; } = new Dictionary<int, List<DynamicTrip>>();

    public ODMatrix(int zoneCount)
    {
        ZoneCount = zoneCount;
        int size = Math.Max(zoneCount, 0);
        Trips = new float[size, size];
        CarTrips = new float[size, size];
        TransitTrips = new float[size, size];
        WalkTrips = new float[size, size];
    }

    /// <summary>
    /// Returns the fraction of daily travel demand occurring during this hour.
    /// Real urban commute curve: sharp 7-9 AM morning rush, secondary 16:30-18:30 PM rush, quiet night.
    /// </summary>
    public static float GetDemandMultiplier(float hour)
    {
        float h = hour % 24f;
        if (h < 0) h += 24f;
        int h0 = Mathf.FloorToInt(h);
        int h1 = (h0 + 1) % 24;
        float t = h - h0;

        return Mathf.Lerp(GetDemandVal(h0), GetDemandVal(h1), t);
    }

    private static float GetDemandVal(int hr)
    {
        return hr switch
        {
            0 => 0.015f,
            1 => 0.010f,
            2 => 0.008f,
            3 => 0.008f,
            4 => 0.015f,
            5 => 0.035f,
            6 => 0.080f,
            7 => 0.150f, // 🌅 Morning peak rush start
            8 => 0.130f, // 🌅 Morning peak rush
            9 => 0.075f,
            10 => 0.050f,
            11 => 0.055f,
            12 => 0.065f, // Lunch mini-peak
            13 => 0.055f,
            14 => 0.050f,
            15 => 0.065f,
            16 => 0.095f,
            17 => 0.145f, // 🌆 Evening peak rush (downtown exit)
            18 => 0.110f, // 🌆 Evening peak rush
            19 => 0.060f,
            20 => 0.045f,
            21 => 0.035f,
            22 => 0.025f,
            23 => 0.018f,
            _ => 0.020f
        };
    }

    /// <summary>
    /// 1.0 = 100% Residential -> Commercial (morning rush into work)
    /// 0.0 = 100% Commercial -> Residential (evening rush home)
    /// 0.5 = Balanced bidirectional
    /// </summary>
    public static float GetDirectionalBias(float hour)
    {
        float h = hour % 24f;
        if (h < 0) h += 24f;
        int h0 = Mathf.FloorToInt(h);
        int h1 = (h0 + 1) % 24;
        float t = h - h0;

        return Mathf.Lerp(GetBiasVal(h0), GetBiasVal(h1), t);
    }

    private static float GetBiasVal(int hr)
    {
        return hr switch
        {
            >= 0 and <= 5 => 0.50f,
            6 => 0.75f,
            7 => 0.90f, // 90% heading into downtown/work
            8 => 0.85f,
            9 => 0.70f,
            10 or 11 => 0.55f,
            12 or 13 => 0.50f,
            14 or 15 => 0.45f,
            16 => 0.25f,
            17 => 0.10f, // 90% heading out of downtown back home!
            18 => 0.15f,
            19 => 0.30f,
            >= 20 and <= 23 => 0.48f,
            _ => 0.50f
        };
    }

    private class ODCandidate
    {
        public int ZoneId = -1;
        public int ParcelId = -1;
        public ZoneType Type;
        public int Population;
        public int Jobs;
        public Vector2 Position;
        public int AccessNodeId = -1;
        public int EdgeId = -1;
        public RoadsideParcel Parcel;
        public Zone Zone;
    }

    /// <summary>
    /// Recalculates travel demand, gravity trip distribution, and 3-mode split across continuous parcels and/or zones.
    /// </summary>
    public void Recalculate(
        float hour,
        CityGrid grid,
        float[,] distances,
        bool hasTransit,
        HashSet<int> coveredZoneIds = null,
        float averageTicketPrice = 12f,
        float[,] walkingDistances = null,
        TransitManager transitManager = null,
        ParcelManager parcelManager = null,
        RoadGraph roadGraph = null)
    {
        CurrentHour = hour;
        float demandMult = GetDemandMultiplier(hour);
        float dirBias = GetDirectionalBias(hour);

        if (Trips != null) Array.Clear(Trips, 0, Trips.Length);
        if (CarTrips != null) Array.Clear(CarTrips, 0, CarTrips.Length);
        if (TransitTrips != null) Array.Clear(TransitTrips, 0, TransitTrips.Length);
        if (WalkTrips != null) Array.Clear(WalkTrips, 0, WalkTrips.Length);

        DynamicTrips.Clear();
        TripsByOriginParcel.Clear();
        TripsByDestParcel.Clear();
        TripsByOriginZone.Clear();
        TripsByDestZone.Clear();

        TotalTrips = 0f;
        TotalCarTrips = 0f;
        TotalTransitTrips = 0f;
        TotalWalkTrips = 0f;

        // 1. Collect all active origins and destinations from parcels and/or grid zones
        var candidates = new List<ODCandidate>();

        if (grid != null)
        {
            var activeIds = grid.ActiveZoneIds;
            for (int i = 0; i < activeIds.Count; i++)
            {
                var z = grid.GetZone(activeIds[i]);
                if (z == null || z.Type == ZoneType.Empty) continue;
                candidates.Add(new ODCandidate
                {
                    ZoneId = z.Id,
                    Type = z.Type,
                    Population = z.Population,
                    Jobs = z.Jobs,
                    Position = grid.GetWorldCenter(z.Id),
                    AccessNodeId = z.Id,
                    Zone = z
                });
            }
        }

        if (parcelManager != null)
        {
            foreach (var p in parcelManager.ActiveParcels)
            {
                int accessNode = p.GetAccessNodeId(roadGraph);
                candidates.Add(new ODCandidate
                {
                    ParcelId = p.Id,
                    Type = p.ZoneType,
                    Population = p.Population,
                    Jobs = p.Jobs,
                    Position = p.Center,
                    AccessNodeId = accessNode,
                    EdgeId = p.EdgeId,
                    Parcel = p
                });
            }
        }

        int totalPop = (grid?.TotalPopulation() ?? 0) + (parcelManager?.TotalPopulation ?? 0);
        if (candidates.Count < 2 || totalPop <= 0) return;

        // 2. Compute gravity model impedance and raw attractiveness scores
        var tripPairs = new List<(ODCandidate a, ODCandidate b, float rawScore, float carTime, float walkTime, float netDist, List<int> path)>();
        float rawTotalScore = 0f;

        for (int i = 0; i < candidates.Count; i++)
        {
            var orig = candidates[i];

            float prod = (orig.Type == ZoneType.Residential)
                ? orig.Population * dirBias
                : orig.Jobs * (1f - dirBias);

            if (prod <= 0f) continue;

            for (int j = 0; j < candidates.Count; j++)
            {
                if (i == j) continue;
                var dest = candidates[j];

                if (orig.ZoneId >= 0 && dest.ZoneId >= 0 && orig.ZoneId == dest.ZoneId) continue;
                if (orig.ParcelId >= 0 && dest.ParcelId >= 0 && orig.ParcelId == dest.ParcelId) continue;

                float attr = (dest.Type == ZoneType.Commercial || dest.Type == ZoneType.Industrial)
                    ? dest.Jobs * dirBias
                    : dest.Population * (1f - dirBias);

                if (attr <= 0f) continue;

                // Determine distance, travel time, and shortest path
                float carTravelTime = float.MaxValue;
                float walkTravelTime = float.MaxValue;
                float netDist = 0f;
                List<int> edgePath = null;

                if (orig.EdgeId >= 0 && dest.EdgeId >= 0 && orig.EdgeId == dest.EdgeId && roadGraph != null && orig.EdgeId < roadGraph.Edges.Count)
                {
                    // Both parcels on the same road edge curve
                    var edge = roadGraph.Edges[orig.EdgeId];
                    if (orig.Parcel != null && dest.Parcel != null && edge.Curve != null)
                    {
                        netDist = Mathf.Abs(orig.Parcel.NormalizedT - dest.Parcel.NormalizedT) * edge.Curve.Length;
                        if (orig.ParcelId != dest.ParcelId)
                        {
                            netDist = Mathf.Max(netDist, orig.Position.DistanceTo(dest.Position));
                        }
                    }
                    else
                    {
                        netDist = orig.Position.DistanceTo(dest.Position);
                    }
                    carTravelTime = netDist / Mathf.Max(edge.FreeFlowSpeed, 10f);
                    walkTravelTime = netDist / Mathf.Max(edge.WalkingSpeed, 1f);
                    edgePath = new List<int> { orig.EdgeId };
                }
                else if (roadGraph != null)
                {
                    int u = orig.AccessNodeId;
                    int v = dest.AccessNodeId;
                    if (u != -1 && v != -1)
                    {
                        if (u == v)
                        {
                            netDist = orig.Position.DistanceTo(dest.Position);
                            carTravelTime = netDist / 50f;
                            walkTravelTime = netDist / 5f;
                            edgePath = new List<int>();
                            if (orig.EdgeId >= 0) edgePath.Add(orig.EdgeId);
                            if (dest.EdgeId >= 0 && dest.EdgeId != orig.EdgeId) edgePath.Add(dest.EdgeId);
                        }
                        else
                        {
                            if (!roadGraph.PathCache.TryGetValue((u, v), out edgePath) || edgePath == null)
                            {
                                edgePath = roadGraph.GetShortestPath(u, v);
                                if (edgePath != null) roadGraph.PathCache[(u, v)] = edgePath;
                            }

                            if (edgePath != null && edgePath.Count > 0)
                            {
                                float pathDist = 0f;
                                float pathCarTime = 0f;
                                for (int p = 0; p < edgePath.Count; p++)
                                {
                                    var e = roadGraph.Edges[edgePath[p]];
                                    pathDist += e.Length;
                                    pathCarTime += e.GetTravelTime();
                                }

                                var nodeU = roadGraph.GetNode(u);
                                var nodeV = roadGraph.GetNode(v);
                                float accessDist = (nodeU != null ? orig.Position.DistanceTo(nodeU.WorldPosition) : 0f) +
                                                   (nodeV != null ? dest.Position.DistanceTo(nodeV.WorldPosition) : 0f);

                                netDist = pathDist + accessDist;
                                carTravelTime = pathCarTime + (accessDist / 50f);

                                if (roadGraph.WalkingPathCache.TryGetValue((u, v), out var walkPath) ||
                                    (walkPath = roadGraph.GetShortestWalkingPath(u, v)) != null)
                                {
                                    float pathWalkTime = 0f;
                                    for (int p = 0; p < walkPath.Count; p++)
                                    {
                                        pathWalkTime += roadGraph.Edges[walkPath[p]].GetWalkingTravelTime();
                                    }
                                    walkTravelTime = pathWalkTime + (accessDist / 5f);
                                }
                                else
                                {
                                    walkTravelTime = netDist / 5f;
                                }
                            }
                        }
                    }
                }
                else if (orig.ZoneId >= 0 && dest.ZoneId >= 0 && distances != null)
                {
                    int zi = orig.ZoneId;
                    int zj = dest.ZoneId;
                    if (zi < ZoneCount && zj < ZoneCount)
                    {
                        carTravelTime = distances[zi, zj];
                        if (carTravelTime > 0f && carTravelTime < float.MaxValue)
                        {
                            walkTravelTime = (walkingDistances != null && walkingDistances[zi, zj] < float.MaxValue)
                                ? walkingDistances[zi, zj]
                                : (carTravelTime * 10f);
                            netDist = carTravelTime * 50f;
                            edgePath = new List<int>();
                        }
                    }
                }

                if (carTravelTime <= 0f || carTravelTime >= float.MaxValue) continue;

                // Standard gravity model impedance function
                float impedance = 1.0f / Mathf.Pow(carTravelTime + 2.0f, 1.25f);
                float score = prod * attr * impedance;

                rawTotalScore += score;
                tripPairs.Add((orig, dest, score, carTravelTime, walkTravelTime, netDist, edgePath));
            }
        }

        // 3. Realistic population trip scaling & normalization
        float dailyTripsTarget = totalPop * 1.5f;
        float targetHourlyTrips = dailyTripsTarget * demandMult;
        float normalizationScale = rawTotalScore > 0f ? (targetHourlyTrips / rawTotalScore) : 0f;

        float actualTotalTrips = 0f;
        float actualTotalCar = 0f;
        float actualTotalTransit = 0f;
        float actualTotalWalk = 0f;

        // 4. Mode Split & Assignment
        for (int p = 0; p < tripPairs.Count; p++)
        {
            var (orig, dest, score, travelTime, walkTime, netDist, edgePath) = tripPairs[p];
            float finalTrips = score * normalizationScale;
            if (finalTrips <= 0f) continue;

            // -------------------------------------------------------------
            // 3-Mode Multinomial Choice Model (Walk, Car, Transit)
            // -------------------------------------------------------------
            // A. Pure Walking generalized cost (high probability for short trips < 400-600m)
            float walkCost;
            if (netDist < 400f)
            {
                walkCost = walkTime * 0.8f;
            }
            else
            {
                walkCost = walkTime * 0.9f + Mathf.Pow(Mathf.Max(0f, walkTime - 25f), 1.5f) * 0.7f;
            }

            // B. Private Car generalized cost
            float carCost = (travelTime * 1.4f) + 22.0f;

            // C. Multimodal Transit generalized cost
            float transitCost;
            if (transitManager != null && transitManager.Routes.Count > 0 && transitManager.Stops.Count > 0)
            {
                float bestAccessDist = float.MaxValue;
                float bestEgressDist = float.MaxValue;

                foreach (var stop in transitManager.Stops.Values)
                {
                    var stopNode = roadGraph?.GetNode(stop.NodeId);
                    Vector2 stopPos = stopNode?.WorldPosition ?? (grid != null ? grid.GetWorldCenter(stop.ZoneId) : Vector2.Zero);

                    float dI = orig.Position.DistanceTo(stopPos);
                    if (dI < bestAccessDist) bestAccessDist = dI;

                    float dJ = dest.Position.DistanceTo(stopPos);
                    if (dJ < bestEgressDist) bestEgressDist = dJ;
                }

                if (bestAccessDist > 350f || bestEgressDist > 350f)
                {
                    // Outside walking catchment of transit stops
                    transitCost = 500f;
                }
                else
                {
                    float accessWalk = bestAccessDist / 5.0f;
                    float egressWalk = bestEgressDist / 5.0f;
                    float waitTime = 5.0f;
                    float inVeh = travelTime * 1.15f;
                    transitCost = averageTicketPrice + (accessWalk + egressWalk) * 0.8f + (waitTime * 1.2f) + (inVeh * 1.0f);
                }
            }
            else if (coveredZoneIds != null && coveredZoneIds.Count > 0 && orig.ZoneId >= 0 && dest.ZoneId >= 0)
            {
                bool originCovered = coveredZoneIds.Contains(orig.ZoneId);
                bool destCovered = coveredZoneIds.Contains(dest.ZoneId);

                if (originCovered && destCovered)
                {
                    transitCost = averageTicketPrice + 10.0f + (travelTime * 1.15f);
                }
                else if (originCovered || destCovered)
                {
                    transitCost = averageTicketPrice + 25.0f + (travelTime * 1.15f);
                }
                else
                {
                    transitCost = 500f;
                }
            }
            else if (hasTransit)
            {
                transitCost = averageTicketPrice + 35.0f + (travelTime * 1.15f);
            }
            else
            {
                transitCost = 500f;
            }

            // Multinomial Logit calculation
            float lambda = 0.08f;
            float uWalk = -walkCost * lambda;
            float uCar = -carCost * lambda;
            float uTransit = -transitCost * lambda;

            float uMax = Mathf.Max(uWalk, Mathf.Max(uCar, uTransit));
            float expWalk = Mathf.Exp(uWalk - uMax);
            float expCar = Mathf.Exp(uCar - uMax);
            float expTransit = Mathf.Exp(uTransit - uMax);
            float sumExp = expWalk + expCar + expTransit;

            float pWalk = expWalk / sumExp;
            float pCar = expCar / sumExp;
            float pTransit = expTransit / sumExp;

            // Exact trip conservation: Walk + Car + Transit == finalTrips
            float wTrips = finalTrips * pWalk;
            float cTrips = finalTrips * pCar;
            float tTrips = finalTrips - wTrips - cTrips;
            if (tTrips < 0f) tTrips = 0f;

            if (orig.ZoneId >= 0 && dest.ZoneId >= 0 && orig.ZoneId < ZoneCount && dest.ZoneId < ZoneCount && Trips != null)
            {
                Trips[orig.ZoneId, dest.ZoneId] += finalTrips;
                CarTrips[orig.ZoneId, dest.ZoneId] += cTrips;
                TransitTrips[orig.ZoneId, dest.ZoneId] += tTrips;
                WalkTrips[orig.ZoneId, dest.ZoneId] += wTrips;
            }

            actualTotalTrips += finalTrips;
            actualTotalWalk += wTrips;
            actualTotalCar += cTrips;
            actualTotalTransit += tTrips;

            var dTrip = new DynamicTrip
            {
                OriginZoneId = orig.ZoneId,
                OriginParcelId = orig.ParcelId,
                OriginAccessNodeId = orig.AccessNodeId,
                OriginPos = orig.Position,
                OriginType = orig.Type,
                DestZoneId = dest.ZoneId,
                DestParcelId = dest.ParcelId,
                DestAccessNodeId = dest.AccessNodeId,
                DestPos = dest.Position,
                DestType = dest.Type,
                TotalTrips = finalTrips,
                CarTrips = cTrips,
                TransitTrips = tTrips,
                WalkTrips = wTrips,
                NetworkDistance = netDist,
                TravelTime = travelTime,
                EdgePath = edgePath != null ? new List<int>(edgePath) : new List<int>()
            };

            DynamicTrips.Add(dTrip);

            if (orig.ParcelId >= 0) AddTripToDict(TripsByOriginParcel, orig.ParcelId, dTrip);
            if (dest.ParcelId >= 0) AddTripToDict(TripsByDestParcel, dest.ParcelId, dTrip);
            if (orig.ZoneId >= 0) AddTripToDict(TripsByOriginZone, orig.ZoneId, dTrip);
            if (dest.ZoneId >= 0) AddTripToDict(TripsByDestZone, dest.ZoneId, dTrip);
        }

        TotalTrips = actualTotalTrips;
        TotalWalkTrips = actualTotalWalk;
        TotalCarTrips = actualTotalCar;
        TotalTransitTrips = actualTotalTransit;
    }

    private static void AddTripToDict(Dictionary<int, List<DynamicTrip>> dict, int key, DynamicTrip trip)
    {
        if (!dict.TryGetValue(key, out var list))
        {
            list = new List<DynamicTrip>();
            dict[key] = list;
        }
        list.Add(trip);
    }
}

/// <summary>
/// Modernized continuous OD trip matrix alias supporting non-tile roadside ribbon parcels and legacy zones.
/// </summary>
public class DynamicODMatrix : ODMatrix
{
    public DynamicODMatrix(int zoneCount = 0) : base(zoneCount) { }
}
