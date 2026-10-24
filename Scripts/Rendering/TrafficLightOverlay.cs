using Godot;
using CitySim.Simulation;

namespace CitySim.Rendering;

/// <summary>
/// Draws traffic-light signal heads. It is the only per-frame-animated part of the road layer, so it lives
/// in its own node and redraws every frame while <see cref="RoadRenderer"/> redraws roads only on changes.
/// </summary>
public partial class TrafficLightOverlay : Node2D
{
    private static readonly Color HousingColor = new Color(0.2f, 0.2f, 0.25f, 0.8f);
    private static readonly Color GreenColor = new Color(0.1f, 0.95f, 0.2f);
    private static readonly Color RedColor = new Color(0.95f, 0.15f, 0.15f);

    private const float HousingRadius = 3.5f;
    private const float SignalRadius = 2.2f;
    private const float FallbackSignalOffset = 9f;

    private TrafficLightManager _trafficLights;

    public void Initialize(TrafficLightManager trafficLights)
    {
        _trafficLights = trafficLights;
        QueueRedraw();
    }

    public override void _Process(double delta)
    {
        if (_trafficLights != null && _trafficLights.Intersections.Count > 0)
        {
            QueueRedraw();
        }
    }

    public override void _Draw()
    {
        if (_trafficLights == null) return;

        foreach (var kvp in _trafficLights.Intersections)
        {
            var light = kvp.Value;
            Vector2 pos = light.Position;

            DrawCircle(pos, HousingRadius, HousingColor);

            if (light.ApproachSignals != null && light.ApproachSignals.Count > 0)
            {
                foreach (var app in light.ApproachSignals)
                {
                    bool isGreen = app.Phase == 0
                        ? (light.CurrentState == SignalState.NorthSouthGreen)
                        : (light.CurrentState == SignalState.EastWestGreen);

                    DrawCircle(app.Position, SignalRadius, isGreen ? GreenColor : RedColor);
                }
            }
            else
            {
                Color nsColor = light.CurrentState == SignalState.NorthSouthGreen ? GreenColor : RedColor;
                Color ewColor = light.CurrentState == SignalState.EastWestGreen ? GreenColor : RedColor;

                DrawCircle(pos + new Vector2(0, -FallbackSignalOffset), SignalRadius, nsColor);
                DrawCircle(pos + new Vector2(0, FallbackSignalOffset), SignalRadius, nsColor);
                DrawCircle(pos + new Vector2(-FallbackSignalOffset, 0), SignalRadius, ewColor);
                DrawCircle(pos + new Vector2(FallbackSignalOffset, 0), SignalRadius, ewColor);
            }
        }
    }
}
