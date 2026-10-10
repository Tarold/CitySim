using Godot;
using System;
using System.Collections.Generic;

namespace CitySim.Simulation;

/// <summary>
/// Specifies the type of snap target currently engaged by the snapping engine.
/// </summary>
public enum SnapType
{
    /// <summary>No snapping applied; freeform continuous coordinate.</summary>
    None,
    /// <summary>Snapped exactly to an existing road node.</summary>
    Node,
    /// <summary>Snapped to a point along an existing road curve (T-junction insertion).</summary>
    Edge,
    /// <summary>Snapped to a 45° or 90° angular increment.</summary>
    Angle,
    /// <summary>Snapped to the continuous tangent of an existing connected road.</summary>
    Tangent
}

/// <summary>
/// Holds the results of a road cursor snapping query.
/// </summary>
public struct RoadSnapResult
{
    /// <summary>The active snap type.</summary>
    public SnapType Type;

    /// <summary>The final snapped 2D world position.</summary>
    public Vector2 Position;

    /// <summary>The snapped road node, or null if not a node snap.</summary>
    public RoadNode SnappedNode;

    /// <summary>The snapped road edge, or null if not an edge snap.</summary>
    public RoadEdge SnappedEdge;

    /// <summary>The normalized parameter t along the snapped edge curve, if edge snap.</summary>
    public float EdgeT;

    /// <summary>The snapped angle in degrees, if angle snap.</summary>
    public float AngleDegrees;

    /// <summary>The tangent alignment direction vector, if tangent snap.</summary>
    public Vector2 GuideDirection;

    /// <summary>Human-readable description for UI hints and tooltips.</summary>
    public string Description;
}

/// <summary>
/// Multi-tier snapping engine supporting Node, Edge (T-junction), Tangent, and Angle snapping.
/// </summary>
public static class RoadSnappingEngine
{
    /// <summary>Default snapping radius in world coordinate units (pixels/meters).</summary>
    public const float DefaultSnapRadius = 24.0f;

    /// <summary>Angle snap threshold in radians (approximately 7.5 degrees).</summary>
    public const float AngleSnapThresholdRad = 0.13f;

    /// <summary>Tangent snap threshold in radians (approximately 15 degrees).</summary>
    public const float TangentSnapThresholdRad = 0.26f;

    /// <summary>
    /// Evaluates the cursor position against the road graph and pending construction context,
    /// returning the highest-priority snap result.
    /// Priority: Node Snap > Edge Snap > Tangent Snap > Angle Snap > None.
    /// </summary>
    /// <param name="cursorWorld">Raw mouse cursor world position.</param>
    /// <param name="graph">The road graph to query against.</param>
    /// <param name="startPoint">The locked start point of the road segment being constructed, if any.</param>
    /// <param name="startNode">The road node at the start of the segment, if starting from an existing node.</param>
    /// <param name="forceAngleSnap">Whether angle snapping is forced (e.g. Shift key held).</param>
    /// <param name="snapRadius">Maximum distance to consider for node and edge snapping.</param>
    /// <returns>A populated <see cref="RoadSnapResult"/>.</returns>
    public static RoadSnapResult FindSnap(
        Vector2 cursorWorld,
        RoadGraph graph,
        Vector2? startPoint = null,
        RoadNode startNode = null,
        bool forceAngleSnap = false,
        float snapRadius = DefaultSnapRadius)
    {
        if (graph == null)
        {
            return new RoadSnapResult
            {
                Type = SnapType.None,
                Position = cursorWorld,
                Description = "Freeform"
            };
        }

        // 1. TIER 1: NODE SNAP (Highest priority)
        RoadNode closestNode = null;
        float bestNodeDist = snapRadius;

        foreach (var node in graph.Nodes)
        {
            // Do not snap to the start node itself if we are choosing the end point
            if (startNode != null && node.Id == startNode.Id)
                continue;

            float dist = cursorWorld.DistanceTo(node.WorldPosition);
            if (dist < bestNodeDist)
            {
                bestNodeDist = dist;
                closestNode = node;
            }
        }

        if (closestNode != null)
        {
            return new RoadSnapResult
            {
                Type = SnapType.Node,
                Position = closestNode.WorldPosition,
                SnappedNode = closestNode,
                Description = $"Node #{closestNode.Id}"
            };
        }

        // 2. TIER 2: EDGE SNAP (T-Junction insertion)
        RoadEdge closestEdge = null;
        float bestEdgeDist = snapRadius;
        Vector2 bestEdgePoint = Vector2.Zero;
        float bestEdgeT = 0f;

        // HashSet to avoid testing reverse edges of the same physical segment twice
        var checkedSegments = new HashSet<(int, int)>();

        foreach (var edge in graph.Edges)
        {
            if (edge.FromId == -1 || edge.ToId == -1) continue;

            int minId = Math.Min(edge.FromId, edge.ToId);
            int maxId = Math.Max(edge.FromId, edge.ToId);
            if (checkedSegments.Contains((minId, maxId))) continue;
            checkedSegments.Add((minId, maxId));

            // Avoid snapping to an edge immediately touching startNode at t ~ 0 or t ~ 1
            var curve = edge.Curve ?? new CurveSegment(
                graph.GetNode(edge.FromId).WorldPosition,
                graph.GetNode(edge.ToId).WorldPosition
            );

            var (t, pt, dist) = curve.GetClosestPoint(cursorWorld);

            // Avoid edge extremities if connected to startNode
            if (startNode != null && (edge.FromId == startNode.Id || edge.ToId == startNode.Id))
            {
                // If cursor is near the connection to startNode, ignore this edge
                float distToStart = pt.DistanceTo(startNode.WorldPosition);
                if (distToStart < snapRadius * 1.5f)
                    continue;
            }

            if (dist < bestEdgeDist)
            {
                bestEdgeDist = dist;
                closestEdge = edge;
                bestEdgePoint = pt;
                bestEdgeT = t;
            }
        }

        if (closestEdge != null)
        {
            return new RoadSnapResult
            {
                Type = SnapType.Edge,
                Position = bestEdgePoint,
                SnappedEdge = closestEdge,
                EdgeT = bestEdgeT,
                Description = $"Edge #{closestEdge.Id} (T-Junction)"
            };
        }

        // 3. TIER 3 & 4: CONTEXTUAL SNAPPING (Only if startPoint is defined)
        if (startPoint.HasValue)
        {
            Vector2 origin = startPoint.Value;
            Vector2 delta = cursorWorld - origin;
            float length = delta.Length();

            if (length > 10.0f)
            {
                // 3a. TIER 3: TANGENT SNAP (Smooth 180° continuation from existing roads)
                if (startNode != null && graph.AdjacencyEdges.TryGetValue(startNode.Id, out var outgoingEdges) && outgoingEdges.Count > 0)
                {
                    Vector2 currentDir = delta / length;
                    Vector2 bestTangent = Vector2.Zero;
                    float minAngleDiff = TangentSnapThresholdRad;

                    foreach (int eid in outgoingEdges)
                    {
                        var edge = graph.Edges[eid];
                        if (edge.FromId == -1 || edge.ToId == -1) continue;

                        // Tangent pointing away from neighbor towards startNode:
                        // Incoming direction at startNode is -tangent at startNode
                        var otherNode = graph.GetNode(edge.ToId);
                        if (otherNode == null) continue;

                        Vector2 neighborToStart;
                        if (edge.Curve != null)
                        {
                            // At startNode (t=0 of outgoing edge), edge leaves startNode.
                            // The continuation direction is opposite to the outgoing tangent!
                            Vector2 outTangent = edge.Curve.GetTangent(0.0f);
                            neighborToStart = -outTangent;
                        }
                        else
                        {
                            neighborToStart = (startNode.WorldPosition - otherNode.WorldPosition).Normalized();
                        }

                        // We want to continue along neighborToStart (180° continuation)
                        float angleDiff = Mathf.Abs(currentDir.AngleTo(neighborToStart));
                        if (angleDiff < minAngleDiff)
                        {
                            minAngleDiff = angleDiff;
                            bestTangent = neighborToStart;
                        }
                    }

                    if (bestTangent != Vector2.Zero)
                    {
                        Vector2 tangentPos = origin + bestTangent * length;
                        return new RoadSnapResult
                        {
                            Type = SnapType.Tangent,
                            Position = tangentPos,
                            GuideDirection = bestTangent,
                            Description = "Tangent Snap (Smooth Continuation)"
                        };
                    }
                }

                // 3b. TIER 4: ANGLE SNAP (45° and 90° increments)
                float angleRad = delta.Angle(); // in [-PI, PI]
                float stepRad = Mathf.Pi / 4.0f; // 45 degrees
                float snappedRad = Mathf.Round(angleRad / stepRad) * stepRad;
                float angleDiffRad = Mathf.Abs(Mathf.AngleDifference(angleRad, snappedRad));

                if (forceAngleSnap || angleDiffRad <= AngleSnapThresholdRad)
                {
                    Vector2 snappedDir = new Vector2(Mathf.Cos(snappedRad), Mathf.Sin(snappedRad));
                    Vector2 anglePos = origin + snappedDir * length;
                    float deg = Mathf.RadToDeg(snappedRad);
                    if (deg < 0f) deg += 360f;

                    return new RoadSnapResult
                    {
                        Type = SnapType.Angle,
                        Position = anglePos,
                        AngleDegrees = deg,
                        GuideDirection = snappedDir,
                        Description = $"Angle Snap ({Mathf.RoundToInt(deg)}°)"
                    };
                }
            }
        }

        // Freeform continuous 2D position
        return new RoadSnapResult
        {
            Type = SnapType.None,
            Position = cursorWorld,
            Description = "Freeform"
        };
    }
}
