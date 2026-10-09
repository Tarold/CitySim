using Godot;
using System.Collections.Generic;
using CitySim.Simulation;

namespace CitySim.Rendering;

public class CommuteTarget
{
    public int ZoneId;
    public Vector2 Position;
    public float Volume;
    public ZoneType Type;
    public float TravelTime;
    public List<int> EdgePath = new List<int>();
}

public partial class CommuteOverlayRenderer : Node2D
{
    private CityGrid _grid;
    private ODMatrix _odMatrix;
    private RoadGraph _graph;
    private float _pulseTime = 0f;

    public int SelectedZoneId = -1;
    public List<CommuteTarget> Targets = new List<CommuteTarget>();

    public void Initialize(CityGrid grid, ODMatrix od, RoadGraph graph)
    {
        _grid = grid;
        _odMatrix = od;
        _graph = graph;
    }

    public void SelectZone(int zoneId, float[,] distances, RoadGraph graph)
    {
        _graph = graph;
        SelectedZoneId = zoneId;
        Targets = CommuteAnalytics.ComputeCommuteTargets(zoneId, _grid, _odMatrix, distances, graph);
        if (Targets.Count == 0 && (zoneId < 0 || zoneId >= _grid.ZoneCount || _grid.GetZone(zoneId)?.Type == ZoneType.Empty))
        {
            SelectedZoneId = -1;
        }

        QueueRedraw();
    }

    public override void _Process(double delta)
    {
        _pulseTime += (float)delta * 3f;
        if (SelectedZoneId != -1) QueueRedraw();
    }

    public override void _Draw()
    {
        if (_grid == null || _graph == null) return;

        // 1. Draw Sector District Headers on the map
        DrawDistrictLabels();

        // 2. Draw Road Paths if a zone is selected
        if (SelectedZoneId != -1 && Targets.Count > 0)
        {
            var sourceZone = _grid.GetZone(SelectedZoneId);
            Vector2 fromPos = _grid.GetWorldCenter(SelectedZoneId);

            // Glowing golden halo at the selected building
            float pulse = 18f + Mathf.Sin(_pulseTime * 2f) * 3f;
            DrawArc(fromPos, pulse, 0, Mathf.Tau, 24, Colors.Gold, 2.8f, true);
            DrawCircle(fromPos, 4f, Colors.Gold);

            float maxVol = Targets[0].Volume;

            foreach (var target in Targets)
            {
                float relativeWeight = Mathf.Clamp(target.Volume / Mathf.Max(maxVol, 0.01f), 0.2f, 1.0f);
                float lineWidth = 2.5f + relativeWeight * 4.0f;

                Color corridorColor = sourceZone.Type switch
                {
                    ZoneType.Commercial => new Color(0.20f, 0.80f, 1.0f, 0.90f), // Glowing Cyan for commercial corridors
                    ZoneType.Industrial => new Color(1.0f, 0.65f, 0.15f, 0.90f), // Glowing Amber for industrial corridors
                    _ => (target.Type == ZoneType.Commercial ? new Color(0.20f, 0.80f, 1.0f, 0.90f) : new Color(1.0f, 0.65f, 0.15f, 0.90f))
                };

                Color pinColor = target.Type switch
                {
                    ZoneType.Commercial => new Color(0.15f, 0.70f, 1.0f, 0.95f),
                    ZoneType.Industrial => new Color(1.0f, 0.65f, 0.15f, 0.95f),
                    _ => new Color(0.35f, 0.95f, 0.45f, 0.95f) // Lime Green for residential origins
                };

                // A. Draw the EXACT road street path edges
                if (target.EdgePath != null && target.EdgePath.Count > 0)
                {
                    foreach (var edgeId in target.EdgePath)
                    {
                        if (edgeId >= 0 && edgeId < _graph.Edges.Count)
                        {
                            var edge = _graph.Edges[edgeId];
                            var fn = _graph.GetNode(edge.FromId);
                            var tn = _graph.GetNode(edge.ToId);
                            if (fn != null && tn != null)
                            {
                                // Draw high-visibility illuminated street corridor
                                DrawLine(fn.WorldPosition, tn.WorldPosition, corridorColor, lineWidth, true);
                            }
                        }
                    }
                }
                else
                {
                    // Fallback direct ray if path cache doesn't have intermediate nodes
                    DrawLine(fromPos, target.Position, corridorColor, lineWidth, true);
                }

                // B. Draw origin / destination pin badge
                DrawCircle(target.Position, 6.5f + relativeWeight * 2f, pinColor);
                DrawArc(target.Position, 7f + relativeWeight * 2f, 0, Mathf.Tau, 16, Colors.White, 1.4f, true);
                DrawCircle(target.Position, 2.5f, Colors.White);
            }
        }
    }

    private void DrawDistrictLabels()
    {
        if (_grid == null) return;
        float cellSize = _grid.CellSize;

        int resPop = 0;
        int comJobs = 0;
        int indJobs = 0;

        for (int i = 0; i < _grid.ZoneCount; i++)
        {
            var z = _grid.GetZone(i);
            if (z == null) continue;
            if (z.Type == ZoneType.Residential) resPop += z.Population;
            else if (z.Type == ZoneType.Commercial) comJobs += z.Jobs;
            else if (z.Type == ZoneType.Industrial) indJobs += z.Jobs;
        }
        
        // West Sector Header
        Vector2 westPos = new Vector2(4.5f * cellSize, 1.2f * cellSize);
        DrawString(ThemeDB.FallbackFont, westPos, $"🏡 ЖИТЛОВИЙ СЕКТОР ({resPop:N0})", HorizontalAlignment.Center, -1, 14, new Color(0.4f, 0.9f, 0.5f, 0.9f));

        // Central Sector Header
        Vector2 centerPos = new Vector2(9.5f * cellSize, 3.2f * cellSize);
        DrawString(ThemeDB.FallbackFont, centerPos, $"🏢 ДІЛОВИЙ ЦЕНТР ({comJobs:N0} ОФІСІВ)", HorizontalAlignment.Center, -1, 14, new Color(0.4f, 0.7f, 1f, 0.9f));

        // East Sector Header
        Vector2 eastPos = new Vector2(14.5f * cellSize, 1.8f * cellSize);
        DrawString(ThemeDB.FallbackFont, eastPos, $"🏭 ПРОМЗОНА ({indJobs:N0} ЗАВОДІВ)", HorizontalAlignment.Center, -1, 14, new Color(1f, 0.75f, 0.2f, 0.9f));
    }
}
