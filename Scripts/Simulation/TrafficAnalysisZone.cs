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

    // Public transit catchment metrics (routes with stops within 350m)
    public HashSet<int> AccessibleTransitRoutes { get; } = new();
    public float AvgTransitAccessDistance { get; set; } = float.MaxValue;
    public float BestTransitWaitTime { get; set; } = 15.0f;

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
        Connectors.Clear();
        InternalEdgeIds.Clear();
        AccessibleTransitRoutes.Clear();
        Zones.Clear();
        Parcels.Clear();
    }
}
