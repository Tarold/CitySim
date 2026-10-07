using Godot;
using System;

namespace CitySim.Simulation;

public class ODMatrix
{
    public int ZoneCount;
    public float[,] Trips;
    public float[,] CarTrips;
    public float[,] TransitTrips;
    public float TotalTrips;
    public float CurrentHour;

    public ODMatrix(int zoneCount)
    {
        ZoneCount = zoneCount;
        Trips = new float[zoneCount, zoneCount];
        CarTrips = new float[zoneCount, zoneCount];
        TransitTrips = new float[zoneCount, zoneCount];
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

    public void Recalculate(float hour, CityGrid grid, float[,] distances, bool hasTransit)
    {
        CurrentHour = hour;
        float demandMult = GetDemandMultiplier(hour);
        float dirBias = GetDirectionalBias(hour);

        Array.Clear(Trips, 0, Trips.Length);
        Array.Clear(CarTrips, 0, CarTrips.Length);
        Array.Clear(TransitTrips, 0, TransitTrips.Length);
        TotalTrips = 0f;

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
        for (int a = 0; a < activeCount; a++)
        {
            int i = activeIds[a];
            for (int b = 0; b < activeCount; b++)
            {
                int j = activeIds[b];
                float score = Trips[i, j];
                if (score <= 0f) continue;

                float finalTrips = score * normalizationScale;
                Trips[i, j] = finalTrips;

                if (hasTransit)
                {
                    TransitTrips[i, j] = finalTrips * 0.35f;
                    CarTrips[i, j] = finalTrips * 0.65f;
                }
                else
                {
                    TransitTrips[i, j] = 0f;
                    CarTrips[i, j] = finalTrips;
                }

                actualTotalTrips += finalTrips;
            }
        }

        TotalTrips = actualTotalTrips;
    }
}
