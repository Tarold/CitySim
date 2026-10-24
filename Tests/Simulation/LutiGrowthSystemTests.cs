using NUnit.Framework;
using CitySim.Simulation;

namespace CitySim.Tests.Simulation;

[TestFixture]
public class LutiGrowthSystemTests
{
    private const int TestResidentialCap = 1000;

    private CityGrid _grid;
    private RoadGraph _graph;
    private LutiGrowthSystem _growth;

    // Entrance at (0,4) connected by a street to residential cells (1..3,4).
    // Residential cell (8,8) has a road node but no road: unreachable from the entrance.
    [SetUp]
    public void Setup()
    {
        _grid = new CityGrid(10, 10, 64f);
        _graph = new RoadGraph();
        _growth = new LutiGrowthSystem();

        _grid.ZoneCell(0, 4, ZoneType.Entrance);
        for (int x = 1; x <= 3; x++)
        {
            _grid.ZoneCell(x, 4, ZoneType.Residential, population: TestResidentialCap);
        }
        _grid.ZoneCell(8, 8, ZoneType.Residential, population: TestResidentialCap);

        foreach (int id in new[] { Id(0, 4), Id(1, 4), Id(2, 4), Id(3, 4), Id(8, 8) })
        {
            _graph.EnsureNode(id, _grid.GetWorldCenter(id));
        }
        for (int x = 0; x < 3; x++)
        {
            _graph.AddRoadSegment(Id(x, 4), Id(x + 1, 4));
        }

        foreach (int id in new[] { Id(1, 4), Id(2, 4), Id(3, 4), Id(8, 8) })
        {
            _grid.Zones[id].AccessibilityScore = 1.0f;
        }
    }

    private int Id(int x, int y) => _grid.GetZoneId(x, y);

    private Zone ZoneAt(int x, int y) => _grid.Zones[Id(x, y)];

    private void SetAllEdgeCongestion(float ratio)
    {
        foreach (var edge in _graph.Edges)
        {
            edge.CurrentVolume = edge.Capacity * ratio;
        }
    }

    private static int ExpectedStep(float access, float damping = 1f) =>
        System.Math.Max(
            LutiGrowthSystem.MinResidentialGrowthStep,
            (int)System.MathF.Round(LutiGrowthSystem.BaseResidentialGrowth
                * (LutiGrowthSystem.GrowthAccessibilityOffset + LutiGrowthSystem.GrowthAccessibilityWeight * access) * damping));

    [Test]
    public void Process_NoEntranceZones_DoesNothing()
    {
        // CityGrid also seeds default entrances at the middle of the left and right edges
        _grid.DezoneCell(Id(0, 4));
        _grid.DezoneCell(Id(0, 5));
        _grid.DezoneCell(Id(9, 5));

        var result = _growth.Process(_grid, _graph, null, null);

        Assert.That(result.LandUseChanged, Is.False);
        Assert.That(ZoneAt(1, 4).Population, Is.EqualTo(0));
        Assert.That(_growth.ReachabilityRecomputeCount, Is.EqualTo(0));
    }

    [Test]
    public void Process_NullGridOrGraph_ReturnsNoChange()
    {
        Assert.That(_growth.Process(null, _graph, null, null).LandUseChanged, Is.False);
        Assert.That(_growth.Process(_grid, null, null, null).LandUseChanged, Is.False);
    }

    [Test]
    public void Process_ReachableResidentialZone_GrowsByAccessibilityScaledStep()
    {
        var result = _growth.Process(_grid, _graph, null, null);

        Assert.That(result.LandUseChanged, Is.True);
        Assert.That(result.DensityChanged, Is.False);
        Assert.That(ZoneAt(1, 4).Population, Is.EqualTo(ExpectedStep(1.0f)));
        Assert.That(ZoneAt(3, 4).Population, Is.EqualTo(ExpectedStep(1.0f)));
    }

    [Test]
    public void Process_HigherAccessibility_GrowsFaster()
    {
        ZoneAt(2, 4).AccessibilityScore = 1.8f;

        _growth.Process(_grid, _graph, null, null);

        Assert.That(ZoneAt(2, 4).Population, Is.EqualTo(ExpectedStep(1.8f)));
        Assert.That(ZoneAt(2, 4).Population, Is.GreaterThan(ZoneAt(1, 4).Population));
    }

    [Test]
    public void Process_UnreachableZone_DoesNotGrow()
    {
        _growth.Process(_grid, _graph, null, null);

        Assert.That(ZoneAt(8, 8).Population, Is.EqualTo(0));
        Assert.That(_growth.ReachableNodes, Does.Not.Contain(Id(8, 8)));
        Assert.That(_growth.ReachableNodes, Does.Contain(Id(3, 4)));
    }

    [Test]
    public void Process_ZeroAccessibility_DoesNotGrow()
    {
        ZoneAt(1, 4).AccessibilityScore = 0f;

        _growth.Process(_grid, _graph, null, null);

        Assert.That(ZoneAt(1, 4).Population, Is.EqualTo(0));
    }

    [Test]
    public void Process_ModerateCongestion_DampsGrowth()
    {
        SetAllEdgeCongestion(1.2f);

        _growth.Process(_grid, _graph, null, null);

        float damping = 1f - LutiGrowthSystem.CongestionDampingSlope * (1.2f - LutiGrowthSystem.CongestionDampingStart);
        Assert.That(ZoneAt(2, 4).LocalCongestion, Is.EqualTo(1.2f).Within(1e-4f));
        Assert.That(ZoneAt(2, 4).Population, Is.EqualTo(ExpectedStep(1.0f, damping)));
    }

    [Test]
    public void Process_GridlockAboveStagnationThreshold_Stagnates()
    {
        SetAllEdgeCongestion(ParcelManager.CongestionStagnationThreshold + 0.5f);

        var result = _growth.Process(_grid, _graph, null, null);

        Assert.That(result.LandUseChanged, Is.False);
        Assert.That(ZoneAt(2, 4).Population, Is.EqualTo(0));
    }

    [Test]
    public void Process_NearCap_ClampsPopulationToCap()
    {
        ZoneAt(1, 4).Population = TestResidentialCap - 5;
        ZoneAt(1, 4).AccessibilityScore = 0.5f; // below the medium-density threshold: no upgrade

        _growth.Process(_grid, _graph, null, null);

        Assert.That(ZoneAt(1, 4).Population, Is.EqualTo(TestResidentialCap));
        Assert.That(ZoneAt(1, 4).Density, Is.EqualTo(DensityLevel.Low));
    }

    [Test]
    public void Process_HighOccupancyAndAccessibility_UpgradesDensityAndMarksTazDirty()
    {
        var taz = new TazManager { IsDirty = false };
        ZoneAt(1, 4).Population = (int)(TestResidentialCap * LutiGrowthSystem.DensityUpgradeOccupancy);
        ZoneAt(1, 4).AccessibilityScore = ParcelManager.MediumDensityAccessibilityThreshold;

        var result = _growth.Process(_grid, _graph, taz, null);

        Assert.That(result.DensityChanged, Is.True);
        Assert.That(result.LandUseChanged, Is.True);
        Assert.That(ZoneAt(1, 4).Density, Is.EqualTo(DensityLevel.Medium));
        Assert.That(taz.IsDirty, Is.True);
    }

    [Test]
    public void Process_RepeatedSteps_ComputesReachabilityOnce()
    {
        for (int i = 0; i < 5; i++)
        {
            _growth.Process(_grid, _graph, null, null);
        }

        Assert.That(_growth.ReachabilityRecomputeCount, Is.EqualTo(1));
    }

    [Test]
    public void Process_AfterInvalidate_RecomputesReachability()
    {
        _growth.Process(_grid, _graph, null, null);
        _growth.InvalidateReachability();
        _growth.Process(_grid, _graph, null, null);

        Assert.That(_growth.ReachabilityRecomputeCount, Is.EqualTo(2));
    }

    [Test]
    public void Process_NewEntranceZone_RecomputesWithoutInvalidate()
    {
        _growth.Process(_grid, _graph, null, null);
        Assert.That(ZoneAt(8, 8).Population, Is.EqualTo(0));

        _grid.ZoneCell(8, 9, ZoneType.Entrance);
        _graph.EnsureNode(Id(8, 9), _grid.GetWorldCenter(Id(8, 9)));
        _graph.AddRoadSegment(Id(8, 9), Id(8, 8));
        _growth.Process(_grid, _graph, null, null);

        Assert.That(_growth.ReachabilityRecomputeCount, Is.EqualTo(2));
        Assert.That(ZoneAt(8, 8).Population, Is.GreaterThan(0));
    }

    [Test]
    public void Coordinator_DemolishedRoad_InvalidatesReachabilityAndStopsGrowthBeyondCut()
    {
        using var coordinator = new TopologyChangeCoordinator(_graph, _grid, null, null, null, null, null, null, _growth);
        _growth.Process(_grid, _graph, null, null);
        int populationBeforeCut = ZoneAt(3, 4).Population;

        _graph.RemoveRoadSegment(Id(1, 4), Id(2, 4));
        coordinator.ApplyChanges();
        _growth.Process(_grid, _graph, null, null);

        Assert.That(_growth.ReachabilityRecomputeCount, Is.EqualTo(2));
        Assert.That(_growth.ReachableNodes, Does.Not.Contain(Id(3, 4)));
        Assert.That(ZoneAt(3, 4).Population, Is.EqualTo(populationBeforeCut));
        Assert.That(ZoneAt(1, 4).Population, Is.GreaterThan(populationBeforeCut));
    }
}
