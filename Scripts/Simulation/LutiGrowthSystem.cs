using Godot;
using System.Collections.Generic;

namespace CitySim.Simulation;

/// <summary>
/// Outcome of one <see cref="LutiGrowthSystem.Process"/> step.
/// </summary>
public readonly struct LutiGrowthResult
{
    public LutiGrowthResult(bool landUseChanged, bool densityChanged)
    {
        LandUseChanged = landUseChanged;
        DensityChanged = densityChanged;
    }

    /// <summary>Population of a grid zone or parcel changed, or density was upgraded (HUD/infographics stale).</summary>
    public bool LandUseChanged { get; }

    /// <summary>A grid zone or parcel moved to a higher density level (TAZ is marked dirty, map needs redraw).</summary>
    public bool DensityChanged { get; }
}

/// <summary>
/// LUTI land-use evolution step: grows residential grid zones that are reachable from a city entrance,
/// depending on Hansen accessibility of their TAZ and local congestion, upgrades their density, and runs
/// <see cref="ParcelManager.ProcessLutiEvolution"/> for roadside parcels.
/// </summary>
/// <remarks>
/// The set of road nodes reachable from entrances depends only on connectivity, so it is cached and
/// recomputed only after <see cref="InvalidateReachability"/> (called by
/// <see cref="TopologyChangeCoordinator"/>) or when the set of entrance zones changes.
/// </remarks>
public sealed class LutiGrowthSystem
{
    /// <summary>Base residents added per growth step at neutral accessibility.</summary>
    public const float BaseResidentialGrowth = 50f;

    /// <summary>Growth factor = <see cref="GrowthAccessibilityOffset"/> + <see cref="GrowthAccessibilityWeight"/> × access.</summary>
    public const float GrowthAccessibilityOffset = 0.4f;
    public const float GrowthAccessibilityWeight = 1.2f;

    /// <summary>Minimum residents added per step to a growing zone.</summary>
    public const int MinResidentialGrowthStep = 10;

    /// <summary>Accessibility scores are clamped to this range before use.</summary>
    public const float MaxAccessibilityScore = 2.0f;

    /// <summary>Zones at or below this accessibility do not grow.</summary>
    public const float MinGrowthAccessibility = 0.01f;

    /// <summary>Congestion ratio above which growth is damped (it stops entirely above the stagnation threshold).</summary>
    public const float CongestionDampingStart = 1.0f;

    /// <summary>Growth reduction per unit of congestion ratio above <see cref="CongestionDampingStart"/>.</summary>
    public const float CongestionDampingSlope = 0.5f;

    /// <summary>Occupancy share of the residential cap required before a density upgrade.</summary>
    public const float DensityUpgradeOccupancy = 0.85f;

    private readonly ShortestPathTreeBuffer _treeBuffer = new ShortestPathTreeBuffer();
    private readonly HashSet<int> _reachableNodes = new HashSet<int>();
    private readonly List<int> _entranceZoneIds = new List<int>();
    private readonly List<int> _cachedEntranceZoneIds = new List<int>();
    private bool _reachabilityValid;

    /// <summary>Road nodes reachable from any entrance at the last <see cref="Process"/> call.</summary>
    public IReadOnlySet<int> ReachableNodes => _reachableNodes;

    /// <summary>Number of times reachability was recomputed (diagnostics and tests).</summary>
    public int ReachabilityRecomputeCount { get; private set; }

    /// <summary>Forces reachability to be recomputed on the next step (road topology changed).</summary>
    public void InvalidateReachability() => _reachabilityValid = false;

    /// <summary>
    /// Runs one growth step. Does nothing when the grid has no entrance zones.
    /// </summary>
    public LutiGrowthResult Process(CityGrid grid, RoadGraph roadGraph, TazManager tazManager, ParcelManager parcelManager)
    {
        if (grid == null || roadGraph == null) return default;

        CollectEntranceZones(grid);
        if (_entranceZoneIds.Count == 0) return default;

        RefreshReachability(roadGraph);

        bool landUseChanged = false;
        bool densityChanged = false;

        for (int i = 0; i < grid.ZoneCount; i++)
        {
            var zone = grid.GetZone(i);
            if (zone == null || zone.Type == ZoneType.Empty || zone.Type == ZoneType.Entrance) continue;
            // Grid zone ids double as their road node ids
            if (!_reachableNodes.Contains(i)) continue;

            UpdateZoneAccessAndCongestion(zone, roadGraph, tazManager);

            // Stagnation check: zones gridlocked in traffic jams (R_congestion > 1.5) stagnate
            if (zone.LocalCongestion > ParcelManager.CongestionStagnationThreshold)
                continue;

            float accessScore = Mathf.Clamp(zone.AccessibilityScore, 0.0f, MaxAccessibilityScore);
            if (accessScore <= MinGrowthAccessibility) continue;

            if (zone.Type == ZoneType.Residential)
            {
                var (grew, upgraded) = GrowResidentialZone(zone, accessScore);
                landUseChanged |= grew || upgraded;
                densityChanged |= upgraded;
            }
        }

        if (parcelManager != null)
        {
            var (parcelPopChanged, parcelDensityUpgraded) = parcelManager.ProcessLutiEvolution(
                roadGraph,
                tazManager,
                _reachableNodes);

            landUseChanged |= parcelPopChanged;
            densityChanged |= parcelDensityUpgraded;
        }

        if (densityChanged)
        {
            tazManager?.MarkDirty();
        }

        return new LutiGrowthResult(landUseChanged, densityChanged);
    }

    private void CollectEntranceZones(CityGrid grid)
    {
        _entranceZoneIds.Clear();
        for (int i = 0; i < grid.ZoneCount; i++)
        {
            var zone = grid.GetZone(i);
            if (zone != null && zone.Type == ZoneType.Entrance)
                _entranceZoneIds.Add(i);
        }
    }

    private void RefreshReachability(RoadGraph roadGraph)
    {
        if (_reachabilityValid && SameEntrances()) return;

        _reachableNodes.Clear();
        for (int e = 0; e < _entranceZoneIds.Count; e++)
        {
            roadGraph.ComputeDijkstraIntoBuffer(_entranceZoneIds[e], _treeBuffer);
            for (int k = 0; k < _treeBuffer.VisitedCount; k++)
            {
                _reachableNodes.Add(_treeBuffer.VisitedNodesOrder[k]);
            }
        }

        _cachedEntranceZoneIds.Clear();
        _cachedEntranceZoneIds.AddRange(_entranceZoneIds);
        _reachabilityValid = true;
        ReachabilityRecomputeCount++;
    }

    private bool SameEntrances()
    {
        if (_cachedEntranceZoneIds.Count != _entranceZoneIds.Count) return false;
        for (int i = 0; i < _entranceZoneIds.Count; i++)
        {
            if (_cachedEntranceZoneIds[i] != _entranceZoneIds[i]) return false;
        }
        return true;
    }

    private static void UpdateZoneAccessAndCongestion(Zone zone, RoadGraph roadGraph, TazManager tazManager)
    {
        float nodeCongestion = OptimizedODMatrix.GetNodeCongestion(zone.Id, roadGraph);
        var taz = tazManager?.GetTaz(tazManager.GetTazIdForZoneTile(zone.GridPos));

        if (taz != null)
        {
            zone.AccessibilityScore = taz.AccessibilityScore;
            zone.LocalCongestion = Mathf.Max(nodeCongestion, taz.AverageCongestion);
        }
        else
        {
            zone.LocalCongestion = nodeCongestion;
        }
    }

    private static (bool Grew, bool Upgraded) GrowResidentialZone(Zone zone, float accessScore)
    {
        float congestionDamping = zone.LocalCongestion > CongestionDampingStart
            ? (1.0f - CongestionDampingSlope * (zone.LocalCongestion - CongestionDampingStart))
            : 1.0f;

        int growthStep = Mathf.Max(
            MinResidentialGrowthStep,
            Mathf.RoundToInt(BaseResidentialGrowth * (GrowthAccessibilityOffset + GrowthAccessibilityWeight * accessScore) * congestionDamping));

        bool grew = false;
        if (zone.Population < zone.ResidentialCap)
        {
            zone.Population = Mathf.Min(zone.ResidentialCap, zone.Population + growthStep);
            grew = true;
        }

        bool upgraded = false;
        if (zone.ResidentialCap > 0 && zone.Population >= Mathf.RoundToInt(zone.ResidentialCap * DensityUpgradeOccupancy))
        {
            if ((zone.Density == DensityLevel.Low && accessScore >= ParcelManager.MediumDensityAccessibilityThreshold) ||
                (zone.Density == DensityLevel.Medium && accessScore >= ParcelManager.HighDensityAccessibilityThreshold))
            {
                upgraded = zone.UpgradeDensity();
            }
        }

        return (grew, upgraded);
    }
}
