using Godot;
using System;
using System.Collections.Generic;

namespace CitySim.Simulation;

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

    public ODMatrix(int zoneCount)
    {
        ZoneCount = zoneCount;
        Trips = new float[zoneCount, zoneCount];
        CarTrips = new float[zoneCount, zoneCount];
        TransitTrips = new float[zoneCount, zoneCount];
        WalkTrips = new float[zoneCount, zoneCount];
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

    public void Recalculate(
        float hour,
        CityGrid grid,
        float[,] distances,
        bool hasTransit,
        HashSet<int> coveredZoneIds = null,
        float averageTicketPrice = 12f,
        float[,] walkingDistances = null,
        TransitManager transitManager = null)
    {
        CurrentHour = hour;
        float demandMult = GetDemandMultiplier(hour);
        float dirBias = GetDirectionalBias(hour);

        Array.Clear(Trips, 0, Trips.Length);
        Array.Clear(CarTrips, 0, CarTrips.Length);
        Array.Clear(TransitTrips, 0, TransitTrips.Length);
        Array.Clear(WalkTrips, 0, WalkTrips.Length);
        TotalTrips = 0f;
        TotalCarTrips = 0f;
        TotalTransitTrips = 0f;
        TotalWalkTrips = 0f;

        // 1. Calculate raw gravity attraction only for active populated zones
        float rawTotalScore = 0f;
        var activeIds = grid.ActiveZoneIds;
        int activeCount = activeIds.Count;

        for (int a = 0; a < activeCount; a++)
        {
            int i = activeIds[a];
            var zoneI = grid.GetZone(i);

            float prod = (zoneI.Type == ZoneType.Residential)
                ? zoneI.Population * dirBias
                : zoneI.Jobs * (1f - dirBias);

            if (prod <= 0f) continue;

            for (int b = 0; b < activeCount; b++)
            {
                if (a == b) continue;
                int j = activeIds[b];
                var zoneJ = grid.GetZone(j);

                float attr = (zoneJ.Type == ZoneType.Commercial || zoneJ.Type == ZoneType.Industrial)
                    ? zoneJ.Jobs * dirBias
                    : zoneJ.Population * (1f - dirBias);

                if (attr <= 0f) continue;

                float travelTime = distances[i, j];
                if (travelTime <= 0f || travelTime >= float.MaxValue) continue;

                // Standard gravity model impedance function
                float impedance = 1.0f / Mathf.Pow(travelTime + 2.0f, 1.25f);
                float score = prod * attr * impedance;

                Trips[i, j] = score;
                rawTotalScore += score;
            }
        }

        // 2. Realistic 1,000,000+ population trip scaling:
        int totalPop = grid.TotalPopulation();
        float dailyTripsTarget = totalPop * 1.5f;
        float targetHourlyTrips = dailyTripsTarget * demandMult;

        float normalizationScale = rawTotalScore > 0f ? (targetHourlyTrips / rawTotalScore) : 0f;

        float actualTotalTrips = 0f;
        float actualTotalCar = 0f;
        float actualTotalTransit = 0f;
        float actualTotalWalk = 0f;

        for (int a = 0; a < activeCount; a++)
        {
            int i = activeIds[a];
            var zoneI = grid.GetZone(i);

            for (int b = 0; b < activeCount; b++)
            {
                if (a == b) continue;
                int j = activeIds[b];
                var zoneJ = grid.GetZone(j);

                float score = Trips[i, j];
                if (score <= 0f) continue;

                float travelTime = distances[i, j];
                float finalTrips = score * normalizationScale;
                Trips[i, j] = finalTrips;

                // -------------------------------------------------------------
                // 3-Mode Multinomial Choice Model (Walk, Car, Transit)
                // -------------------------------------------------------------
                float walkTime = (walkingDistances != null && walkingDistances[i, j] < float.MaxValue)
                    ? walkingDistances[i, j]
                    : (travelTime * 10f);

                // A. Pure Walking generalized cost (zero fare, high distance penalty)
                float walkCost = walkTime * 0.9f + Mathf.Pow(Mathf.Max(0f, walkTime - 25f), 1.5f) * 0.7f;

                // B. Private Car generalized cost (vehicle travel time + operating cost/parking)
                float carCost = (travelTime * 1.4f) + 22.0f;

                // C. Multimodal Transit generalized cost (Access Walk + Wait + In-Vehicle + Fare + Egress Walk)
                float transitCost;
                if (transitManager != null && transitManager.Routes.Count > 0)
                {
                    float bestAccessDist = float.MaxValue;
                    float bestEgressDist = float.MaxValue;

                    foreach (var stop in transitManager.Stops.Values)
                    {
                        var sZone = grid.GetZone(stop.ZoneId);
                        if (sZone == null) continue;
                        int dI = Mathf.Abs(zoneI.GridPos.X - sZone.GridPos.X) + Mathf.Abs(zoneI.GridPos.Y - sZone.GridPos.Y);
                        if (dI < bestAccessDist) bestAccessDist = dI;
                        int dJ = Mathf.Abs(zoneJ.GridPos.X - sZone.GridPos.X) + Mathf.Abs(zoneJ.GridPos.Y - sZone.GridPos.Y);
                        if (dJ < bestEgressDist) bestEgressDist = dJ;
                    }

                    if (bestAccessDist > 3.5f || bestEgressDist > 3.5f)
                    {
                        // Outside walking catchment of transit stops
                        transitCost = 500f;
                    }
                    else
                    {
                        float accessWalk = bestAccessDist * 12.8f;
                        float egressWalk = bestEgressDist * 12.8f;
                        float waitTime = 5.0f;
                        float inVeh = travelTime * 1.15f;
                        transitCost = averageTicketPrice + (accessWalk + egressWalk) * 0.8f + (waitTime * 1.2f) + (inVeh * 1.0f);
                    }
                }
                else if (coveredZoneIds != null && coveredZoneIds.Count > 0)
                {
                    bool originCovered = coveredZoneIds.Contains(i);
                    bool destCovered = coveredZoneIds.Contains(j);

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

                WalkTrips[i, j] = wTrips;
                CarTrips[i, j] = cTrips;
                TransitTrips[i, j] = tTrips;

                actualTotalTrips += finalTrips;
                actualTotalWalk += wTrips;
                actualTotalCar += cTrips;
                actualTotalTransit += tTrips;
            }
        }

        TotalTrips = actualTotalTrips;
        TotalWalkTrips = actualTotalWalk;
        TotalCarTrips = actualTotalCar;
        TotalTransitTrips = actualTotalTransit;
    }
}
