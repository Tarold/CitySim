namespace CitySim.Simulation;

/// <summary>
/// Defines available game scenario modes that can be loaded into the simulator.
/// </summary>
public enum ScenarioType
{
    /// <summary>
    /// Open sandbox canvas with default grid.
    /// </summary>
    Sandbox,

    /// <summary>
    /// Curving organic tutorial city with starter population and transit.
    /// </summary>
    Tutorial,

    /// <summary>
    /// Economic benchmark scenario with distinct demographic districts and transit.
    /// </summary>
    EconomyTest,

    /// <summary>
    /// Routing benchmark scenario with parallel travel corridors and bottleneck choke points.
    /// </summary>
    RoutingTest,

    /// <summary>
    /// Clean reference canvas for testing map editing tools and topological integrity.
    /// </summary>
    MapEditingTest
}
