using NUnit.Framework;
using System.Linq;
using CitySim.Simulation;
using CitySim.Simulation.Commands;

namespace CitySim.Tests.Simulation.Commands;

[TestFixture]
public class ZoningCommandsTests
{
    private SimulationWorld _world;
    private int _macroRecalculations;

    [SetUp]
    public void Setup()
    {
        _world = CommandTestWorld.Create();
        _macroRecalculations = 0;
        _world.MacroModelRecalculated += () => _macroRecalculations++;
    }

    [TearDown]
    public void TearDown() => _world.Dispose();

    [Test]
    public void ZoneCell_EmptyCell_ZonesSpendsCreatesNodeAndRecalculates()
    {
        int zoneId = CommandTestWorld.FindEmptyCellWithoutNode(_world);

        var result = new ZoneCellCommand(zoneId, ZoneType.Residential).Execute(_world);

        Assert.That(result.Status, Is.EqualTo(CommandStatus.Applied));
        Assert.That(result.Cost, Is.EqualTo(EconomyManager.ZoningCost));
        Assert.That(result.Topology, Is.Not.Null, "A new road node is a topology change");
        Assert.That(result.Message, Does.Contain("Residential"));
        Assert.That(_world.Grid.GetZone(zoneId).Type, Is.EqualTo(ZoneType.Residential));
        Assert.That(_world.RoadGraph.GetNode(zoneId), Is.Not.Null);
        Assert.That(_world.Economy.Money, Is.EqualTo(CommandTestWorld.AmpleBudget - EconomyManager.ZoningCost));
        Assert.That(_world.Topology.HasPendingChanges, Is.False);
        Assert.That(_macroRecalculations, Is.EqualTo(1));
    }

    [Test]
    public void ZoneCell_AlreadySameType_ReturnsNoChangeAndKeepsMoney()
    {
        int zoneId = CommandTestWorld.FindEmptyCellWithoutNode(_world);
        new ZoneCellCommand(zoneId, ZoneType.Commercial).Execute(_world);
        float money = _world.Economy.Money;

        var result = new ZoneCellCommand(zoneId, ZoneType.Commercial).Execute(_world);

        Assert.That(result.Status, Is.EqualTo(CommandStatus.NoChange));
        Assert.That(result.Tone, Is.EqualTo(CommandTone.Neutral));
        Assert.That(_world.Economy.Money, Is.EqualTo(money));
    }

    [Test]
    public void ZoneCell_ExistingNode_RecalculatesWithoutTopologyChange()
    {
        int zoneId = CommandTestWorld.FindEmptyCellWithoutNode(_world);
        new ZoneCellCommand(zoneId, ZoneType.Residential).Execute(_world);

        var result = new ZoneCellCommand(zoneId, ZoneType.Industrial).Execute(_world);

        Assert.That(result.IsApplied, Is.True);
        Assert.That(result.Topology, Is.Null);
        Assert.That(_macroRecalculations, Is.EqualTo(2));
    }

    [Test]
    public void ZoneCell_InsufficientFunds_RejectedAndWorldUnchanged()
    {
        int zoneId = CommandTestWorld.FindEmptyCellWithoutNode(_world);
        _world.Economy.SetBalance(EconomyManager.ZoningCost - 1f);

        var result = new ZoneCellCommand(zoneId, ZoneType.Residential).Execute(_world);

        Assert.That(result.Status, Is.EqualTo(CommandStatus.Rejected));
        Assert.That(result.Message, Does.Contain("Insufficient funds"));
        Assert.That(_world.Grid.GetZone(zoneId).Type, Is.EqualTo(ZoneType.Empty));
        Assert.That(_world.RoadGraph.GetNode(zoneId), Is.Null);
        Assert.That(_macroRecalculations, Is.EqualTo(0));
    }

    [Test]
    public void ZoneCell_InvalidZoneId_Rejected()
    {
        Assert.That(new ZoneCellCommand(-1, ZoneType.Residential).Execute(_world).Status, Is.EqualTo(CommandStatus.Rejected));
    }

    [Test]
    public void DezoneCell_ZonedCellWithoutRoads_ClearsCellAndRemovesOrphanNode()
    {
        int zoneId = CommandTestWorld.FindEmptyCellWithoutNode(_world);
        new ZoneCellCommand(zoneId, ZoneType.Residential).Execute(_world);

        var result = new DezoneCellCommand(zoneId).Execute(_world);

        Assert.That(result.IsApplied, Is.True);
        Assert.That(result.Tone, Is.EqualTo(CommandTone.Clearing));
        Assert.That(result.Topology, Is.Not.Null);
        Assert.That(_world.Grid.GetZone(zoneId).Type, Is.EqualTo(ZoneType.Empty));
        Assert.That(_world.RoadGraph.GetNode(zoneId), Is.Null);
    }

    [Test]
    public void DezoneCell_EmptyCellWithOrphanNode_RemovesNode()
    {
        int zoneId = CommandTestWorld.FindEmptyCellWithoutNode(_world);
        _world.RoadGraph.EnsureNode(zoneId, _world.Grid.GetWorldCenter(zoneId));
        _world.ApplyTopologyChange();

        var result = new DezoneCellCommand(zoneId).Execute(_world);

        Assert.That(result.IsApplied, Is.True);
        Assert.That(result.Message, Does.Contain("orphan"));
        Assert.That(_world.RoadGraph.GetNode(zoneId), Is.Null);
    }

    [Test]
    public void DezoneCell_EmptyCellWithoutNode_ReturnsNoChange()
    {
        int zoneId = CommandTestWorld.FindEmptyCellWithoutNode(_world);

        var result = new DezoneCellCommand(zoneId).Execute(_world);

        Assert.That(result.Status, Is.EqualTo(CommandStatus.NoChange));
        Assert.That(_macroRecalculations, Is.EqualTo(0));
    }

    [Test]
    public void ZoneParcels_EmptyLots_ZonesAllChargesPerLotAndRecalculates()
    {
        var parcels = CommandTestWorld.FindEmptyParcelsOnOneSide(_world, 2);

        var result = new ZoneParcelsCommand(parcels, parcels[0].Side, ZoneType.Commercial).Execute(_world);

        Assert.That(result.IsApplied, Is.True);
        Assert.That(result.Cost, Is.EqualTo(2 * EconomyManager.ZoningCost));
        Assert.That(result.Message, Does.Contain("Zoned 2 roadside lots"));
        Assert.That(parcels.All(p => p.ZoneType == ZoneType.Commercial), Is.True);
        Assert.That(_world.Economy.Money, Is.EqualTo(CommandTestWorld.AmpleBudget - 2 * EconomyManager.ZoningCost));
        Assert.That(_macroRecalculations, Is.EqualTo(1));
    }

    [Test]
    public void ZoneParcels_SomeAlreadyTargetType_ChargesOnlyChangedLots()
    {
        var parcels = CommandTestWorld.FindEmptyParcelsOnOneSide(_world, 2);
        new ZoneParcelsCommand(parcels.Take(1).ToList(), parcels[0].Side, ZoneType.Residential).Execute(_world);
        float money = _world.Economy.Money;

        var result = new ZoneParcelsCommand(parcels, parcels[0].Side, ZoneType.Residential).Execute(_world);

        Assert.That(result.Cost, Is.EqualTo(EconomyManager.ZoningCost));
        Assert.That(_world.Economy.Money, Is.EqualTo(money - EconomyManager.ZoningCost));
    }

    [Test]
    public void ZoneParcels_InsufficientFunds_RejectedAndLotsUnchanged()
    {
        var parcels = CommandTestWorld.FindEmptyParcelsOnOneSide(_world, 2);
        _world.Economy.SetBalance(EconomyManager.ZoningCost); // enough for one lot only

        var result = new ZoneParcelsCommand(parcels, parcels[0].Side, ZoneType.Residential).Execute(_world);

        Assert.That(result.Status, Is.EqualTo(CommandStatus.Rejected));
        Assert.That(parcels.All(p => p.ZoneType == ZoneType.Empty), Is.True);
    }

    [Test]
    public void DezoneParcels_ZonedLots_ClearsForFree()
    {
        var parcels = CommandTestWorld.FindEmptyParcelsOnOneSide(_world, 2);
        new ZoneParcelsCommand(parcels, parcels[0].Side, ZoneType.Industrial).Execute(_world);
        float money = _world.Economy.Money;

        var result = new DezoneParcelsCommand(parcels, parcels[0].Side).Execute(_world);

        Assert.That(result.IsApplied, Is.True);
        Assert.That(result.Tone, Is.EqualTo(CommandTone.Clearing));
        Assert.That(parcels.All(p => p.ZoneType == ZoneType.Empty), Is.True);
        Assert.That(_world.Economy.Money, Is.EqualTo(money));
    }

    [Test]
    public void DezoneParcels_AlreadyEmpty_ReturnsNoChange()
    {
        var parcels = CommandTestWorld.FindEmptyParcelsOnOneSide(_world, 1);

        var result = new DezoneParcelsCommand(parcels, parcels[0].Side).Execute(_world);

        Assert.That(result.Status, Is.EqualTo(CommandStatus.NoChange));
        Assert.That(_macroRecalculations, Is.EqualTo(0));
    }
}
