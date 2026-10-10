using Godot;
using System;
using System.Collections.Generic;
using CitySim.Rendering;

namespace CitySim.Simulation;

/// <summary>
/// Origin record representing incoming commute traffic to a workplace.
/// </summary>
public record CommuteOrigin(Zone Zone, float Trips, float Percentage);

/// <summary>
/// Destination record representing outgoing commute traffic to a workplace.
/// </summary>
public record CommuteDestination(Zone Zone, float Trips, float Percentage);

/// <summary>
/// Dynamic continuous origin record for roadside parcels or zones.
/// </summary>
public class DynamicCommuteOrigin
{
    public Zone Zone { get; set; }
    public RoadsideParcel Parcel { get; set; }
    public float Trips { get; set; }
    public float Percentage { get; set; }
    public ZoneType Type => Parcel != null ? Parcel.ZoneType : (Zone != null ? Zone.Type : ZoneType.Empty);
    public string DisplayName => Parcel != null ? $"Парсель #{Parcel.Id}" : $"Квартал ({Zone?.GridPos.X}, {Zone?.GridPos.Y})";
}

/// <summary>
/// Dynamic continuous destination record for roadside parcels or zones.
/// </summary>
public class DynamicCommuteDestination
{
    public Zone Zone { get; set; }
    public RoadsideParcel Parcel { get; set; }
    public float Trips { get; set; }
    public float Percentage { get; set; }
    public ZoneType Type => Parcel != null ? Parcel.ZoneType : (Zone != null ? Zone.Type : ZoneType.Empty);
    public string DisplayName => Parcel != null ? $"Парсель #{Parcel.Id}" : $"Квартал ({Zone?.GridPos.X}, {Zone?.GridPos.Y})";
}

/// <summary>
/// Dynamic analytics computation for zone-level, parcel-level, and city-wide commute metrics and mode splits.
/// </summary>
public static class CommuteAnalytics
{
    /// <summary>
    /// Calculates private car and public transit percentage shares from trip counts (legacy 2-mode overload).
    /// Guarantees carPct + transitPct == 100%.
    /// </summary>
    public static (float carPct, float transitPct) CalculateModeSplit(float carTrips, float transitTrips)
    {
        var (c, t, _) = CalculateModeSplit(carTrips, transitTrips, 0f);
        return (c, t);
    }

    /// <summary>
    /// Calculates 3-mode percentage shares: private car, public transit, and walking.
    /// Guarantees carPct + transitPct + walkPct == 100.0%.
    /// </summary>
    public static (float carPct, float transitPct, float walkPct) CalculateModeSplit(float carTrips, float transitTrips, float walkTrips)
    {
        float total = carTrips + transitTrips + walkTrips;
        if (total <= 0.0001f)
        {
            return (100f, 0f, 0f);
        }

        float carPct = (carTrips / total) * 100f;
        float transitPct = (transitTrips / total) * 100f;
        float walkPct = 100f - carPct - transitPct;
        return (carPct, transitPct, walkPct);
    }

    /// <summary>
    /// Computes dynamic incoming commute origins, trip volumes, and 3-mode split for a workplace (Commercial or Industrial).
    /// Accounts for bidirectional commute relationships across time-of-day directional biases.
    /// </summary>
    public static (float incomingCar, float incomingTransit, float incomingWalk, float totalTrips, float carPct, float transitPct, float walkPct, List<CommuteOrigin> origins) 
        GetIncomingCommute(Zone workplace, CityGrid grid, ODMatrix od)
    {
        if (workplace == null || grid == null || od == null)
        {
            return (0f, 0f, 0f, 0f, 100f, 0f, 0f, new List<CommuteOrigin>());
        }

        float inCar = 0f;
        float inTransit = 0f;
        float inWalk = 0f;
        var originCandidates = new List<(Zone zone, float trips)>();

        for (int i = 0; i < grid.ZoneCount; i++)
        {
            var origin = grid.GetZone(i);
            if (origin == null || origin.Type != ZoneType.Residential) continue;

            float c = od.CarTrips[i, workplace.Id];
            float t = od.TransitTrips[i, workplace.Id];
            float w = od.WalkTrips != null ? od.WalkTrips[i, workplace.Id] : 0f;
            float pairTrips = c + t + w;

            // If directional bias strongly favors the return trip (evening rush),
            // fallback to reverse commute flow so the commute relationship is preserved
            if (pairTrips <= 0.001f)
            {
                c = od.CarTrips[workplace.Id, i];
                t = od.TransitTrips[workplace.Id, i];
                w = od.WalkTrips != null ? od.WalkTrips[workplace.Id, i] : 0f;
                pairTrips = c + t + w;
            }

            inCar += c;
            inTransit += t;
            inWalk += w;

            if (pairTrips > 0.01f)
            {
                originCandidates.Add((origin, pairTrips));
            }
        }

        float totalTrips = inCar + inTransit + inWalk;
        var (carPct, transitPct, walkPct) = CalculateModeSplit(inCar, inTransit, inWalk);

        originCandidates.Sort((a, b) => b.trips.CompareTo(a.trips));
        var origins = new List<CommuteOrigin>();
        foreach (var (candZone, candTrips) in originCandidates)
        {
            float pct = totalTrips > 0f ? (candTrips / totalTrips * 100f) : 0f;
            origins.Add(new CommuteOrigin(candZone, candTrips, pct));
        }

        return (inCar, inTransit, inWalk, totalTrips, carPct, transitPct, walkPct, origins);
    }

    /// <summary>
    /// Computes dynamic incoming commute origins, trip volumes, and 3-mode split for a roadside workplace parcel.
    /// </summary>
    public static (float incomingCar, float incomingTransit, float incomingWalk, float totalTrips, float carPct, float transitPct, float walkPct, List<DynamicCommuteOrigin> origins)
        GetIncomingCommuteForParcel(RoadsideParcel workplace, ODMatrix od, ParcelManager parcelManager = null, CityGrid grid = null)
    {
        if (workplace == null || od == null)
        {
            return (0f, 0f, 0f, 0f, 100f, 0f, 0f, new List<DynamicCommuteOrigin>());
        }

        float inCar = 0f;
        float inTransit = 0f;
        float inWalk = 0f;
        var originCandidates = new List<(Zone zone, RoadsideParcel parcel, float trips)>();

        if (od.DynamicTrips != null)
        {
            for (int t = 0; t < od.DynamicTrips.Count; t++)
            {
                var trip = od.DynamicTrips[t];
                bool isIncoming = trip.DestParcelId == workplace.Id;
                bool isReverse = trip.OriginParcelId == workplace.Id && trip.DestType == ZoneType.Residential;

                if (isIncoming || isReverse)
                {
                    inCar += trip.CarTrips;
                    inTransit += trip.TransitTrips;
                    inWalk += trip.WalkTrips;

                    if (trip.TotalTrips > 0.01f)
                    {
                        Zone origZone = null;
                        RoadsideParcel origParcel = null;

                        int otherParcelId = isIncoming ? trip.OriginParcelId : trip.DestParcelId;
                        int otherZoneId = isIncoming ? trip.OriginZoneId : trip.DestZoneId;

                        if (otherParcelId >= 0 && parcelManager != null && parcelManager.ParcelMap.TryGetValue(otherParcelId, out var p))
                        {
                            origParcel = p;
                        }
                        else if (otherZoneId >= 0 && grid != null)
                        {
                            origZone = grid.GetZone(otherZoneId);
                        }

                        originCandidates.Add((origZone, origParcel, trip.TotalTrips));
                    }
                }
            }
        }

        float totalTrips = inCar + inTransit + inWalk;
        var (carPct, transitPct, walkPct) = CalculateModeSplit(inCar, inTransit, inWalk);

        originCandidates.Sort((a, b) => b.trips.CompareTo(a.trips));
        var origins = new List<DynamicCommuteOrigin>();
        foreach (var (candZone, candParcel, candTrips) in originCandidates)
        {
            float pct = totalTrips > 0f ? (candTrips / totalTrips * 100f) : 0f;
            origins.Add(new DynamicCommuteOrigin
            {
                Zone = candZone,
                Parcel = candParcel,
                Trips = candTrips,
                Percentage = pct
            });
        }

        return (inCar, inTransit, inWalk, totalTrips, carPct, transitPct, walkPct, origins);
    }

    /// <summary>
    /// Computes dynamic outgoing commute destinations, workplace breakdown (Commercial vs Industrial),
    /// and 3-mode split for a residential zone.
    /// </summary>
    public static (float outgoingCar, float outgoingTransit, float outgoingWalk, float totalTrips, float comTrips, float indTrips,
        float comPct, float indPct, float carPct, float transitPct, float walkPct, List<CommuteDestination> destinations)
        GetResidentialCommute(Zone residence, CityGrid grid, ODMatrix od)
    {
        if (residence == null || grid == null || od == null)
        {
            return (0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 100f, 0f, 0f, new List<CommuteDestination>());
        }

        float outCar = 0f;
        float outTransit = 0f;
        float outWalk = 0f;
        float comTrips = 0f;
        float indTrips = 0f;
        var destCandidates = new List<(Zone zone, float trips)>();

        for (int j = 0; j < grid.ZoneCount; j++)
        {
            var dest = grid.GetZone(j);
            if (dest == null || (dest.Type != ZoneType.Commercial && dest.Type != ZoneType.Industrial)) continue;

            float c = od.CarTrips[residence.Id, j];
            float t = od.TransitTrips[residence.Id, j];
            float w = od.WalkTrips != null ? od.WalkTrips[residence.Id, j] : 0f;
            float pairTrips = c + t + w;

            // Evening directional bias fallback
            if (pairTrips <= 0.001f)
            {
                c = od.CarTrips[j, residence.Id];
                t = od.TransitTrips[j, residence.Id];
                w = od.WalkTrips != null ? od.WalkTrips[j, residence.Id] : 0f;
                pairTrips = c + t + w;
            }

            outCar += c;
            outTransit += t;
            outWalk += w;

            if (dest.Type == ZoneType.Commercial) comTrips += pairTrips;
            else if (dest.Type == ZoneType.Industrial) indTrips += pairTrips;

            if (pairTrips > 0.01f)
            {
                destCandidates.Add((dest, pairTrips));
            }
        }

        float totalWorkTrips = comTrips + indTrips;
        float comPct = totalWorkTrips > 0f ? (comTrips / totalWorkTrips * 100f) : 0f;
        float indPct = totalWorkTrips > 0f ? (100f - comPct) : 0f;

        float totalTrips = outCar + outTransit + outWalk;
        var (carPct, transitPct, walkPct) = CalculateModeSplit(outCar, outTransit, outWalk);

        destCandidates.Sort((a, b) => b.trips.CompareTo(a.trips));
        var destinations = new List<CommuteDestination>();
        foreach (var (candZone, candTrips) in destCandidates)
        {
            float pct = totalTrips > 0f ? (candTrips / totalTrips * 100f) : 0f;
            destinations.Add(new CommuteDestination(candZone, candTrips, pct));
        }

        return (outCar, outTransit, outWalk, totalTrips, comTrips, indTrips, comPct, indPct, carPct, transitPct, walkPct, destinations);
    }

    /// <summary>
    /// Computes dynamic outgoing commute destinations, workplace breakdown (Commercial vs Industrial),
    /// and 3-mode split for a residential roadside parcel.
    /// </summary>
    public static (float outgoingCar, float outgoingTransit, float outgoingWalk, float totalTrips, float comTrips, float indTrips,
        float comPct, float indPct, float carPct, float transitPct, float walkPct, List<DynamicCommuteDestination> destinations)
        GetResidentialCommuteForParcel(RoadsideParcel residence, ODMatrix od, ParcelManager parcelManager = null, CityGrid grid = null)
    {
        if (residence == null || od == null)
        {
            return (0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 100f, 0f, 0f, new List<DynamicCommuteDestination>());
        }

        float outCar = 0f;
        float outTransit = 0f;
        float outWalk = 0f;
        float comTrips = 0f;
        float indTrips = 0f;
        var destCandidates = new List<(Zone zone, RoadsideParcel parcel, float trips)>();

        if (od.DynamicTrips != null)
        {
            for (int t = 0; t < od.DynamicTrips.Count; t++)
            {
                var trip = od.DynamicTrips[t];
                bool isOutgoing = trip.OriginParcelId == residence.Id;
                bool isReverse = trip.DestParcelId == residence.Id && (trip.OriginType == ZoneType.Commercial || trip.OriginType == ZoneType.Industrial);

                if (isOutgoing || isReverse)
                {
                    outCar += trip.CarTrips;
                    outTransit += trip.TransitTrips;
                    outWalk += trip.WalkTrips;

                    ZoneType workType = isOutgoing ? trip.DestType : trip.OriginType;
                    if (workType == ZoneType.Commercial) comTrips += trip.TotalTrips;
                    else if (workType == ZoneType.Industrial) indTrips += trip.TotalTrips;

                    if (trip.TotalTrips > 0.01f)
                    {
                        Zone dZone = null;
                        RoadsideParcel dParcel = null;

                        int otherParcelId = isOutgoing ? trip.DestParcelId : trip.OriginParcelId;
                        int otherZoneId = isOutgoing ? trip.DestZoneId : trip.OriginZoneId;

                        if (otherParcelId >= 0 && parcelManager != null && parcelManager.ParcelMap.TryGetValue(otherParcelId, out var p))
                        {
                            dParcel = p;
                        }
                        else if (otherZoneId >= 0 && grid != null)
                        {
                            dZone = grid.GetZone(otherZoneId);
                        }

                        destCandidates.Add((dZone, dParcel, trip.TotalTrips));
                    }
                }
            }
        }

        float totalWorkTrips = comTrips + indTrips;
        float comPct = totalWorkTrips > 0f ? (comTrips / totalWorkTrips * 100f) : 0f;
        float indPct = totalWorkTrips > 0f ? (100f - comPct) : 0f;

        float totalTrips = outCar + outTransit + outWalk;
        var (carPct, transitPct, walkPct) = CalculateModeSplit(outCar, outTransit, outWalk);

        destCandidates.Sort((a, b) => b.trips.CompareTo(a.trips));
        var destinations = new List<DynamicCommuteDestination>();
        foreach (var (candZone, candParcel, candTrips) in destCandidates)
        {
            float pct = totalTrips > 0f ? (candTrips / totalTrips * 100f) : 0f;
            destinations.Add(new DynamicCommuteDestination
            {
                Zone = candZone,
                Parcel = candParcel,
                Trips = candTrips,
                Percentage = pct
            });
        }

        return (outCar, outTransit, outWalk, totalTrips, comTrips, indTrips, comPct, indPct, carPct, transitPct, walkPct, destinations);
    }

    /// <summary>
    /// Computes city-wide employment distribution (Commercial vs Industrial) and total 3-mode split.
    /// Supports parcels and legacy zones.
    /// </summary>
    public static (int comJobs, int indJobs, int totalJobs, float comPct, float indPct,
        float totalCarTrips, float totalTransitTrips, float totalWalkTrips, float totalTrips, float carPct, float transitPct, float walkPct)
        GetCityOverview(CityGrid grid, ODMatrix od, ParcelManager parcelManager = null)
    {
        if (od == null)
        {
            return (0, 0, 0, 0f, 0f, 0f, 0f, 0f, 0f, 100f, 0f, 0f);
        }

        int comJobs = 0;
        int indJobs = 0;

        if (grid != null)
        {
            for (int i = 0; i < grid.ZoneCount; i++)
            {
                var z = grid.GetZone(i);
                if (z == null) continue;
                if (z.Type == ZoneType.Commercial) comJobs += z.Jobs;
                else if (z.Type == ZoneType.Industrial) indJobs += z.Jobs;
            }
        }

        if (parcelManager != null)
        {
            foreach (var p in parcelManager.Parcels)
            {
                if (p.ZoneType == ZoneType.Commercial) comJobs += p.Jobs;
                else if (p.ZoneType == ZoneType.Industrial) indJobs += p.Jobs;
            }
        }

        int totalJobs = comJobs + indJobs;
        float comPct = totalJobs > 0 ? ((float)comJobs / totalJobs * 100f) : 0f;
        float indPct = totalJobs > 0 ? (100f - comPct) : 0f;

        float totalCarTrips = od.TotalCarTrips;
        float totalTransitTrips = od.TotalTransitTrips;
        float totalWalkTrips = od.TotalWalkTrips;
        float totalTrips = od.TotalTrips;

        // If od totals are not set (e.g. uninitialized), fallback to matrix iteration
        if (totalTrips <= 0f && od.Trips != null)
        {
            for (int i = 0; i < od.ZoneCount; i++)
            {
                for (int j = 0; j < od.ZoneCount; j++)
                {
                    totalCarTrips += od.CarTrips[i, j];
                    totalTransitTrips += od.TransitTrips[i, j];
                    totalWalkTrips += od.WalkTrips != null ? od.WalkTrips[i, j] : 0f;
                }
            }
            totalTrips = totalCarTrips + totalTransitTrips + totalWalkTrips;
        }

        var (carPct, transitPct, walkPct) = CalculateModeSplit(totalCarTrips, totalTransitTrips, totalWalkTrips);

        return (comJobs, indJobs, totalJobs, comPct, indPct, totalCarTrips, totalTransitTrips, totalWalkTrips, totalTrips, carPct, transitPct, walkPct);
    }

    /// <summary>
    /// Resolves illuminated commute corridors and target markers for a selected zone.
    /// Accounts for bidirectional commute relationships between residential origins and workplace destinations.
    /// </summary>
    public static List<CommuteTarget> ComputeCommuteTargets(int zoneId, CityGrid grid, ODMatrix odMatrix, float[,] distances, RoadGraph graph)
    {
        var targets = new List<CommuteTarget>();
        if (grid == null || odMatrix == null || graph == null || zoneId < 0 || zoneId >= grid.ZoneCount)
        {
            return targets;
        }

        var sourceZone = grid.GetZone(zoneId);
        if (sourceZone == null || sourceZone.Type == ZoneType.Empty)
        {
            return targets;
        }

        // 1. Check DynamicTrips first if available
        if (odMatrix.DynamicTrips != null && odMatrix.DynamicTrips.Count > 0)
        {
            bool isResidential = sourceZone.Type == ZoneType.Residential;
            for (int t = 0; t < odMatrix.DynamicTrips.Count; t++)
            {
                var trip = odMatrix.DynamicTrips[t];
                bool match = isResidential ? (trip.OriginZoneId == zoneId || trip.DestZoneId == zoneId)
                                           : (trip.DestZoneId == zoneId || trip.OriginZoneId == zoneId);
                if (match && trip.TotalTrips > 0.05f)
                {
                    bool isForward = isResidential ? (trip.OriginZoneId == zoneId) : (trip.DestZoneId == zoneId);
                    Vector2 targetPos = isForward ? trip.DestPos : trip.OriginPos;
                    ZoneType targetType = isForward ? trip.DestType : trip.OriginType;
                    int targetZoneId = isForward ? trip.DestZoneId : trip.OriginZoneId;
                    int targetParcelId = isForward ? trip.DestParcelId : trip.OriginParcelId;

                    targets.Add(new CommuteTarget
                    {
                        ZoneId = targetZoneId,
                        ParcelId = targetParcelId,
                        Position = targetPos,
                        Volume = trip.TotalTrips,
                        Type = targetType,
                        TravelTime = trip.TravelTime,
                        EdgePath = trip.EdgePath != null ? new List<int>(trip.EdgePath) : new List<int>()
                    });
                }
            }

            targets.Sort((a, b) => b.Volume.CompareTo(a.Volume));
            if (targets.Count > 8) targets.RemoveRange(8, targets.Count - 8);
            return targets;
        }

        // 2. Legacy fallback
        if (sourceZone.Type == ZoneType.Residential)
        {
            for (int j = 0; j < grid.ZoneCount; j++)
            {
                if (zoneId == j) continue;
                var dest = grid.GetZone(j);
                if (dest == null || dest.Type == ZoneType.Empty) continue;

                float trips = Mathf.Max(odMatrix.Trips[zoneId, j], odMatrix.Trips[j, zoneId]);
                if (trips > 0.05f)
                {
                    List<int> edgePath = null;
                    if (graph.PathCache.TryGetValue((zoneId, j), out var path) && path.Count > 0)
                    {
                        edgePath = path;
                    }
                    else if (graph.PathCache.TryGetValue((j, zoneId), out var revPath) && revPath.Count > 0)
                    {
                        edgePath = revPath;
                    }

                    float travelTime = (distances != null && distances[zoneId, j] < float.MaxValue) ? distances[zoneId, j] : 0f;

                    targets.Add(new CommuteTarget
                    {
                        ZoneId = j,
                        Position = grid.GetWorldCenter(j),
                        Volume = trips,
                        Type = dest.Type,
                        TravelTime = travelTime,
                        EdgePath = edgePath != null ? new List<int>(edgePath) : new List<int>()
                    });
                }
            }
        }
        else if (sourceZone.Type == ZoneType.Commercial || sourceZone.Type == ZoneType.Industrial)
        {
            for (int i = 0; i < grid.ZoneCount; i++)
            {
                if (zoneId == i) continue;
                var origin = grid.GetZone(i);
                if (origin == null || origin.Type != ZoneType.Residential) continue;

                float trips = Mathf.Max(odMatrix.Trips[i, zoneId], odMatrix.Trips[zoneId, i]);
                if (trips > 0.05f)
                {
                    List<int> edgePath = null;
                    if (graph.PathCache.TryGetValue((i, zoneId), out var path) && path.Count > 0)
                    {
                        edgePath = path;
                    }
                    else if (graph.PathCache.TryGetValue((zoneId, i), out var revPath) && revPath.Count > 0)
                    {
                        edgePath = revPath;
                    }

                    float travelTime = (distances != null && distances[i, zoneId] < float.MaxValue) ? distances[i, zoneId] : 0f;

                    targets.Add(new CommuteTarget
                    {
                        ZoneId = i,
                        Position = grid.GetWorldCenter(i),
                        Volume = trips,
                        Type = origin.Type,
                        TravelTime = travelTime,
                        EdgePath = edgePath != null ? new List<int>(edgePath) : new List<int>()
                    });
                }
            }
        }

        // Sort by commuter volume descending and keep top 8 most prominent paths
        targets.Sort((a, b) => b.Volume.CompareTo(a.Volume));
        if (targets.Count > 8) targets.RemoveRange(8, targets.Count - 8);

        return targets;
    }

    /// <summary>
    /// Resolves illuminated commute corridors and target markers for a selected roadside ribbon parcel.
    /// </summary>
    public static List<CommuteTarget> ComputeCommuteTargetsForParcel(int parcelId, ParcelManager parcelManager, ODMatrix odMatrix, RoadGraph graph)
    {
        var targets = new List<CommuteTarget>();
        if (parcelManager == null || odMatrix == null || graph == null || parcelId <= 0)
        {
            return targets;
        }

        if (!parcelManager.ParcelMap.TryGetValue(parcelId, out var sourceParcel) || sourceParcel.ZoneType == ZoneType.Empty)
        {
            return targets;
        }

        bool isResidential = sourceParcel.ZoneType == ZoneType.Residential;

        if (odMatrix.DynamicTrips != null)
        {
            for (int t = 0; t < odMatrix.DynamicTrips.Count; t++)
            {
                var trip = odMatrix.DynamicTrips[t];
                bool match = isResidential ? (trip.OriginParcelId == parcelId || trip.DestParcelId == parcelId)
                                           : (trip.DestParcelId == parcelId || trip.OriginParcelId == parcelId);

                if (match && trip.TotalTrips > 0.05f)
                {
                    bool isForward = isResidential ? (trip.OriginParcelId == parcelId) : (trip.DestParcelId == parcelId);
                    Vector2 targetPos = isForward ? trip.DestPos : trip.OriginPos;
                    ZoneType targetType = isForward ? trip.DestType : trip.OriginType;
                    int targetZoneId = isForward ? trip.DestZoneId : trip.OriginZoneId;
                    int targetParcelId = isForward ? trip.DestParcelId : trip.OriginParcelId;

                    targets.Add(new CommuteTarget
                    {
                        ZoneId = targetZoneId,
                        ParcelId = targetParcelId,
                        Position = targetPos,
                        Volume = trip.TotalTrips,
                        Type = targetType,
                        TravelTime = trip.TravelTime,
                        EdgePath = trip.EdgePath != null ? new List<int>(trip.EdgePath) : new List<int>()
                    });
                }
            }
        }

        targets.Sort((a, b) => b.Volume.CompareTo(a.Volume));
        if (targets.Count > 8) targets.RemoveRange(8, targets.Count - 8);

        return targets;
    }
}
