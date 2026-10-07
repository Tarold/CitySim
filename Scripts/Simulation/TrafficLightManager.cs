using Godot;
using System;
using System.Collections.Generic;

namespace CitySim.Simulation;

public enum SignalState
{
    NorthSouthGreen,
    EastWestGreen
}

public class IntersectionLight
{
    public int NodeId;
    public Vector2 Position;
    public SignalState CurrentState;
    public float Timer;
    public float PhaseDuration = 7.0f; // Seconds per green phase

    public bool IsApproachGreen(Vector2 fromPos)
    {
        // Determine whether the incoming road is vertical (North-South) or horizontal (East-West)
        float dx = Mathf.Abs(fromPos.X - Position.X);
        float dy = Mathf.Abs(fromPos.Y - Position.Y);
        bool isNorthSouth = dy > dx;

        return isNorthSouth ? (CurrentState == SignalState.NorthSouthGreen) : (CurrentState == SignalState.EastWestGreen);
    }

    public void Update(float delta, float gameSpeed)
    {
        // Scale with game speed so lights cycle at a visible, natural rate
        float speedFactor = Mathf.Clamp(gameSpeed, 0.5f, 5.0f);
        Timer += delta * speedFactor;
        if (Timer >= PhaseDuration)
        {
            Timer = 0f;
            CurrentState = (CurrentState == SignalState.NorthSouthGreen) 
                ? SignalState.EastWestGreen 
                : SignalState.NorthSouthGreen;
        }
    }
}

public class TrafficLightManager
{
    public Dictionary<int, IntersectionLight> Intersections = new Dictionary<int, IntersectionLight>();

    public void BuildIntersections(RoadGraph graph)
    {
        Intersections.Clear();

        foreach (var node in graph.Nodes)
        {
            if (!graph.AdjacencyEdges.TryGetValue(node.Id, out var edgeIds)) continue;
            if (edgeIds.Count < 3) continue; // Only 3-way (T-junction) and 4-way intersections

            bool hasHorizontal = false;
            bool hasVertical = false;

            foreach (var eid in edgeIds)
            {
                var edge = graph.Edges[eid];
                var neighbor = graph.GetNode(edge.ToId);
                if (neighbor == null) continue;

                float dx = Mathf.Abs(neighbor.WorldPosition.X - node.WorldPosition.X);
                float dy = Mathf.Abs(neighbor.WorldPosition.Y - node.WorldPosition.Y);
                if (dx > 10f) hasHorizontal = true;
                if (dy > 10f) hasVertical = true;
            }

            // Only install traffic lights where conflicting perpendicular traffic crosses
            if (hasHorizontal && hasVertical)
            {
                // Stagger initial phase across the city grid so they don't all flip in unison
                float stagger = (node.WorldPosition.X + node.WorldPosition.Y) * 0.05f;
                SignalState initial = ((int)stagger % 2 == 0) ? SignalState.NorthSouthGreen : SignalState.EastWestGreen;

                Intersections[node.Id] = new IntersectionLight
                {
                    NodeId = node.Id,
                    Position = node.WorldPosition,
                    CurrentState = initial,
                    Timer = (stagger % 7.0f),
                    PhaseDuration = 7.0f
                };
            }
        }
    }

    public void Update(float delta, float gameSpeed)
    {
        foreach (var light in Intersections.Values)
        {
            light.Update(delta, gameSpeed);
        }
    }

    public bool IsGreen(int intersectionNodeId, Vector2 fromPos)
    {
        if (Intersections.TryGetValue(intersectionNodeId, out var light))
        {
            return light.IsApproachGreen(fromPos);
        }
        return true; // No traffic light means free flow
    }
}
