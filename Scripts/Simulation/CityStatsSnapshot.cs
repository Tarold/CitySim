namespace CitySim.Simulation;

/// <summary>
/// Cached city-wide HUD statistics. Full scans over zones, parcels, edges and transit stops are done
/// on the macro tick (OD recalculation) instead of every frame; population and jobs can be refreshed
/// separately when LUTI growth changes them.
/// </summary>
public readonly struct CityStatsSnapshot
{
    public CityStatsSnapshot(int population, int jobs, float totalTrips, float averageCongestion, float transitCoverage)
    {
        Population = population;
        Jobs = jobs;
        TotalTrips = totalTrips;
        AverageCongestion = averageCongestion;
        TransitCoverage = transitCoverage;
    }

    /// <summary>Residents in grid zones plus roadside parcels.</summary>
    public int Population { get; }

    /// <summary>Jobs in grid zones plus roadside parcels.</summary>
    public int Jobs { get; }

    /// <summary>Hourly trips from the last OD recalculation.</summary>
    public float TotalTrips { get; }

    /// <summary>Mean V/C ratio over loaded active edges after the last traffic assignment.</summary>
    public float AverageCongestion { get; }

    /// <summary>Share of residential zones and parcels within walking distance of a transit stop [0..1].</summary>
    public float TransitCoverage { get; }

    /// <summary>
    /// Captures all statistics. Intended for the macro tick, right after traffic assignment.
    /// </summary>
    public static CityStatsSnapshot Capture(
        CityGrid grid,
        ParcelManager parcelManager,
        OptimizedODMatrix odMatrix,
        TrafficEngine trafficEngine,
        RoadGraph roadGraph,
        TransitManager transitManager,
        float? precomputedTransitCoverage = null)
    {
        var (population, jobs) = CountLandUse(grid, parcelManager);
        float trips = odMatrix?.TotalTrips ?? 0f;
        float congestion = (trafficEngine != null && roadGraph != null) ? trafficEngine.GetAverageCongestion(roadGraph) : 0f;
        float coverage = precomputedTransitCoverage ?? transitManager?.GetTransitCoverage(grid, roadGraph, parcelManager) ?? 0f;
        return new CityStatsSnapshot(population, jobs, trips, congestion, coverage);
    }

    /// <summary>
    /// Returns a copy with population and jobs recounted, keeping the macro-tick statistics.
    /// </summary>
    public CityStatsSnapshot WithLandUse(CityGrid grid, ParcelManager parcelManager)
    {
        var (population, jobs) = CountLandUse(grid, parcelManager);
        return new CityStatsSnapshot(population, jobs, TotalTrips, AverageCongestion, TransitCoverage);
    }

    private static (int Population, int Jobs) CountLandUse(CityGrid grid, ParcelManager parcelManager)
    {
        int population = (grid?.TotalPopulation() ?? 0) + (parcelManager?.TotalPopulation ?? 0);
        int jobs = (grid?.TotalJobs() ?? 0) + (parcelManager?.TotalJobs ?? 0);
        return (population, jobs);
    }
}
