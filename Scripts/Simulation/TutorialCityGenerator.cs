using Godot;
using System.Collections.Generic;
using System.Linq;

namespace CitySim.Simulation;

/// <summary>
/// Handcrafts an organic, curved-road tutorial city with Bézier avenues,
/// pre-zoned roadside ribbon parcels, starter population and employment,
/// and an active default public transit line.
/// </summary>
public static class TutorialCityGenerator
{
    /// <summary>
    /// Generates the complete organic tutorial city on the provided simulation managers.
    /// </summary>
    public static void Generate(
        CityGrid grid,
        RoadGraph roadGraph,
        ParcelManager parcelManager,
        TransitManager transitManager,
        EconomyManager economyManager)
    {
        if (grid == null || roadGraph == null) return;

        // 1. Clear previous state
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

        // 2. Setup City Entrances in Grid
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

        // 3. Create Road Nodes
        // Entrance nodes
        var nEntWest = roadGraph.CreateNode(grid.GetWorldCenter(entWestId), entWestId);
        var nEntEast = roadGraph.CreateNode(grid.GetWorldCenter(entEastId), entEastId);
        var nEntNorth = roadGraph.CreateNode(grid.GetWorldCenter(entNorthId), entNorthId);
        var nEntSouth = roadGraph.CreateNode(grid.GetWorldCenter(entSouthId), entSouthId);

        // East-West Central Arterial nodes
        var nWestJunction = roadGraph.CreateNode(new Vector2(340, 672));
        var nDowntownWest = roadGraph.CreateNode(new Vector2(520, 650));
        var nDowntownCenter = roadGraph.CreateNode(new Vector2(672, 672));
        var nDowntownEast = roadGraph.CreateNode(new Vector2(820, 690));
        var nIndHub = roadGraph.CreateNode(new Vector2(1000, 672));

        // North-South Grand Avenue nodes
        var nNorthCivic = roadGraph.CreateNode(new Vector2(672, 380));
        var nSouthCivic = roadGraph.CreateNode(new Vector2(672, 960));

        // West Residential Suburb curve nodes
        var nWestResNorth = roadGraph.CreateNode(new Vector2(240, 440));
        var nWestResMid = roadGraph.CreateNode(new Vector2(380, 320));
        var nWestResSouth = roadGraph.CreateNode(new Vector2(240, 900));
        var nWestResSouthMid = roadGraph.CreateNode(new Vector2(400, 1020));

        // East Industrial Complex curve nodes
        var nIndNorth = roadGraph.CreateNode(new Vector2(1100, 440));
        var nIndNorthMid = roadGraph.CreateNode(new Vector2(960, 320));
        var nIndSouth = roadGraph.CreateNode(new Vector2(1100, 900));
        var nIndSouthMid = roadGraph.CreateNode(new Vector2(940, 1020));

        // 4. Build Curved Road Segments
        // A. East-West Curved Arterial Avenue
        AddCurvedEdge(roadGraph, nEntWest, nWestJunction, new Vector2(218, 650), 2000f);
        AddCurvedEdge(roadGraph, nWestJunction, nDowntownWest, new Vector2(430, 640), 2000f);
        AddCurvedEdge(roadGraph, nDowntownWest, nDowntownCenter, new Vector2(596, 680), 2000f);
        AddCurvedEdge(roadGraph, nDowntownCenter, nDowntownEast, new Vector2(746, 660), 2000f);
        AddCurvedEdge(roadGraph, nDowntownEast, nIndHub, new Vector2(910, 700), 2000f);
        AddCurvedEdge(roadGraph, nIndHub, nEntEast, new Vector2(1092, 650), 2000f);

        // B. North-South Grand Avenue
        AddCurvedEdge(roadGraph, nEntNorth, nNorthCivic, new Vector2(650, 238), 2000f);
        AddCurvedEdge(roadGraph, nNorthCivic, nDowntownCenter, new Vector2(694, 520), 2000f);
        AddCurvedEdge(roadGraph, nDowntownCenter, nSouthCivic, new Vector2(650, 810), 2000f);
        AddCurvedEdge(roadGraph, nSouthCivic, nEntSouth, new Vector2(694, 1072), 2000f);

        // C. West Residential Curving Districts
        AddCurvedEdge(roadGraph, nWestJunction, nWestResNorth, new Vector2(260, 560), 1200f);
        AddCurvedEdge(roadGraph, nWestResNorth, nWestResMid, new Vector2(290, 360), 1200f);
        AddCurvedEdge(roadGraph, nWestResMid, nNorthCivic, new Vector2(520, 330), 1200f);
        AddCurvedEdge(roadGraph, nWestJunction, nWestResSouth, new Vector2(260, 780), 1200f);
        AddCurvedEdge(roadGraph, nWestResSouth, nWestResSouthMid, new Vector2(300, 980), 1200f);
        AddCurvedEdge(roadGraph, nWestResSouthMid, nSouthCivic, new Vector2(530, 1010), 1200f);

        // D. East Industrial Curving Complex
        AddCurvedEdge(roadGraph, nIndHub, nIndNorth, new Vector2(1070, 560), 1400f);
        AddCurvedEdge(roadGraph, nIndNorth, nIndNorthMid, new Vector2(1050, 360), 1400f);
        AddCurvedEdge(roadGraph, nIndNorthMid, nNorthCivic, new Vector2(820, 330), 1400f);
        AddCurvedEdge(roadGraph, nIndHub, nIndSouth, new Vector2(1070, 780), 1400f);
        AddCurvedEdge(roadGraph, nIndSouth, nIndSouthMid, new Vector2(1040, 980), 1400f);
        AddCurvedEdge(roadGraph, nIndSouthMid, nSouthCivic, new Vector2(810, 1010), 1400f);

        // 5. Generate and Zone Roadside Parcels
        if (parcelManager != null)
        {
            parcelManager.RefreshParcels(roadGraph);

            var westResNodeIds = new HashSet<int>
            {
                nWestResNorth.Id, nWestResMid.Id, nWestResSouth.Id, nWestResSouthMid.Id
            };

            var downtownNodeIds = new HashSet<int>
            {
                nDowntownWest.Id, nDowntownCenter.Id, nDowntownEast.Id
            };

            var indNodeIds = new HashSet<int>
            {
                nIndNorth.Id, nIndNorthMid.Id, nIndSouth.Id, nIndSouthMid.Id
            };

            foreach (var parcel in parcelManager.Parcels)
            {
                if (parcel.EdgeId < 0 || parcel.EdgeId >= roadGraph.Edges.Count) continue;
                var edge = roadGraph.Edges[parcel.EdgeId];
                if (edge.FromId == -1 || edge.ToId == -1) continue;

                // West Residential Parcels
                if (westResNodeIds.Contains(edge.FromId) || westResNodeIds.Contains(edge.ToId))
                {
                    parcel.SetZone(ZoneType.Residential, population: 85, residentialCap: 120);
                    parcel.AccessNodeId = westResNodeIds.Contains(edge.FromId) ? edge.FromId : edge.ToId;
                }
                // Downtown Commercial Parcels
                else if (downtownNodeIds.Contains(edge.FromId) || downtownNodeIds.Contains(edge.ToId))
                {
                    parcel.SetZone(ZoneType.Commercial, jobs: 70, commercialCap: 100);
                    parcel.AccessNodeId = downtownNodeIds.Contains(edge.FromId) ? edge.FromId : edge.ToId;
                }
                // East Industrial Parcels
                else if (indNodeIds.Contains(edge.FromId) || indNodeIds.Contains(edge.ToId))
                {
                    parcel.SetZone(ZoneType.Industrial, jobs: 80);
                    parcel.AccessNodeId = indNodeIds.Contains(edge.FromId) ? edge.FromId : edge.ToId;
                }
            }
        }

        // 6. Establish Default Public Transit Line
        if (transitManager != null)
        {
            var stopIds = new List<int>
            {
                nWestResNorth.Id,
                nWestJunction.Id,
                nDowntownCenter.Id,
                nIndHub.Id
            };

            var fullPath = new List<int>();
            for (int i = 0; i < stopIds.Count - 1; i++)
            {
                var leg = roadGraph.GetShortestNodePath(stopIds[i], stopIds[i + 1]);
                if (leg != null)
                {
                    int startIdx = (i == 0) ? 0 : 1;
                    for (int k = startIdx; k < leg.Count; k++)
                    {
                        fullPath.Add(leg[k]);
                    }
                }
            }

            if (fullPath.Count >= 2)
            {
                transitManager.RegisterStop(nWestResNorth.Id, -1, "West Suburb Station");
                transitManager.RegisterStop(nWestJunction.Id, -1, "Parkway Exchange");
                transitManager.RegisterStop(nDowntownCenter.Id, -1, "Downtown Metro Hub");
                transitManager.RegisterStop(nIndHub.Id, -1, "Industrial Works Terminal");

                transitManager.CreateRoute(
                    "Trans-City Metro (Line 1)",
                    fullPath,
                    stopIds,
                    new Color(0.18f, 0.60f, 0.98f),
                    isLoop: false,
                    fleetSize: 4,
                    ticketPrice: 12.0f
                );
            }
        }

        // 7. Economy Balance
        if (economyManager != null)
        {
            economyManager.SetBalance(75000f);
        }
    }

    private static void AddCurvedEdge(RoadGraph graph, RoadNode n1, RoadNode n2, Vector2 apex, float capacity = 1000f)
    {
        var curve = CurveSegment.CreateFromThreePoints(n1.WorldPosition, apex, n2.WorldPosition);
        graph.AddCurvedRoadSegment(n1.Id, n2.Id, curve, capacity);
    }
}
