using Godot;
using NUnit.Framework;
using CitySim.Simulation;
using CitySim.Simulation.Commands;

namespace CitySim.Tests.Simulation.Commands;

[TestFixture]
public class BuildRoadCommandTests
{
    private SimulationWorld _world;
    private RoadNode _start;
    private RoadNode _end;

    [SetUp]
    public void Setup()
    {
        _world = CommandTestWorld.Create();
        _start = _world.RoadGraph.CreateNode(new Vector2(200f, 1180f));
        _end = _world.RoadGraph.CreateNode(new Vector2(392f, 1180f)); // 3 cells away
        _world.ApplyTopologyChange();
    }

    [TearDown]
    public void TearDown() => _world.Dispose();

    private CurveSegment StraightCurve() => new CurveSegment(_start.WorldPosition, _end.WorldPosition);

    [Test]
    public void CalculateCost_ProportionalToLengthWithMinimum()
    {
        var threeCells = new CurveSegment(Vector2.Zero, new Vector2(3f * BuildRoadCommand.CostReferenceLength, 0f));
        var tiny = new CurveSegment(Vector2.Zero, new Vector2(5f, 0f));

        Assert.That(BuildRoadCommand.CalculateCost(threeCells), Is.EqualTo(3f * EconomyManager.RoadSegmentCost).Within(0.5f));
        Assert.That(BuildRoadCommand.CalculateCost(tiny), Is.EqualTo(BuildRoadCommand.MinRoadCost));
    }

    [Test]
    public void Execute_ValidSegment_AddsTwoWayRoadSpendsAndAppliesTopology()
    {
        var curve = StraightCurve();
        float expectedCost = BuildRoadCommand.CalculateCost(curve);
        int edgesBefore = CommandTestWorld.ActiveEdgeCount(_world);

        var result = new BuildRoadCommand(_start.Id, _end.Id, curve).Execute(_world);

        Assert.That(result.IsApplied, Is.True);
        Assert.That(result.Cost, Is.EqualTo(expectedCost));
        Assert.That(result.Topology, Is.Not.Null);
        Assert.That(result.Message, Does.Contain("Road built"));
        Assert.That(_world.RoadGraph.HasEdge(_start.Id, _end.Id), Is.True);
        Assert.That(_world.RoadGraph.HasEdge(_end.Id, _start.Id), Is.True);
        Assert.That(CommandTestWorld.ActiveEdgeCount(_world), Is.EqualTo(edgesBefore + 2));
        Assert.That(_world.Economy.Money, Is.EqualTo(CommandTestWorld.AmpleBudget - expectedCost));
        Assert.That(_world.Topology.HasPendingChanges, Is.False);
    }

    [Test]
    public void Execute_CurvedSegment_ChargesCurveLength()
    {
        var curve = CurveSegment.CreateFromThreePoints(_start.WorldPosition, new Vector2(296f, 1080f), _end.WorldPosition);

        var result = new BuildRoadCommand(_start.Id, _end.Id, curve).Execute(_world);

        Assert.That(result.Cost, Is.EqualTo(BuildRoadCommand.CalculateCost(curve)));
        Assert.That(result.Cost, Is.GreaterThan(BuildRoadCommand.CalculateCost(StraightCurve())));
    }

    [Test]
    public void Execute_SameStartAndEnd_RejectedAsWarning()
    {
        var result = new BuildRoadCommand(_start.Id, _start.Id, StraightCurve()).Execute(_world);

        Assert.That(result.Status, Is.EqualTo(CommandStatus.Rejected));
        Assert.That(result.Tone, Is.EqualTo(CommandTone.Warning));
        Assert.That(result.Message, Does.Contain("must be different"));
    }

    [Test]
    public void Execute_RoadAlreadyExists_RejectedWithoutCharge()
    {
        new BuildRoadCommand(_start.Id, _end.Id, StraightCurve()).Execute(_world);
        float money = _world.Economy.Money;

        var result = new BuildRoadCommand(_start.Id, _end.Id, StraightCurve()).Execute(_world);

        Assert.That(result.Status, Is.EqualTo(CommandStatus.Rejected));
        Assert.That(result.Message, Does.Contain("already exists"));
        Assert.That(_world.Economy.Money, Is.EqualTo(money));
    }

    [Test]
    public void Execute_InsufficientFunds_RejectedAndNoRoadAdded()
    {
        _world.Economy.SetBalance(BuildRoadCommand.CalculateCost(StraightCurve()) - 1f);

        var result = new BuildRoadCommand(_start.Id, _end.Id, StraightCurve()).Execute(_world);

        Assert.That(result.Status, Is.EqualTo(CommandStatus.Rejected));
        Assert.That(result.Tone, Is.EqualTo(CommandTone.Error));
        Assert.That(_world.RoadGraph.HasEdge(_start.Id, _end.Id), Is.False);
        Assert.That(_world.Topology.HasPendingChanges, Is.False);
    }
}
