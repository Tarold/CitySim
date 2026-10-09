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

        // 1. Draw Road Edges
        foreach (var edge in _graph.Edges)
        {
            var fromNode = _graph.GetNode(edge.FromId);
            var toNode = _graph.GetNode(edge.ToId);
            if (fromNode == null || toNode == null) continue;

            if (HeatmapEnabled)
            {
                float ratio = edge.GetCongestionRatio();
                Color color = GetHeatmapColor(ratio);
                float width = Mathf.Clamp(3.5f + ratio * 4.5f, 3.5f, 10f);
                DrawLine(fromNode.WorldPosition, toNode.WorldPosition, color, width, true);
            }
            else
            {
                // Clean road asphalt style
                DrawLine(fromNode.WorldPosition, toNode.WorldPosition, new Color(0.18f, 0.18f, 0.22f, 0.9f), 4.5f, true);
                DrawLine(fromNode.WorldPosition, toNode.WorldPosition, new Color(0.35f, 0.35f, 0.40f, 0.6f), 1.0f, true);
            }
        }

        // 2. Draw Traffic Lights at Intersections
        if (_trafficLights != null)
        {
            foreach (var kvp in _trafficLights.Intersections)
            {
                var light = kvp.Value;
                Vector2 pos = light.Position;

                // Intersection center hub
                DrawCircle(pos, 3.5f, new Color(0.2f, 0.2f, 0.25f, 0.8f));

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
