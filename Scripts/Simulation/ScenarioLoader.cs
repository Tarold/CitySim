namespace CitySim.Simulation;

/// <summary>
/// Populates freshly created simulation subsystems with the content of a <see cref="ScenarioType"/>.
/// </summary>
public static class ScenarioLoader
{
    /// <summary>
    /// Generates the scenario's grid zoning, road network, roadside parcels, transit routes and budget.
    /// <paramref name="parcelManager"/> must already be initialized with <paramref name="roadGraph"/>.
    /// </summary>
    public static void Load(
        ScenarioType scenario,
        CityGrid grid,
        RoadGraph roadGraph,
        ParcelManager parcelManager,
        TransitManager transitManager,
        EconomyManager economyManager)
    {
        switch (scenario)
        {
            case ScenarioType.Tutorial:
                TutorialCityGenerator.Generate(grid, roadGraph, parcelManager, transitManager, economyManager);
                break;
            case ScenarioType.EconomyTest:
                EconomyTestCityGenerator.Generate(grid, roadGraph, parcelManager, transitManager, economyManager);
                break;
            case ScenarioType.RoutingTest:
                RoutingTestCityGenerator.Generate(grid, roadGraph, parcelManager, transitManager, economyManager);
                break;
            case ScenarioType.MapEditingTest:
                MapEditingTestCityGenerator.Generate(grid, roadGraph, parcelManager, transitManager, economyManager);
                break;
            case ScenarioType.Sandbox:
            default:
                roadGraph.BuildFromGrid(grid);
                transitManager.CreateDefaultRoutes(grid, roadGraph);
                parcelManager.RefreshParcels(roadGraph);
                break;
        }
    }
}
