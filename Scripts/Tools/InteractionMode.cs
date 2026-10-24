// Kept in the CitySim.UI namespace for compatibility with existing UI code and tests;
// lives in Scripts/Tools because the headless tool classes depend on it.
namespace CitySim.UI;

/// <summary>
/// Determines how mouse clicks on the game map are interpreted.
/// </summary>
public enum InteractionMode
{
    /// <summary>Click zones to inspect commuters, mode split, and analytics.</summary>
    Inspect,
    /// <summary>Click or drag between adjacent grid cells to construct road segments.</summary>
    BuildRoad,
    /// <summary>Click an existing road connection to tear it down.</summary>
    Demolish,
    /// <summary>Designate empty or existing cells as Residential district.</summary>
    ZoneResidential,
    /// <summary>Designate empty or existing cells as Commercial district.</summary>
    ZoneCommercial,
    /// <summary>Designate empty or existing cells as Industrial district.</summary>
    ZoneIndustrial,
    /// <summary>Clear existing zoned cell back to empty terrain.</summary>
    Dezone,
    /// <summary>Design and construct a new public transit route with custom stops.</summary>
    CreateTransitRoute,
    /// <summary>Click start, end, and drag/click curvature apex to build curved Bézier roads.</summary>
    BuildCurvedRoad,
    /// <summary>Click on an existing road segment to toggle dedicated public transit bus lanes.</summary>
    ToggleBusLane
}

/// <summary>
/// Classification helpers for <see cref="InteractionMode"/>.
/// </summary>
public static class InteractionModeExtensions
{
    /// <summary>True for the drag-capable zoning modes (residential, commercial, industrial, dezone).</summary>
    public static bool IsZoningMode(this InteractionMode mode) =>
        mode == InteractionMode.ZoneResidential ||
        mode == InteractionMode.ZoneCommercial ||
        mode == InteractionMode.ZoneIndustrial ||
        mode == InteractionMode.Dezone;

    /// <summary>True for straight and curved road construction.</summary>
    public static bool IsRoadBuildMode(this InteractionMode mode) =>
        mode == InteractionMode.BuildRoad || mode == InteractionMode.BuildCurvedRoad;

    /// <summary>Zone type applied by a zoning mode (<see cref="CitySim.Simulation.ZoneType.Empty"/> for dezone and others).</summary>
    public static CitySim.Simulation.ZoneType GetZoneType(this InteractionMode mode) => mode switch
    {
        InteractionMode.ZoneResidential => CitySim.Simulation.ZoneType.Residential,
        InteractionMode.ZoneCommercial  => CitySim.Simulation.ZoneType.Commercial,
        InteractionMode.ZoneIndustrial  => CitySim.Simulation.ZoneType.Industrial,
        _                               => CitySim.Simulation.ZoneType.Empty
    };
}
