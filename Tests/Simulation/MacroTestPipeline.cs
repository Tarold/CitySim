using CitySim.Simulation;

namespace CitySim.Tests.Simulation;

/// <summary>
/// Runs the production macro pipeline (TAZ rebuild → OD + mode split → MSA assignment) on standalone
/// subsystems, for tests that build their own grid/graph instead of a <see cref="SimulationWorld"/>.
/// </summary>
internal static class MacroTestPipeline
{
    public const float MorningPeakHour = 8f;

    public static (TazManager Taz, OptimizedODMatrix Od, TrafficEngine Engine) Run(
        CityGrid grid,
        RoadGraph graph,
        ParcelManager parcels = null,
        TransitManager transit = null,
        float hour = MorningPeakHour,
        float ticketFare = SimulationWorld.DefaultTicketPrice,
        bool assignFlows = true)
    {
        var taz = new TazManager();
        var od = new OptimizedODMatrix();
        var engine = new TrafficEngine();

        taz.Rebuild(grid, parcels, graph, transit);
        od.Recalculate(taz, graph, hour, ticketFare);
        if (assignFlows)
        {
            engine.AssignFlowsFromTazMsa(taz, od, graph, SimulationWorld.MsaIterations);
        }
        return (taz, od, engine);
    }
}
