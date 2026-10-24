using Godot;
using System.Collections.Generic;

namespace CitySim.Simulation;

/// <summary>
/// Manages the city's economy, including player funds, construction costs, and periodic financial ticks.
/// </summary>
public class EconomyManager
{
    /// <summary>
    /// Gets the current money balance of the city.
    /// </summary>
    public float Money { get; private set; }

    /// <summary>
    /// Gets the net income or expense (delta) calculated during the last financial tick.
    /// </summary>
    public float LastDelta { get; private set; }

    /// <summary>
    /// Gets the total tax revenue collected during the last financial tick.
    /// </summary>
    public float LastTaxRevenue { get; private set; }

    /// <summary>
    /// Gets the transit fare income collected during the last financial tick.
    /// </summary>
    public float LastTransitRevenue { get; private set; }

    /// <summary>
    /// Gets the operating expenses deducted during the last financial tick.
    /// </summary>
    public float LastExpenses { get; private set; }

    /// <summary>
    /// Gets the population tax revenue collected during the last financial tick.
    /// </summary>
    public float LastPopulationTax { get; private set; }

    /// <summary>
    /// Gets the job tax revenue collected during the last financial tick.
    /// </summary>
    public float LastJobTax { get; private set; }
    
    /// <summary>
    /// The cost to build a single road segment.
    /// </summary>
    public const float RoadSegmentCost = 100f;

    /// <summary>
    /// The cost to designate a grid cell as a zone.
    /// </summary>
    public const float ZoningCost = 50f;

    /// <summary>
    /// The base cost to launch a new transit route.
    /// </summary>
    public const float TransitRouteBaseCost = 500f;
    
    /// <summary>
    /// The maintenance cost per active road segment per financial tick.
    /// </summary>
    public const float RoadMaintenancePerSegment = 2f;

    /// <summary>
    /// The tax revenue generated per residential population per financial tick (scaled to daily).
    /// </summary>
    public float TaxPerPopulation { get; set; } = 0.05f;

    /// <summary>
    /// The tax revenue generated per filled job per financial tick (scaled to daily).
    /// </summary>
    public float TaxPerJob { get; set; } = 0.1f;

    /// <summary>
    /// Initializes a new instance of the EconomyManager class.
    /// </summary>
    /// <param name="initialMoney">The starting money balance.</param>
    public EconomyManager(float initialMoney = 50000f)
    {
        Money = initialMoney;
    }

    /// <summary>
    /// Checks if the player has sufficient funds for a transaction.
    /// </summary>
    /// <param name="amount">The cost to check.</param>
    /// <returns>True if the player can afford the amount; otherwise, false.</returns>
    public bool CanAfford(float amount)
    {
        return Money >= amount;
    }

    /// <summary>
    /// Attempts to deduct the specified amount from the player's funds.
    /// </summary>
    /// <param name="amount">The amount to spend.</param>
    /// <returns>True if the transaction was successful; otherwise, false.</returns>
    public bool Spend(float amount)
    {
        if (CanAfford(amount))
        {
            Money -= amount;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Adds income directly to the player's funds.
    /// </summary>
    /// <param name="amount">The amount of income to add.</param>
    public void AddIncome(float amount)
    {
        Money += amount;
    }

    /// <summary>
    /// Sets the player's funds balance directly.
    /// </summary>
    /// <param name="amount">The new balance.</param>
    public void SetBalance(float amount)
    {
        Money = amount;
    }

    /// <summary>
    /// Gets the tax revenue lost due to severe traffic congestion (R_congestion &gt; 1.5) during the last financial tick.
    /// </summary>
    public float LastCongestionTaxLoss { get; private set; }

    /// <summary>
    /// Computes the LUTI tax attractiveness multiplier for a zone or parcel based on local road congestion (R_congestion).
    /// Districts gridlocked in severe traffic jams (R_congestion &gt; 1.5) lose tax attractiveness.
    /// </summary>
    public static float CalculateTaxAttractivenessMultiplier(float congestionRatio, float accessibilityScore = 1.0f)
    {
        if (congestionRatio > ParcelManager.CongestionStagnationThreshold)
        {
            float excess = congestionRatio - ParcelManager.CongestionStagnationThreshold;
            float penalty = 1.0f / (1.0f + 0.85f * excess);
            if (accessibilityScore < 0.8f)
            {
                penalty *= Mathf.Lerp(0.75f, 1.0f, Mathf.Clamp(accessibilityScore / 0.8f, 0f, 1f));
            }
            return Mathf.Clamp(penalty, 0.25f, 0.95f);
        }
        return 1.0f;
    }

    /// <summary>
    /// Adds one land-use unit's hourly tax: residents for residential units, jobs for commercial/industrial,
    /// scaled by the congestion/accessibility attractiveness multiplier.
    /// </summary>
    private void AccumulateLandUseTax(
        ILandUseUnit unit, float congestionRatio, float accessibility,
        ref float baseTaxWithoutPenalty, ref float populationTax, ref float jobTax)
    {
        float taxMult = CalculateTaxAttractivenessMultiplier(congestionRatio, accessibility);

        if (unit.LandUseType == ZoneType.Residential)
        {
            float baseTax = (unit.Population * TaxPerPopulation) / 24f;
            baseTaxWithoutPenalty += baseTax;
            populationTax += baseTax * taxMult;
        }
        else if (LandUse.IsWorkplace(unit.LandUseType))
        {
            float baseTax = (unit.Jobs * TaxPerJob) / 24f;
            baseTaxWithoutPenalty += baseTax;
            jobTax += baseTax * taxMult;
        }
    }

    /// <summary>
    /// Processes a periodic financial tick, deducting maintenance costs and adding tax revenues.
    /// Districts blocked in severe congestion (R_congestion &gt; 1.5) suffer reduced tax attractiveness.
    /// </summary>
    /// <param name="grid">The city grid used to calculate tax revenues from zones.</param>
    /// <param name="roadGraph">The road graph used to calculate road maintenance expenses and local congestion.</param>
    /// <param name="transitManager">The transit manager used to calculate transit network expenses.</param>
    /// <param name="parcelManager">Optional roadside parcel manager for ribbon parcel tax revenues.</param>
    /// <param name="tazManager">Optional TAZ manager for macro-district congestion and accessibility lookup.</param>
    public void ProcessFinancialTick(
        CityGrid grid,
        RoadGraph roadGraph,
        TransitManager transitManager,
        ParcelManager parcelManager = null,
        TazManager tazManager = null)
    {
        float expenses = 0f;
        float popTax = 0f;
        float jobTax = 0f;
        float baseTaxWithoutPenalty = 0f;
        float transitIncome = 0f;

        // 1. Road Maintenance
        if (roadGraph != null)
        {
            int activeSegments = 0;
            foreach (var edge in roadGraph.Edges)
            {
                if (edge.FromId != -1) activeSegments++;
            }
            activeSegments /= 2; // Because edges are bidirectional
            expenses += (activeSegments * RoadMaintenancePerSegment) / 24f;
        }

        // 2. Transit Maintenance (Using Fleet Size) and Transit Fare Income
        if (transitManager != null)
        {
            foreach (var route in transitManager.Routes)
            {
                expenses += (route.FleetSize * 15f) / 24f; // Fixed cost per vehicle per tick
                expenses += 50f / 24f; // Route maintenance

                // Transit fare income per hourly tick (from route turnover)
                transitIncome += route.DailyRevenue / 24f;
            }
        }
        
        // 3. Taxes from Grid Zones (with LUTI congestion attractiveness evaluation)
        if (grid != null)
        {
            for (int i = 0; i < grid.ZoneCount; i++)
            {
                var zone = grid.GetZone(i);
                if (zone == null || zone.Type == ZoneType.Empty || zone.Type == ZoneType.Entrance) continue;

                float rCong = zone.LocalCongestion;
                float access = zone.AccessibilityScore;

                if (roadGraph != null)
                {
                    rCong = Mathf.Max(rCong, OptimizedODMatrix.GetNodeCongestion(zone.Id, roadGraph));
                }
                if (tazManager != null)
                {
                    int tazId = tazManager.GetTazIdForZoneTile(zone.GridPos);
                    var taz = tazManager.GetTaz(tazId);
                    if (taz != null)
                    {
                        rCong = Mathf.Max(rCong, Mathf.Max(taz.AverageCongestion, taz.MaxCongestion));
                        access = taz.AccessibilityScore;
                    }
                }

                AccumulateLandUseTax(zone, rCong, access, ref baseTaxWithoutPenalty, ref popTax, ref jobTax);
            }
        }

        // 4. Taxes from Roadside Parcels (with LUTI congestion attractiveness evaluation)
        if (parcelManager != null)
        {
            var parcels = parcelManager.Parcels;
            for (int i = 0; i < parcels.Count; i++)
            {
                var parcel = parcels[i];
                if (parcel == null || parcel.ZoneType == ZoneType.Empty) continue;

                float rCong = parcel.LocalCongestion;
                float access = parcel.AccessibilityScore;

                if (roadGraph != null)
                {
                    rCong = Mathf.Max(rCong, OptimizedODMatrix.GetParcelEdgeCongestion(parcel, roadGraph));
                }
                if (tazManager != null)
                {
                    int tazId = tazManager.GetTazIdForParcel(parcel.Id);
                    var taz = tazManager.GetTaz(tazId);
                    if (taz != null)
                    {
                        rCong = Mathf.Max(rCong, taz.AverageCongestion);
                        access = taz.AccessibilityScore;
                    }
                }

                AccumulateLandUseTax(parcel, rCong, access, ref baseTaxWithoutPenalty, ref popTax, ref jobTax);
            }
        }

        float taxIncome = popTax + jobTax;
        float income = taxIncome + transitIncome;

        // Apply
        float delta = income - expenses;
        Money += delta;
        LastDelta = delta;

        LastPopulationTax = popTax;
        LastJobTax = jobTax;
        LastTaxRevenue = taxIncome;
        LastCongestionTaxLoss = Mathf.Max(0f, baseTaxWithoutPenalty - taxIncome);
        LastTransitRevenue = transitIncome;
        LastExpenses = expenses;
    }
}
