using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CitySim.Simulation;

public enum VehicleState
{
    Moving,
    AtStop,
    TerminusLayover
}

public class TransitStop
{
    public int NodeId;
    public int ZoneId;
    public float WaitingPassengers;
    public string Name;
}

public class TransitRoute
{
    public int Id;
    public string Name;
    public List<int> PathNodeIds = new List<int>(); // Complete sequential road nodes
    public HashSet<int> StopNodeIds = new HashSet<int>(); // Stations where vehicle stops
    public Color RouteColor;
    public float FrequencyMinutes;
    public bool IsLoop;

    // Financial & Operational Turnovers
    public float TicketPrice = 12.0f; // 12 ₴ / $ per ticket
    public float DailyPassengers = 0f;
    public float DailyRevenue = 0f;
    public float DailyOperatingCost = 0f;
    public float NetDailyProfit => DailyRevenue - DailyOperatingCost;
    public float RoundTripTimeMinutes = 35f; // Cycle time
    public int FleetSize = 6;
    public float HeadwayMinutes => FleetSize > 0 ? (RoundTripTimeMinutes / FleetSize) : FrequencyMinutes;
    public float AverageOccupancy = 65f; // % capacity utilized
}

public class TransitVehicle
{
    public int Id;
    public int RouteId;
    public int CurrentPathIndex; // Index in route.PathNodeIds
    public float ProgressToNext;  // 0.0 to 1.0
    public float Passengers;
    public int Capacity = 75;
    public bool Forward = true;
    public VehicleState State = VehicleState.Moving;
    public float StateTimer = 0f;

    public Vector2 GetWorldPosition(TransitRoute route, RoadGraph graph)
    {
        if (route.PathNodeIds.Count == 0) return Vector2.Zero;
        if (route.PathNodeIds.Count == 1)
        {
            var single = graph.GetNode(route.PathNodeIds[0]);
            return single?.WorldPosition ?? Vector2.Zero;
        }

        int currIdx = Mathf.Clamp(CurrentPathIndex, 0, route.PathNodeIds.Count - 1);
        int nextIdx;

        if (route.IsLoop)
        {
            nextIdx = (currIdx + 1) % route.PathNodeIds.Count;
        }
        else
        {
            nextIdx = Forward ? currIdx + 1 : currIdx - 1;
            nextIdx = Mathf.Clamp(nextIdx, 0, route.PathNodeIds.Count - 1);
        }

        var node1 = graph.GetNode(route.PathNodeIds[currIdx]);
        var node2 = graph.GetNode(route.PathNodeIds[nextIdx]);

        if (node1 == null || node2 == null) return node1?.WorldPosition ?? Vector2.Zero;

        Vector2 basePos = node1.WorldPosition.Lerp(node2.WorldPosition, ProgressToNext);

        // Right-hand traffic lane offset
        Vector2 dir = (node2.WorldPosition - node1.WorldPosition).Normalized();
        if (dir != Vector2.Zero)
        {
            Vector2 rightNormal = new Vector2(-dir.Y, dir.X);
            basePos += rightNormal * 5.0f;
        }

        return basePos;
    }
}

public class TransitManager
{
    public List<TransitRoute> Routes = new List<TransitRoute>();
    public List<TransitVehicle> Vehicles = new List<TransitVehicle>();
    public Dictionary<int, TransitStop> Stops = new Dictionary<int, TransitStop>();

    private Random _random = new Random(123);

    public void CreateDefaultRoutes(CityGrid grid, RoadGraph graph)
    {
        Routes.Clear();
        Vehicles.Clear();
        Stops.Clear();

        int routeIdCounter = 0;
        int vehicleIdCounter = 0;

        // =========================================================================
        // ROUTE 1: Trans-City Trunk (Blue Line) - Connects West Homes to East Factories
        // =========================================================================
        var r1 = new TransitRoute
        {
            Id = routeIdCounter++,
            Name = "Trans-City Express",
            RouteColor = new Color(0.20f, 0.55f, 0.98f),
            FrequencyMinutes = 3.5f,
            IsLoop = false
        };

        // Complete consecutive road nodes along row 10 (from West x=2 to East x=17)
        for (int x = 2; x <= 17; x++)
        {
            int id = grid.GetZoneId(x, 10);
            if (graph.NodeMap.ContainsKey(id))
            {
                r1.PathNodeIds.Add(id);
                // Major interchange stops
                if (x == 2 || x == 5 || x == 8 || x == 10 || x == 13 || x == 17)
                {
                    r1.StopNodeIds.Add(id);
                    RegisterStop(id, id, $"Trans-City Hub ({x}, 10)");
                }
            }
        }

        if (r1.PathNodeIds.Count > 1)
        {
            r1.FleetSize = 6;
            r1.RoundTripTimeMinutes = 36f;
            r1.DailyPassengers = 48500f;
            r1.DailyRevenue = 48500f * r1.TicketPrice;
            r1.DailyOperatingCost = 210000f;
            r1.AverageOccupancy = 72f;

            Routes.Add(r1);
            DeployLineVehicles(r1, 6, ref vehicleIdCounter);
        }

        // =========================================================================
        // ROUTE 2: Residential Feeder (Red Line) - North-South through West Suburbs
        // =========================================================================
        var r2 = new TransitRoute
        {
            Id = routeIdCounter++,
            Name = "West Suburb Feeder",
            RouteColor = new Color(0.95f, 0.22f, 0.22f),
            FrequencyMinutes = 4f,
            IsLoop = false
        };

        // Complete consecutive road nodes along column 5 (from y=2 to y=17)
        for (int y = 2; y <= 17; y++)
        {
            int id = grid.GetZoneId(5, y);
            if (graph.NodeMap.ContainsKey(id))
            {
                r2.PathNodeIds.Add(id);
                if (y == 2 || y == 5 || y == 10 || y == 14 || y == 17)
                {
                    r2.StopNodeIds.Add(id);
                    RegisterStop(id, id, $"Residential Stop (5, {y})");
                }
            }
        }

        if (r2.PathNodeIds.Count > 1)
        {
            r2.FleetSize = 4;
            r2.RoundTripTimeMinutes = 30f;
            r2.DailyPassengers = 29000f;
            r2.DailyRevenue = 29000f * r2.TicketPrice;
            r2.DailyOperatingCost = 135000f;
            r2.AverageOccupancy = 64f;

            Routes.Add(r2);
            DeployLineVehicles(r2, 4, ref vehicleIdCounter);
        }

        // =========================================================================
        // ROUTE 3: Industrial & Downtown Ring (Green Line) - Factory-Office Loop
        // =========================================================================
        var r3 = new TransitRoute
        {
            Id = routeIdCounter++,
            Name = "Industrial-Downtown Ring",
            RouteColor = new Color(0.18f, 0.88f, 0.38f),
            FrequencyMinutes = 5f,
            IsLoop = true
        };

        // Top edge: (8,6) to (15,6)
        for (int x = 8; x <= 15; x++) AddPathNode(r3, x, 6, grid, graph);
        // Right edge: (15,7) to (15,14)
        for (int y = 7; y <= 14; y++) AddPathNode(r3, 15, y, grid, graph);
        // Bottom edge: (14,14) down to (8,14)
        for (int x = 14; x >= 8; x--) AddPathNode(r3, x, 14, grid, graph);
        // Left edge: (8,13) up to (8,7)
        for (int y = 13; y >= 7; y--) AddPathNode(r3, 8, y, grid, graph);

        int[] ringStopCoords = new int[]
        {
            grid.GetZoneId(8, 6), grid.GetZoneId(12, 6), grid.GetZoneId(15, 6),
            grid.GetZoneId(15, 10), grid.GetZoneId(15, 14), grid.GetZoneId(11, 14),
            grid.GetZoneId(8, 14), grid.GetZoneId(8, 10)
        };

        foreach (var sid in ringStopCoords)
        {
            if (r3.PathNodeIds.Contains(sid))
            {
                r3.StopNodeIds.Add(sid);
                RegisterStop(sid, sid, "Factory-Office Station");
            }
        }

        if (r3.PathNodeIds.Count > 3)
        {
            r3.FleetSize = 6;
            r3.RoundTripTimeMinutes = 42f;
            r3.DailyPassengers = 39000f;
            r3.DailyRevenue = 39000f * r3.TicketPrice;
            r3.DailyOperatingCost = 180000f;
            r3.AverageOccupancy = 68f;

            Routes.Add(r3);
            DeployLoopVehicles(r3, 6, ref vehicleIdCounter);
        }
    }

    private void AddPathNode(TransitRoute route, int x, int y, CityGrid grid, RoadGraph graph)
    {
        int id = grid.GetZoneId(x, y);
        if (graph.NodeMap.ContainsKey(id))
        {
            route.PathNodeIds.Add(id);
        }
    }

    private void RegisterStop(int nodeId, int zoneId, string name)
    {
        if (!Stops.ContainsKey(nodeId))
        {
            Stops[nodeId] = new TransitStop
            {
                NodeId = nodeId,
                ZoneId = zoneId,
                WaitingPassengers = 15f + (float)_random.NextDouble() * 20f,
                Name = name
            };
        }
    }

    private void DeployLineVehicles(TransitRoute route, int count, ref int vehicleIdCounter)
    {
        int totalNodes = route.PathNodeIds.Count;
        for (int i = 0; i < count; i++)
        {
            // Evenly space vehicles along the line:
            int startIdx = (totalNodes > 1) ? (i * (totalNodes - 1) / Mathf.Max(count - 1, 1)) : 0;
            startIdx = Mathf.Clamp(startIdx, 0, totalNodes - 1);
            bool fwd = (i % 2 == 0); // Half forward, half backward

            // Terminus edge safety
            if (startIdx == 0) fwd = true;
            if (startIdx == totalNodes - 1) fwd = false;

            Vehicles.Add(new TransitVehicle
            {
                Id = vehicleIdCounter++,
                RouteId = route.Id,
                CurrentPathIndex = startIdx,
                ProgressToNext = 0.05f,
                Passengers = 20f + _random.Next(20),
                Capacity = 75,
                Forward = fwd,
                State = VehicleState.Moving,
                StateTimer = 0f
            });
        }
    }

    private void DeployLoopVehicles(TransitRoute route, int count, ref int vehicleIdCounter)
    {
        int totalNodes = route.PathNodeIds.Count;
        float spacing = (float)totalNodes / count;

        for (int i = 0; i < count; i++)
        {
            int idx = Mathf.FloorToInt(i * spacing) % totalNodes;
            Vehicles.Add(new TransitVehicle
            {
                Id = vehicleIdCounter++,
                RouteId = route.Id,
                CurrentPathIndex = idx,
                ProgressToNext = 0.05f,
                Passengers = 20f + _random.Next(25),
                Capacity = 75,
                Forward = true,
                State = VehicleState.Moving,
                StateTimer = 0f
            });
        }
    }

    public void Update(float delta, float gameSpeedMultiplier, RoadGraph graph, TrafficLightManager trafficLights)
    {
        float speedMult = Mathf.Clamp(gameSpeedMultiplier, 0.2f, 15.0f);

        // Periodically refresh passenger demand at stops
        foreach (var stop in Stops.Values)
        {
            stop.WaitingPassengers = Mathf.Min(stop.WaitingPassengers + delta * speedMult * 0.4f, 80f);
        }

        foreach (var vehicle in Vehicles)
        {
            var route = Routes.FirstOrDefault(r => r.Id == vehicle.RouteId);
            if (route == null || route.PathNodeIds.Count < 2) continue;

            // Safe clamping
            vehicle.CurrentPathIndex = Mathf.Clamp(vehicle.CurrentPathIndex, 0, route.PathNodeIds.Count - 1);

            // Handle dwell / stop states
            if (vehicle.State == VehicleState.AtStop)
            {
                vehicle.StateTimer -= delta * speedMult;
                if (vehicle.StateTimer <= 0f)
                {
                    vehicle.State = VehicleState.Moving;
                }
                continue; // Do not move while dwelling at stop
            }
            else if (vehicle.State == VehicleState.TerminusLayover)
            {
                vehicle.StateTimer -= delta * speedMult;
                if (vehicle.StateTimer <= 0f)
                {
                    // Cleanly resume service in reversed direction
                    vehicle.State = VehicleState.Moving;
                }
                continue; // Vehicle rests at terminus
            }

            // Normal moving state:
            if (!route.IsLoop)
            {
                if (vehicle.CurrentPathIndex <= 0) vehicle.Forward = true;
                else if (vehicle.CurrentPathIndex >= route.PathNodeIds.Count - 1) vehicle.Forward = false;
            }

            int currNodeId = route.PathNodeIds[vehicle.CurrentPathIndex];
            int nextIdx = route.IsLoop 
                ? (vehicle.CurrentPathIndex + 1) % route.PathNodeIds.Count
                : (vehicle.Forward ? vehicle.CurrentPathIndex + 1 : vehicle.CurrentPathIndex - 1);

            nextIdx = Mathf.Clamp(nextIdx, 0, route.PathNodeIds.Count - 1);
            int nextNodeId = route.PathNodeIds[nextIdx];

            // Check traffic light when approaching intersection
            if (vehicle.ProgressToNext > 0.82f)
            {
                var currNode = graph.GetNode(currNodeId);
                if (currNode != null && !trafficLights.IsGreen(nextNodeId, currNode.WorldPosition))
                {
                    // Wait at red traffic light
                    vehicle.ProgressToNext = 0.85f;
                    continue;
                }
            }

            // Advance vehicle along the segment
            float moveSpeed = 0.55f * speedMult;
            vehicle.ProgressToNext += delta * moveSpeed;

            // Reached next node:
            if (vehicle.ProgressToNext >= 1.0f)
            {
                vehicle.ProgressToNext = 0f;
                vehicle.CurrentPathIndex = nextIdx;
                int arrivedNodeId = route.PathNodeIds[vehicle.CurrentPathIndex];

                // 1. Check if arrived at LINE TERMINUS (end of route)
                if (!route.IsLoop && (vehicle.CurrentPathIndex <= 0 || vehicle.CurrentPathIndex >= route.PathNodeIds.Count - 1))
                {
                    // REALISTIC TRANSIT: At terminus, ALL passengers disembark!
                    vehicle.Passengers = 0;
                    vehicle.State = VehicleState.TerminusLayover;
                    vehicle.StateTimer = 4.0f; // Layover dwell before return trip
                    vehicle.Forward = (vehicle.CurrentPathIndex <= 0); // Reverse direction cleanly
                    continue;
                }

                // 2. Check if arrived at a DESIGNATED BUS STOP
                if (route.StopNodeIds.Contains(arrivedNodeId))
                {
                    vehicle.State = VehicleState.AtStop;
                    vehicle.StateTimer = 2.5f; // Stop dwell time

                    // Passengers alight (20% to 40%)
                    int alighting = Mathf.RoundToInt(vehicle.Passengers * (0.20f + (float)_random.NextDouble() * 0.20f));
                    vehicle.Passengers = Mathf.Max(0, vehicle.Passengers - alighting);

                    // Passengers board from waiting stop queue
                    if (Stops.TryGetValue(arrivedNodeId, out var stop))
                    {
                        int boarding = Mathf.Min(Mathf.RoundToInt(stop.WaitingPassengers), vehicle.Capacity - (int)vehicle.Passengers);
                        if (boarding > 0)
                        {
                            vehicle.Passengers += boarding;
                            stop.WaitingPassengers = Mathf.Max(0, stop.WaitingPassengers - boarding);

                            // Real-time financial turnover tracking
                            route.DailyPassengers += boarding * 6f;
                            route.DailyRevenue += (boarding * 6f) * route.TicketPrice;
                        }
                    }
                }
            }

            // Real-time operating expenses (fuel, driver salary, vehicle maintenance)
            route.DailyOperatingCost += delta * speedMult * 6.5f;
            route.AverageOccupancy = Mathf.Lerp(route.AverageOccupancy, (vehicle.Passengers / vehicle.Capacity) * 100f, 0.02f);
        }
    }

    public float GetTransitCoverage(CityGrid grid)
    {
        int residentialZones = 0;
        int coveredResidentialZones = 0;

        for (int i = 0; i < grid.ZoneCount; i++)
        {
            var zone = grid.GetZone(i);
            if (zone.Type == ZoneType.Residential)
            {
                residentialZones++;
                bool isCovered = false;

                foreach (var stop in Stops.Values)
                {
                    var stopZone = grid.GetZone(stop.ZoneId);
                    if (stopZone == null) continue;

                    int dist = Mathf.Abs(zone.GridPos.X - stopZone.GridPos.X) + Mathf.Abs(zone.GridPos.Y - stopZone.GridPos.Y);
                    if (dist <= 3)
                    {
                        isCovered = true;
                        break;
                    }
                }

                if (isCovered) coveredResidentialZones++;
            }
        }

        if (residentialZones == 0) return 0f;
        return (float)coveredResidentialZones / residentialZones;
    }
}
