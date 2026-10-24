using System;
using System.Linq;
using NUnit.Framework;
using CitySim.Simulation;

namespace CitySim.Tests.Performance;

/// <summary>
/// Performance budgets for a ~1.2M-resident metropolis (≈1 500 TAZ, ≈37 000 road nodes, 150 bus lines).
/// Explicit: run with
/// <c>dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~MetropolisBudgetBenchmark" --logger "console;verbosity=detailed"</c>.
/// Budgets come from docs/scaling-audit.md; numbers depend on the machine (reference: 16 cores).
/// </summary>
[TestFixture, Explicit("Performance budgets, not a regression test")]
public class MetropolisBudgetBenchmark
{
    /// <summary>Background (worker) part of one periodic macro recalculation.</summary>
    public const double WorkerCycleBudgetMs = 1500;

    /// <summary>Main-thread part of applying a background recalculation.</summary>
    public const double MainThreadApplyBudgetMs = 3;

    /// <summary>Managed memory held by the macro model after warm cycles.</summary>
    public const double MacroMemoryBudgetMb = 150;

    private const int WarmCycles = 3;

    private SimulationWorld _world;
    private double _coldCycleMs;
    private double _warmWorkerMs;
    private double _warmApplyMs;
    private double _macroMemoryMb;

    [OneTimeSetUp]
    public void Measure()
    {
        var started = DateTime.UtcNow;
        _world = SimulationWorld.CreateMetropolis(MetropolisOptions.Million);
        double buildSeconds = (DateTime.UtcNow - started).TotalSeconds;
        long memoryBefore = ManagedMemory();

        _world.RecalculateMacroModel();
        _coldCycleMs = _world.LastMacroComputeMilliseconds;

        _world.UseBackgroundMacroModel = true;
        double worker = 0, apply = 0;
        for (int i = 0; i < WarmCycles; i++)
        {
            _world.RecalculateMacroModel();
            _world.WaitForMacroModel();
            worker += _world.LastMacroComputeMilliseconds;
            apply += _world.LastMacroMainThreadMilliseconds;
        }
        _warmWorkerMs = worker / WarmCycles;
        _warmApplyMs = apply / WarmCycles;
        _macroMemoryMb = (ManagedMemory() - memoryBefore) / (1024.0 * 1024.0);

        TestContext.Progress.WriteLine(
            $"Metropolis: pop={_world.Grid.TotalPopulation():N0} nodes={_world.RoadGraph.Nodes.Count:N0} edges={_world.RoadGraph.Edges.Count:N0} " +
            $"taz={_world.Taz.ActiveZones.Count:N0} lines={_world.Transit.Routes.Count} build={buildSeconds:F1}s");
        TestContext.Progress.WriteLine(
            $"  cold sync cycle={_coldCycleMs:F0}ms | warm worker={_warmWorkerMs:F0}ms (budget {WorkerCycleBudgetMs}) | " +
            $"main-thread apply={_warmApplyMs:F1}ms (budget {MainThreadApplyBudgetMs}) | macro memory={_macroMemoryMb:F0}MB (budget {MacroMemoryBudgetMb})");
    }

    [OneTimeTearDown]
    public void TearDown() => _world?.Dispose();

    private static long ManagedMemory()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return GC.GetTotalMemory(forceFullCollection: true);
    }

    [Test]
    public void WorkerCycle_WithinBudget() => Assert.That(_warmWorkerMs, Is.LessThanOrEqualTo(WorkerCycleBudgetMs));

    [Test]
    public void MainThreadApply_WithinBudget() => Assert.That(_warmApplyMs, Is.LessThanOrEqualTo(MainThreadApplyBudgetMs));

    [Test]
    public void MacroMemory_WithinBudget() => Assert.That(_macroMemoryMb, Is.LessThanOrEqualTo(MacroMemoryBudgetMb));
}
