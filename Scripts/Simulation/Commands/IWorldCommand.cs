namespace CitySim.Simulation.Commands;

/// <summary>
/// A player edit applied to a <see cref="SimulationWorld"/>: validates the target and the budget,
/// mutates the world, runs the topology chain or macro recalculation, and reports the outcome.
/// Commands are headless; the Godot layer refreshes rendering and shows <see cref="CommandResult.Message"/>.
/// </summary>
public interface IWorldCommand
{
    CommandResult Execute(SimulationWorld world);
}

/// <summary>
/// Shared message fragments for command results.
/// </summary>
public static class CommandMessages
{
    /// <summary>Suffix describing transit routes re-routed or removed by a topology change.</summary>
    public static string DescribeTransitChanges(TopologyChangeResult result)
    {
        var transit = result.Transit;
        if (!transit.HasChanges) return string.Empty;

        string note = "";
        if (transit.ReroutedRouteIds.Count > 0) note += $" 🚌 {transit.ReroutedRouteIds.Count} bus route(s) re-routed.";
        if (transit.RemovedRouteIds.Count > 0) note += $" ⚠️ {transit.RemovedRouteIds.Count} bus route(s) removed (no road path).";
        return note;
    }

    /// <summary>Display name of a zone type used in zoning messages.</summary>
    public static string ZoneTypeName(ZoneType type) => type switch
    {
        ZoneType.Residential => "Residential (🏡)",
        ZoneType.Commercial  => "Commercial (🏢)",
        ZoneType.Industrial  => "Industrial (🏭)",
        _ => type.ToString()
    };

    public static string SideName(ParcelSide side) => side == ParcelSide.Left ? "Left" : "Right";
}
