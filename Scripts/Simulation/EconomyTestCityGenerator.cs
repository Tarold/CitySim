using Godot;
using System.Collections.Generic;

namespace CitySim.Simulation;

/// <summary>
/// Generates a dedicated economic benchmark map with distinct demographic districts
/// (residential suburbs, commercial offices, industrial plants), transit routes,
/// and economy settings to test economic revenue, operating expenses, and tax rate sensitivity.
/// </summary>
public static class EconomyTestCityGenerator
{
    /// <summary>
    /// Generates the complete economic benchmark scenario on the provided simulation managers.
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

        // 3. Create Road Nodes for Districts
        var nEntWest = roadGraph.CreateNode(grid.GetWorldCenter(entWestId), entWestId);
        var nEntEast = roadGraph.CreateNode(grid.GetWorldCenter(entEastId), entEastId);
        var nEntNorth = roadGraph.CreateNode(grid.GetWorldCenter(entNorthId), entNorthId);
        var nEntSouth = roadGraph.CreateNode(grid.GetWorldCenter(entSouthId), entSouthId);

        // District 1: West Residential Suburb
        var nResNorth = roadGraph.CreateNode(new Vector2(260, 420));
        var nResHub = roadGraph.CreateNode(new Vector2(360, 650));
        var nResSouth = roadGraph.CreateNode(new Vector2(260, 880));

        // District 2: Central Commercial / Financial Core
        var nComNorth = roadGraph.CreateNode(new Vector2(672, 420));
        var nComCenter = roadGraph.CreateNode(new Vector2(672, 650));
        var nComSouth = roadGraph.CreateNode(new Vector2(672, 880));

        // District 3: East Industrial Complex
        var nIndNorth = roadGraph.CreateNode(new Vector2(1080, 420));
        var nIndHub = roadGraph.CreateNode(new Vector2(980, 650));
        var nIndSouth = roadGraph.CreateNode(new Vector2(1080, 880));

        // 4. Build Road Network (Arterials and District Connectors)
        // West Suburb internal
        AddCurvedEdge(roadGraph, nEntWest, nResHub, new Vector2(230, 650), 2000f);
        AddCurvedEdge(roadGraph, nResNorth, nResHub, new Vector2(290, 520), 1200f);
        AddCurvedEdge(roadGraph, nResHub, nResSouth, new Vector2(290, 780), 1200f);

        // East Industrial internal
        AddCurvedEdge(roadGraph, nIndHub, nEntEast, new Vector2(1110, 650), 2000f);
        AddCurvedEdge(roadGraph, nIndNorth, nIndHub, new Vector2(1050, 520), 1400f);
        AddCurvedEdge(roadGraph, nIndHub, nIndSouth, new Vector2(1050, 780), 1400f);

        // Central Commercial Spine
        AddCurvedEdge(roadGraph, nEntNorth, nComNorth, new Vector2(672, 230), 2000f);
        AddCurvedEdge(roadGraph, nComNorth, nComCenter, new Vector2(672, 535), 2000f);
        AddCurvedEdge(roadGraph, nComCenter, nComSouth, new Vector2(672, 765), 2000f);
        AddCurvedEdge(roadGraph, nComSouth, nEntSouth, new Vector2(672, 1070), 2000f);

        // East-West Arterial Connections
        AddCurvedEdge(roadGraph, nResHub, nComCenter, new Vector2(516, 640), 2500f);
        AddCurvedEdge(roadGraph, nComCenter, nIndHub, new Vector2(826, 660), 2500f);

        // North and South Bypass Links
        AddCurvedEdge(roadGraph, nResNorth, nComNorth, new Vector2(466, 380), 1500f);
        AddCurvedEdge(roadGraph, nComNorth, nIndNorth, new Vector2(876, 380), 1500f);
        AddCurvedEdge(roadGraph, nResSouth, nComSouth, new Vector2(466, 920), 1500f);
        AddCurvedEdge(roadGraph, nComSouth, nIndSouth, new Vector2(876, 920), 1500f);

        // 5. Generate and Zone Roadside Parcels
        if (parcelManager != null)
        {
            parcelManager.RefreshParcels(roadGraph);

            var resNodeIds = new HashSet<int> { nResNorth.Id, nResHub.Id, nResSouth.Id, nEntWest.Id };
            var comNodeIds = new HashSet<int> { nComNorth.Id, nComCenter.Id, nComSouth.Id };
            var indNodeIds = new HashSet<int> { nIndNorth.Id, nIndHub.Id, nIndSouth.Id, nEntEast.Id };

            foreach (var parcel in parcelManager.Parcels)
            {
                if (parcel.EdgeId < 0 || parcel.EdgeId >= roadGraph.Edges.Count) continue;
                var edge = roadGraph.Edges[parcel.EdgeId];
                if (edge.FromId == -1 || edge.ToId == -1) continue;

                // Residential District
                if (resNodeIds.Contains(edge.FromId) && resNodeIds.Contains(edge.ToId))
                {
                    parcel.SetZone(ZoneType.Residential, population: 120, residentialCap: 150);
                    parcel.AccessNodeId = edge.FromId;
                }
                // Commercial District
                else if (comNodeIds.Contains(edge.FromId) && comNodeIds.Contains(edge.ToId))
                {
                    parcel.SetZone(ZoneType.Commercial, jobs: 90, commercialCap: 120);
                    parcel.AccessNodeId = edge.FromId;
                }
                // Industrial District
                else if (indNodeIds.Contains(edge.FromId) && indNodeIds.Contains(edge.ToId))
                {
                    parcel.SetZone(ZoneType.Industrial, jobs: 100);
                    parcel.AccessNodeId = edge.FromId;
                }
                // Inter-district arterials
                else if (resNodeIds.Contains(edge.FromId) || resNodeIds.Contains(edge.ToId))
                {
                    parcel.SetZone(ZoneType.Residential, population: 80, residentialCap: 100);
                    parcel.AccessNodeId = resNodeIds.Contains(edge.FromId) ? edge.FromId : edge.ToId;
                }
                else if (indNodeIds.Contains(edge.FromId) || indNodeIds.Contains(edge.ToId))
                {
                    parcel.SetZone(ZoneType.Industrial, jobs: 75);
                    parcel.AccessNodeId = indNodeIds.Contains(edge.FromId) ? edge.FromId : edge.ToId;
                }
                else
                {
                    parcel.SetZone(ZoneType.Commercial, jobs: 60, commercialCap: 80);
                    parcel.AccessNodeId = edge.FromId;
                }
            }
        }

        // 6. Setup Public Transit Routes
        if (transitManager != null)
        {
            // Line 1: East-West Arterial Metro (Residential -> Downtown -> Industrial)
            var stopsLine1 = new List<int> { nResHub.Id, nComCenter.Id, nIndHub.Id };
            var pathLine1 = new List<int>();
            for (int i = 0; i < stopsLine1.Count - 1; i++)
            {
                var leg = roadGraph.GetShortestNodePath(stopsLine1[i], stopsLine1[i + 1]);
                if (leg != null)
                {
                    int startIdx = (i == 0) ? 0 : 1;
                    for (int k = startIdx; k < leg.Count; k++) pathLine1.Add(leg[k]);
                }
            }

            if (pathLine1.Count >= 2)
            {
                transitManager.RegisterStop(nResHub.Id, -1, "West Suburb Transit Hub");
                transitManager.RegisterStop(nComCenter.Id, -1, "Financial Center Metro");
                transitManager.RegisterStop(nIndHub.Id, -1, "East Industrial Depot");

                var route1 = transitManager.CreateRoute(
                    "Commuter Rail Express",
                    pathLine1,
                    stopsLine1,
                    new Color(0.2f, 0.7f, 0.3f),
                    isLoop: false,
                    fleetSize: 4,
                    ticketPrice: 15.0f
                );

                // Baseline turnover for benchmark validation
                route1.DailyPassengers = 1200f;
                route1.DailyRevenue = route1.DailyPassengers * route1.TicketPrice;
            }

            // Line 2: North-South Civic Line (Downtown Feeder)
            var stopsLine2 = new List<int> { nComNorth.Id, nComCenter.Id, nComSouth.Id };
            var pathLine2 = new List<int>();
            for (int i = 0; i < stopsLine2.Count - 1; i++)
            {
                var leg = roadGraph.GetShortestNodePath(stopsLine2[i], stopsLine2[i + 1]);
                if (leg != null)
                {
                    int startIdx = (i == 0) ? 0 : 1;
                    for (int k = startIdx; k < leg.Count; k++) pathLine2.Add(leg[k]);
                }
            }

            if (pathLine2.Count >= 2)
            {
                transitManager.RegisterStop(nComNorth.Id, -1, "North Financial Boulevard");
                transitManager.RegisterStop(nComSouth.Id, -1, "South Commerce Terminal");

                var route2 = transitManager.CreateRoute(
                    "Downtown Civic Line",
                    pathLine2,
                    stopsLine2,
                    new Color(0.9f, 0.45f, 0.15f),
                    isLoop: false,
                    fleetSize: 2,
                    ticketPrice: 10.0f
                );

                route2.DailyPassengers = 600f;
                route2.DailyRevenue = route2.DailyPassengers * route2.TicketPrice;
            }
        }

        // 7. Economic Starting Settings
        if (economyManager != null)
        {
            economyManager.SetBalance(100000f);
            economyManager.TaxPerPopulation = 0.08f;
            economyManager.TaxPerJob = 0.12f;
        }
    }

    private static void AddCurvedEdge(RoadGraph graph, RoadNode n1, RoadNode n2, Vector2 apex, float capacity = 1000f)
    {
        var curve = CurveSegment.CreateFromThreePoints(n1.WorldPosition, apex, n2.WorldPosition);
        graph.AddCurvedRoadSegment(n1.Id, n2.Id, curve, capacity);
    }
}
