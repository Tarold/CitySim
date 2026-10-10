using Godot;
using System.Collections.Generic;

namespace CitySim.Simulation;

/// <summary>
/// Generates a routing benchmark map featuring parallel travel corridors
/// (direct arterial road with a bottleneck choke point, a high-capacity bypass curve,
/// and a rapid transit line) to test commuter path choice and modal split shifts.
/// </summary>
public static class RoutingTestCityGenerator
{
    /// <summary>
    /// Generates the complete routing benchmark scenario on the provided simulation managers.
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

        // 3. Create Road Nodes for Parallel Corridors
        var nEntWest = roadGraph.CreateNode(grid.GetWorldCenter(entWestId), entWestId);
        var nEntEast = roadGraph.CreateNode(grid.GetWorldCenter(entEastId), entEastId);
        var nEntNorth = roadGraph.CreateNode(grid.GetWorldCenter(entNorthId), entNorthId);
        var nEntSouth = roadGraph.CreateNode(grid.GetWorldCenter(entSouthId), entSouthId);

        // West Residential District Hub
        var nWestRes = roadGraph.CreateNode(new Vector2(250, 650));
        var nWestSub = roadGraph.CreateNode(new Vector2(200, 500));

        // Short Walk Test Nodes (100 px apart < 400m threshold)
        var nWalkHome = roadGraph.CreateNode(new Vector2(250, 750));
        var nWalkWork = roadGraph.CreateNode(new Vector2(250, 850));

        // East Employment Hub (Commercial & Industrial)
        var nEastEmp = roadGraph.CreateNode(new Vector2(1050, 650));
        var nEastInd = roadGraph.CreateNode(new Vector2(1100, 500));

        // Corridor 1: Direct Arterial nodes with Bottleneck Bridge in middle
        var nMidWest = roadGraph.CreateNode(new Vector2(500, 650));
        var nMidEast = roadGraph.CreateNode(new Vector2(800, 650));

        // Corridor 2: North Bypass Highway Curve node
        var nBypassNorth = roadGraph.CreateNode(new Vector2(650, 260));

        // 4. Build Road Edges
        // Entrances to hubs
        AddCurvedEdge(roadGraph, nEntWest, nWestRes, new Vector2(190, 650), 2000f);
        AddCurvedEdge(roadGraph, nEastEmp, nEntEast, new Vector2(1110, 650), 2000f);
        AddCurvedEdge(roadGraph, nEntNorth, nBypassNorth, new Vector2(650, 180), 2000f);
        AddCurvedEdge(roadGraph, nMidWest, nEntSouth, new Vector2(500, 950), 1500f);

        // West internal
        AddCurvedEdge(roadGraph, nWestSub, nWestRes, new Vector2(210, 580), 1200f);
        AddCurvedEdge(roadGraph, nWestRes, nWalkHome, new Vector2(250, 700), 1000f);
        AddCurvedEdge(roadGraph, nWalkHome, nWalkWork, new Vector2(250, 800), 1000f);

        // East internal
        AddCurvedEdge(roadGraph, nEastEmp, nEastInd, new Vector2(1090, 580), 1200f);

        // Corridor 1: Direct Arterial with Bottleneck Choke Point
        // High capacity approaches (1800) leading into a narrow bottleneck bridge (400)
        AddCurvedEdge(roadGraph, nWestRes, nMidWest, new Vector2(375, 650), 1800f);
        AddCurvedEdge(roadGraph, nMidWest, nMidEast, new Vector2(650, 650), 400f); // Bottleneck choke point!
        AddCurvedEdge(roadGraph, nMidEast, nEastEmp, new Vector2(925, 650), 1800f);

        // Corridor 2: High-Capacity Bypass Curve
        // Longer distance (~1250 px vs 800 px direct) but massive capacity (3000)
        AddCurvedEdge(roadGraph, nWestRes, nBypassNorth, new Vector2(400, 380), 3000f);
        AddCurvedEdge(roadGraph, nBypassNorth, nEastEmp, new Vector2(900, 380), 3000f);

        // 5. Generate and Zone Roadside Parcels
        if (parcelManager != null)
        {
            parcelManager.RefreshParcels(roadGraph);

            var westResNodeIds = new HashSet<int> { nWestRes.Id, nWestSub.Id, nEntWest.Id };
            var eastEmpNodeIds = new HashSet<int> { nEastEmp.Id, nEastInd.Id, nEntEast.Id };

            foreach (var parcel in parcelManager.Parcels)
            {
                if (parcel.EdgeId < 0 || parcel.EdgeId >= roadGraph.Edges.Count) continue;
                var edge = roadGraph.Edges[parcel.EdgeId];
                if (edge.FromId == -1 || edge.ToId == -1) continue;

                // Short walk test corridor
                if ((edge.FromId == nWalkHome.Id && edge.ToId == nWalkWork.Id) ||
                    (edge.FromId == nWalkWork.Id && edge.ToId == nWalkHome.Id))
                {
                    parcel.SetZone(ZoneType.Commercial, jobs: 90, commercialCap: 120);
                    parcel.AccessNodeId = nWalkWork.Id;
                }
                else if ((edge.FromId == nWestRes.Id && edge.ToId == nWalkHome.Id) ||
                         (edge.FromId == nWalkHome.Id && edge.ToId == nWestRes.Id))
                {
                    parcel.SetZone(ZoneType.Residential, population: 100, residentialCap: 130);
                    parcel.AccessNodeId = nWalkHome.Id;
                }
                // West Residential Origins
                else if (westResNodeIds.Contains(edge.FromId) || westResNodeIds.Contains(edge.ToId))
                {
                    parcel.SetZone(ZoneType.Residential, population: 120, residentialCap: 160);
                    parcel.AccessNodeId = westResNodeIds.Contains(edge.FromId) ? edge.FromId : edge.ToId;
                }
                // East Employment Destinations
                else if (eastEmpNodeIds.Contains(edge.FromId) || eastEmpNodeIds.Contains(edge.ToId))
                {
                    parcel.SetZone(ZoneType.Commercial, jobs: 110, commercialCap: 150);
                    parcel.AccessNodeId = eastEmpNodeIds.Contains(edge.FromId) ? edge.FromId : edge.ToId;
                }
            }
        }

        // 6. Setup Corridor 3: Rapid Public Transit Line
        if (transitManager != null)
        {
            transitManager.RegisterStop(nWestRes.Id, -1, "West Parkway Station");
            transitManager.RegisterStop(nMidWest.Id, -1, "Mid-Arterial Interchange");
            transitManager.RegisterStop(nEastEmp.Id, -1, "East Metro Terminal");

            var stopIds = new List<int> { nWestRes.Id, nMidWest.Id, nEastEmp.Id };
            var fullPath = roadGraph.GetShortestNodePath(nWestRes.Id, nEastEmp.Id) ??
                           new List<int> { nWestRes.Id, nMidWest.Id, nMidEast.Id, nEastEmp.Id };

            if (fullPath.Count >= 2)
            {
                var route = transitManager.CreateRoute(
                    "Cross-City Rapid Transit",
                    fullPath,
                    stopIds,
                    new Color(0.1f, 0.5f, 0.95f),
                    isLoop: false,
                    fleetSize: 4,
                    ticketPrice: 8.0f
                );

                route.DailyPassengers = 1500f;
                route.DailyRevenue = route.DailyPassengers * route.TicketPrice;
            }
        }

        // 7. Economy settings
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
