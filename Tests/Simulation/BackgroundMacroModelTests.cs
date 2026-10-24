using NUnit.Framework;
using System;
using System.Linq;
using System.Threading;
using CitySim.Simulation;
using CitySim.Simulation.Commands;
using CitySim.Tests.Tools;
using CitySim.Tools;

namespace CitySim.Tests.Simulation;

[TestFixture]
public class BackgroundMacroModelTests
{
    private static readonly TimeSpan JobTimeout = TimeSpan.FromSeconds(30);

    private SimulationWorld _world;
    private int _recalculations;

    [SetUp]
    public void Setup()
    {
        _world = SimulationWorld.Create(ScenarioType.Tutorial);
        _world.UseBackgroundMacroModel = true;
        _world.Economy.SetBalance(1_000_000f);
        _recalculations = 0;
        _world.MacroModelRecalculated += () => _recalculations++;
    }

    [TearDown]
    public void TearDown() => _world.Dispose();

    [Test]
    public void RecalculateMacroModel_Background_ReturnsBeforeResultsAndAppliesOnWait()
    {
        _world.RecalculateMacroModel();

        Assert.That(_world.IsMacroJobPending, Is.True);
        Assert.That(_recalculations, Is.EqualTo(0), "Results are applied on the main thread later");

        _world.WaitForMacroModel();

        Assert.That(_world.IsMacroJobPending, Is.False);
        Assert.That(_recalculations, Is.EqualTo(1));
        Assert.That(_world.RoadGraph.Edges.Any(e => e.CurrentVolume > 0f), Is.True);
        Assert.That(_world.TrafficEngine.HasPendingVolumes, Is.False);
        Assert.That(_world.Stats.TotalTrips, Is.GreaterThan(0f));
    }

    [Test]
    public void Background_TwoCycles_MatchSynchronousResults()
    {
        using var sync = SimulationWorld.Create(ScenarioType.Tutorial);
        for (int cycle = 0; cycle < 2; cycle++)
        {
            sync.RecalculateMacroModel();
            _world.RecalculateMacroModel();
            _world.WaitForMacroModel();
        }

        for (int e = 0; e < sync.RoadGraph.Edges.Count; e++)
        {
            Assert.That(_world.RoadGraph.Edges[e].CurrentVolume, Is.EqualTo(sync.RoadGraph.Edges[e].CurrentVolume), $"car volume, edge {e}");
            Assert.That(_world.RoadGraph.Edges[e].PedestrianVolume, Is.EqualTo(sync.RoadGraph.Edges[e].PedestrianVolume), $"pedestrian volume, edge {e}");
        }
        Assert.That(_world.Taz.ActiveZones.Select(t => t.AccessibilityScore), Is.EqualTo(sync.Taz.ActiveZones.Select(t => t.AccessibilityScore)));
    }

    [Test]
    public void FrontModel_WhileJobRuns_StaysThePreviousResult()
    {
        _world.RecalculateMacroModel();
        _world.WaitForMacroModel();
        var frontTaz = _world.Taz;
        var volumesBefore = _world.RoadGraph.Edges.Select(e => e.CurrentVolume).ToArray();

        _world.InvalidateMacroStructure();
        _world.RecalculateMacroModel();

        Assert.That(_world.Taz, Is.SameAs(frontTaz));
        Assert.That(_world.RoadGraph.Edges.Select(e => e.CurrentVolume), Is.EqualTo(volumesBefore));

        _world.WaitForMacroModel();
        Assert.That(_world.Taz, Is.Not.SameAs(frontTaz), "Buffers swapped after apply");
    }

    [Test]
    public void RecalculateMacroModel_RequestsDuringJob_AreCoalescedIntoOneFollowUp()
    {
        _world.RecalculateMacroModel();
        _world.RecalculateMacroModel();
        _world.RecalculateMacroModel();

        _world.WaitForMacroModel();

        Assert.That(_recalculations, Is.EqualTo(2));
    }

    [Test]
    public void Command_DuringJob_WaitsAppliesAndSchedulesNewRecalculation()
    {
        _world.RecalculateMacroModel();
        var parcel = _world.Parcels.Parcels.First(p => p.ZoneType == ZoneType.Empty);

        var result = new ZoneParcelsCommand(new[] { parcel }, parcel.Side, ZoneType.Commercial).Execute(_world);

        Assert.That(result.IsApplied, Is.True);
        Assert.That(_recalculations, Is.EqualTo(1), "In-flight job applied before the edit");
        Assert.That(_world.IsMacroJobPending, Is.True, "Edit scheduled its own recalculation");

        _world.WaitForMacroModel();
        Assert.That(_recalculations, Is.EqualTo(2));
        Assert.That(_world.Taz.ActiveZones.SelectMany(t => t.Parcels), Does.Contain(parcel));
    }

    [Test]
    public void Tick_CompletedJob_IsAppliedOnNextTick()
    {
        _world.RecalculateMacroModel();
        Assert.That(SpinWait.SpinUntil(() => _world.IsMacroJobReady, JobTimeout), Is.True);

        _world.Tick(0f);

        Assert.That(_recalculations, Is.EqualTo(1));
        Assert.That(_world.IsMacroJobPending, Is.False);
    }

    [Test]
    public void ApplyTopologyChange_Background_RebuildsTazAfterJob()
    {
        _world.RecalculateMacroModel();
        _world.WaitForMacroModel();
        var edge = _world.RoadGraph.Edges.First(e => e.FromId != -1 && e.ToId != -1);
        int nodesBefore = _world.RoadGraph.Nodes.Count;

        _world.RoadGraph.SplitEdge(edge.Id, 0.5f);
        var result = _world.ApplyTopologyChange();

        Assert.That(result.Applied, Is.True);
        Assert.That(_world.IsMacroJobPending, Is.True);
        _world.WaitForMacroModel();
        Assert.That(_world.RoadGraph.Nodes.Count, Is.EqualTo(nodesBefore + 1));
        Assert.That(_world.Taz.IsDirty, Is.False);
        Assert.That(_world.RoadGraph.Edges.Any(e => e.CurrentVolume > 0f), Is.True);
    }

    [Test]
    public void Tick_ManyFramesAtSpeed_RunsBackgroundCyclesWithoutErrors()
    {
        _world.RecalculateMacroModel();
        _world.Clock.Speed = 50f;

        Assert.DoesNotThrow(() =>
        {
            for (int frame = 0; frame < 600; frame++) _world.Tick(1f / 60f);
            _world.WaitForMacroModel();
        });
        Assert.That(_recalculations, Is.GreaterThan(1));
    }

    [Test]
    public void BeginEdit_NoJob_IsNoOp()
    {
        Assert.DoesNotThrow(() => _world.BeginEdit());
        Assert.That(_recalculations, Is.EqualTo(0));
    }

    [Test]
    public void RoadTool_SplitsEdgeWhileJobRuns_WaitsForJobFirst()
    {
        _world.RecalculateMacroModel();
        var host = new FakeToolHost(_world);
        var tool = new RoadBuildTool(host, curved: false);
        var edge = _world.RoadGraph.Edges.First(e => e.FromId != -1 && e.ToId != -1 && e.Curve != null && e.Curve.Length > 60f);

        tool.OnPrimaryClick(new ToolInput(edge.Curve.Evaluate(0.5f)));

        Assert.That(_recalculations, Is.EqualTo(1), "In-flight job applied before the graph was split");
        Assert.That(tool.Step, Is.EqualTo(1));
        Assert.That(_world.Topology.HasPendingChanges, Is.True);

        _world.Tick(0f); // safety net applies the split and schedules a recalculation
        _world.WaitForMacroModel();
        Assert.That(_world.Topology.HasPendingChanges, Is.False);
        Assert.That(_recalculations, Is.EqualTo(2));
    }
}
