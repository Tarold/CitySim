using Godot;
using System.Collections.Generic;
using CitySim.Simulation;

namespace CitySim.Rendering;

public partial class VehicleRenderer : Node2D
{
    private TransitManager _transitManager;
    private CarTrafficManager _carManager;
    private RoadGraph _graph;
    private float _animTime = 0f;

    public void Initialize(TransitManager tm, CarTrafficManager carMgr, RoadGraph graph)
    {
        _transitManager = tm;
        _carManager = carMgr;
        _graph = graph;
    }

    public override void _Process(double delta)
    {
        _animTime += (float)delta * 4f;
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_graph == null) return;

        // =========================================================================
        // 1. DRAW TRANSIT ROUTE CORRIDORS (TRACK LINES)
        // =========================================================================
        if (_transitManager != null)
        {
            foreach (var route in _transitManager.Routes)
            {
                Color trackColor = new Color(route.RouteColor.R, route.RouteColor.G, route.RouteColor.B, 0.65f);
                
                // Draw connecting route segments
                for (int i = 0; i < route.PathNodeIds.Count - 1; i++)
                {
                    var n1 = _graph.GetNode(route.PathNodeIds[i]);
                    var n2 = _graph.GetNode(route.PathNodeIds[i + 1]);
                    if (n1 != null && n2 != null)
                    {
                        DrawLine(n1.WorldPosition, n2.WorldPosition, trackColor, 3.0f, true);
                    }
                }

                // If loop route, close the circuit
                if (route.IsLoop && route.PathNodeIds.Count > 2)
                {
                    var last = _graph.GetNode(route.PathNodeIds[route.PathNodeIds.Count - 1]);
                    var first = _graph.GetNode(route.PathNodeIds[0]);
                    if (last != null && first != null)
                    {
                        DrawLine(last.WorldPosition, first.WorldPosition, trackColor, 3.0f, true);
                    }
                }
            }

            // Draw designated bus stops
            foreach (var route in _transitManager.Routes)
            {
                foreach (var stopId in route.StopNodeIds)
                {
                    var node = _graph.GetNode(stopId);
                    if (node == null) continue;

                    Vector2 stopPos = node.WorldPosition;
                    // Stop platform marker
                    DrawRect(new Rect2(stopPos.X - 5.5f, stopPos.Y - 5.5f, 11f, 11f), route.RouteColor);
                    DrawRect(new Rect2(stopPos.X - 5.5f, stopPos.Y - 5.5f, 11f, 11f), Colors.White, false, 1.2f);

                    // Waiting passengers badge
                    if (_transitManager.Stops.TryGetValue(stopId, out var stopData) && stopData.WaitingPassengers > 5f)
                    {
                        DrawCircle(stopPos + new Vector2(6, -6), 2.8f, Colors.Gold);
                    }
                }
            }
        }

        // =========================================================================
        // 2. DRAW VISUAL CAR TRAFFIC
        // =========================================================================
        if (_carManager != null)
        {
            foreach (var car in _carManager.Cars)
            {
                if (car.EdgeId < 0 || car.EdgeId >= _graph.Edges.Count) continue;
                var edge = _graph.Edges[car.EdgeId];
                if (edge.FromId == -1 || edge.ToId == -1) continue;
                var fromNode = _graph.GetNode(edge.FromId);
                var toNode = _graph.GetNode(edge.ToId);
                if (fromNode == null || toNode == null) continue;

                Vector2 deltaVec = toNode.WorldPosition - fromNode.WorldPosition;
                float edgeLen = deltaVec.Length();
                if (edgeLen < 1f) continue;

                Vector2 dir = deltaVec / edgeLen;
                Vector2 rightNormal = new Vector2(-dir.Y, dir.X);

                // Right lane position
                Vector2 carPos = fromNode.WorldPosition.Lerp(toNode.WorldPosition, car.Progress) + rightNormal * 4.5f;

                // Fast crisp car rendering
                Vector2 front = carPos + dir * 3.6f;
                Vector2 back = carPos - dir * 3.6f;

                DrawLine(back, front, car.CarColor, 3.5f, true);

                if (car.IsStopped)
                {
                    DrawCircle(back, 1.3f, Colors.Red); // Braking light
                }
            }
        }

        // =========================================================================
        // 3. DRAW PUBLIC TRANSIT VEHICLES (BUSES)
        // =========================================================================
        if (_transitManager != null)
        {
            foreach (var vehicle in _transitManager.Vehicles)
            {
                var route = _transitManager.Routes.Find(r => r.Id == vehicle.RouteId);
                if (route == null) continue;

                Vector2 pos = vehicle.GetWorldPosition(route, _graph);
                if (pos == Vector2.Zero) continue;

                // Stop / Dwell indicators
                if (vehicle.State == VehicleState.AtStop)
                {
                    // Pulsing green/amber passenger exchange halo
                    float pulse = 8f + Mathf.Sin(_animTime * 2f) * 3f;
                    DrawArc(pos, pulse, 0, Mathf.Tau, 20, new Color(1f, 0.9f, 0.2f, 0.85f), 2.0f, true);
                }
                else if (vehicle.State == VehicleState.TerminusLayover)
                {
                    // Blue rest halo at terminus
                    DrawArc(pos, 10f, 0, Mathf.Tau, 20, new Color(0.3f, 0.7f, 1f, 0.9f), 1.5f, true);
                }

                // Bus body: distinct rounded circle
                DrawCircle(pos, 7.5f, route.RouteColor);
                DrawArc(pos, 7.5f, 0, Mathf.Tau, 20, Colors.White, 1.6f, true);

                // Windshield / roof detail
                DrawCircle(pos, 3f, new Color(0.9f, 0.95f, 1f, 0.9f));

                // Passenger count label
                DrawString(
                    ThemeDB.FallbackFont, 
                    pos + new Vector2(-12, -11), 
                    $"{(int)vehicle.Passengers}/{vehicle.Capacity}", 
                    HorizontalAlignment.Center, 
                    -1, 
                    9, 
                    Colors.White
                );
            }
        }
    }
}
