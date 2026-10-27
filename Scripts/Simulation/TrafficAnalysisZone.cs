using System.Collections.Generic;
using Godot;

namespace CitySim.Simulation;

/// <summary>
/// Centroid connector linking a TAZ to an adjacent road network intersection.
/// Allows distributing trip origins and destinations across nearby road nodes.
/// </summary>
public readonly struct TazConnector
{
    public readonly int RoadNodeId;
    public readonly float Weight;
    public readonly float AccessTime;

    public TazConnector(int roadNodeId, float weight, float accessTime)
    {
        RoadNodeId = roadNodeId;
        Weight = weight;
        AccessTime = accessTime;
    }
}

/// <summary>
/// Transit stop within walking catchment of a TAZ centroid, ordered by walking distance.
/// </summary>
public readonly struct TazStopAccess
{
    public readonly int StopNodeId;
    public readonly float DistanceMeters;

    public TazStopAccess(int stopNodeId, float distanceMeters)
    {
        StopNodeId = stopNodeId;
        DistanceMeters = distanceMeters;
    }
}

/// <summary>
/// Traffic Analysis Zone (TAZ) macro-district aggregating discrete grid zones and continuous ribbon parcels.
/// Compresses the origin-destination trip matrix from O(N^2) to stable O(K^2).
/// </summary>
public sealed class TrafficAnalysisZone
{
    public int Id { get; set; }
    public Vector2I GridCoord { get; init; }
    public Vector2 Centroid { get; set; }

    // Demographic and development metrics
    public int TotalPopulation { get; set; }
    public int CommercialJobs { get; set; }
    public int IndustrialJobs { get; set; }
    public int TotalJobs => CommercialJobs + IndustrialJobs;
    public int TotalCapacity { get; set; }

    // Road network topological binding
    public int PrimaryRoadNodeId { get; set; } = -1;
    public List<TazConnector> Connectors { get; } = new(3);

    /// <summary>
    /// Road edges located within this TAZ cell boundary.
    /// Used for dispersing internal trips (i == j) and transit boarding/alighting pedestrians.
    /// </summary>
    public List<int> InternalEdgeIds { get; } = new();

    // Public transit catchment metrics (stops and routes within 350m)
    public List<TazStopAccess> TransitStops { get; } = new(4);
    public HashSet<int> AccessibleTransitRoutes { get; } = new();
    public float AvgTransitAccessDistance { get; set; } = float.MaxValue;
    public float BestTransitWaitTime { get; set; } = 15.0f;

    // LUTI (Land-Use & Transport Interaction) Hansen Accessibility & Congestion metrics
    /// <summary>
    /// Raw gravity denominator / Hansen accessibility sum (_rowScoreSums[i]) before normalization.
    /// </summary>
    public float RawAccessibility { get; set; } = 0.0f;

    /// <summary>
    /// Normalized Hansen transport accessibility index in [0.0, 2.0].
    /// Reflects how many jobs and services are reachable from this TAZ considering road congestion and transit.
    /// </summary>
    public float AccessibilityScore { get; set; } = 1.0f;

    /// <summary>
    /// Alias for <see cref="AccessibilityScore"/> matching the Hansen AccessibilityIndex terminology.
    /// </summary>
    public float AccessibilityIndex
    {
        get => AccessibilityScore;
        set => AccessibilityScore = value;
    }

    /// <summary>
    /// Average road volume-to-capacity ratio (R_congestion) across edges serving this TAZ.
    /// </summary>
    public float AverageCongestion { get; set; } = 0.0f;

    /// <summary>
    /// Peak road volume-to-capacity ratio (R_congestion) among edges serving this TAZ.
    /// </summary>
    public float MaxCongestion { get; set; } = 0.0f;

    /// <summary>
    /// Indicates whether this TAZ is gridlocked in severe traffic congestion (R_congestion &gt; 1.5),
    /// causing growth stagnation and reduced tax attractiveness.
    /// </summary>
    public bool IsCongestionStagnant => AverageCongestion > 1.5f || MaxCongestion > 1.5f;

    // Direct reverse references for UI inspectors and analytics deaggregation
    public List<Zone> Zones { get; } = new();
    public List<RoadsideParcel> Parcels { get; } = new();

    public void Reset()
    {
        TotalPopulation = 0;
        CommercialJobs = 0;
        IndustrialJobs = 0;
        TotalCapacity = 0;
        PrimaryRoadNodeId = -1;
        AvgTransitAccessDistance = float.MaxValue;
        BestTransitWaitTime = 15.0f;
        RawAccessibility = 0.0f;
        AccessibilityScore = 0.0f;
        AverageCongestion = 0.0f;
        MaxCongestion = 0.0f;
        Connectors.Clear();
        InternalEdgeIds.Clear();
        TransitStops.Clear();
        AccessibleTransitRoutes.Clear();
        Zones.Clear();
        Parcels.Clear();
    }
}
