using System;

namespace CitySim.Simulation;

/// <summary>
/// Outcome of <see cref="TopologyChangeCoordinator.ApplyChanges"/>.
/// </summary>
public readonly struct TopologyChangeResult
{
    public TopologyChangeResult(bool applied, TransitRevalidationResult transit)
    {
        Applied = applied;
        Transit = transit ?? new TransitRevalidationResult();
    }

    /// <summary>True when the invalidation chain actually ran.</summary>
    public bool Applied { get; }

    /// <summary>Transit routes that were re-routed or removed because of the change.</summary>
    public TransitRevalidationResult Transit { get; }
}

/// <summary>
/// Single owner of the road-topology invalidation chain. Every subsystem that caches data derived from
/// <see cref="RoadGraph"/> topology is refreshed here, in one fixed order, instead of at each call site.
/// The coordinator listens to <see cref="RoadGraph"/> mutation events and tracks pending changes, so edits
/// made without an explicit <see cref="ApplyChanges"/> call (e.g. an edge split while drafting a road)
/// are still picked up by <see cref="ApplyPendingChanges"/>.
/// </summary>
/// <remarks>
/// Order: graph path caches → traffic lights → roadside parcels → transit routes → car agents →
/// pedestrian agents (they depend on transit stops) → LUTI entrance reachability → TAZ dirty flag. Demand recalculation and
/// rendering refresh stay with the caller.
/// </remarks>
public sealed class TopologyChangeCoordinator : IDisposable
{
    private readonly RoadGraph _roadGraph;
    private readonly CityGrid _grid;
    private readonly TrafficLightManager _trafficLights;
    private readonly ParcelManager _parcelManager;
    private readonly TransitManager _transitManager;
    private readonly CarTrafficManager _carTrafficManager;
    private readonly PedestrianManager _pedestrianManager;
    private readonly TazManager _tazManager;
    private readonly LutiGrowthSystem _growthSystem;

    public TopologyChangeCoordinator(
        RoadGraph roadGraph,
        CityGrid grid,
        TrafficLightManager trafficLights,
        ParcelManager parcelManager,
        TransitManager transitManager,
        CarTrafficManager carTrafficManager,
        PedestrianManager pedestrianManager,
        TazManager tazManager,
        LutiGrowthSystem growthSystem = null)
    {
        _roadGraph = roadGraph ?? throw new ArgumentNullException(nameof(roadGraph));
        _grid = grid;
        _trafficLights = trafficLights;
        _parcelManager = parcelManager;
        _transitManager = transitManager;
        _carTrafficManager = carTrafficManager;
        _pedestrianManager = pedestrianManager;
        _tazManager = tazManager;
        _growthSystem = growthSystem;

        _roadGraph.NodeAdded += OnNodeAdded;
        _roadGraph.NodeRemoved += OnNodeOrEdgeRemoved;
        _roadGraph.EdgeAdded += OnEdgeAdded;
        _roadGraph.EdgeRemoved += OnNodeOrEdgeRemoved;
        _roadGraph.EdgeSplit += OnEdgeSplit;
    }

    /// <summary>True when the road graph was mutated since the last <see cref="ApplyChanges"/>.</summary>
    public bool HasPendingChanges { get; private set; }

    /// <summary>Number of times the invalidation chain has run (diagnostics and tests).</summary>
    public int AppliedChangeCount { get; private set; }

    /// <summary>Flags a change that the graph events do not cover (e.g. direct field edits in tests).</summary>
    public void MarkChanged() => HasPendingChanges = true;

    /// <summary>Runs the invalidation chain only if the graph changed since the last application.</summary>
    public TopologyChangeResult ApplyPendingChanges()
    {
        return HasPendingChanges ? ApplyChanges() : new TopologyChangeResult(false, null);
    }

    /// <summary>
    /// Unconditionally refreshes every topology-dependent subsystem in the fixed order.
    /// </summary>
    public TopologyChangeResult ApplyChanges()
    {
        _roadGraph.WalkingPathCache.Clear();
        _roadGraph.EnsureSpatialIndexSynced();

        _trafficLights?.BuildIntersections(_roadGraph);
        _parcelManager?.RefreshParcels(_roadGraph);

        var transit = _transitManager?.RevalidateRoutes(_roadGraph);

        _carTrafficManager?.HandleInvalidatedEdges(_roadGraph);
        _pedestrianManager?.HandleInvalidatedEdges(_roadGraph, _grid, _transitManager);
        _growthSystem?.InvalidateReachability();
        _tazManager?.MarkDirty();

        HasPendingChanges = false;
        AppliedChangeCount++;
        return new TopologyChangeResult(true, transit);
    }

    public void Dispose()
    {
        _roadGraph.NodeAdded -= OnNodeAdded;
        _roadGraph.NodeRemoved -= OnNodeOrEdgeRemoved;
        _roadGraph.EdgeAdded -= OnEdgeAdded;
        _roadGraph.EdgeRemoved -= OnNodeOrEdgeRemoved;
        _roadGraph.EdgeSplit -= OnEdgeSplit;
    }

    private void OnNodeAdded(RoadNode node) => HasPendingChanges = true;
    private void OnEdgeAdded(RoadEdge edge) => HasPendingChanges = true;
    private void OnNodeOrEdgeRemoved(int id) => HasPendingChanges = true;
    private void OnEdgeSplit(int edgeId, RoadNode junction) => HasPendingChanges = true;
}
