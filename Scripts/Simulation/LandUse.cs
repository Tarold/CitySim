namespace CitySim.Simulation;

/// <summary>
/// Common view of a land-use unit: a grid cell (<see cref="Zone"/>) or a roadside lot
/// (<see cref="RoadsideParcel"/>). Lets TAZ aggregation, statistics and the economy treat both alike while
/// type-specific rules (growth formulas, congestion sampling) stay with each type.
/// </summary>
public interface ILandUseUnit
{
    ZoneType LandUseType { get; }
    int Population { get; }
    int Jobs { get; }
    int ResidentialCap { get; }
    int CommercialCap { get; }
    DensityLevel Density { get; }
    float AccessibilityScore { get; }
    float LocalCongestion { get; }
}

/// <summary>
/// Shared rules for <see cref="ILandUseUnit"/>.
/// </summary>
public static class LandUse
{
    /// <summary>Residential plus commercial capacity.</summary>
    public static int TotalCapacity(ILandUseUnit unit) => unit.ResidentialCap + unit.CommercialCap;

    /// <summary>
    /// True when the unit generates or attracts trips and is aggregated into a TAZ: zoned (not empty) and
    /// with residents, jobs or capacity.
    /// </summary>
    public static bool ContributesToTaz(ILandUseUnit unit) =>
        unit != null &&
        unit.LandUseType != ZoneType.Empty &&
        (unit.Population > 0 || unit.Jobs > 0 || TotalCapacity(unit) > 0);

    /// <summary>True for commercial and industrial (job-providing) units.</summary>
    public static bool IsWorkplace(ZoneType type) => type == ZoneType.Commercial || type == ZoneType.Industrial;
}
