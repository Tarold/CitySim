using System.Diagnostics;
using System.Linq;
using Godot;
using NUnit.Framework;
using CitySim.Simulation;

namespace CitySim.Tests.Performance;

/// <summary>
/// Timing breakdown of the macro pipeline on a synthetic fully built grid city. Explicit: run with
/// <c>dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~MacroPipelineBenchmark" --logger "console;verbosity=detailed"</c>.
/// </summary>
[TestFixture, Explicit("Performance measurement, not a regression test")]
public class MacroPipelineBenchmark
{
    private const int Repetitions = 3;

    /// <summary>Builds an n×n grid where every cell is zoned and joined to its neighbours, with 2·6 bus lines.</summary>
    internal static (CityGrid Grid, RoadGraph Graph, ParcelManager Parcels, TransitManager Transit) BuildSyntheticCity(int n)
    {
        var grid = new CityGrid(n, n, 64f);
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                var type = x % 4 == 0 ? ZoneType.Commercial : ZoneType.Residential;
                grid.ZoneCell(x, y, type);
                if (type == ZoneType.Residential) grid.GetZone(x, y).Population = 800;
            }
        }

        var graph = new RoadGraph();
        graph.BuildFromGrid(grid);
        var parcels = new ParcelManager();
        parcels.Initialize(graph);

        var transit = new TransitManager();
        for (int r = 0; r < 6; r++)
        {
            int line = (r + 1) * n / 7;
            var rowPath = Enumerable.Range(1, n - 2).Select(x => grid.GetZoneId(x, line)).ToList();
            transit.CreateRoute($"R{r}", rowPath, rowPath.Where((_, i) => i % 4 == 0), Colors.Red, false, 4);
            var colPath = Enumerable.Range(1, n - 2).Select(y => grid.GetZoneId(line, y)).ToList();
            transit.CreateRoute($"C{r}", colPath, colPath.Where((_, i) => i % 4 == 0), Colors.Blue, false, 4);
        }

        return (grid, graph, parcels, transit);
    }

    [TestCase(40)]
    [TestCase(80)]
    public void MacroPipeline_SyntheticGridCity_ReportsStepTimings(int n)
    {
        var (grid, graph, parcels, transit) = BuildSyntheticCity(n);
        var taz = new TazManager();
        var od = new OptimizedODMatrix();
        var engine = new TrafficEngine();
        var sw = new Stopwatch();

        for (int rep = 0; rep < Repetitions; rep++)
        {
            if (rep == 0) taz.MarkDirty();

            sw.Restart();
            bool rebuilt = taz.RefreshOrRebuild(grid, parcels, graph, transit); long tTaz = sw.ElapsedMilliseconds;
            od.Recalculate(taz, graph, 8f, 12f); long tOd = sw.ElapsedMilliseconds;
            engine.AssignFlowsFromTazMsa(taz, od, graph, SimulationWorld.MsaIterations); long tMsa = sw.ElapsedMilliseconds;
            taz.UpdateTransitConnectionMatrix(transit, graph); long tTransit = sw.ElapsedMilliseconds;
            od.UpdateAccessibilityScores(taz, graph); long tAccess = sw.ElapsedMilliseconds;
            transit.AssignPassengerDemandFromTaz(taz, od, graph); long tDemand = sw.ElapsedMilliseconds;

            TestContext.Progress.WriteLine(
                $"n={n} nodes={graph.Nodes.Count} edges={graph.Edges.Count} taz={taz.ActiveZones.Count} | " +
                $"taz({(rebuilt ? "rebuild" : "refresh")})={tTaz} od={tOd - tTaz} msa={tMsa - tOd} transitMatrix={tTransit - tMsa} " +
                $"access={tAccess - tTransit} transitDemand={tDemand - tAccess} total={tDemand}ms");
        }

        var origins = taz.ActiveZones.Select(t => t.PrimaryRoadNodeId).Distinct().ToArray();
        var buffer = new ShortestPathTreeBuffer();
        sw.Restart();
        foreach (int origin in origins) graph.ComputeDijkstraIntoBuffer(origin, buffer);
        TestContext.Progress.WriteLine($"   {origins.Length} RoadGraph searches (sequential): {sw.ElapsedMilliseconds}ms");

        var routing = new RoutingSnapshot();
        sw.Restart();
        routing.Rebuild(graph);
        long build = sw.ElapsedMilliseconds;
        sw.Restart();
        foreach (int origin in origins) routing.ComputeShortestPathTree(origin, buffer);
        TestContext.Progress.WriteLine($"   RoutingSnapshot build {build}ms, {origins.Length} searches (sequential): {sw.ElapsedMilliseconds}ms");
    }
}
