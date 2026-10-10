using Godot;
using System;
using System.Collections.Generic;

namespace CitySim.Simulation;

public class VisualPedestrian
{
    public int EdgeId;
    public float Progress;
    public float Speed;
    public Color ClothingColor;
    public int Side; // 1 for right sidewalk, -1 for left sidewalk

    /// <summary>
    /// Computes the 2D world position of this pedestrian along the curved road sidewalk.
    /// </summary>
    public Vector2 GetWorldPosition(RoadGraph graph, float sidewalkOffset = 7.5f)
    {
        if (graph == null || EdgeId < 0 || EdgeId >= graph.Edges.Count) return Vector2.Zero;
        var edge = graph.Edges[EdgeId];
        return PedestrianManager.GetSidewalkPosition(edge, Progress, Side, sidewalkOffset, graph);
    }

    /// <summary>
    /// Computes the heading rotation angle in radians of this pedestrian facing along the curve tangent.
    /// </summary>
    public float GetRotation(RoadGraph graph)
    {
        if (graph == null || EdgeId < 0 || EdgeId >= graph.Edges.Count) return 0f;
        var edge = graph.Edges[EdgeId];
        return PedestrianManager.GetSidewalkRotation(edge, Progress, graph);
    }
}

public class PedestrianManager
{
    public const int MaxPedestrians = 250;
    public const float DefaultSidewalkOffset = 7.5f;

    /// <summary>
    /// Computes the 2D world position along a road edge sidewalk at progress parameter [0, 1].
    /// Offsets perpendicularly using the curve's normal vector.
    /// </summary>
    public static Vector2 GetSidewalkPosition(RoadEdge edge, float progress, int side = 1, float sidewalkOffset = DefaultSidewalkOffset, RoadGraph graph = null)
    {
        if (edge == null) return Vector2.Zero;
        float t = Mathf.Clamp(progress, 0.0f, 1.0f);

        if (edge.Curve != null)
        {
            Vector2 basePos = edge.Curve.Evaluate(t);
            Vector2 normal = edge.Curve.GetNormal(t);
            return basePos + normal * (sidewalkOffset * side);
        }

        if (graph != null)
        {
            var fromNode = graph.GetNode(edge.FromId);
            var toNode = graph.GetNode(edge.ToId);
            if (fromNode != null && toNode != null)
            {
                Vector2 basePos = fromNode.WorldPosition.Lerp(toNode.WorldPosition, t);
                Vector2 delta = toNode.WorldPosition - fromNode.WorldPosition;
                if (delta.LengthSquared() > 1e-4f)
                {
                    Vector2 dir = delta.Normalized();
                    Vector2 normal = new Vector2(-dir.Y, dir.X);
                    return basePos + normal * (sidewalkOffset * side);
                }
                return basePos;
            }
        }

        return Vector2.Zero;
    }

    /// <summary>
    /// Computes the heading rotation angle in radians along the curved sidewalk tangent.
    /// </summary>
    public static float GetSidewalkRotation(RoadEdge edge, float progress, RoadGraph graph = null)
    {
        if (edge == null) return 0f;
        float t = Mathf.Clamp(progress, 0.0f, 1.0f);

        if (edge.Curve != null)
        {
            return edge.Curve.GetTangent(t).Angle();
        }

        if (graph != null)
        {
            var fromNode = graph.GetNode(edge.FromId);
            var toNode = graph.GetNode(edge.ToId);
            if (fromNode != null && toNode != null)
            {
                Vector2 delta = toNode.WorldPosition - fromNode.WorldPosition;
                if (delta.LengthSquared() > 1e-4f)
                {
                    return delta.Angle();
                }
            }
        }

        return 0f;
    }

    public List<VisualPedestrian> Pedestrians = new List<VisualPedestrian>();
    private Random _random = new Random(1234);
    private List<int> _activeEdgeIds = new List<int>();

    private static readonly Color[] ClothingPalette = new Color[]
    {
        new Color(0.85f, 0.25f, 0.25f), // Red
        new Color(0.25f, 0.45f, 0.85f), // Blue
        new Color(0.25f, 0.75f, 0.35f), // Green
        new Color(0.95f, 0.95f, 0.95f), // White
        new Color(0.15f, 0.15f, 0.15f), // Black
        new Color(0.85f, 0.85f, 0.25f), // Yellow
        new Color(0.65f, 0.35f, 0.75f)  // Purple
    };

    /// <summary>
    /// Instantiates and registers a visual pedestrian on the specified edge sidewalk.
    /// </summary>
    public VisualPedestrian CreatePedestrian(int edgeId, float progress = 0f, float speed = 2.0f, int side = 1, Color? color = null)
    {
        var ped = new VisualPedestrian
        {
            EdgeId = edgeId,
            Progress = progress,
            Speed = speed,
            Side = side,
            ClothingColor = color ?? ClothingPalette[_random.Next(ClothingPalette.Length)]
        };
        Pedestrians.Add(ped);
        return ped;
    }

    /// <summary>
    /// Determines whether a road edge has actual pedestrian/urban activity.
    /// Activity is defined by non-zero traffic demand, connection to populated residential or workplace zones,
    /// or transit stops with waiting passengers. Roads in empty/inactive areas return false.
    /// </summary>
    public static bool IsEdgeActiveUrban(RoadEdge edge, RoadGraph graph, CityGrid grid = null, TransitManager transit = null)
    {
        if (edge == null || edge.FromId == -1 || edge.ToId == -1) return false;

        // 1. Edge has actual traffic volume or pedestrian volume
        if (edge.CurrentVolume > 0.05f || edge.PedestrianVolume > 0.05f) return true;

        // 2. Edge connects to populated residential or workplace zones
        if (grid != null && graph != null)
        {
            var fromNode = graph.GetNode(edge.FromId);
            var toNode = graph.GetNode(edge.ToId);

            if (fromNode != null && fromNode.ZoneId >= 0 && fromNode.ZoneId < grid.ZoneCount)
            {
                var z = grid.GetZone(fromNode.ZoneId);
                if (z != null)
                {
                    if (z.Type == ZoneType.Residential && z.Population > 0) return true;
                    if ((z.Type == ZoneType.Commercial || z.Type == ZoneType.Industrial) && z.Jobs > 0) return true;
                }
            }

            if (toNode != null && toNode.ZoneId >= 0 && toNode.ZoneId < grid.ZoneCount)
            {
                var z = grid.GetZone(toNode.ZoneId);
                if (z != null)
                {
                    if (z.Type == ZoneType.Residential && z.Population > 0) return true;
                    if ((z.Type == ZoneType.Commercial || z.Type == ZoneType.Industrial) && z.Jobs > 0) return true;
                }
            }
        }

        // 3. Transit stops with waiting passengers
        if (transit != null)
        {
            if (transit.Stops.TryGetValue(edge.FromId, out var s1) && s1.WaitingPassengers > 0.5f) return true;
            if (transit.Stops.TryGetValue(edge.ToId, out var s2) && s2.WaitingPassengers > 0.5f) return true;
        }

        return false;
    }

    public List<int> GetActiveUrbanEdgeIds(RoadGraph graph, CityGrid grid = null, TransitManager transit = null)
    {
        var list = new List<int>();
        if (graph == null) return list;
        for (int i = 0; i < graph.Edges.Count; i++)
        {
            if (IsEdgeActiveUrban(graph.Edges[i], graph, grid, transit))
            {
                list.Add(i);
            }
        }
        return list;
    }

    public int CalculateTargetPedestrianCount(RoadGraph graph, CityGrid grid = null, TransitManager transit = null)
    {
        if (graph == null || graph.Edges.Count == 0) return 0;
        var active = GetActiveUrbanEdgeIds(graph, grid, transit);
        if (active.Count == 0) return 0;

        // Scale dynamically with active urban activity
        int baseCount = active.Count * 2;
        int popBonus = grid != null ? Mathf.RoundToInt(grid.TotalPopulation() * 0.00015f) : 0;
        int target = Mathf.Clamp(Mathf.Max(baseCount, popBonus), 1, MaxPedestrians);
        target = Mathf.Min(target, active.Count * 6);
        return Mathf.Clamp(target, 1, MaxPedestrians);
    }

    public void Initialize(RoadGraph graph, CityGrid grid = null, TransitManager transit = null)
    {
        Pedestrians.Clear();
        _activeEdgeIds = GetActiveUrbanEdgeIds(graph, grid, transit);
    }

    public void HandleInvalidatedEdges(RoadGraph graph, CityGrid grid = null, TransitManager transit = null)
    {
        _activeEdgeIds = GetActiveUrbanEdgeIds(graph, grid, transit);

        for (int i = Pedestrians.Count - 1; i >= 0; i--)
        {
            var ped = Pedestrians[i];
            if (ped.EdgeId < 0 || ped.EdgeId >= graph.Edges.Count || 
                !IsEdgeActiveUrban(graph.Edges[ped.EdgeId], graph, grid, transit))
            {
                if (!TryRespawnPedestrian(ped, graph, _activeEdgeIds))
                {
                    Pedestrians.RemoveAt(i);
                }
            }
        }
    }

    public void Update(float delta, float gameSpeed, RoadGraph graph, CityGrid grid = null, TransitManager transit = null)
    {
        if (graph == null || graph.Edges.Count == 0)
        {
            Pedestrians.Clear();
            return;
        }

        _activeEdgeIds = GetActiveUrbanEdgeIds(graph, grid, transit);

        // In 100% flow-based architecture, visualization is rendered directly from edge sidewalk flows (Zero-Agent).
        if (Pedestrians.Count == 0) return;

        float speedMult = Mathf.Clamp(gameSpeed, 0.1f, 10.0f);

        for (int i = Pedestrians.Count - 1; i >= 0; i--)
        {
            var ped = Pedestrians[i];

            if (ped.EdgeId < 0 || ped.EdgeId >= graph.Edges.Count ||
                !IsEdgeActiveUrban(graph.Edges[ped.EdgeId], graph, grid, transit))
            {
                if (!TryRespawnPedestrian(ped, graph, _activeEdgeIds))
                {
                    Pedestrians.RemoveAt(i);
                    continue;
                }
            }

            var edge = graph.Edges[ped.EdgeId];
            float edgeLen = edge.Length > 0.1f ? edge.Length : (edge.Curve != null ? edge.Curve.Length : 50f);
            float speed = ped.Speed > 0f ? ped.Speed : 2.0f;

            ped.Progress += (speed * delta * speedMult) / Mathf.Max(edgeLen, 1f);

            if (ped.Progress >= 1.0f)
            {
                TransitionToNextEdge(ped, edge, graph, grid, transit);
            }
        }
    }

    private void TransitionToNextEdge(VisualPedestrian ped, RoadEdge currentEdge, RoadGraph graph, CityGrid grid, TransitManager transit)
    {
        if (graph.AdjacencyEdges.TryGetValue(currentEdge.ToId, out var nextEdgeIds) && nextEdgeIds.Count > 0)
        {
            var validEdges = new List<int>();
            for (int i = 0; i < nextEdgeIds.Count; i++)
            {
                int candidateId = nextEdgeIds[i];
                if (candidateId >= 0 && candidateId < graph.Edges.Count)
                {
                    if (IsEdgeActiveUrban(graph.Edges[candidateId], graph, grid, transit))
                    {
                        validEdges.Add(candidateId);
                    }
                }
            }

            if (validEdges.Count > 0)
            {
                ped.EdgeId = validEdges[_random.Next(validEdges.Count)];
                ped.Progress = 0.0f;
                if (_random.NextDouble() > 0.7)
                {
                    ped.Side *= -1;
                }
                return;
            }
        }

        // If no outgoing edge has urban activity, respawn on active urban edge or despawn
        if (!TryRespawnPedestrian(ped, graph, _activeEdgeIds))
        {
            ped.EdgeId = -1;
        }
    }

    private bool SpawnPedestrian(RoadGraph graph, CityGrid grid, TransitManager transit)
    {
        var ped = new VisualPedestrian();
        if (TryRespawnPedestrian(ped, graph, _activeEdgeIds))
        {
            Pedestrians.Add(ped);
            return true;
        }
        return false;
    }

    private bool TryRespawnPedestrian(VisualPedestrian ped, RoadGraph graph, List<int> activeUrbanEdgeIds)
    {
        if (activeUrbanEdgeIds == null || activeUrbanEdgeIds.Count == 0)
        {
            ped.EdgeId = -1;
            return false;
        }

        int candidate = activeUrbanEdgeIds[_random.Next(activeUrbanEdgeIds.Count)];
        if (candidate >= 0 && candidate < graph.Edges.Count)
        {
            ped.EdgeId = candidate;
            ped.Progress = (float)_random.NextDouble();
            ped.Speed = (float)(1.5 + _random.NextDouble() * 1.5);
            ped.ClothingColor = ClothingPalette[_random.Next(ClothingPalette.Length)];
            ped.Side = _random.NextDouble() > 0.5 ? 1 : -1;
            return true;
        }

        ped.EdgeId = -1;
        return false;
    }
}
