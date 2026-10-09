using Godot;
using System.Collections.Generic;
using CitySim.Simulation;

namespace CitySim.Rendering;

public partial class VehicleRenderer : Node2D
{
    private TransitManager _transitManager;
    private CarTrafficManager _carManager;
    private PedestrianManager _pedManager;
    private RoadGraph _graph;
    private float _animTime = 0f;

    public bool ShowTransitRoutes { get; set; } = true;

    public void Initialize(TransitManager tm, CarTrafficManager carMgr, PedestrianManager pedMgr, RoadGraph graph)
    {
        _transitManager = tm;
        _carManager = carMgr;
        _pedManager = pedMgr;
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
        if (ShowTransitRoutes && _transitManager != null)
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
        // 2. DRAW ZERO-AGENT FLOW-BASED VEHICLE TRAFFIC ON DRIVING LANES
        // =========================================================================
        for (int eIdx = 0; eIdx < _graph.Edges.Count; eIdx++)
        {
            var edge = _graph.Edges[eIdx];
            if (edge.FromId == -1 || edge.ToId == -1 || edge.CurrentVolume <= 0.05f) continue;

            var fromNode = _graph.GetNode(edge.FromId);
            var toNode = _graph.GetNode(edge.ToId);
            if (fromNode == null || toNode == null) continue;

            Vector2 deltaVec = toNode.WorldPosition - fromNode.WorldPosition;
            float edgeLen = deltaVec.Length();
            if (edgeLen < 2f) continue;

            Vector2 dir = deltaVec / edgeLen;
            Vector2 rightNormal = new Vector2(-dir.Y, dir.X);
            Vector2 laneOffset = rightNormal * 4.5f;

            float volume = edge.CurrentVolume;
            float capacity = Mathf.Max(edge.Capacity, 1f);
            float congestion = edge.GetCongestionRatio();

            // Congestion slowdown factor
            float speedFactor = 1.0f / (1.0f + 0.35f * Mathf.Pow(congestion, 3.0f));
            float streamSpeed = (edge.FreeFlowSpeed / 12f) * speedFactor;

            // Coloring: white when free-flowing -> amber when congested -> red when gridlocked
            Color streamColor;
            if (congestion < 0.50f)
            {
                streamColor = new Color(0.95f, 0.98f, 1.0f, 0.95f);
            }
            else if (congestion < 0.85f)
            {
                float t = (congestion - 0.50f) / 0.35f;
                streamColor = new Color(1.0f, Mathf.Lerp(0.95f, 0.70f, t), 0.20f, 0.95f);
            }
            else
            {
                float t = Mathf.Clamp((congestion - 0.85f) / 0.40f, 0f, 1f);
                streamColor = new Color(1.0f, Mathf.Lerp(0.40f, 0.15f, t), 0.15f, 1.0f);
            }

            // Pulse count / stream density directly reflecting CurrentVolume
            int pulses = Mathf.Clamp(Mathf.RoundToInt(volume / 250f) + 1, 1, 6);
            float dashLen = Mathf.Clamp(4.0f + (volume / capacity) * 4.0f, 3.5f, 8.0f);

            for (int k = 0; k < pulses; k++)
            {
                float phase = ((_animTime * streamSpeed * 0.15f) + ((float)k / pulses)) % 1.0f;
                if (phase < 0f) phase += 1.0f;

                Vector2 center = fromNode.WorldPosition.Lerp(toNode.WorldPosition, phase) + laneOffset;
                Vector2 p1 = center - dir * (dashLen * 0.5f);
                Vector2 p2 = center + dir * (dashLen * 0.5f);

                DrawLine(p1, p2, streamColor, 3.2f, true);
                DrawCircle(p2, 1.8f, streamColor);
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

                // Fullness indicator bar
                float fillRatio = (float)vehicle.Passengers / Mathf.Max(1, vehicle.Capacity);
                Rect2 barBg = new Rect2(pos.X - 8f, pos.Y - 14f, 16f, 3f);
                DrawRect(barBg, new Color(0.1f, 0.1f, 0.1f, 0.8f));
                
                Rect2 barFill = new Rect2(pos.X - 8f, pos.Y - 14f, 16f * fillRatio, 3f);
                Color fillColor = Colors.Green;
                if (fillRatio > 0.85f) fillColor = Colors.Red;
                else if (fillRatio > 0.5f) fillColor = Colors.Yellow;
                DrawRect(barFill, fillColor);
            }
        }

        // =========================================================================
        // 4. DRAW ZERO-AGENT FLOW-BASED PEDESTRIAN TRAFFIC ON SIDEWALKS
        // =========================================================================
        for (int eIdx = 0; eIdx < _graph.Edges.Count; eIdx++)
        {
            var edge = _graph.Edges[eIdx];
            if (edge.FromId == -1 || edge.ToId == -1 || edge.PedestrianVolume <= 0.05f) continue;

            var fromNode = _graph.GetNode(edge.FromId);
            var toNode = _graph.GetNode(edge.ToId);
            if (fromNode == null || toNode == null) continue;

            Vector2 deltaVec = toNode.WorldPosition - fromNode.WorldPosition;
            float edgeLen = deltaVec.Length();
            if (edgeLen < 2f) continue;

            Vector2 dir = deltaVec / edgeLen;
            Vector2 rightNormal = new Vector2(-dir.Y, dir.X);

            float pedVol = edge.PedestrianVolume;
            float pedRatio = edge.GetPedestrianCongestionRatio();
            float walkSpeed = (edge.WalkingSpeed / 10f) * (1.0f - 0.20f * Mathf.Clamp(pedRatio, 0f, 0.7f));

            // Density: directly reflects PedestrianVolume
            int pedDashes = Mathf.Clamp(Mathf.RoundToInt(pedVol / 200f) + 1, 1, 8);
            Color pedColor = new Color(0.40f, 0.95f, 0.65f, 0.90f);
            if (pedRatio > 0.6f) pedColor = new Color(0.35f, 0.85f, 1.0f, 0.95f);

            for (int p = 0; p < pedDashes; p++)
            {
                float phase = ((_animTime * walkSpeed * 0.20f) + ((float)p / pedDashes)) % 1.0f;
                if (phase < 0f) phase += 1.0f;

                // Right sidewalk
                Vector2 pedPosR = fromNode.WorldPosition.Lerp(toNode.WorldPosition, phase) + rightNormal * 7.5f;
                DrawCircle(pedPosR, 1.6f, pedColor);

                // Both sidewalks active if high pedestrian volume
                if (pedVol > 400f)
                {
                    float phaseL = (phase + 0.5f) % 1.0f;
                    Vector2 pedPosL = fromNode.WorldPosition.Lerp(toNode.WorldPosition, phaseL) - rightNormal * 7.5f;
                    DrawCircle(pedPosL, 1.6f, pedColor);
                }
            }
        }
    }
}
