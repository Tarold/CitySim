using Godot;
using System.Collections.Generic;

namespace CitySim.Simulation;

/// <summary>
/// Generates a clean test canvas with reference nodes, curved avenues,
/// and parcel corridors specifically designed for map editing tests.
/// </summary>
public static class MapEditingTestCityGenerator
{
    /// <summary>
    /// Generates the map editing test canvas on the provided simulation managers.
    /// </summary>
    /// <param name="grid">The city grid structure.</param>
    /// <param name="roadGraph">The road graph network.</param>
    /// <param name="parcelManager">The roadside parcel manager.</param>
    /// <param name="transitManager">The public transit manager.</param>
    /// <param name="economyManager">The city economy manager.</param>
    public static void Generate(
        CityGrid grid,
        RoadGraph roadGraph,
        ParcelManager parcelManager,
        TransitManager transitManager,
        EconomyManager economyManager)
    {
        if (grid == null || roadGraph == null) return;

        // 1. Clear previous simulation states
        roadGraph.Nodes.Clear();
        roadGraph.Edges.Clear();
        roadGraph.NodeMap.Clear();
        roadGraph.AdjacencyEdges.Clear();
        roadGraph.PathCache.Clear();
        roadGraph.WalkingPathCache.Clear();

        transitManager?.Routes.Clear();
        transitManager?.Vehicles.Clear();
        transitManager?.Stops.Clear();

        parcelManager?.ClearAllParcels();

        // 2. Setup City Entrances on Grid
        grid.ActiveZoneIds.Clear();
        for (int y = 0; y < grid.Height; y++)
        {
            for (int x = 0; x < grid.Width; x++)
            {
                int id = grid.GetZoneId(x, y);
                grid.Zones[id].Type = ZoneType.Empty;
                grid.Zones[id].Population = 0;
                grid.Zones[id].Jobs = 0;
                grid.Zones[id].ResidentialCap = 0;
                grid.Zones[id].CommercialCap = 0;
            }
        }

        int entWestId = grid.GetZoneId(1, 10);
        int entEastId = grid.GetZoneId(18, 10);
        int entNorthId = grid.GetZoneId(10, 1);
        int entSouthId = grid.GetZoneId(10, 18);

        grid.ZoneCell(entWestId, ZoneType.Entrance);
        grid.ZoneCell(entEastId, ZoneType.Entrance);
        grid.ZoneCell(entNorthId, ZoneType.Entrance);
        grid.ZoneCell(entSouthId, ZoneType.Entrance);

        // 3. Create Reference Nodes
        var nEntWest = roadGraph.CreateNode(grid.GetWorldCenter(entWestId), entWestId);
        var nEntEast = roadGraph.CreateNode(grid.GetWorldCenter(entEastId), entEastId);
        var nEntNorth = roadGraph.CreateNode(grid.GetWorldCenter(entNorthId), entNorthId);
        var nEntSouth = roadGraph.CreateNode(grid.GetWorldCenter(entSouthId), entSouthId);

        var nRefNW = roadGraph.CreateNode(new Vector2(300, 300));
        var nRefNE = roadGraph.CreateNode(new Vector2(700, 300));
        var nRefSW = roadGraph.CreateNode(new Vector2(300, 700));
        var nRefSE = roadGraph.CreateNode(new Vector2(700, 700));
        var nRefCenter = roadGraph.CreateNode(new Vector2(500, 500));
        var nRefEast = roadGraph.CreateNode(new Vector2(950, 500));

        // 4. Construct Reference Roads
        // A. Curved Avenue: NW to NE with northern apex
        AddCurvedEdge(roadGraph, nRefNW, nRefNE, new Vector2(500, 220), 1800f);

        // B. Straight Reference Avenue: SW to SE
        var straightCurve = new CurveSegment(nRefSW.WorldPosition, nRefSE.WorldPosition);
        roadGraph.AddCurvedRoadSegment(nRefSW.Id, nRefSE.Id, straightCurve, 1800f);

        // C. Western Connector: SW to NW
        AddCurvedEdge(roadGraph, nRefSW, nRefNW, new Vector2(260, 500), 1400f);

        // D. Diagonal link to center
        var centerCurve = new CurveSegment(nRefCenter.WorldPosition, nRefEast.WorldPosition);
        roadGraph.AddCurvedRoadSegment(nRefCenter.Id, nRefEast.Id, centerCurve, 1500f);

        // Connect entrances to nearby reference nodes for network continuity
        AddCurvedEdge(roadGraph, nEntWest, nRefSW, new Vector2(200, 680), 1500f);
        AddCurvedEdge(roadGraph, nEntNorth, nRefNE, new Vector2(680, 200), 1500f);
        AddCurvedEdge(roadGraph, nEntEast, nRefEast, new Vector2(1050, 550), 1500f);
        AddCurvedEdge(roadGraph, nEntSouth, nRefSE, new Vector2(680, 950), 1500f);

        // 5. Generate Reference Parcels
        if (parcelManager != null)
        {
            parcelManager.RefreshParcels(roadGraph);

            // Pre-zone a few parcels for test baselines, leaving remaining parcels unzoned
            if (parcelManager.Parcels.Count >= 2)
            {
                parcelManager.Parcels[0].SetZone(ZoneType.Residential, population: 50, residentialCap: 80);
                parcelManager.Parcels[1].SetZone(ZoneType.Commercial, jobs: 40, commercialCap: 60);
            }
        }

        // 6. Economy settings
        if (economyManager != null)
        {
            economyManager.SetBalance(50000f);
        }
    }

    private static void AddCurvedEdge(RoadGraph graph, RoadNode n1, RoadNode n2, Vector2 apex, float capacity = 1000f)
    {
        var curve = CurveSegment.CreateFromThreePoints(n1.WorldPosition, apex, n2.WorldPosition);
        graph.AddCurvedRoadSegment(n1.Id, n2.Id, curve, capacity);
    }
}
