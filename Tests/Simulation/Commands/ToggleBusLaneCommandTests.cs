using NUnit.Framework;
using System.Linq;
using CitySim.Simulation;
using CitySim.Simulation.Commands;

namespace CitySim.Tests.Simulation.Commands;

[TestFixture]
public class ToggleBusLaneCommandTests
{
    private SimulationWorld _world;
    private RoadEdge _edge;
    private RoadEdge _reverse;
    private int _macroRecalculations;

    [SetUp]
    public void Setup()
    {
        _world = CommandTestWorld.Create(ScenarioType.Tutorial);
        _edge = _world.RoadGraph.Edges.First(e => e.FromId != -1 && e.ToId != -1 && !e.HasBusLane && e.Lanes >= 2);
        _reverse = _world.RoadGraph.GetEdge(_world.RoadGraph.FindEdgeId(_edge.ToId, _edge.FromId));
        _macroRecalculations = 0;
        _world.MacroModelRecalculated += () => _macroRecalculations++;
    }

    [TearDown]
    public void TearDown() => _world.Dispose();

    [Test]
    public void Execute_LaneOff_EnablesBothDirectionsReducesCarCapacityAndRecalculates()
    {
        float capacityBefore = _edge.Capacity;

        var result = new ToggleBusLaneCommand(_edge.Id).Execute(_world);

        Assert.That(result.IsApplied, Is.True);
        Assert.That(result.Tone, Is.EqualTo(CommandTone.Success));
        Assert.That(result.Topology, Is.Null);
        Assert.That(result.Message, Does.Contain("УВІМКНЕНО"));
        Assert.That(_edge.HasBusLane, Is.True);
        Assert.That(_reverse.HasBusLane, Is.True);
        Assert.That(_edge.Capacity, Is.LessThan(capacityBefore));
        Assert.That(_macroRecalculations, Is.EqualTo(1));
    }

    [Test]
    public void Execute_LaneOn_DisablesAndRestoresCapacity()
    {
        float capacityBefore = _edge.Capacity;
        new ToggleBusLaneCommand(_edge.Id).Execute(_world);

        var result = new ToggleBusLaneCommand(_edge.Id).Execute(_world);

        Assert.That(result.IsApplied, Is.True);
        Assert.That(result.Tone, Is.EqualTo(CommandTone.Caution));
        Assert.That(result.Message, Does.Contain("ВИМКНЕНО"));
        Assert.That(_edge.HasBusLane, Is.False);
        Assert.That(_edge.Capacity, Is.EqualTo(capacityBefore));
    }

    [TestCase(-1)]
    [TestCase(int.MaxValue)]
    public void Execute_NoEdge_RejectedWithoutRecalculation(int edgeId)
    {
        var result = new ToggleBusLaneCommand(edgeId).Execute(_world);

        Assert.That(result.Status, Is.EqualTo(CommandStatus.Rejected));
        Assert.That(_macroRecalculations, Is.EqualTo(0));
    }

    [Test]
    public void Execute_DemolishedEdge_Rejected()
    {
        _world.RoadGraph.RemoveRoadSegment(_edge.FromId, _edge.ToId);
        _world.ApplyTopologyChange();
        _macroRecalculations = 0;

        var result = new ToggleBusLaneCommand(_edge.Id).Execute(_world);

        Assert.That(result.Status, Is.EqualTo(CommandStatus.Rejected));
        Assert.That(_macroRecalculations, Is.EqualTo(0));
    }
}
