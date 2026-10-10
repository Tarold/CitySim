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
    private ParcelManager _parcelManager;
    private float _animTime = 0f;

    public bool ShowTransitRoutes { get; set; } = true;

    public void Initialize(TransitManager tm, CarTrafficManager carMgr, PedestrianManager pedMgr, RoadGraph graph, ParcelManager parcelMgr = null)
    {
        _transitManager = tm;
        _carManager = carMgr;
        _pedManager = pedMgr;
        _graph = graph;
        _parcelManager = parcelMgr;
    }

    public void SetParcelManager(ParcelManager parcelMgr)
    {
        _parcelManager = parcelMgr;
    }

    public override void _Process(double delta)
    {
        _animTime += (float)delta * 4f;
        QueueRedraw();
    }

    private void DrawRotatedRect(Vector2 center, Vector2 tangent, float length, float width, Color color)
    {
        Vector2 normal = new Vector2(-tangent.Y, tangent.X);
        float halfL = length * 0.5f;
        float halfW = width * 0.5f;

        Vector2 p1 = center + tangent * halfL + normal * halfW;
        Vector2 p2 = center + tangent * halfL - normal * halfW;
        Vector2 p3 = center - tangent * halfL - normal * halfW;
        Vector2 p4 = center - tangent * halfL + normal * halfW;

        DrawColoredPolygon(new Vector2[] { p1, p2, p3, p4 }, color);
    }

    public override void _Draw()
    {
        if (_graph == null) return;

        // =========================================================================
        // 1. DRAW TRANSIT ROUTE CORRIDORS (CURVED TRACK LINES & SHOULDER STOPS)
        // =========================================================================
        if (ShowTransitRoutes && _transitManager != null)
        {
            foreach (var route in _transitManager.Routes)
            {
                Color trackColor = new Color(route.RouteColor.R, route.RouteColor.G, route.RouteColor.B, 0.65f);
                
                // Draw connecting route segments along curved geometry
                for (int i = 0; i < route.PathNodeIds.Count - 1; i++)
                {
                    int id1 = route.PathNodeIds[i];
                    int id2 = route.PathNodeIds[i + 1];
                    var n1 = _graph.GetNode(id1);
                    var n2 = _graph.GetNode(id2);
                    if (n1 == null || n2 == null) continue;

                    int edgeId = _graph.FindEdgeId(id1, id2);
                    var edge = (edgeId >= 0 && edgeId < _graph.Edges.Count) ? _graph.Edges[edgeId] : null;

                    if (edge != null && edge.Curve != null)
                    {
                        DrawPolyline(edge.Curve.GetSampledPoints(16), trackColor, 3.0f, true);
                    }
                    else
                    {
                        DrawLine(n1.WorldPosition, n2.WorldPosition, trackColor, 3.0f, true);
                    }
                }

                // If loop route, close the circuit
                if (route.IsLoop && route.PathNodeIds.Count > 2)
                {
                    int lastId = route.PathNodeIds[route.PathNodeIds.Count - 1];
                    int firstId = route.PathNodeIds[0];
                    var last = _graph.GetNode(lastId);
                    var first = _graph.GetNode(firstId);
                    if (last != null && first != null)
                    {
                        int loopEdgeId = _graph.FindEdgeId(lastId, firstId);
                        var loopEdge = (loopEdgeId >= 0 && loopEdgeId < _graph.Edges.Count) ? _graph.Edges[loopEdgeId] : null;

                        if (loopEdge != null && loopEdge.Curve != null)
                        {
                            DrawPolyline(loopEdge.Curve.GetSampledPoints(16), trackColor, 3.0f, true);
                        }
                        else
                        {
                            DrawLine(last.WorldPosition, first.WorldPosition, trackColor, 3.0f, true);
                        }
                    }
                }
            }

            // Draw designated bus stops positioned along outer road shoulders using curve normal offsets
            foreach (var route in _transitManager.Routes)
            {
                foreach (var stopId in route.StopNodeIds)
                {
                    var node = _graph.GetNode(stopId);
                    if (node == null) continue;

                    Vector2 stopPos = TransitManager.GetStopWorldPosition(stopId, route, _graph);

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
        // 2. DRAW VEHICLE TRAFFIC (FLOW STREAMS & DISCRETE ROTATED CAR BODIES)
        // =========================================================================
        for (int eIdx = 0; eIdx < _graph.Edges.Count; eIdx++)
        {
            var edge = _graph.Edges[eIdx];
            if (edge.FromId == -1 || edge.ToId == -1 || edge.CurrentVolume <= 0.05f) continue;

            var fromNode = _graph.GetNode(edge.FromId);
            var toNode = _graph.GetNode(edge.ToId);
            if (fromNode == null || toNode == null) continue;

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

            // Determine active traffic extent [0, T_max] on dead-end roads
            float tMax = _graph.GetEdgeTrafficExtent(edge, _parcelManager);

            // Pulse count / stream density directly reflecting CurrentVolume
            int pulses = Mathf.Clamp(Mathf.RoundToInt(volume / 250f) + 1, 1, 6);
            float dashLen = Mathf.Clamp(4.0f + (volume / capacity) * 4.0f, 3.5f, 8.0f);

            for (int k = 0; k < pulses; k++)
            {
                float phase = ((_animTime * streamSpeed * 0.15f) + ((float)k / pulses)) % 1.0f;
                if (phase < 0f) phase += 1.0f;

                float tAlongEdge = phase * tMax;

                Vector2 center, tangent;
                if (edge.Curve != null)
                {
                    center = edge.Curve.Evaluate(tAlongEdge) + edge.Curve.GetNormal(tAlongEdge) * 4.5f;
                    tangent = edge.Curve.GetTangent(tAlongEdge);
                }
                else
                {
                    Vector2 dir = (toNode.WorldPosition - fromNode.WorldPosition).Normalized();
                    Vector2 normal = new Vector2(-dir.Y, dir.X);
                    center = fromNode.WorldPosition.Lerp(toNode.WorldPosition, tAlongEdge) + normal * 4.5f;
                    tangent = dir;
                }

                Vector2 p1 = center - tangent * (dashLen * 0.5f);
                Vector2 p2 = center + tangent * (dashLen * 0.5f);

                DrawLine(p1, p2, streamColor, 3.2f, true);
                DrawCircle(p2, 1.8f, streamColor);
            }
        }

        // Draw discrete visual cars with smooth curve positions and tangent rotations
        if (_carManager != null && _carManager.Cars.Count > 0)
        {
            foreach (var car in _carManager.Cars)
            {
                if (car.EdgeId < 0 || car.EdgeId >= _graph.Edges.Count) continue;
                var edge = _graph.Edges[car.EdgeId];
                if (edge.FromId == -1 || edge.ToId == -1 || !CarTrafficManager.HasDemand(edge)) continue;

                Vector2 pos = car.GetWorldPosition(_graph);
                float rot = car.GetRotation(_graph);
                Vector2 tangent = new Vector2(Mathf.Cos(rot), Mathf.Sin(rot));
                Vector2 normal = new Vector2(-tangent.Y, tangent.X);

                // Vehicle body: rotated rectangle aligned with curve tangent
                DrawRotatedRect(pos, tangent, 7.5f, 3.8f, car.CarColor);

                // Windshield
                DrawRotatedRect(pos + tangent * 0.8f, tangent, 2.0f, 3.0f, new Color(0.15f, 0.20f, 0.25f, 0.85f));

                // Front headlights
                Vector2 headL = pos + tangent * 3.5f - normal * 1.3f;
                Vector2 headR = pos + tangent * 3.5f + normal * 1.3f;
                DrawCircle(headL, 0.85f, Colors.White);
                DrawCircle(headR, 0.85f, Colors.White);

                // Rear taillights
                Vector2 tailL = pos - tangent * 3.5f - normal * 1.3f;
                Vector2 tailR = pos - tangent * 3.5f + normal * 1.3f;
                Color tailColor = car.IsStopped ? Colors.Red : new Color(0.85f, 0.15f, 0.15f, 0.85f);
                DrawCircle(tailL, 0.85f, tailColor);
                DrawCircle(tailR, 0.85f, tailColor);
            }
        }

        // =========================================================================
        // 3. DRAW PUBLIC TRANSIT VEHICLES (ROTATED BUSES ALONG CURVED SPLINES)
        // =========================================================================
        if (_transitManager != null)
        {
            foreach (var vehicle in _transitManager.Vehicles)
            {
                var route = _transitManager.Routes.Find(r => r.Id == vehicle.RouteId);
                if (route == null) continue;

                Vector2 pos = vehicle.GetWorldPosition(route, _graph);
                if (pos == Vector2.Zero) continue;

                float rot = vehicle.GetRotation(route, _graph);
                Vector2 tangent = new Vector2(Mathf.Cos(rot), Mathf.Sin(rot));
                Vector2 normal = new Vector2(-tangent.Y, tangent.X);

                // Stop / Dwell indicators
                if (vehicle.State == VehicleState.AtStop)
                {
                    // Pulsing green/amber passenger exchange halo
                    float pulse = 9f + Mathf.Sin(_animTime * 2f) * 3f;
                    DrawArc(pos, pulse, 0, Mathf.Tau, 20, new Color(1f, 0.9f, 0.2f, 0.85f), 2.0f, true);
                }
                else if (vehicle.State == VehicleState.TerminusLayover)
                {
                    // Blue rest halo at terminus
                    DrawArc(pos, 11f, 0, Mathf.Tau, 20, new Color(0.3f, 0.7f, 1f, 0.9f), 1.6f, true);
                }

                // Rotated Bus body: elongated rectangle with route color
                DrawRotatedRect(pos, tangent, 14.5f, 7.2f, route.RouteColor);

                // Front windshield
                DrawRotatedRect(pos + tangent * 4.6f, tangent, 2.6f, 5.8f, new Color(0.85f, 0.95f, 1f, 0.9f));

                // Roof detail
                DrawRotatedRect(pos - tangent * 0.5f, tangent, 4.0f, 3.2f, new Color(1f, 1f, 1f, 0.35f));

                // Front headlights
                DrawCircle(pos + tangent * 6.8f - normal * 2.4f, 1.1f, Colors.White);
                DrawCircle(pos + tangent * 6.8f + normal * 2.4f, 1.1f, Colors.White);

                // Rear taillights
                DrawCircle(pos - tangent * 6.8f - normal * 2.4f, 1.1f, Colors.Red);
                DrawCircle(pos - tangent * 6.8f + normal * 2.4f, 1.1f, Colors.Red);

                // Passenger count label
                DrawString(
                    ThemeDB.FallbackFont, 
                    pos + new Vector2(-12, -12), 
                    $"{(int)vehicle.Passengers}/{vehicle.Capacity}", 
                    HorizontalAlignment.Center, 
                    -1, 
                    9, 
                    Colors.White
                );

                // Fullness indicator bar
                float fillRatio = (float)vehicle.Passengers / Mathf.Max(1, vehicle.Capacity);
                Rect2 barBg = new Rect2(pos.X - 8f, pos.Y - 15f, 16f, 3f);
                DrawRect(barBg, new Color(0.1f, 0.1f, 0.1f, 0.8f));
                
                Rect2 barFill = new Rect2(pos.X - 8f, pos.Y - 15f, 16f * fillRatio, 3f);
                Color fillColor = Colors.Green;
                if (fillRatio > 0.85f) fillColor = Colors.Red;
                else if (fillRatio > 0.5f) fillColor = Colors.Yellow;
                DrawRect(barFill, fillColor);
            }
        }

        // =========================================================================
        // 4. DRAW PEDESTRIAN TRAFFIC (FLOW ON CURVED SIDEWALKS & DISCRETE WALKERS)
        // =========================================================================
        for (int eIdx = 0; eIdx < _graph.Edges.Count; eIdx++)
        {
            var edge = _graph.Edges[eIdx];
            if (edge.FromId == -1 || edge.ToId == -1 || edge.PedestrianVolume <= 0.05f) continue;

            var fromNode = _graph.GetNode(edge.FromId);
            var toNode = _graph.GetNode(edge.ToId);
            if (fromNode == null || toNode == null) continue;

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
                Vector2 pedPosR, tangentR;
                if (edge.Curve != null)
                {
                    pedPosR = edge.Curve.Evaluate(phase) + edge.Curve.GetNormal(phase) * 7.5f;
                    tangentR = edge.Curve.GetTangent(phase);
                }
                else
                {
                    Vector2 dir = (toNode.WorldPosition - fromNode.WorldPosition).Normalized();
                    Vector2 normal = new Vector2(-dir.Y, dir.X);
                    pedPosR = fromNode.WorldPosition.Lerp(toNode.WorldPosition, phase) + normal * 7.5f;
                    tangentR = dir;
                }

                float walkBobR = Mathf.Sin((_animTime * 12f) + p * 2.0f) * 0.6f;
                Vector2 bobPosR = pedPosR + tangentR * walkBobR;
                DrawCircle(bobPosR, 1.6f, pedColor);
                DrawCircle(bobPosR + tangentR * 1.0f, 0.8f, pedColor.Lightened(0.25f)); // Walking direction

                // Both sidewalks active if high pedestrian volume
                if (pedVol > 400f)
                {
                    float phaseL = (phase + 0.5f) % 1.0f;
                    Vector2 pedPosL, tangentL;
                    if (edge.Curve != null)
                    {
                        pedPosL = edge.Curve.Evaluate(phaseL) - edge.Curve.GetNormal(phaseL) * 7.5f;
                        tangentL = edge.Curve.GetTangent(phaseL);
                    }
                    else
                    {
                        Vector2 dir = (toNode.WorldPosition - fromNode.WorldPosition).Normalized();
                        Vector2 normal = new Vector2(-dir.Y, dir.X);
                        pedPosL = fromNode.WorldPosition.Lerp(toNode.WorldPosition, phaseL) - normal * 7.5f;
                        tangentL = dir;
                    }

                    float walkBobL = Mathf.Sin((_animTime * 12f) + (p + 3) * 2.0f) * 0.6f;
                    Vector2 bobPosL = pedPosL + tangentL * walkBobL;
                    DrawCircle(bobPosL, 1.6f, pedColor);
                    DrawCircle(bobPosL + tangentL * 1.0f, 0.8f, pedColor.Lightened(0.25f));
                }
            }
        }

        // Draw discrete visual pedestrians with walking animation along curve tangents
        if (_pedManager != null && _pedManager.Pedestrians.Count > 0)
        {
            foreach (var ped in _pedManager.Pedestrians)
            {
                if (ped.EdgeId < 0 || ped.EdgeId >= _graph.Edges.Count) continue;
                Vector2 pos = ped.GetWorldPosition(_graph);
                float rot = ped.GetRotation(_graph);
                Vector2 tangent = new Vector2(Mathf.Cos(rot), Mathf.Sin(rot));

                float bob = Mathf.Sin((_animTime * 12f) + ped.Progress * 20f) * 0.7f;
                Vector2 animPos = pos + tangent * bob;

                DrawCircle(animPos, 1.8f, ped.ClothingColor);
                DrawCircle(animPos + tangent * 1.1f, 1.0f, Colors.Bisque); // Head
            }
        }
    }
}
