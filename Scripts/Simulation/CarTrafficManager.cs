using Godot;
using System;
using System.Collections.Generic;

namespace CitySim.Simulation;

public class VisualCar
{
    public int EdgeId;
    public float Progress; // 0.0 to 1.0
    public float Speed;
    public Color CarColor;
    public bool IsStopped;
}

public class CarTrafficManager
{
    public const int MaxCars = 160;
    public const float DemandThreshold = 0.05f;

    public List<VisualCar> Cars = new List<VisualCar>();
    private Random _random = new Random(42);
    private List<int> _busyEdgeIds = new List<int>();

    private static readonly Color[] Palette = new Color[]
    {
        new Color(0.95f, 0.95f, 0.95f), // White
        new Color(0.22f, 0.22f, 0.25f), // Charcoal
        new Color(0.70f, 0.70f, 0.75f), // Silver
        new Color(0.85f, 0.20f, 0.18f), // Red
        new Color(0.18f, 0.45f, 0.85f), // Blue
        new Color(0.88f, 0.75f, 0.18f), // Taxi Yellow
        new Color(0.35f, 0.60f, 0.35f)  // Green
    };

    public static bool HasDemand(RoadEdge edge)
    {
        return edge != null && edge.FromId != -1 && edge.ToId != -1 && edge.CurrentVolume > DemandThreshold;
    }

    public List<int> GetActiveDemandEdgeIds(RoadGraph graph)
    {
        var list = new List<int>();
        if (graph == null) return list;
        for (int i = 0; i < graph.Edges.Count; i++)
        {
            var edge = graph.Edges[i];
            if (HasDemand(edge))
            {
                list.Add(i);
            }
        }
        return list;
    }

    public int CalculateTargetCarCount(RoadGraph graph)
    {
        if (graph == null || graph.Edges.Count == 0) return 0;
        float totalVol = 0f;
        int activeEdgeCount = 0;
        for (int i = 0; i < graph.Edges.Count; i++)
        {
            var edge = graph.Edges[i];
            if (HasDemand(edge))
            {
                totalVol += edge.CurrentVolume;
                activeEdgeCount++;
            }
        }

        if (activeEdgeCount == 0 || totalVol <= DemandThreshold) return 0;

        // Scale dynamically with actual total traffic volume
        int countFromVolume = Mathf.RoundToInt(totalVol * 0.12f);
        int target = Mathf.Clamp(countFromVolume, 1, MaxCars);
        // Avoid overloading if very few active edges
        target = Mathf.Min(target, activeEdgeCount * 4);
        return Mathf.Clamp(target, 1, MaxCars);
    }

    public void Initialize(RoadGraph graph)
    {
        Cars.Clear();
        _busyEdgeIds.Clear();

        if (graph == null || graph.Edges.Count == 0) return;
        RefreshBusyEdges(graph);
    }

    public void RefreshBusyEdges(RoadGraph graph)
    {
        _busyEdgeIds.Clear();
        if (graph == null) return;
        for (int i = 0; i < graph.Edges.Count; i++)
        {
            var edge = graph.Edges[i];
            if (HasDemand(edge))
            {
                _busyEdgeIds.Add(i);
            }
        }
    }

    /// <summary>
    /// Respawns or cleans any cars referencing demolished, invalidated, or zero-demand edges.
    /// </summary>
    public void HandleInvalidatedEdges(RoadGraph graph)
    {
        RefreshBusyEdges(graph);

        for (int i = 0; i < Cars.Count; i++)
        {
            var car = Cars[i];
            if (car.EdgeId < 0 || car.EdgeId >= graph.Edges.Count ||
                graph.Edges[car.EdgeId].FromId == -1 || graph.Edges[car.EdgeId].ToId == -1 ||
                !HasDemand(graph.Edges[car.EdgeId]))
            {
                if (!TryRespawnCar(car, graph, _busyEdgeIds))
                {
                    car.EdgeId = -1;
                }
            }
        }
    }

    public void Update(float delta, float gameSpeed, RoadGraph graph, TrafficLightManager trafficLights)
    {
        if (graph == null || graph.Edges.Count == 0)
        {
            Cars.Clear();
            return;
        }

        RefreshBusyEdges(graph);

        // In 100% flow-based architecture, visualization is rendered directly from edge flows (Zero-Agent).
        // If any legacy car structs are added by external unit tests, cleanly maintain them:
        if (Cars.Count == 0) return;

        float speedMult = Mathf.Clamp(gameSpeed, 0.1f, 10.0f);

        for (int i = Cars.Count - 1; i >= 0; i--)
        {
            var car = Cars[i];
            if (car.EdgeId < 0 || car.EdgeId >= graph.Edges.Count)
            {
                if (!TryRespawnCar(car, graph, _busyEdgeIds))
                {
                    Cars.RemoveAt(i);
                    continue;
                }
            }

            var edge = graph.Edges[car.EdgeId];
            if (!HasDemand(edge))
            {
                if (!TryRespawnCar(car, graph, _busyEdgeIds))
                {
                    Cars.RemoveAt(i);
                    continue;
                }
            }
        }
    }

    private void TransitionToNextEdge(VisualCar car, RoadEdge currentEdge, RoadGraph graph)
    {
        if (graph.AdjacencyEdges.TryGetValue(currentEdge.ToId, out var nextEdgeIds) && nextEdgeIds.Count > 0)
        {
            // Pick next outgoing edge that has active volume (> 0.05f) and does not instantly reverse
            int bestEdgeId = -1;
            float bestVol = DemandThreshold;

            for (int i = 0; i < nextEdgeIds.Count; i++)
            {
                int candidateId = nextEdgeIds[i];
                if (candidateId < 0 || candidateId >= graph.Edges.Count) continue;
                var candidate = graph.Edges[candidateId];
                if (!HasDemand(candidate)) continue;
                if (candidate.ToId == currentEdge.FromId) continue; // Avoid 180 u-turn

                if (candidate.CurrentVolume > bestVol)
                {
                    bestVol = candidate.CurrentVolume;
                    bestEdgeId = candidateId;
                }
            }

            if (bestEdgeId != -1)
            {
                car.EdgeId = bestEdgeId;
                car.Progress = 0.01f;
                return;
            }
        }

        // If no outgoing edge has volume, respawn on an active demand edge or despawn
        if (!TryRespawnCar(car, graph, _busyEdgeIds))
        {
            car.EdgeId = -1; // Despawn
        }
    }

    private bool TryRespawnCar(VisualCar car, RoadGraph graph, List<int> activeDemandEdgeIds)
    {
        if (activeDemandEdgeIds == null || activeDemandEdgeIds.Count == 0)
        {
            car.EdgeId = -1;
            return false;
        }

        int candidate = activeDemandEdgeIds[_random.Next(activeDemandEdgeIds.Count)];
        if (candidate >= 0 && candidate < graph.Edges.Count && HasDemand(graph.Edges[candidate]))
        {
            car.EdgeId = candidate;
            car.Progress = (float)_random.NextDouble() * 0.3f;
            car.Speed = graph.Edges[candidate].FreeFlowSpeed;
            car.IsStopped = false;
            return true;
        }

        car.EdgeId = -1;
        return false;
    }
}
