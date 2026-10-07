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

    public void Initialize(RoadGraph graph)
    {
        Cars.Clear();
        _busyEdgeIds.Clear();

        if (graph.Edges.Count == 0) return;

        for (int i = 0; i < MaxCars; i++)
        {
            int edgeIdx = _random.Next(graph.Edges.Count);
            Cars.Add(new VisualCar
            {
                EdgeId = edgeIdx,
                Progress = (float)_random.NextDouble(),
                Speed = graph.Edges[edgeIdx].FreeFlowSpeed,
                CarColor = Palette[_random.Next(Palette.Length)],
                IsStopped = false
            });
        }
    }

    public void RefreshBusyEdges(RoadGraph graph)
    {
        _busyEdgeIds.Clear();
        for (int i = 0; i < graph.Edges.Count; i++)
        {
            if (graph.Edges[i].CurrentVolume > 10f)
            {
                _busyEdgeIds.Add(i);
            }
        }
    }

    public void Update(float delta, float gameSpeed, RoadGraph graph, TrafficLightManager trafficLights)
    {
        if (graph.Edges.Count == 0 || Cars.Count == 0) return;

        float speedMult = Mathf.Clamp(gameSpeed, 0.1f, 10.0f);

        for (int i = 0; i < Cars.Count; i++)
        {
            var car = Cars[i];
            if (car.EdgeId < 0 || car.EdgeId >= graph.Edges.Count)
            {
                RespawnCar(car, graph);
                continue;
            }

            var edge = graph.Edges[car.EdgeId];
            var fromNode = graph.GetNode(edge.FromId);
            var toNode = graph.GetNode(edge.ToId);
            if (fromNode == null || toNode == null)
            {
                RespawnCar(car, graph);
                continue;
            }

            // Congestion slowdown factor based on edge volume
            float congestion = edge.GetCongestionRatio();
            float speedFactor = 1.0f / (1.0f + 0.25f * Mathf.Pow(congestion, 3.0f));
            float targetSpeed = (edge.FreeFlowSpeed * 1.5f * speedFactor) * speedMult;

            // Check traffic light at intersection
            bool redLight = false;
            if (car.Progress > 0.80f)
            {
                if (!trafficLights.IsGreen(edge.ToId, fromNode.WorldPosition))
                {
                    redLight = true;
                }
            }

            if (redLight && car.Progress >= 0.85f)
            {
                car.Speed = 0f;
                car.IsStopped = true;
                car.Progress = 0.85f; // Hold before intersection
            }
            else
            {
                car.Speed = targetSpeed;
                car.IsStopped = false;
                float progressStep = (car.Speed * delta) / Mathf.Max(edge.Length, 15f);
                car.Progress += progressStep;

                if (car.Progress >= 1.0f)
                {
                    TransitionToNextEdge(car, edge, graph);
                }
            }
        }
    }

    private void TransitionToNextEdge(VisualCar car, RoadEdge currentEdge, RoadGraph graph)
    {
        if (graph.AdjacencyEdges.TryGetValue(currentEdge.ToId, out var nextEdgeIds) && nextEdgeIds.Count > 0)
        {
            // Pick next outgoing edge that does not instantly reverse
            int bestEdgeId = -1;
            float bestVol = -1f;

            for (int i = 0; i < nextEdgeIds.Count; i++)
            {
                int candidateId = nextEdgeIds[i];
                var candidate = graph.Edges[candidateId];
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

        RespawnCar(car, graph);
    }

    private void RespawnCar(VisualCar car, RoadGraph graph)
    {
        if (_busyEdgeIds.Count > 0)
        {
            car.EdgeId = _busyEdgeIds[_random.Next(_busyEdgeIds.Count)];
        }
        else
        {
            car.EdgeId = _random.Next(graph.Edges.Count);
        }
        car.Progress = (float)_random.NextDouble() * 0.3f;
        car.IsStopped = false;
    }
}
