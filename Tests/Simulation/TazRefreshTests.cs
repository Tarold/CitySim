using NUnit.Framework;
using System.Linq;
using CitySim.Simulation;
using CitySim.Simulation.Commands;

namespace CitySim.Tests.Simulation;

[TestFixture]
public class TazRefreshTests
{
    private SimulationWorld _world;

    [SetUp]
    public void Setup()
    {
        _world = SimulationWorld.Create(ScenarioType.Tutorial);
        _world.RecalculateMacroModel();
        _world.Economy.SetBalance(1_000_000f);
    }

    [TearDown]
    public void TearDown() => _world.Dispose();

    private RoadsideParcel FirstResidentialParcelInTaz() =>
        _world.Taz.ActiveZones.SelectMany(t => t.Parcels).First(p => p.ZoneType == ZoneType.Residential);

    [Test]
    public void RefreshOrRebuild_PopulationOnlyChange_RefreshesTotalsWithoutRebuilding()
    {
        var tazBefore = _world.Taz.ActiveZones.ToList();
        var parcel = FirstResidentialParcelInTaz();
        parcel.Population += 500;

        bool rebuilt = _world.Taz.RefreshOrRebuild(_world.Grid, _world.Parcels, _world.RoadGraph, _world.Transit);

        Assert.That(rebuilt, Is.False);
        Assert.That(_world.Taz.ActiveZones, Is.EqualTo(tazBefore), "Same TAZ objects: no re-clustering");
        Assert.That(_world.Taz.ActiveZones.Sum(t => t.TotalPopulation),
            Is.EqualTo(tazBefore.SelectMany(t => t.Zones).Sum(z => z.Population) + tazBefore.SelectMany(t => t.Parcels).Sum(p => p.Population)));
    }

    [Test]
    public void TryRefreshAggregates_AfterGrowth_MatchesFullRebuildTotals()
    {
        foreach (var parcel in _world.Taz.ActiveZones.SelectMany(t => t.Parcels).Where(p => p.ZoneType == ZoneType.Residential))
        {
            parcel.Population += 123;
        }

        Assert.That(_world.Taz.TryRefreshAggregates(_world.Grid, _world.Parcels), Is.True);

        var reference = new TazManager();
        reference.Rebuild(_world.Grid, _world.Parcels, _world.RoadGraph, _world.Transit);
        Assert.That(_world.Taz.ActiveZones.Count, Is.EqualTo(reference.ActiveZones.Count));
        for (int i = 0; i < reference.ActiveZones.Count; i++)
        {
            var a = _world.Taz.ActiveZones[i];
            var b = reference.ActiveZones[i];
            Assert.That(a.TotalPopulation, Is.EqualTo(b.TotalPopulation), $"TAZ {i} population");
            Assert.That(a.TotalJobs, Is.EqualTo(b.TotalJobs), $"TAZ {i} jobs");
            Assert.That(a.TotalCapacity, Is.EqualTo(b.TotalCapacity), $"TAZ {i} capacity");
        }
    }

    [Test]
    public void RefreshOrRebuild_MarkedDirty_Rebuilds()
    {
        var tazBefore = _world.Taz.ActiveZones.First();
        _world.InvalidateMacroStructure();

        bool rebuilt = _world.Taz.RefreshOrRebuild(_world.Grid, _world.Parcels, _world.RoadGraph, _world.Transit);

        Assert.That(rebuilt, Is.True);
        Assert.That(_world.Taz.ActiveZones.First(), Is.Not.SameAs(tazBefore));
        Assert.That(_world.Taz.IsDirty, Is.False);
    }

    [Test]
    public void RefreshOrRebuild_EntityZonedWithoutMarkDirty_FallsBackToRebuild()
    {
        int freeCell = Enumerable.Range(0, _world.Grid.ZoneCount).First(id => _world.Grid.GetZone(id).Type == ZoneType.Empty);
        _world.Grid.ZoneCell(freeCell, ZoneType.Commercial);

        bool rebuilt = _world.Taz.RefreshOrRebuild(_world.Grid, _world.Parcels, _world.RoadGraph, _world.Transit);

        Assert.That(rebuilt, Is.True, "New contributing zone changes the entity count");
    }

    [Test]
    public void World_RecalculationAfterPopulationGrowth_KeepsTazStructure()
    {
        var tazBefore = _world.Taz.ActiveZones.ToList();
        var parcel = FirstResidentialParcelInTaz();
        int tazPopulationBefore = _world.Taz.ActiveZones.Sum(t => t.TotalPopulation);
        parcel.Population += 250;

        _world.RecalculateMacroModel();

        Assert.That(_world.Taz.ActiveZones, Is.EqualTo(tazBefore));
        Assert.That(_world.Taz.ActiveZones.Sum(t => t.TotalPopulation), Is.EqualTo(tazPopulationBefore + 250));
    }

    [Test]
    public void World_DensityUpgradeDuringGrowth_MarksStructureDirty()
    {
        const int maxGrowthSteps = 400;
        bool densityChanged = false;
        for (int i = 0; i < maxGrowthSteps && !densityChanged; i++)
        {
            densityChanged = _world.Growth.Process(_world.Grid, _world.RoadGraph, _world.Taz, _world.Parcels).DensityChanged;
        }

        Assume.That(densityChanged, "Scenario reaches a density upgrade");
        Assert.That(_world.Taz.IsDirty, Is.True);
    }

    [Test]
    public void World_ZoningCommand_ForcesStructuralRebuild()
    {
        var tazBefore = _world.Taz.ActiveZones.First();
        var parcel = _world.Parcels.Parcels.First(p => p.ZoneType == ZoneType.Empty);

        new ZoneParcelsCommand(new[] { parcel }, parcel.Side, ZoneType.Commercial).Execute(_world);

        Assert.That(_world.Taz.ActiveZones.First(), Is.Not.SameAs(tazBefore));
    }

    [Test]
    public void World_TopologyChange_ForcesStructuralRebuild()
    {
        var tazBefore = _world.Taz.ActiveZones.First();
        var edge = _world.RoadGraph.Edges.First(e => e.FromId != -1 && e.ToId != -1);

        _world.RoadGraph.SplitEdge(edge.Id, 0.5f);
        _world.ApplyTopologyChange();

        Assert.That(_world.Taz.ActiveZones.First(), Is.Not.SameAs(tazBefore));
    }
}
