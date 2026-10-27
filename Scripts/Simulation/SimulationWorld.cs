using System;
using System.Threading.Tasks;

namespace CitySim.Simulation;

/// <summary>
/// Headless owner of every simulation subsystem and of the per-frame simulation loop.
/// <see cref="Tick"/> advances the <see cref="SimulationClock"/>, runs the due macro, financial and
/// LUTI growth steps and updates the microscopic agents. The Godot layer (<c>Main</c>) only forwards
/// input, reacts to the world events to refresh rendering, and reads <see cref="Stats"/> for the HUD.
/// </summary>
/// <remarks>
/// <para><b>Background macro model.</b> With <see cref="UseBackgroundMacroModel"/> the expensive part of a
/// macro recalculation (TAZ refresh/rebuild, OD + mode split, MSA assignment) runs on a worker thread
/// into a second set of macro-model objects while the front set (<see cref="Taz"/>, <see cref="OdMatrix"/>)
/// stays readable by UI, growth and economy. The single <see cref="TrafficEngine"/> carries the warm MSA
/// equilibrium and travel-time skim across cycles and publishes volumes only during apply. When the job completes,
/// <see cref="Tick"/> applies it on the main thread in the same order as the synchronous pipeline
/// (publish volumes → accessibility → transit connections → accessibility → transit demand), swaps the
/// sets and raises <see cref="MacroModelRecalculated"/>.</para>
/// <para>While a job runs, the worker reads the road graph, zoning, parcels and transit routes; the main
/// thread must not change their structure. Every edit therefore starts with <see cref="BeginEdit"/>, which
/// waits for and applies the in-flight job (commands and tools do this). Population growth and agent
/// updates only change scalar fields and keep running.</para>
/// </remarks>
public sealed class SimulationWorld : IDisposable
{
    /// <summary>One set of macro-model objects (double-buffered in background mode).</summary>
    private sealed class MacroModelBuffers
    {
        public readonly TazManager Taz = new TazManager();
        public readonly OptimizedODMatrix OdMatrix = new OptimizedODMatrix();
    }

    // Single traffic engine: it carries the warm MSA equilibrium and the travel-time skim from cycle to
    // cycle, whichever buffer is in front. Only one recalculation runs at a time.
    private readonly TrafficEngine _engine = new TrafficEngine { WarmStart = true };

    // Stop-and-line transit model: snapshot, warm-started assignment and the single shared transit skim
    private readonly TransitNetworkSnapshot _transitSnapshot = new TransitNetworkSnapshot();
    private readonly TransitEngine _transitEngine = new TransitEngine { WarmStart = true };


    /// <summary>Transit engine (stop-and-line assignment with crowding and the shared transit skim).</summary>
    public TransitEngine TransitEngine => _transitEngine;

    /// <summary>Shared public transit skim of the last macro recalculation.</summary>
    public TransitSkim TransitSkim => _transitEngine.Skim;

    private MacroModelBuffers _front = new MacroModelBuffers();
    private MacroModelBuffers _back = new MacroModelBuffers();
    private Task _macroJob;
    private bool _macroRecalculationRequested;

    public const int DefaultGridWidth = 20;
    public const int DefaultGridHeight = 20;
    public const float DefaultCellSize = 64f;

    /// <summary>
    /// MSA iterations of a cold traffic assignment (first cycle, after road topology changes); warm cycles
    /// run <see cref="TrafficEngine.WarmStartIterationsPerCycle"/>.
    /// </summary>
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
    /// <summary>Current (front) TAZ set; replaced after each background recalculation.</summary>
    public TazManager Taz => _front.Taz;

    /// <summary>Current (front) OD matrix; replaced after each background recalculation.</summary>
    public OptimizedODMatrix OdMatrix => _front.OdMatrix;

    /// <summary>Traffic engine (warm-started MSA equilibrium and travel-time skim).</summary>
    public TrafficEngine TrafficEngine => _engine;
    public TrafficLightManager TrafficLights { get; }
    public CarTrafficManager Cars { get; }
    public PedestrianManager Pedestrians { get; }
    public LutiGrowthSystem Growth { get; }
    public SimulationClock Clock { get; }
    public TopologyChangeCoordinator Topology { get; private set; }

    /// <summary>
    /// Run macro recalculations on a worker thread (game). False (default) keeps them synchronous, which
    /// headless tests rely on.
    /// </summary>
    public bool UseBackgroundMacroModel { get; set; }

    /// <summary>True while a background macro job is computing or waiting to be applied.</summary>
    public bool IsMacroJobPending => _macroJob != null;

    /// <summary>True when a background macro job has finished computing and will be applied by the next <see cref="Tick"/>.</summary>
    public bool IsMacroJobReady => _macroJob != null && _macroJob.IsCompleted;

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
        world.InitializeDerivedSubsystems();
        return world;
    }

    private void InitializeDerivedSubsystems()
    {
        TrafficLights.BuildIntersections(RoadGraph);
        Cars.Initialize(RoadGraph, Parcels);
        Pedestrians.Initialize(RoadGraph, Grid, Transit);

        // Created after scenario generation so the initial build does not count as a pending change.
        // TAZ invalidation is done by ApplyTopologyChange for both macro-model buffers.
        Topology = new TopologyChangeCoordinator(
            RoadGraph, Grid, TrafficLights, Parcels,
            Transit, Cars, Pedestrians, tazManager: null, Growth);
    }

    /// <summary>
    /// Creates a world containing a generated <see cref="MetropolisCityGenerator"/> city (benchmarks,
    /// stress tests). The grid is sized to the options.
    /// </summary>
    public static SimulationWorld CreateMetropolis(MetropolisOptions options)
    {
        var world = new SimulationWorld(new CityGrid(options.GridSize, options.GridSize, DefaultCellSize));
        world.Parcels.Initialize(world.RoadGraph);
        MetropolisCityGenerator.Generate(world.Grid, world.RoadGraph, world.Transit, options);
        world.InitializeDerivedSubsystems();
        return world;
    }

    /// <summary>Wall-clock time of the last macro recalculation part that runs off the main thread (ms).</summary>
    public double LastMacroComputeMilliseconds { get; private set; }

    /// <summary>Wall-clock time of the last main-thread macro step: full pipeline in sync mode, apply in background mode (ms).</summary>
    public double LastMacroMainThreadMilliseconds { get; private set; }

    /// <summary>Time spent in OD matrix distribution and mode split during the last macro calculation (ms).</summary>
    public double LastOdRecalculateMilliseconds { get; private set; }

    /// <summary>Time spent in car traffic assignment during the last macro calculation (ms).</summary>
    public double LastCarAssignmentMilliseconds { get; private set; }

    /// <summary>Time spent in transit assignment during the last macro calculation (ms).</summary>
    public double LastTransitAssignmentMilliseconds { get; private set; }

    /// <summary>
    /// Recalculates the macro model: TAZ refresh/rebuild → OD + mode split → MSA traffic assignment →
    /// transit connections → accessibility → transit demand, then refreshes <see cref="Stats"/> and raises
    /// <see cref="MacroModelRecalculated"/>. Synchronous by default; with <see cref="UseBackgroundMacroModel"/>
    /// it schedules a background job (coalescing requests made while one is running).
    /// </summary>
    public void RecalculateMacroModel()
    {
        if (!UseBackgroundMacroModel)
        {
            RecalculateSynchronously();
            return;
        }

        if (_macroJob != null)
        {
            _macroRecalculationRequested = true;
            return;
        }

        StartMacroJob();
    }

    /// <summary>
    /// Forces a full TAZ rebuild on the next macro recalculation. Call after edits that change which zones
    /// or parcels exist, their zoning, transit routes or road capacities; population growth alone does not
    /// need it (TAZ totals are re-summed every recalculation).
    /// </summary>
    public void InvalidateMacroStructure()
    {
        _front.Taz.MarkDirty();
        _back.Taz.MarkDirty();
    }

    /// <summary>
    /// Call on the main thread before changing world structure (roads, zoning, parcels, transit routes).
    /// Waits for an in-flight background macro job and applies its results. No-op in synchronous mode.
    /// </summary>
    public void BeginEdit()
    {
        if (_macroJob == null) return;

        _macroJob.Wait();
        ApplyMacroJob(startFollowUp: false);
    }

    /// <summary>
    /// Blocks until no background macro job is pending or requested, applying each result (tests, shutdown).
    /// </summary>
    public void WaitForMacroModel()
    {
        while (_macroJob != null || (_macroRecalculationRequested && UseBackgroundMacroModel))
        {
            if (_macroJob == null)
            {
                _macroRecalculationRequested = false;
                StartMacroJob();
            }
            _macroJob.Wait();
            ApplyMacroJob(startFollowUp: false);
        }
    }

    /// <summary>
    /// Refreshes every topology-dependent subsystem and recalculates the macro model.
    /// </summary>
    public TopologyChangeResult ApplyTopologyChange()
    {
        BeginEdit();
        var result = Topology.ApplyChanges();
        InvalidateMacroStructure();
        RecalculateMacroModel();
        TopologyApplied?.Invoke(result);
        return result;
    }

    private void RecalculateSynchronously()
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        var model = _front;
        ComputeMacroModel(model, Clock.Hour, GetAverageTicketPrice());
        PublishMacroModel(model);

        Clock.MarkMacroRecalculated();
        FinishMacroRecalculation();

        LastMacroComputeMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        LastMacroMainThreadMilliseconds = LastMacroComputeMilliseconds;
    }

    /// <summary>
    /// Heavy part of a macro recalculation. Reads the world and writes only into <paramref name="model"/> and the
    /// engines' pending buffers (never into RoadEdge, TransitRoute, TransitStop, zones or parcels), so it can run
    /// on the worker thread. Must be followed by <see cref="PublishMacroModel"/> on the main thread.
    /// </summary>
    // Transit coverage is computed on the worker (O(parcels)) so the main-thread apply stays cheap
    private float? _pendingTransitCoverage;

    private void ComputeMacroModel(MacroModelBuffers model, float hour, float ticketPrice)
    {
        bool rebuilt = model.Taz.RefreshOrRebuild(Grid, Parcels, RoadGraph, Transit);

        // Stop-and-line snapshot on the currently published road volumes (worker never touches TransitRoute)
        _transitSnapshot.Rebuild(Transit, RoadGraph, carVolumes: null, updateRoutePrefixSums: false);
        _transitSnapshot.UpdateTravelTimes(RoadGraph);
        if (rebuilt || !_transitEngine.Skim.Covers(model.Taz, _transitSnapshot))
        {
            // Cold start or structural change: otherwise the skim of the previous assignment is reused (one cycle lag)
            _transitEngine.ComputeSkim(model.Taz, _transitSnapshot);
        }

        long tStart = System.Diagnostics.Stopwatch.GetTimestamp();
        model.OdMatrix.Recalculate(model.Taz, RoadGraph, hour, ticketPrice, _engine.Skim, _transitEngine.Skim, propagateAccessibility: false);
        LastOdRecalculateMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(tStart).TotalMilliseconds;

        tStart = System.Diagnostics.Stopwatch.GetTimestamp();
        _engine.AssignFlowsFromTazMsa(model.Taz, model.OdMatrix, RoadGraph, MsaIterations, deferPublish: true);
        LastCarAssignmentMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(tStart).TotalMilliseconds;

        // Transit assignment and accessibility on the freshly assigned (still unpublished) car volumes
        var carVolumes = _engine.PendingCarVolumes;
        _transitSnapshot.UpdateTravelTimes(RoadGraph, carVolumes);
        tStart = System.Diagnostics.Stopwatch.GetTimestamp();
        _transitEngine.AssignFlows(model.Taz, model.OdMatrix, _transitSnapshot, RoadGraph, Transit, deferPublish: true);
        LastTransitAssignmentMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(tStart).TotalMilliseconds;

        model.OdMatrix.ComputeTazAccessibilityScores(model.Taz, RoadGraph, -1f, _transitEngine.Skim, carVolumes);
        _pendingTransitCoverage = Transit.GetTransitCoverage(Grid, RoadGraph, Parcels);
    }

    /// <summary>Main-thread publication of a computed model: O(edges + routes + stops + parcels).</summary>
    private void PublishMacroModel(MacroModelBuffers model)
    {
        _engine.PublishPendingVolumes(RoadGraph);
        _transitEngine.PublishPendingDemand(Transit, RoadGraph);
        model.OdMatrix.PropagateAccessibilityToLandUse(model.Taz, RoadGraph);
    }

    private void StartMacroJob()
    {
        float hour = Clock.Hour;
        float ticketPrice = GetAverageTicketPrice();
        Clock.MarkMacroRecalculated();

        var model = _back;
        _macroJob = Task.Run(() =>
        {
            long jobStart = System.Diagnostics.Stopwatch.GetTimestamp();
            ComputeMacroModel(model, hour, ticketPrice);
            LastMacroComputeMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(jobStart).TotalMilliseconds;
        });
    }

    /// <summary>Applies a finished background job on the main thread and swaps the macro-model buffers.</summary>
    private void ApplyMacroJob(bool startFollowUp)
    {
        var job = _macroJob;
        _macroJob = null;
        job.GetAwaiter().GetResult(); // surface worker exceptions on the main thread
        long applyStart = System.Diagnostics.Stopwatch.GetTimestamp();

        PublishMacroModel(_back);

        (_front, _back) = (_back, _front);
        FinishMacroRecalculation();
        LastMacroMainThreadMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(applyStart).TotalMilliseconds;

        if (startFollowUp && _macroRecalculationRequested)
        {
            _macroRecalculationRequested = false;
            StartMacroJob();
        }
    }

    private void FinishMacroRecalculation()
    {
        Cars.RefreshBusyEdges(RoadGraph);
        Stats = CityStatsSnapshot.Capture(Grid, Parcels, OdMatrix, TrafficEngine, RoadGraph, Transit, _pendingTransitCoverage);
        _pendingTransitCoverage = null;
        MacroModelRecalculated?.Invoke();
    }

    /// <summary>
    /// Advances the simulation by one frame of real time.
    /// </summary>
    public void Tick(float realDeltaSeconds)
    {
        if (_macroJob != null && _macroJob.IsCompleted)
        {
            ApplyMacroJob(startFollowUp: true);
        }

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

        // A request deferred by BeginEdit (edit that did not recalculate) starts here
        if (UseBackgroundMacroModel && _macroRecalculationRequested && _macroJob == null)
        {
            _macroRecalculationRequested = false;
            StartMacroJob();
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
            if (growth.DensityChanged)
            {
                InvalidateMacroStructure();
            }
            if (growth.LandUseChanged || growth.DensityChanged)
            {
                Stats = Stats.WithLandUse(Grid, Parcels);
                LandUseChanged?.Invoke(growth);
            }
        }
    }

    public void Dispose()
    {
        try
        {
            _macroJob?.Wait();
        }
        catch (AggregateException)
        {
            // Shutting down: a failed background job has nothing left to apply
        }
        _macroJob = null;
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
