using Godot;

namespace CitySim.Simulation.Commands;

/// <summary>
/// Builds a (straight or curved) two-way road segment between two existing road nodes,
/// charging a cost proportional to the curve length.
/// </summary>
public sealed class BuildRoadCommand : IWorldCommand
{
    /// <summary>Minimum price of any road segment.</summary>
    public const float MinRoadCost = 50f;

    /// <summary>Curve length that costs exactly <see cref="EconomyManager.RoadSegmentCost"/> (one grid cell).</summary>
    public const float CostReferenceLength = 64f;

    private readonly int _startNodeId;
    private readonly int _endNodeId;
    private readonly CurveSegment _curve;

    public BuildRoadCommand(int startNodeId, int endNodeId, CurveSegment curve)
    {
        _startNodeId = startNodeId;
        _endNodeId = endNodeId;
        _curve = curve;
    }

    /// <summary>Construction cost of a road following <paramref name="curve"/>.</summary>
    public static float CalculateCost(CurveSegment curve) =>
        Mathf.Max(MinRoadCost, Mathf.Round(EconomyManager.RoadSegmentCost * (curve.Length / CostReferenceLength)));

    public CommandResult Execute(SimulationWorld world)
    {
        if (_startNodeId == _endNodeId)
            return CommandResult.Rejected("⚠️ Start and end points must be different.", CommandTone.Warning);

        if (world.RoadGraph.HasEdge(_startNodeId, _endNodeId))
            return CommandResult.Rejected("⚠️ Road already exists between these points.", CommandTone.Warning);

        float cost = CalculateCost(_curve);
        if (!world.Economy.CanAfford(cost))
            return CommandResult.Rejected($"⚠️ Insufficient funds! Costs ${cost:F0}.");

        if (!world.RoadGraph.AddCurvedRoadSegment(_startNodeId, _endNodeId, _curve))
            return CommandResult.Rejected(string.Empty);

        world.Economy.Spend(cost);
        var topology = world.ApplyTopologyChange();
        return CommandResult.Applied(
            "✅ Road built successfully! Click to build another." + CommandMessages.DescribeTransitChanges(topology),
            cost: cost,
            topology: topology);
    }
}
