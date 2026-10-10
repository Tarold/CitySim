using Godot;
using System.Collections.Generic;
using CitySim.Simulation;

namespace CitySim.Rendering;

public partial class RoadRenderer : Node2D
{
    private RoadGraph _graph;
    private TrafficLightManager _trafficLights;
    public bool HeatmapEnabled { get; set; } = false;
    private float _maxVolume = 1f;

    public void Initialize(RoadGraph graph, TrafficLightManager trafficLights)
    {
        _graph = graph;
        _trafficLights = trafficLights;
    }

    public void UpdateMaxVolume(float maxVol)
    {
        _maxVolume = Mathf.Max(maxVol, 1f);
        QueueRedraw();
    }

    public void Refresh()
    {
        QueueRedraw();
    }

    public override void _Process(double delta)
    {
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_graph == null) return;

        // 1. Draw Road Edges (Smooth Curved Ribbons)
        var drawnSegments = new HashSet<(int, int)>();

        foreach (var edge in _graph.Edges)
        {
            if (edge.FromId == -1 || edge.ToId == -1) continue;

            var fromNode = _graph.GetNode(edge.FromId);
            var toNode = _graph.GetNode(edge.ToId);
            if (fromNode == null || toNode == null) continue;

            Vector2[] points = edge.Curve != null
                ? edge.Curve.GetSampledPoints(20)
                : new Vector2[] { fromNode.WorldPosition, toNode.WorldPosition };

            if (HeatmapEnabled)
            {
                float ratio = edge.GetCongestionRatio();
                Color color = GetHeatmapColor(ratio);
                float width = Mathf.Clamp(3.5f + ratio * 4.5f, 3.5f, 10f);
                DrawPolyline(points, color, width, true);
            }
            else
            {
                // In normal mode, draw each undirected segment once to prevent overlapping z-fight
                int minId = Mathf.Min(edge.FromId, edge.ToId);
                int maxId = Mathf.Max(edge.FromId, edge.ToId);
                if (drawnSegments.Contains((minId, maxId))) continue;
                drawnSegments.Add((minId, maxId));

                // A. Outer Road Borders / Curbs
                DrawPolyline(points, new Color(0.12f, 0.12f, 0.15f, 0.95f), 6.5f, true);

                // B. Asphalt Surface
                DrawPolyline(points, new Color(0.22f, 0.22f, 0.26f, 0.95f), 5.0f, true);

                // C. Center Dashed Lane Divider
                float curveLen = edge.Curve != null ? edge.Curve.Length : fromNode.WorldPosition.DistanceTo(toNode.WorldPosition);
                if (curveLen > 12f)
                {
                    float cycle = 11f; // 6px dash, 5px gap
                    int dashCount = Mathf.FloorToInt(curveLen / cycle);
                    Color dashColor = new Color(0.95f, 0.88f, 0.35f, 0.80f);

                    for (int d = 0; d < dashCount; d++)
                    {
                        float s1 = d * cycle + 1.0f;
                        float s2 = s1 + 5.5f;
                        if (s2 >= curveLen) break;

                        float t1 = s1 / curveLen;
                        float t2 = s2 / curveLen;

                        Vector2 p1 = edge.Curve != null ? edge.Curve.Evaluate(t1) : fromNode.WorldPosition.Lerp(toNode.WorldPosition, t1);
                        Vector2 p2 = edge.Curve != null ? edge.Curve.Evaluate(t2) : fromNode.WorldPosition.Lerp(toNode.WorldPosition, t2);

                        DrawLine(p1, p2, dashColor, 1.0f, true);
                    }
                }
            }
        }

        // Draw smooth circular/filleted junction asphalt caps at intersection nodes to cleanly seal multi-road junctions without jagged seams
        if (!HeatmapEnabled)
        {
            foreach (var node in _graph.Nodes)
            {
                int degree = _graph.AdjacencyEdges.TryGetValue(node.Id, out var edges) ? edges.Count : 0;
                if (degree >= 3)
                {
                    // Multi-road junction / intersection: larger fillet to seal wider ribbon joins cleanly
                    DrawCircle(node.WorldPosition, 4.5f, new Color(0.12f, 0.12f, 0.15f, 0.95f)); // Outer curb fillet
                    DrawCircle(node.WorldPosition, 3.6f, new Color(0.22f, 0.22f, 0.26f, 0.95f)); // Inner asphalt cap
                }
                else if (degree == 2)
                {
                    // Regular 2-way road joint / bend
                    DrawCircle(node.WorldPosition, 3.8f, new Color(0.12f, 0.12f, 0.15f, 0.95f));
                    DrawCircle(node.WorldPosition, 3.0f, new Color(0.22f, 0.22f, 0.26f, 0.95f));
                }
                else
                {
                    // Cul-de-sac / isolated node
                    DrawCircle(node.WorldPosition, 3.5f, new Color(0.12f, 0.12f, 0.15f, 0.95f));
                    DrawCircle(node.WorldPosition, 2.8f, new Color(0.22f, 0.22f, 0.26f, 0.95f));
                }
            }
        }

        // 2. Draw Traffic Lights at Intersections Aligned with Approach Vectors
        if (_trafficLights != null)
        {
            foreach (var kvp in _trafficLights.Intersections)
            {
                var light = kvp.Value;
                Vector2 pos = light.Position;

                // Intersection center hub
                DrawCircle(pos, 3.5f, new Color(0.2f, 0.2f, 0.25f, 0.8f));

                if (light.ApproachSignals != null && light.ApproachSignals.Count > 0)
                {
                    // Angle-aware approach indicator dots aligned with incoming road vectors
                    foreach (var app in light.ApproachSignals)
                    {
                        bool isGreen = app.Phase == 0 
                            ? (light.CurrentState == SignalState.NorthSouthGreen) 
                            : (light.CurrentState == SignalState.EastWestGreen);

                        Color signalColor = isGreen
                            ? new Color(0.1f, 0.95f, 0.2f) 
                            : new Color(0.95f, 0.15f, 0.15f);

                        DrawCircle(app.Position, 2.2f, signalColor);
                    }
                }
                else
                {
                    // Orthogonal fallback if no approach signals registered
                    Color nsColor = light.CurrentState == SignalState.NorthSouthGreen 
                        ? new Color(0.1f, 0.95f, 0.2f) 
                        : new Color(0.95f, 0.15f, 0.15f);

                    Color ewColor = light.CurrentState == SignalState.EastWestGreen 
                        ? new Color(0.1f, 0.95f, 0.2f) 
                        : new Color(0.95f, 0.15f, 0.15f);

                    // North & South signal dots
                    DrawCircle(pos + new Vector2(0, -9), 2.2f, nsColor);
                    DrawCircle(pos + new Vector2(0, 9), 2.2f, nsColor);

                    // East & West signal dots
                    DrawCircle(pos + new Vector2(-9, 0), 2.2f, ewColor);
                    DrawCircle(pos + new Vector2(9, 0), 2.2f, ewColor);
                }
            }
        }

        // 3. Highlight congested bottlenecks in heatmap mode
        if (HeatmapEnabled)
        {
            foreach (var node in _graph.Nodes)
            {
                float maxRatio = 0f;
                if (_graph.AdjacencyEdges.TryGetValue(node.Id, out var edgeIds))
                {
                    foreach (var eid in edgeIds)
                    {
                        float r = _graph.Edges[eid].GetCongestionRatio();
                        if (r > maxRatio) maxRatio = r;
                    }
                }

                if (maxRatio > 0.85f)
                {
                    Color hotColor = GetHeatmapColor(maxRatio);
                    DrawCircle(node.WorldPosition, 5f + maxRatio * 2f, hotColor);
                }
            }
        }
    }

    private static Color GetHeatmapColor(float ratio)
    {
        // Cities: Skylines calibrated heatmap spectrum:
        // Free flow (0.0 - 0.35) -> Green
        // Moderate (0.35 - 0.65) -> Yellow
        // Heavy (0.65 - 0.90)    -> Orange
        // Congested (0.90 - 1.15)-> Red
        // Severe Gridlock (>1.15)-> Deep Purple / Crimson
        Color freeFlow = new Color(0.15f, 0.85f, 0.25f);
        Color moderate = new Color(0.95f, 0.85f, 0.15f);
        Color heavy    = new Color(0.95f, 0.50f, 0.10f);
        Color red      = new Color(0.95f, 0.15f, 0.15f);
        Color gridlock = new Color(0.65f, 0.05f, 0.40f);

        if (ratio <= 0.35f) return freeFlow;
        if (ratio <= 0.65f) return freeFlow.Lerp(moderate, (ratio - 0.35f) / 0.30f);
        if (ratio <= 0.90f) return moderate.Lerp(heavy, (ratio - 0.65f) / 0.25f);
        if (ratio <= 1.15f) return heavy.Lerp(red, (ratio - 0.90f) / 0.25f);
        return red.Lerp(gridlock, Mathf.Clamp((ratio - 1.15f) / 0.50f, 0f, 1f));
    }
}
