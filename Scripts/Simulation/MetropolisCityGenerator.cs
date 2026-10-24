using Godot;
using System;
using System.Collections.Generic;

namespace CitySim.Simulation;

/// <summary>
/// Size and shape of a generated metropolis.
/// </summary>
public sealed class MetropolisOptions
{
    /// <summary>Superblocks per side; each superblock is bounded by arterials.</summary>
    public int Superblocks { get; init; } = 24;

    /// <summary>Grid cells per superblock side (arterial spacing).</summary>
    public int CellsPerSuperblock { get; init; } = 8;

    /// <summary>Intermediate shape nodes inserted into every curved local street segment.</summary>
    public int CurveSubdivisions { get; init; } = 2;

    /// <summary>Bus lines to create along arterials.</summary>
    public int TransitLines { get; init; } = 150;

    /// <summary>Target number of residents (distributed with a density gradient).</summary>
    public int TargetPopulation { get; init; } = 1_200_000;

    /// <summary>Jobs per resident.</summary>
    public float JobsPerResident { get; init; } = 0.5f;

    public int Seed { get; init; } = 2026;

    /// <summary>~1.2M residents, ~1 500 TAZ, ~37 000 road nodes, 150 bus lines.</summary>
    public static MetropolisOptions Million => new MetropolisOptions();

    /// <summary>Small variant for fast tests.</summary>
    public static MetropolisOptions Small => new MetropolisOptions
    {
        Superblocks = 4,
        TransitLines = 12,
        TargetPopulation = 60_000
    };

    /// <summary>Grid cells per side needed for these options.</summary>
    public int GridSize => Superblocks * CellsPerSuperblock + 1;
}

/// <summary>
/// Generates a large benchmark city: an arterial grid every superblock, a lattice of curved local streets
/// inside each superblock (split into short segments like edited curved roads), grid-zone land use with a
/// downtown density gradient and bus lines along arterials. Deterministic for a given seed.
/// </summary>
public static class MetropolisCityGenerator
{
    public const float ArterialCapacity = 2200f;
    public const float LocalCapacity = 700f;

    /// <summary>Local lattice lines per superblock side (at cell offsets 2, 4, 6 for 8-cell superblocks).</summary>
    private const int LocalLinesPerSuperblock = 3;

    /// <summary>Maximum sideways bend of a local street segment, as a fraction of its length.</summary>
    private const float MaxCurveBend = 0.18f;

    /// <summary>Stops are placed every this many arterial nodes along a line.</summary>
    private const int StopSpacingNodes = 2;

    private const int MinLineSuperblocks = 4;
    private const int BusFleetSize = 6;

    /// <summary>Share of cells zoned commercial at the very centre (falls off with distance).</summary>
    private const float DowntownCommercialShare = 0.55f;
    private const float SuburbCommercialShare = 0.08f;
    private const float IndustrialBandShare = 0.12f;

    /// <summary>
    /// Populates <paramref name="grid"/> (must be <see cref="MetropolisOptions.GridSize"/> cells per side),
    /// <paramref name="graph"/> and <paramref name="transit"/>.
    /// </summary>
    public static void Generate(CityGrid grid, RoadGraph graph, TransitManager transit, MetropolisOptions options)
    {
        if (grid.Width < options.GridSize || grid.Height < options.GridSize)
            throw new ArgumentException($"Grid must be at least {options.GridSize}x{options.GridSize} cells.");

        var random = new Random(options.Seed);
        var arterialNodes = BuildRoads(grid, graph, options, random);
        ZoneLandUse(grid, options, random);
        BuildTransitLines(graph, transit, arterialNodes, options, random);
    }

    /// <summary>Cells between neighbouring lattice points (local street spacing).</summary>
    private static int LatticeStep(MetropolisOptions options) => options.CellsPerSuperblock / (LocalLinesPerSuperblock + 1);

    /// <summary>Lattice points between neighbouring arterials.</summary>
    private static int ArterialPeriod(MetropolisOptions options) => options.CellsPerSuperblock / LatticeStep(options);

    /// <summary>Builds the street lattice; returns lattice node ids indexed by [x, y].</summary>
    private static int[,] BuildRoads(CityGrid grid, RoadGraph graph, MetropolisOptions options, Random random)
    {
        int step = LatticeStep(options);
        int lattice = options.Superblocks * options.CellsPerSuperblock / step + 1;
        float cell = grid.CellSize;
        var nodeAt = new int[lattice, lattice];

        // Lattice points every `step` cells: arterial points lie on superblock boundaries
        for (int ly = 0; ly < lattice; ly++)
        {
            for (int lx = 0; lx < lattice; lx++)
            {
                var pos = new Vector2(lx * step * cell + cell * 0.5f, ly * step * cell + cell * 0.5f);
                nodeAt[lx, ly] = graph.CreateNode(pos).Id;
            }
        }

        int arterialPeriod = ArterialPeriod(options);
        for (int ly = 0; ly < lattice; ly++)
        {
            for (int lx = 0; lx < lattice; lx++)
            {
                if (lx + 1 < lattice) Connect(graph, nodeAt[lx, ly], nodeAt[lx + 1, ly], ly % arterialPeriod == 0, options, random);
                if (ly + 1 < lattice) Connect(graph, nodeAt[lx, ly], nodeAt[lx, ly + 1], lx % arterialPeriod == 0, options, random);
            }
        }

        return nodeAt;
    }

    private static void Connect(RoadGraph graph, int fromId, int toId, bool arterial, MetropolisOptions options, Random random)
    {
        var a = graph.GetNode(fromId).WorldPosition;
        var b = graph.GetNode(toId).WorldPosition;

        if (arterial || options.CurveSubdivisions <= 0)
        {
            graph.AddCurvedRoadSegment(fromId, toId, new CurveSegment(a, b), arterial ? ArterialCapacity : LocalCapacity);
            return;
        }

        // Curved local street: bend sideways and split into short segments through shape nodes
        Vector2 dir = b - a;
        Vector2 normal = new Vector2(-dir.Y, dir.X).Normalized();
        float bend = (float)(random.NextDouble() * 2.0 - 1.0) * MaxCurveBend * dir.Length();
        Vector2 control = (a + b) * 0.5f + normal * bend;

        int prev = fromId;
        Vector2 prevPos = a;
        int segments = options.CurveSubdivisions + 1;
        for (int k = 1; k <= segments; k++)
        {
            float t = (float)k / segments;
            Vector2 p = QuadraticPoint(a, control, b, t);
            int next = k == segments ? toId : graph.CreateNode(p).Id;
            graph.AddCurvedRoadSegment(prev, next, new CurveSegment(prevPos, p), LocalCapacity);
            prev = next;
            prevPos = p;
        }
    }

    private static Vector2 QuadraticPoint(Vector2 a, Vector2 control, Vector2 b, float t)
    {
        float u = 1f - t;
        return u * u * a + 2f * u * t * control + t * t * b;
    }

    private static void ZoneLandUse(CityGrid grid, MetropolisOptions options, Random random)
    {
        int size = options.GridSize;
        var center = new Vector2(size * 0.5f, size * 0.5f);
        float maxDist = center.Length();
        int industrialBandEnd = (int)(size * IndustrialBandShare);

        // First pass: types and relative density weights
        var weights = new float[size * size];
        var types = new ZoneType[size * size];
        float residentialWeight = 0f;
        float jobWeight = 0f;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int idx = y * size + x;
                float closeness = 1f - new Vector2(x, y).DistanceTo(center) / maxDist;
                float density = 0.25f + closeness * closeness * 3f;
                float commercialShare = Mathf.Lerp(SuburbCommercialShare, DowntownCommercialShare, closeness * closeness);

                ZoneType type;
                if (x < industrialBandEnd) type = ZoneType.Industrial;
                else if (random.NextDouble() < commercialShare) type = ZoneType.Commercial;
                else type = ZoneType.Residential;

                types[idx] = type;
                weights[idx] = density * (0.75f + (float)random.NextDouble() * 0.5f);
                if (type == ZoneType.Residential) residentialWeight += weights[idx];
                else jobWeight += weights[idx];
            }
        }

        float popPerWeight = options.TargetPopulation / Math.Max(residentialWeight, 1f);
        float jobsPerWeight = options.TargetPopulation * options.JobsPerResident / Math.Max(jobWeight, 1f);

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int idx = y * size + x;
                int zoneId = grid.GetZoneId(x, y);
                var type = types[idx];
                if (type == ZoneType.Residential)
                {
                    int pop = Math.Max(1, (int)(weights[idx] * popPerWeight));
                    grid.ZoneCell(zoneId, type, population: pop);
                    grid.Zones[zoneId].Population = pop;
                }
                else
                {
                    int jobs = Math.Max(1, (int)(weights[idx] * jobsPerWeight));
                    grid.ZoneCell(zoneId, type, jobs: jobs, commercialCap: type == ZoneType.Commercial ? jobs : -1);
                }
            }
        }
    }

    private static void BuildTransitLines(RoadGraph graph, TransitManager transit, int[,] nodeAt, MetropolisOptions options, Random random)
    {
        int lattice = nodeAt.GetLength(0);
        int period = ArterialPeriod(options);
        var arterialLines = new List<int>();
        for (int i = 0; i < lattice; i += period) arterialLines.Add(i);

        int minLength = Math.Min(lattice - 1, MinLineSuperblocks * period);
        var colors = new[] { Colors.OrangeRed, Colors.DodgerBlue, Colors.SeaGreen, Colors.Gold, Colors.MediumPurple };

        for (int line = 0; line < options.TransitLines; line++)
        {
            bool horizontal = line % 2 == 0;
            int fixedIndex = arterialLines[random.Next(arterialLines.Count)];
            int length = random.Next(minLength, lattice);
            int start = random.Next(0, lattice - length);

            var path = new List<int>(length + 1);
            var stops = new List<int>();
            for (int k = 0; k <= length; k++)
            {
                int along = start + k;
                int nodeId = horizontal ? nodeAt[along, fixedIndex] : nodeAt[fixedIndex, along];
                path.Add(nodeId);
                if (k % StopSpacingNodes == 0 || k == length) stops.Add(nodeId);
            }

            transit.CreateRoute($"M{line + 1}", path, stops, colors[line % colors.Length], isLoop: false, fleetSize: BusFleetSize);
        }
    }
}
