using NUnit.Framework;
using CitySim.Simulation;
using CitySim.Simulation.Commands;

namespace CitySim.Tests.Simulation.Commands;

[TestFixture]
public class DemolishCommandsTests
{
    private SimulationWorld _world;

    [SetUp]
    public void Setup() => _world = CommandTestWorld.Create();

    [TearDown]
    public void TearDown() => _world.Dispose();

    /// <summary>Two horizontally adjacent free cells joined by a new road.</summary>
    private (int A, int B) BuildRoadBetweenFreeNeighbours()
    {
        for (int y = 0; y < _world.Grid.Height; y++)
        {
            for (int x = 0; x < _world.Grid.Width - 1; x++)
            {
                int a = _world.Grid.GetZoneId(x, y);
                int b = _world.Grid.GetZoneId(x + 1, y);
                if (IsFree(a) && IsFree(b))
                {
                    _world.RoadGraph.EnsureNode(a, _world.Grid.GetWorldCenter(a));
                    _world.RoadGraph.EnsureNode(b, _world.Grid.GetWorldCenter(b));
                    _world.RoadGraph.AddRoadSegment(a, b);
                    _world.ApplyTopologyChange();
                    return (a, b);
                }
            }
        }
        throw new System.InvalidOperationException("No free neighbouring cells");
    }

    private bool IsFree(int id) => _world.Grid.GetZone(id).Type == ZoneType.Empty && _world.RoadGraph.GetNode(id) == null;

    [Test]
    public void DemolishRoad_BetweenEmptyCells_RemovesRoadAndOrphanedEndpointNodes()
    {
        var (a, b) = BuildRoadBetweenFreeNeighbours();
        int edgesBefore = CommandTestWorld.ActiveEdgeCount(_world);

        var result = new DemolishRoadCommand(a, b).Execute(_world);

        Assert.That(result.IsApplied, Is.True);
        Assert.That(result.Tone, Is.EqualTo(CommandTone.Demolition));
        Assert.That(result.Topology, Is.Not.Null);
        Assert.That(result.Message, Does.Contain("Road demolished"));
        Assert.That(CommandTestWorld.ActiveEdgeCount(_world), Is.EqualTo(edgesBefore - 2));
        Assert.That(_world.RoadGraph.GetNode(a), Is.Null);
        Assert.That(_world.RoadGraph.GetNode(b), Is.Null);
        Assert.That(_world.Topology.HasPendingChanges, Is.False);
    }

    [Test]
    public void DemolishRoad_ZonedEndpoint_KeepsItsNode()
    {
        var (a, b) = BuildRoadBetweenFreeNeighbours();
        new ZoneCellCommand(a, ZoneType.Residential).Execute(_world);

        new DemolishRoadCommand(a, b).Execute(_world);

        Assert.That(_world.RoadGraph.GetNode(a), Is.Not.Null, "Zoned cell keeps its connector");
        Assert.That(_world.RoadGraph.GetNode(b), Is.Null);
    }

    [Test]
    public void DemolishRoad_NoRoadBetweenCells_RejectedAndGraphUnchanged()
    {
        var (a, _) = BuildRoadBetweenFreeNeighbours();
        int other = CommandTestWorld.FindEmptyCellWithoutNode(_world);
        int edgesBefore = CommandTestWorld.ActiveEdgeCount(_world);

        var result = new DemolishRoadCommand(a, other).Execute(_world);

        Assert.That(result.Status, Is.EqualTo(CommandStatus.Rejected));
        Assert.That(result.Message, Does.Contain("Failed to demolish"));
        Assert.That(CommandTestWorld.ActiveEdgeCount(_world), Is.EqualTo(edgesBefore));
    }

    [Test]
    public void DemolishIsolated_ZonedCellWithoutRoads_RemovesNodeAndZoning()
    {
        int zoneId = CommandTestWorld.FindEmptyCellWithoutNode(_world);
        new ZoneCellCommand(zoneId, ZoneType.Commercial).Execute(_world);

        var result = new DemolishIsolatedCommand(zoneId).Execute(_world);

        Assert.That(result.IsApplied, Is.True);
        Assert.That(result.Message, Does.Contain("Demolished isolated"));
        Assert.That(_world.RoadGraph.GetNode(zoneId), Is.Null);
        Assert.That(_world.Grid.GetZone(zoneId).Type, Is.EqualTo(ZoneType.Empty));
    }

    [Test]
    public void DemolishIsolated_ConnectedNode_Rejected()
    {
        var (a, _) = BuildRoadBetweenFreeNeighbours();

        var result = new DemolishIsolatedCommand(a).Execute(_world);

        Assert.That(result.Status, Is.EqualTo(CommandStatus.Rejected));
        Assert.That(result.Message, Does.Contain("no road connections"));
        Assert.That(_world.RoadGraph.GetNode(a), Is.Not.Null);
    }

    [Test]
    public void DemolishIsolated_EmptyCellWithoutNode_Rejected()
    {
        int zoneId = CommandTestWorld.FindEmptyCellWithoutNode(_world);

        Assert.That(new DemolishIsolatedCommand(zoneId).Execute(_world).Status, Is.EqualTo(CommandStatus.Rejected));
    }
}
