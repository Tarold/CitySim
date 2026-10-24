using System.Collections.Generic;
using System.Linq;
using CitySim.Simulation;

namespace CitySim.Tests.Simulation.Commands;

/// <summary>
/// Helpers that build a ready-to-edit <see cref="SimulationWorld"/> for command tests.
/// </summary>
internal static class CommandTestWorld
{
    public const float AmpleBudget = 1_000_000f;

    /// <summary>Map editing canvas with flows computed and a large budget.</summary>
    public static SimulationWorld Create(ScenarioType scenario = ScenarioType.MapEditingTest)
    {
        var world = SimulationWorld.Create(scenario);
        world.RecalculateMacroModel();
        world.Economy.SetBalance(AmpleBudget);
        return world;
    }

    /// <summary>First grid cell that is empty and has no road node.</summary>
    public static int FindEmptyCellWithoutNode(SimulationWorld world)
    {
        for (int id = 0; id < world.Grid.ZoneCount; id++)
        {
            if (world.Grid.GetZone(id).Type == ZoneType.Empty && world.RoadGraph.GetNode(id) == null)
                return id;
        }
        throw new System.InvalidOperationException("No free cell in test world");
    }

    /// <summary>Up to <paramref name="count"/> empty parcels sharing the same edge and side.</summary>
    public static List<RoadsideParcel> FindEmptyParcelsOnOneSide(SimulationWorld world, int count)
    {
        return world.Parcels.Parcels
            .Where(p => p.ZoneType == ZoneType.Empty)
            .GroupBy(p => (p.EdgeId, p.Side))
            .Where(g => g.Count() >= count)
            .Select(g => g.Take(count).ToList())
            .First();
    }

    public static int ActiveEdgeCount(SimulationWorld world) =>
        world.RoadGraph.Edges.Count(e => e.FromId != -1 && e.ToId != -1);
}
