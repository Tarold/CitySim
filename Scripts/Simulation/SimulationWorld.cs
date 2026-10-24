using System;

namespace CitySim.Simulation;

/// <summary>
/// Headless owner of every simulation subsystem and of the per-frame simulation loop.
/// <see cref="Tick"/> advances the <see cref="SimulationClock"/>, runs the due macro, financial and
/// LUTI growth steps and updates the microscopic agents. The Godot layer (<c>Main</c>) only forwards
/// input, reacts to the world events to refresh rendering, and reads <see cref="Stats"/> for the HUD.
/// </summary>
public sealed class SimulationWorld : IDisposable
{
    public const int DefaultGridWidth = 20;
    public const int DefaultGridHeight = 20;
    public const float DefaultCellSize = 64f;

    /// <summary>MSA iterations of the traffic assignment per macro recalculation.</summary>
    public const int MsaIterations = 4;

    /// <summary>Ticket price assumed by the mode split when no transit route exists.</summary>
    public const float DefaultTicketPrice = 12f;

    private SimulationWorld(CityGrid grid)
    {
        Grid = grid;
        RoadGraph = new RoadGraph();
        Parcels = new ParcelManager();
        Transit = new TransitManager();
        Economy = new EconomyManager();
        Taz = new TazManager();
        OdMatrix = new OptimizedODMatrix();
        TrafficEngine = new TrafficEngine();
        TrafficLights = new TrafficLightManager();
        Cars = new CarTrafficManager();
        Pedestrians = new PedestrianManager();
        Growth = new LutiGrowthSystem();
        Clock = new SimulationClock();
    }

    public CityGrid Grid { get; }
    public RoadGraph RoadGraph { get; }
    public ParcelManager Parcels { get; }
    public TransitManager Transit { get; }
    public EconomyManager Economy { get; }
    public TazManager Taz { get; }
    public OptimizedODMatrix OdMatrix { get; }
    public TrafficEngine TrafficEngine { get; }
    public TrafficLightManager TrafficLights { get; }
    public CarTrafficManager Cars { get; }
    public PedestrianManager Pedestrians { get; }
    public LutiGrowthSystem Growth { get; }
    public SimulationClock Clock { get; }
    public TopologyChangeCoordinator Topology { get; private set; }

    /// <summary>City-wide statistics from the last macro recalculation (population/jobs also after growth).</summary>
    public CityStatsSnapshot Stats { get; private set; }

    /// <summary>Passengers currently on board all buses (O(vehicles), cheap enough per frame).</summary>
    public float TransitRidership
    {
        get
        {
            float total = 0f;
            var vehicles = Transit.Vehicles;
            for (int i = 0; i < vehicles.Count; i++) total += vehicles[i].Passengers;
            return total;
        }
    }

    /// <summary>Raised after every macro model recalculation (periodic tick or edit).</summary>
    public event Action MacroModelRecalculated;

    /// <summary>Raised after the topology invalidation chain and the following macro recalculation.</summary>
    public event Action<TopologyChangeResult> TopologyApplied;

    /// <summary>Raised after a LUTI growth step that changed population or density.</summary>
    public event Action<LutiGrowthResult> LandUseChanged;

    /// <summary>
    /// Creates a world, loads <paramref name="scenario"/> and initializes all derived subsystems.
    /// Call <see cref="RecalculateMacroModel"/> once listeners are attached to compute the first flows.
    /// </summary>
    public static SimulationWorld Create(
        ScenarioType scenario,
        int gridWidth = DefaultGridWidth,
        int gridHeight = DefaultGridHeight,
        float cellSize = DefaultCellSize)
    {
        var world = new SimulationWorld(new CityGrid(gridWidth, gridHeight, cellSize));
        world.Parcels.Initialize(world.RoadGraph);

        ScenarioLoader.Load(scenario, world.Grid, world.RoadGraph, world.Parcels, world.Transit, world.Economy);

        world.TrafficLights.BuildIntersections(world.RoadGraph);
        world.Cars.Initialize(world.RoadGraph, world.Parcels);
        world.Pedestrians.Initialize(world.RoadGraph, world.Grid, world.Transit);

        // Created after scenario generation so the initial build does not count as a pending change
        world.Topology = new TopologyChangeCoordinator(
            world.RoadGraph, world.Grid, world.TrafficLights, world.Parcels,
            world.Transit, world.Cars, world.Pedestrians, world.Taz, world.Growth);

        return world;
    }

    /// <summary>
    /// Runs the macro pipeline: TAZ rebuild → OD + mode split → MSA traffic assignment → transit
    /// connections → accessibility → transit demand, then refreshes <see cref="Stats"/>.
    /// </summary>
    public void RecalculateMacroModel()
    {
        Taz.Rebuild(Grid, Parcels, RoadGraph, Transit);
        OdMatrix.Recalculate(Taz, RoadGraph, Clock.Hour, GetAverageTicketPrice());
        TrafficEngine.AssignFlowsFromTazMsa(Taz, OdMatrix, RoadGraph, MsaIterations);
        Taz.UpdateTransitConnectionMatrix(Transit, RoadGraph);
        OdMatrix.UpdateAccessibilityScores(Taz, RoadGraph);
        Transit.AssignPassengerDemandFromTaz(Taz, OdMatrix, RoadGraph);

        Cars.RefreshBusyEdges(RoadGraph);
        Clock.MarkMacroRecalculated();
        Stats = CityStatsSnapshot.Capture(Grid, Parcels, OdMatrix, TrafficEngine, RoadGraph, Transit);

        MacroModelRecalculated?.Invoke();
    }

    /// <summary>
    /// Refreshes every topology-dependent subsystem and recalculates the macro model.
    /// </summary>
    public TopologyChangeResult ApplyTopologyChange()
    {
        var result = Topology.ApplyChanges();
        RecalculateMacroModel();
        TopologyApplied?.Invoke(result);
        return result;
    }

    /// <summary>
    /// Advances the simulation by one frame of real time.
    /// </summary>
    public void Tick(float realDeltaSeconds)
    {
        // Safety net: graph edits not followed by an explicit ApplyTopologyChange() (e.g. an edge split
        // while drafting a road) are applied before agents move on stale edges.
        if (Topology.HasPendingChanges)
        {
            ApplyTopologyChange();
        }

        var ticks = Clock.Advance(realDeltaSeconds);
        float gameSpeed = Clock.Speed;

        if ((ticks & SimulationTicks.MacroRecalculation) != 0)
        {
            RecalculateMacroModel();
        }

        if ((ticks & SimulationTicks.Financial) != 0)
        {
            Economy.ProcessFinancialTick(Grid, RoadGraph, Transit, Parcels, Taz);
        }

        TrafficLights.Update(realDeltaSeconds, gameSpeed);
        Cars.Update(realDeltaSeconds, gameSpeed, RoadGraph, TrafficLights, Parcels);
        Transit.Update(realDeltaSeconds, gameSpeed, RoadGraph, TrafficLights, Clock.Hour);
        Pedestrians.Update(realDeltaSeconds, gameSpeed, RoadGraph, Grid, Transit);

        if ((ticks & SimulationTicks.Growth) != 0)
        {
            var growth = Growth.Process(Grid, RoadGraph, Taz, Parcels);
            if (growth.LandUseChanged || growth.DensityChanged)
            {
                Stats = Stats.WithLandUse(Grid, Parcels);
                LandUseChanged?.Invoke(growth);
            }
        }
    }

    public void Dispose()
    {
        Topology?.Dispose();
    }

    private float GetAverageTicketPrice()
    {
        var routes = Transit.Routes;
        if (routes.Count == 0) return DefaultTicketPrice;

        float total = 0f;
        for (int i = 0; i < routes.Count; i++) total += routes[i].TicketPrice;
        return total / routes.Count;
    }
}
