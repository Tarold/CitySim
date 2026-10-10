using Godot;
using System;
using System.Collections.Generic;

namespace CitySim.Simulation;

public enum SignalState
{
    NorthSouthGreen = 0,
    EastWestGreen = 1,
    PhaseAGreen = 0,
    PhaseBGreen = 1
}

public class ApproachSignal
{
    public int EdgeId;
    public int FromNodeId;
    public Vector2 OutwardDirection; // Unit vector pointing away from intersection along the road
    public Vector2 Position;         // Visual position for indicator dot
    public int Phase;                // 0 (NorthSouthGreen / Phase A) or 1 (EastWestGreen / Phase B)
    public float AxialAngle;         // Road axis angle in [0, PI)
}

public class IntersectionLight
{
    public int NodeId;
    public Vector2 Position;
    public SignalState CurrentState;
    public float Timer;
    public float PhaseDuration = 7.0f; // Seconds per green phase
    public float PrimaryAxisAngle = Mathf.Pi * 0.5f; // North-South default
    public List<ApproachSignal> ApproachSignals = new List<ApproachSignal>();

    public bool IsApproachGreen(Vector2 fromPos)
    {
        if (ApproachSignals != null && ApproachSignals.Count > 0)
        {
            Vector2 queryDir = fromPos - Position;
            if (queryDir.LengthSquared() > 1e-4f)
            {
                queryDir = queryDir.Normalized();
                float bestDot = -2.0f;
                int bestPhase = 0;

                foreach (var app in ApproachSignals)
                {
                    float dot = app.OutwardDirection.Dot(queryDir);
                    if (dot > bestDot)
                    {
                        bestDot = dot;
                        bestPhase = app.Phase;
                    }
                }

                if (bestDot > 0.4f)
                {
                    return bestPhase == 0 
                        ? (CurrentState == SignalState.NorthSouthGreen) 
                        : (CurrentState == SignalState.EastWestGreen);
                }
            }
        }

        // Geometric fallback for arbitrary fromPos or unpopulated signals
        float dx = Mathf.Abs(fromPos.X - Position.X);
        float dy = Mathf.Abs(fromPos.Y - Position.Y);
        bool isNorthSouth = dy > dx;

        return isNorthSouth ? (CurrentState == SignalState.NorthSouthGreen) : (CurrentState == SignalState.EastWestGreen);
    }

    public bool IsApproachGreen(RoadEdge edge)
    {
        if (edge == null) return true;
        if (ApproachSignals != null && ApproachSignals.Count > 0)
        {
            foreach (var app in ApproachSignals)
            {
                if (app.EdgeId == edge.Id)
                {
                    return app.Phase == 0 
                        ? (CurrentState == SignalState.NorthSouthGreen) 
                        : (CurrentState == SignalState.EastWestGreen);
                }
            }
        }

        Vector2 fromPos = edge.Curve != null ? edge.Curve.P0 : Position;
        return IsApproachGreen(fromPos);
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
        if (graph == null) return;

        foreach (var node in graph.Nodes)
        {
            if (!graph.AdjacencyEdges.TryGetValue(node.Id, out var edgeIds)) continue;
            if (edgeIds.Count < 3) continue; // Only 3-way (T/Y-junction) and 4-way intersections

            var branches = new List<(int eid, int toId, Vector2 outwardVec, float axial)>();

            foreach (var eid in edgeIds)
            {
                if (eid < 0 || eid >= graph.Edges.Count) continue;
                var edge = graph.Edges[eid];
                if (edge == null || edge.FromId == -1 || edge.ToId == -1) continue;

                var neighbor = graph.GetNode(edge.ToId);
                if (neighbor == null) continue;

                Vector2 outwardVec = edge.Curve != null 
                    ? edge.Curve.GetTangent(0.0f) 
                    : (neighbor.WorldPosition - node.WorldPosition).Normalized();

                if (outwardVec.LengthSquared() < 1e-4f)
                {
                    outwardVec = (neighbor.WorldPosition - node.WorldPosition).Normalized();
                }

                if (outwardVec.LengthSquared() < 1e-4f) continue;

                float angle = Mathf.Atan2(outwardVec.Y, outwardVec.X);
                float axial = angle % Mathf.Pi;
                if (axial < 0f) axial += Mathf.Pi;

                branches.Add((eid, edge.ToId, outwardVec, axial));
            }

            if (branches.Count < 3) continue;

            // Check if conflicting crossing traffic exists (> 45 degrees between any pair of axes)
            bool hasCrossing = false;
            for (int i = 0; i < branches.Count; i++)
            {
                for (int j = i + 1; j < branches.Count; j++)
                {
                    float diff = Mathf.Abs(branches[i].axial - branches[j].axial);
                    if (diff > Mathf.Pi * 0.5f) diff = Mathf.Pi - diff;
                    if (diff > (Mathf.Pi / 4.0f) + 1e-4f) // > 45 degrees
                    {
                        hasCrossing = true;
                        break;
                    }
                }
                if (hasCrossing) break;
            }

            if (!hasCrossing) continue;

            // Determine reference primary axis (Phase 0).
            // Align with North-South (PI/2) if any branch is within 45 degrees of it.
            float primaryAxis = Mathf.Pi * 0.5f;
            bool hasNearNS = false;
            foreach (var b in branches)
            {
                float diff = Mathf.Abs(b.axial - Mathf.Pi * 0.5f);
                if (diff > Mathf.Pi * 0.5f) diff = Mathf.Pi - diff;
                if (diff <= (Mathf.Pi / 4.0f) + 1e-4f)
                {
                    hasNearNS = true;
                    break;
                }
            }

            if (!hasNearNS)
            {
                primaryAxis = branches[0].axial;
            }

            var signals = new List<ApproachSignal>();
            foreach (var b in branches)
            {
                float diff = Mathf.Abs(b.axial - primaryAxis);
                if (diff > Mathf.Pi * 0.5f) diff = Mathf.Pi - diff;

                // Approaches within ~45° of collinearity share green phases; crossing approaches alternate in opposing phases
                int phase = (diff <= (Mathf.Pi / 4.0f) + 1e-4f) ? 0 : 1;

                signals.Add(new ApproachSignal
                {
                    EdgeId = b.eid,
                    FromNodeId = b.toId,
                    OutwardDirection = b.outwardVec,
                    Position = node.WorldPosition + b.outwardVec * 9.0f,
                    Phase = phase,
                    AxialAngle = b.axial
                });
            }

            // Stagger initial phase across the city grid so they don't all flip in unison
            float stagger = (node.WorldPosition.X + node.WorldPosition.Y) * 0.05f;
            SignalState initial = ((int)stagger % 2 == 0) ? SignalState.NorthSouthGreen : SignalState.EastWestGreen;

            Intersections[node.Id] = new IntersectionLight
            {
                NodeId = node.Id,
                Position = node.WorldPosition,
                CurrentState = initial,
                Timer = (stagger % 7.0f),
                PhaseDuration = 7.0f,
                PrimaryAxisAngle = primaryAxis,
                ApproachSignals = signals
            };
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

    public bool IsGreen(int intersectionNodeId, RoadEdge edge)
    {
        if (Intersections.TryGetValue(intersectionNodeId, out var light))
        {
            return light.IsApproachGreen(edge);
        }
        return true;
    }
}
