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
/// Dynamic continuous origin record for roadside parcels, zones, or TAZ clusters.
/// </summary>
public class DynamicCommuteOrigin
{
    public Zone Zone { get; set; }
    public RoadsideParcel Parcel { get; set; }
    public TrafficAnalysisZone Taz { get; set; }
    public float Trips { get; set; }
    public float Percentage { get; set; }
    public ZoneType Type => Parcel != null ? Parcel.ZoneType : (Zone != null ? Zone.Type : (Taz != null ? ZoneType.Residential : ZoneType.Empty));
    public string DisplayName => Parcel != null ? $"Парсель #{Parcel.Id}" : (Zone != null ? $"Квартал ({Zone.GridPos.X}, {Zone.GridPos.Y})" : (Taz != null ? $"Кластер TAZ #{Taz.Id} ({Taz.GridCoord.X}, {Taz.GridCoord.Y})" : "Район"));
}

/// <summary>
/// Dynamic continuous destination record for roadside parcels, zones, or TAZ clusters.
/// </summary>
public class DynamicCommuteDestination
{
    public Zone Zone { get; set; }
    public RoadsideParcel Parcel { get; set; }
    public TrafficAnalysisZone Taz { get; set; }
    public ZoneType ExplicitType { get; set; } = ZoneType.Empty;
    public float Trips { get; set; }
    public float Percentage { get; set; }
    public ZoneType Type => Parcel != null ? Parcel.ZoneType : (Zone != null ? Zone.Type : (ExplicitType != ZoneType.Empty ? ExplicitType : (Taz != null ? (Taz.CommercialJobs >= Taz.IndustrialJobs ? ZoneType.Commercial : ZoneType.Industrial) : ZoneType.Empty)));
    public string DisplayName => Parcel != null ? $"Парсель #{Parcel.Id}" : (Zone != null ? $"Квартал ({Zone.GridPos.X}, {Zone.GridPos.Y})" : (Taz != null ? $"Кластер TAZ #{Taz.Id} ({Taz.GridCoord.X}, {Taz.GridCoord.Y})" : "Район"));
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

    /// <summary>
    /// Safely reconstructs the list of edge IDs for the shortest path between originNodeId and destNodeId
    /// directly from CachedTreeBuffers.
    /// </summary>
    public static List<int> ExtractPathFromTreeBuffer(
        OptimizedODMatrix matrix,
        int originNodeId,
        int destNodeId)
    {
        var path = new List<int>();
        if (matrix == null || originNodeId < 0 || destNodeId < 0 || originNodeId == destNodeId)
            return path;

        if (!matrix.CachedTreeBuffers.TryGetValue(originNodeId, out var tree) || tree == null)
            return path;

        int maxLen = Math.Min(tree.ParentEdgeIds.Length, tree.ParentNodeIds.Length);
        if (destNodeId >= maxLen)
            return path;

        int curr = destNodeId;
        int safety = 0;
        while (curr != originNodeId && curr >= 0 && curr < maxLen && safety++ < 2000)
        {
            int edgeId = tree.ParentEdgeIds[curr];
            if (edgeId < 0) break;

            path.Add(edgeId);
            curr = tree.ParentNodeIds[curr];
        }

        path.Reverse();
        return path;
    }

    // =========================================================================
    // TAZ ON-DEMAND DEAGGREGATION OVERLOADS (O(K) Instant Sampling)
    // =========================================================================

    /// <summary>
    /// Computes city-wide employment distribution and total 3-mode split in O(1) from OptimizedODMatrix.
    /// </summary>
    public static (int comJobs, int indJobs, int totalJobs, float comPct, float indPct,
        float totalCarTrips, float totalTransitTrips, float totalWalkTrips, float totalTrips, float carPct, float transitPct, float walkPct)
        GetCityOverview(CityGrid grid, OptimizedODMatrix od, ParcelManager parcelManager = null)
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

        var (carPct, transitPct, walkPct) = CalculateModeSplit(totalCarTrips, totalTransitTrips, totalWalkTrips);

        return (comJobs, indJobs, totalJobs, comPct, indPct, totalCarTrips, totalTransitTrips, totalWalkTrips, totalTrips, carPct, transitPct, walkPct);
    }

    /// <summary>
    /// On-Demand TAZ deaggregation for a residential grid zone.
    /// Guarantees that the sum of trips across all residential entities in the TAZ equals the TAZ's total flow.
    /// </summary>
    public static (float outgoingCar, float outgoingTransit, float outgoingWalk, float totalTrips, float comTrips, float indTrips,
        float comPct, float indPct, float carPct, float transitPct, float walkPct, List<CommuteDestination> destinations)
        GetResidentialCommute(Zone residence, CityGrid grid, TazManager tazManager, OptimizedODMatrix od)
    {
        if (residence == null || tazManager == null || od == null)
        {
            return (0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 100f, 0f, 0f, new List<CommuteDestination>());
        }

        int origTazId = tazManager.GetTazIdForZoneTile(residence.GridPos);
        var origTaz = tazManager.GetTaz(origTazId);
        if (origTaz == null)
        {
            return (0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 100f, 0f, 0f, new List<CommuteDestination>());
        }

        float alpha = ComputeResidentialShare(origTaz, residence.Population, residence.ResidentialCap);
        var (outCar, outTransit, outWalk, totalTrips, comTrips, indTrips, comPct, indPct, carPct, transitPct, walkPct, dynDestinations) =
            DeaggregateResidentialFromTaz(origTazId, alpha, tazManager, od);

        var destinations = new List<CommuteDestination>(dynDestinations.Count);
        foreach (var d in dynDestinations)
        {
            Zone repZone = d.Zone ?? new Zone(
                d.Taz != null ? d.Taz.Id : 0,
                d.Type != ZoneType.Empty ? d.Type : ZoneType.Commercial,
                d.Taz != null ? d.Taz.GridCoord : Vector2I.Zero);
            destinations.Add(new CommuteDestination(repZone, d.Trips, d.Percentage));
        }

        return (outCar, outTransit, outWalk, totalTrips, comTrips, indTrips, comPct, indPct, carPct, transitPct, walkPct, destinations);
    }

    /// <summary>
    /// On-Demand TAZ deaggregation for a residential roadside parcel.
    /// Guarantees that the sum of trips across all residential entities in the TAZ equals the TAZ's total flow.
    /// </summary>
    public static (float outgoingCar, float outgoingTransit, float outgoingWalk, float totalTrips, float comTrips, float indTrips,
        float comPct, float indPct, float carPct, float transitPct, float walkPct, List<DynamicCommuteDestination> destinations)
        GetResidentialCommuteForParcel(RoadsideParcel residence, TazManager tazManager, OptimizedODMatrix od, ParcelManager parcelManager = null, CityGrid grid = null)
    {
        if (residence == null || tazManager == null || od == null)
        {
            return (0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 100f, 0f, 0f, new List<DynamicCommuteDestination>());
        }

        int origTazId = tazManager.GetTazIdForParcel(residence.Id);
        var origTaz = tazManager.GetTaz(origTazId);
        if (origTaz == null)
        {
            return (0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 100f, 0f, 0f, new List<DynamicCommuteDestination>());
        }

        float alpha = ComputeResidentialShare(origTaz, residence.Population, residence.ResidentialCap);
        return DeaggregateResidentialFromTaz(origTazId, alpha, tazManager, od);
    }

    /// <summary>
    /// On-Demand TAZ deaggregation for a workplace grid zone (Commercial or Industrial).
    /// Guarantees that the sum of incoming trips across all workplaces in the TAZ equals the TAZ's total incoming flow.
    /// </summary>
    public static (float incomingCar, float incomingTransit, float incomingWalk, float totalTrips, float carPct, float transitPct, float walkPct, List<CommuteOrigin> origins)
        GetIncomingCommute(Zone workplace, CityGrid grid, TazManager tazManager, OptimizedODMatrix od)
    {
        if (workplace == null || tazManager == null || od == null)
        {
            return (0f, 0f, 0f, 0f, 100f, 0f, 0f, new List<CommuteOrigin>());
        }

        int destTazId = tazManager.GetTazIdForZoneTile(workplace.GridPos);
        var destTaz = tazManager.GetTaz(destTazId);
        if (destTaz == null)
        {
            return (0f, 0f, 0f, 0f, 100f, 0f, 0f, new List<CommuteOrigin>());
        }

        float beta = ComputeWorkplaceShare(destTaz, workplace.Jobs, workplace.CommercialCap);
        var (inCar, inTransit, inWalk, totalTrips, carPct, transitPct, walkPct, dynOrigins) =
            DeaggregateIncomingFromTaz(destTazId, beta, tazManager, od);

        var origins = new List<CommuteOrigin>(dynOrigins.Count);
        foreach (var o in dynOrigins)
        {
            Zone repZone = o.Zone ?? new Zone(
                o.Taz != null ? o.Taz.Id : 0,
                ZoneType.Residential,
                o.Taz != null ? o.Taz.GridCoord : Vector2I.Zero);
            origins.Add(new CommuteOrigin(repZone, o.Trips, o.Percentage));
        }

        return (inCar, inTransit, inWalk, totalTrips, carPct, transitPct, walkPct, origins);
    }

    /// <summary>
    /// On-Demand TAZ deaggregation for a workplace roadside parcel (Commercial or Industrial).
    /// Guarantees that the sum of incoming trips across all workplaces in the TAZ equals the TAZ's total incoming flow.
    /// </summary>
    public static (float incomingCar, float incomingTransit, float incomingWalk, float totalTrips, float carPct, float transitPct, float walkPct, List<DynamicCommuteOrigin> origins)
        GetIncomingCommuteForParcel(RoadsideParcel workplace, TazManager tazManager, OptimizedODMatrix od, ParcelManager parcelManager = null, CityGrid grid = null)
    {
        if (workplace == null || tazManager == null || od == null)
        {
            return (0f, 0f, 0f, 0f, 100f, 0f, 0f, new List<DynamicCommuteOrigin>());
        }

        int destTazId = tazManager.GetTazIdForParcel(workplace.Id);
        var destTaz = tazManager.GetTaz(destTazId);
        if (destTaz == null)
        {
            return (0f, 0f, 0f, 0f, 100f, 0f, 0f, new List<DynamicCommuteOrigin>());
        }

        float beta = ComputeWorkplaceShare(destTaz, workplace.Jobs, workplace.CommercialCap);
        return DeaggregateIncomingFromTaz(destTazId, beta, tazManager, od);
    }

    /// <summary>
    /// Resolves top-8 illuminated commute corridors for a selected grid zone using CachedTreeBuffers in O(K).
    /// </summary>
    public static List<CommuteTarget> ComputeCommuteTargets(
        int zoneId,
        CityGrid grid,
        TazManager tazManager,
        OptimizedODMatrix odMatrix,
        RoadGraph graph)
    {
        var targets = new List<CommuteTarget>();
        if (grid == null || tazManager == null || odMatrix == null || graph == null || zoneId < 0 || zoneId >= grid.ZoneCount)
        {
            return targets;
        }

        var sourceZone = grid.GetZone(zoneId);
        if (sourceZone == null || sourceZone.Type == ZoneType.Empty)
        {
            return targets;
        }

        int tazId = tazManager.GetTazIdForZoneTile(sourceZone.GridPos);
        var sourceTaz = tazManager.GetTaz(tazId);
        if (sourceTaz == null)
        {
            return targets;
        }

        bool isResidential = sourceZone.Type == ZoneType.Residential;
        float share = isResidential
            ? ComputeResidentialShare(sourceTaz, sourceZone.Population, sourceZone.ResidentialCap)
            : ComputeWorkplaceShare(sourceTaz, sourceZone.Jobs, sourceZone.CommercialCap);

        return BuildCommuteTargetsForTazEntity(sourceTaz, share, isResidential, grid, tazManager, odMatrix, graph);
    }

    /// <summary>
    /// Resolves top-8 illuminated commute corridors for a selected roadside parcel using CachedTreeBuffers in O(K).
    /// </summary>
    public static List<CommuteTarget> ComputeCommuteTargetsForParcel(
        int parcelId,
        ParcelManager parcelManager,
        TazManager tazManager,
        OptimizedODMatrix odMatrix,
        RoadGraph graph,
        CityGrid grid = null)
    {
        var targets = new List<CommuteTarget>();
        if (parcelManager == null || tazManager == null || odMatrix == null || graph == null || parcelId <= 0)
        {
            return targets;
        }

        if (!parcelManager.ParcelMap.TryGetValue(parcelId, out var sourceParcel) || sourceParcel.ZoneType == ZoneType.Empty)
        {
            return targets;
        }

        int tazId = tazManager.GetTazIdForParcel(parcelId);
        var sourceTaz = tazManager.GetTaz(tazId);
        if (sourceTaz == null)
        {
            return targets;
        }

        bool isResidential = sourceParcel.ZoneType == ZoneType.Residential;
        float share = isResidential
            ? ComputeResidentialShare(sourceTaz, sourceParcel.Population, sourceParcel.ResidentialCap)
            : ComputeWorkplaceShare(sourceTaz, sourceParcel.Jobs, sourceParcel.CommercialCap);

        return BuildCommuteTargetsForTazEntity(sourceTaz, share, isResidential, grid, tazManager, odMatrix, graph);
    }

    private static float ComputeResidentialShare(TrafficAnalysisZone taz, int entityPop, int entityResCap)
    {
        if (taz == null) return 0f;
        if (taz.TotalPopulation > 0)
        {
            return (float)entityPop / taz.TotalPopulation;
        }

        int totalResCap = 0;
        for (int i = 0; i < taz.Zones.Count; i++)
        {
            var z = taz.Zones[i];
            if (z.Type == ZoneType.Residential) totalResCap += Math.Max(1, z.ResidentialCap);
        }
        for (int i = 0; i < taz.Parcels.Count; i++)
        {
            var p = taz.Parcels[i];
            if (p.ZoneType == ZoneType.Residential) totalResCap += Math.Max(1, p.ResidentialCap);
        }

        if (totalResCap > 0)
        {
            return (float)Math.Max(1, entityResCap) / totalResCap;
        }

        return taz.TotalCapacity > 0 ? (float)entityResCap / taz.TotalCapacity : 0f;
    }

    private static float ComputeWorkplaceShare(TrafficAnalysisZone taz, int entityJobs, int entityComCap)
    {
        if (taz == null) return 0f;
        if (taz.TotalJobs > 0)
        {
            return (float)entityJobs / taz.TotalJobs;
        }

        int totalWorkCap = 0;
        for (int i = 0; i < taz.Zones.Count; i++)
        {
            var z = taz.Zones[i];
            if (z.Type == ZoneType.Commercial || z.Type == ZoneType.Industrial)
                totalWorkCap += Math.Max(1, z.CommercialCap);
        }
        for (int i = 0; i < taz.Parcels.Count; i++)
        {
            var p = taz.Parcels[i];
            if (p.ZoneType == ZoneType.Commercial || p.ZoneType == ZoneType.Industrial)
                totalWorkCap += Math.Max(1, p.CommercialCap);
        }

        int cap = Math.Max(1, entityComCap);
        if (totalWorkCap > 0)
        {
            return (float)cap / totalWorkCap;
        }

        return taz.TotalCapacity > 0 ? (float)cap / taz.TotalCapacity : 0f;
    }

    private static (float outgoingCar, float outgoingTransit, float outgoingWalk, float totalTrips, float comTrips, float indTrips,
        float comPct, float indPct, float carPct, float transitPct, float walkPct, List<DynamicCommuteDestination> destinations)
        DeaggregateResidentialFromTaz(int origTazId, float alpha, TazManager tazManager, OptimizedODMatrix od)
    {
        float outCar = 0f;
        float outTransit = 0f;
        float outWalk = 0f;
        float comTrips = 0f;
        float indTrips = 0f;
        var destCandidates = new List<(Zone zone, RoadsideParcel parcel, TrafficAnalysisZone taz, ZoneType explicitType, float trips)>();

        int k = tazManager.ActiveZones.Count;
        for (int j = 0; j < k; j++)
        {
            float c = od.GetCarTrips(origTazId, j);
            float t = od.GetTransitTrips(origTazId, j);
            float w = od.GetWalkTrips(origTazId, j);
            float rawTrips = c + t + w;

            if (rawTrips <= 0.001f)
            {
                c = od.GetCarTrips(j, origTazId);
                t = od.GetTransitTrips(j, origTazId);
                w = od.GetWalkTrips(j, origTazId);
                rawTrips = c + t + w;
            }

            float dc = alpha * c;
            float dt = alpha * t;
            float dw = alpha * w;
            float pairTrips = dc + dt + dw;

            outCar += dc;
            outTransit += dt;
            outWalk += dw;

            var destTaz = tazManager.GetTaz(j);
            if (destTaz == null) continue;

            if (destTaz.TotalJobs > 0)
            {
                float comShare = (float)destTaz.CommercialJobs / destTaz.TotalJobs;
                comTrips += pairTrips * comShare;
                indTrips += pairTrips * (1.0f - comShare);
            }
            else
            {
                var (_, _, fallbackType) = SelectPrimaryWorkplaceInTaz(destTaz);
                if (fallbackType == ZoneType.Commercial) comTrips += pairTrips;
                else if (fallbackType == ZoneType.Industrial) indTrips += pairTrips;
            }

            if (pairTrips > 0.01f && (destTaz.TotalJobs > 0 || HasWorkplaceEntities(destTaz) || j != origTazId))
            {
                var (bestZone, bestParcel, bestType) = SelectPrimaryWorkplaceInTaz(destTaz);
                if (bestType != ZoneType.Empty || j != origTazId)
                {
                    destCandidates.Add((bestZone, bestParcel, destTaz, bestType, pairTrips));
                }
            }
        }

        float totalWorkTrips = comTrips + indTrips;
        float comPct = totalWorkTrips > 0f ? (comTrips / totalWorkTrips * 100f) : 0f;
        float indPct = totalWorkTrips > 0f ? (100f - comPct) : 0f;

        float totalTrips = outCar + outTransit + outWalk;
        var (carPct, transitPct, walkPct) = CalculateModeSplit(outCar, outTransit, outWalk);

        destCandidates.Sort((a, b) => b.trips.CompareTo(a.trips));
        var destinations = new List<DynamicCommuteDestination>(destCandidates.Count);
        foreach (var (candZone, candParcel, candTaz, explicitType, candTrips) in destCandidates)
        {
            float pct = totalTrips > 0f ? (candTrips / totalTrips * 100f) : 0f;
            destinations.Add(new DynamicCommuteDestination
            {
                Zone = candZone,
                Parcel = candParcel,
                Taz = candTaz,
                ExplicitType = explicitType,
                Trips = candTrips,
                Percentage = pct
            });
        }

        return (outCar, outTransit, outWalk, totalTrips, comTrips, indTrips, comPct, indPct, carPct, transitPct, walkPct, destinations);
    }

    private static (float incomingCar, float incomingTransit, float incomingWalk, float totalTrips, float carPct, float transitPct, float walkPct, List<DynamicCommuteOrigin> origins)
        DeaggregateIncomingFromTaz(int destTazId, float beta, TazManager tazManager, OptimizedODMatrix od)
    {
        float inCar = 0f;
        float inTransit = 0f;
        float inWalk = 0f;
        var originCandidates = new List<(Zone zone, RoadsideParcel parcel, TrafficAnalysisZone taz, float trips)>();

        int k = tazManager.ActiveZones.Count;
        for (int i = 0; i < k; i++)
        {
            float c = od.GetCarTrips(i, destTazId);
            float t = od.GetTransitTrips(i, destTazId);
            float w = od.GetWalkTrips(i, destTazId);
            float rawTrips = c + t + w;

            if (rawTrips <= 0.001f)
            {
                c = od.GetCarTrips(destTazId, i);
                t = od.GetTransitTrips(destTazId, i);
                w = od.GetWalkTrips(destTazId, i);
                rawTrips = c + t + w;
            }

            float dc = beta * c;
            float dt = beta * t;
            float dw = beta * w;
            float pairTrips = dc + dt + dw;

            inCar += dc;
            inTransit += dt;
            inWalk += dw;

            var origTaz = tazManager.GetTaz(i);
            if (origTaz == null) continue;

            if (pairTrips > 0.01f && (origTaz.TotalPopulation > 0 || HasResidentialEntities(origTaz) || i != destTazId))
            {
                var (bestZone, bestParcel) = SelectPrimaryResidenceInTaz(origTaz);
                originCandidates.Add((bestZone, bestParcel, origTaz, pairTrips));
            }
        }

        float totalTrips = inCar + inTransit + inWalk;
        var (carPct, transitPct, walkPct) = CalculateModeSplit(inCar, inTransit, inWalk);

        originCandidates.Sort((a, b) => b.trips.CompareTo(a.trips));
        var origins = new List<DynamicCommuteOrigin>(originCandidates.Count);
        foreach (var (candZone, candParcel, candTaz, candTrips) in originCandidates)
        {
            float pct = totalTrips > 0f ? (candTrips / totalTrips * 100f) : 0f;
            origins.Add(new DynamicCommuteOrigin
            {
                Zone = candZone,
                Parcel = candParcel,
                Taz = candTaz,
                Trips = candTrips,
                Percentage = pct
            });
        }

        return (inCar, inTransit, inWalk, totalTrips, carPct, transitPct, walkPct, origins);
    }

    private static List<CommuteTarget> BuildCommuteTargetsForTazEntity(
        TrafficAnalysisZone sourceTaz,
        float share,
        bool isResidential,
        CityGrid grid,
        TazManager tazManager,
        OptimizedODMatrix odMatrix,
        RoadGraph graph)
    {
        var targets = new List<CommuteTarget>();
        int k = tazManager.ActiveZones.Count;

        if (sourceTaz.PrimaryRoadNodeId >= 0 && !odMatrix.CachedTreeBuffers.ContainsKey(sourceTaz.PrimaryRoadNodeId))
        {
            odMatrix.UpdateEquilibriumTreesAndTimes(tazManager, graph);
        }

        for (int otherIdx = 0; otherIdx < k; otherIdx++)
        {
            var otherTaz = tazManager.GetTaz(otherIdx);
            if (otherTaz == null) continue;

            int origId = isResidential ? sourceTaz.Id : otherIdx;
            int destId = isResidential ? otherIdx : sourceTaz.Id;

            float c = odMatrix.GetCarTrips(origId, destId);
            float t = odMatrix.GetTransitTrips(origId, destId);
            float w = odMatrix.GetWalkTrips(origId, destId);
            float rawTrips = c + t + w;

            if (rawTrips <= 0.001f)
            {
                c = odMatrix.GetCarTrips(destId, origId);
                t = odMatrix.GetTransitTrips(destId, origId);
                w = odMatrix.GetWalkTrips(destId, origId);
                rawTrips = c + t + w;
            }

            float scaledTrips = share * rawTrips;
            if (scaledTrips <= 0.05f) continue;

            if (isResidential && otherTaz.TotalJobs <= 0 && !HasWorkplaceEntities(otherTaz)) continue;
            if (!isResidential && otherTaz.TotalPopulation <= 0 && !HasResidentialEntities(otherTaz)) continue;

            int origNode = isResidential ? sourceTaz.PrimaryRoadNodeId : otherTaz.PrimaryRoadNodeId;
            int destNode = isResidential ? otherTaz.PrimaryRoadNodeId : sourceTaz.PrimaryRoadNodeId;

            var edgePath = ExtractPathFromTreeBuffer(odMatrix, origNode, destNode);
            if (edgePath.Count == 0 && origNode != destNode)
            {
                edgePath = ExtractPathFromTreeBuffer(odMatrix, destNode, origNode);
            }
            if (edgePath.Count == 0 && origNode == destNode)
            {
                if (otherTaz.InternalEdgeIds.Count > 0)
                    edgePath.Add(otherTaz.InternalEdgeIds[0]);
                else if (sourceTaz.InternalEdgeIds.Count > 0)
                    edgePath.Add(sourceTaz.InternalEdgeIds[0]);
            }

            Vector2 targetPos = otherTaz.Centroid;
            ZoneType targetType;
            int targetZoneId = -1;
            int targetParcelId = -1;

            if (isResidential)
            {
                var (wZone, wParcel, wType) = SelectPrimaryWorkplaceInTaz(otherTaz);
                targetType = wType != ZoneType.Empty ? wType : ZoneType.Commercial;
                if (wParcel != null)
                {
                    targetParcelId = wParcel.Id;
                    targetPos = wParcel.Center;
                }
                else if (wZone != null)
                {
                    targetZoneId = wZone.Id;
                    if (grid != null) targetPos = grid.GetWorldCenter(wZone.Id);
                }
            }
            else
            {
                targetType = ZoneType.Residential;
                var (rZone, rParcel) = SelectPrimaryResidenceInTaz(otherTaz);
                if (rParcel != null)
                {
                    targetParcelId = rParcel.Id;
                    targetPos = rParcel.Center;
                }
                else if (rZone != null)
                {
                    targetZoneId = rZone.Id;
                    if (grid != null) targetPos = grid.GetWorldCenter(rZone.Id);
                }
            }

            float travelTime = odMatrix.GetTravelTime(origId, destId);
            if (travelTime >= float.MaxValue * 0.5f)
            {
                travelTime = odMatrix.GetTravelTime(destId, origId);
                if (travelTime >= float.MaxValue * 0.5f) travelTime = 0f;
            }

            targets.Add(new CommuteTarget
            {
                ZoneId = targetZoneId,
                ParcelId = targetParcelId,
                Position = targetPos,
                Volume = scaledTrips,
                Type = targetType,
                TravelTime = travelTime,
                EdgePath = edgePath
            });
        }

        targets.Sort((a, b) => b.Volume.CompareTo(a.Volume));
        if (targets.Count > 8) targets.RemoveRange(8, targets.Count - 8);

        return targets;
    }

    private static bool HasWorkplaceEntities(TrafficAnalysisZone taz)
    {
        for (int i = 0; i < taz.Zones.Count; i++)
        {
            var t = taz.Zones[i].Type;
            if (t == ZoneType.Commercial || t == ZoneType.Industrial) return true;
        }
        for (int i = 0; i < taz.Parcels.Count; i++)
        {
            var t = taz.Parcels[i].ZoneType;
            if (t == ZoneType.Commercial || t == ZoneType.Industrial) return true;
        }
        return false;
    }

    private static bool HasResidentialEntities(TrafficAnalysisZone taz)
    {
        for (int i = 0; i < taz.Zones.Count; i++)
        {
            if (taz.Zones[i].Type == ZoneType.Residential) return true;
        }
        for (int i = 0; i < taz.Parcels.Count; i++)
        {
            if (taz.Parcels[i].ZoneType == ZoneType.Residential) return true;
        }
        return false;
    }

    private static (Zone zone, RoadsideParcel parcel, ZoneType type) SelectPrimaryWorkplaceInTaz(TrafficAnalysisZone taz)
    {
        Zone bestZone = null;
        RoadsideParcel bestParcel = null;
        int bestWeight = -1;

        for (int i = 0; i < taz.Zones.Count; i++)
        {
            var z = taz.Zones[i];
            if (z.Type != ZoneType.Commercial && z.Type != ZoneType.Industrial) continue;
            int w = z.Jobs > 0 ? z.Jobs : z.CommercialCap + 1;
            if (w > bestWeight)
            {
                bestWeight = w;
                bestZone = z;
                bestParcel = null;
            }
        }

        for (int i = 0; i < taz.Parcels.Count; i++)
        {
            var p = taz.Parcels[i];
            if (p.ZoneType != ZoneType.Commercial && p.ZoneType != ZoneType.Industrial) continue;
            int w = p.Jobs > 0 ? p.Jobs : p.CommercialCap + 1;
            if (w > bestWeight)
            {
                bestWeight = w;
                bestParcel = p;
                bestZone = null;
            }
        }

        ZoneType type = bestParcel != null ? bestParcel.ZoneType :
                        (bestZone != null ? bestZone.Type :
                        (taz.CommercialJobs > 0 || taz.IndustrialJobs > 0
                            ? (taz.CommercialJobs >= taz.IndustrialJobs ? ZoneType.Commercial : ZoneType.Industrial)
                            : ZoneType.Empty));

        return (bestZone, bestParcel, type);
    }

    private static (Zone zone, RoadsideParcel parcel) SelectPrimaryResidenceInTaz(TrafficAnalysisZone taz)
    {
        Zone bestZone = null;
        RoadsideParcel bestParcel = null;
        int bestWeight = -1;

        for (int i = 0; i < taz.Zones.Count; i++)
        {
            var z = taz.Zones[i];
            if (z.Type != ZoneType.Residential) continue;
            int w = z.Population > 0 ? z.Population : z.ResidentialCap + 1;
            if (w > bestWeight)
            {
                bestWeight = w;
                bestZone = z;
                bestParcel = null;
            }
        }

        for (int i = 0; i < taz.Parcels.Count; i++)
        {
            var p = taz.Parcels[i];
            if (p.ZoneType != ZoneType.Residential) continue;
            int w = p.Population > 0 ? p.Population : p.ResidentialCap + 1;
            if (w > bestWeight)
            {
                bestWeight = w;
                bestParcel = p;
                bestZone = null;
            }
        }

        return (bestZone, bestParcel);
    }
}
