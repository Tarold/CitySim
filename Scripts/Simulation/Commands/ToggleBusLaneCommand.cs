namespace CitySim.Simulation.Commands;

/// <summary>
/// Toggles a dedicated bus lane on both directions of a road segment and recalculates flows
/// (car capacity drops by one lane while the bus lane is enabled).
/// </summary>
public sealed class ToggleBusLaneCommand : IWorldCommand
{
    private readonly int _edgeId;

    public ToggleBusLaneCommand(int edgeId)
    {
        _edgeId = edgeId;
    }

    public CommandResult Execute(SimulationWorld world)
    {
        var edge = world.RoadGraph.GetEdge(_edgeId);
        if (edge == null || edge.FromId == -1 || edge.ToId == -1)
            return CommandResult.Rejected("⚠️ Клікніть безпосередньо на сегмент дороги, щоб перемкнути виділену смугу ГТ.");

        bool enabled = world.RoadGraph.ToggleBusLaneBidirectional(_edgeId);
        world.RecalculateMacroModel();

        return enabled
            ? CommandResult.Applied(
                $"✅ Виділену смугу ГТ УВІМКНЕНО на дорозі #{edge.Id}! Автобуси ігнорують затори (ємність авто: {edge.Capacity:F0}).")
            : CommandResult.Applied(
                $"🔄 Виділену смугу ГТ ВИМКНЕНО на дорозі #{edge.Id}. Повну ємність для авто відновлено ({edge.Capacity:F0}).",
                CommandTone.Caution);
    }
}
