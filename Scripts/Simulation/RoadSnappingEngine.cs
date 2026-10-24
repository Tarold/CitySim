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

    private static readonly List<int> _nodeQueryBuffer = new List<int>(32);
    private static readonly List<int> _edgeQueryBuffer = new List<int>(32);
    private static readonly HashSet<(int, int)> _checkedSegmentsBuffer = new HashSet<(int, int)>(32);

    /// <summary>
    /// Evaluates the cursor position against the road graph and pending construction context,
    /// returning the highest-priority snap result.
    /// Priority: Node Snap > Edge Snap > Tangent Snap > Angle Snap > None.
    /// Uses <see cref="SpatialGridIndex.QueryAabb"/> for O(1) spatial queries.
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

        graph.EnsureSpatialIndexSynced();
        var bbox = new Rect2(
            cursorWorld - new Vector2(snapRadius, snapRadius),
            new Vector2(snapRadius * 2.0f, snapRadius * 2.0f)
        );

        // 1. TIER 1: NODE SNAP (Highest priority) via O(1) SpatialGridIndex query
        RoadNode closestNode = null;
        float bestNodeDist = snapRadius;

        graph.SpatialIndex.QueryAabb(bbox, nodeIds: _nodeQueryBuffer);
        for (int i = 0; i < _nodeQueryBuffer.Count; i++)
        {
            int nodeId = _nodeQueryBuffer[i];
            if (startNode != null && nodeId == startNode.Id)
                continue;

            var node = graph.GetNode(nodeId);
            if (node == null) continue;

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

        // 2. TIER 2: EDGE SNAP (T-Junction insertion) via O(1) SpatialGridIndex query
        RoadEdge closestEdge = null;
        float bestEdgeDist = snapRadius;
        Vector2 bestEdgePoint = Vector2.Zero;
        float bestEdgeT = 0f;

        // Reusable HashSet to avoid testing reverse edges of the same physical segment twice
        _checkedSegmentsBuffer.Clear();

        graph.SpatialIndex.QueryAabb(bbox, edgeIds: _edgeQueryBuffer);
        for (int i = 0; i < _edgeQueryBuffer.Count; i++)
        {
            var edge = graph.GetEdge(_edgeQueryBuffer[i]);
            if (edge == null || edge.FromId == -1 || edge.ToId == -1) continue;

            int minId = Math.Min(edge.FromId, edge.ToId);
            int maxId = Math.Max(edge.FromId, edge.ToId);
            if (!_checkedSegmentsBuffer.Add((minId, maxId))) continue;

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

    /// <summary>
    /// Queries candidate road edges whose AABBs overlap the given Bézier curve segment's AABB in O(1) time
    /// via <see cref="SpatialGridIndex.QueryAabb"/>.
    /// </summary>
    public static void QueryCandidateEdgesForCurve(
        CurveSegment curve,
        RoadGraph graph,
        List<int> results,
        float margin = 4.0f)
    {
        results.Clear();
        if (curve == null || graph == null) return;

        graph.EnsureSpatialIndexSynced();
        float minX = Mathf.Min(Mathf.Min(curve.P0.X, curve.P1.X), Mathf.Min(curve.P2.X, curve.P3.X)) - margin;
        float minY = Mathf.Min(Mathf.Min(curve.P0.Y, curve.P1.Y), Mathf.Min(curve.P2.Y, curve.P3.Y)) - margin;
        float maxX = Mathf.Max(Mathf.Max(curve.P0.X, curve.P1.X), Mathf.Max(curve.P2.X, curve.P3.X)) + margin;
        float maxY = Mathf.Max(Mathf.Max(curve.P0.Y, curve.P1.Y), Mathf.Max(curve.P2.Y, curve.P3.Y)) + margin;

        var bbox = new Rect2(new Vector2(minX, minY), new Vector2(maxX - minX, maxY - minY));
        graph.SpatialIndex.QueryAabb(bbox, edgeIds: results);
    }

    /// <summary>
    /// Finds all road edges in <paramref name="graph"/> that geometrically intersect with the candidate Bézier <paramref name="curve"/>
    /// (excluding edges incident to <paramref name="ignoreStartNodeId"/> or <paramref name="ignoreEndNodeId"/>),
    /// using <see cref="SpatialGridIndex.QueryAabb"/> to avoid O(E) full-graph scans.
    /// </summary>
    public static List<(RoadEdge Edge, Vector2 Point, float CurveT, float EdgeT)> FindCurveIntersections(
        CurveSegment curve,
        RoadGraph graph,
        int ignoreStartNodeId = -1,
        int ignoreEndNodeId = -1)
    {
        var intersections = new List<(RoadEdge, Vector2, float, float)>();
        if (curve == null || graph == null) return intersections;

        QueryCandidateEdgesForCurve(curve, graph, _edgeQueryBuffer, margin: 4.0f);
        if (_edgeQueryBuffer.Count == 0) return intersections;

        var checkedPairs = new HashSet<(int, int)>();
        var ptsA = curve.GetSampledPoints(16);

        for (int i = 0; i < _edgeQueryBuffer.Count; i++)
        {
            var edge = graph.GetEdge(_edgeQueryBuffer[i]);
            if (edge == null || edge.FromId == -1 || edge.ToId == -1 || edge.Curve == null)
                continue;

            if ((ignoreStartNodeId != -1 && (edge.FromId == ignoreStartNodeId || edge.ToId == ignoreStartNodeId)) ||
                (ignoreEndNodeId != -1 && (edge.FromId == ignoreEndNodeId || edge.ToId == ignoreEndNodeId)))
            {
                continue;
            }

            int minId = Math.Min(edge.FromId, edge.ToId);
            int maxId = Math.Max(edge.FromId, edge.ToId);
            if (!checkedPairs.Add((minId, maxId))) continue;

            var ptsB = edge.Curve.GetSampledPoints(16);
            for (int a = 0; a < ptsA.Length - 1; a++)
            {
                for (int b = 0; b < ptsB.Length - 1; b++)
                {
                    if (TryIntersectSegments(ptsA[a], ptsA[a + 1], ptsB[b], ptsB[b + 1], out Vector2 hit, out float ua, out float ub))
                    {
                        float tCurve = (a + ua) / (ptsA.Length - 1);
                        float tEdge = (b + ub) / (ptsB.Length - 1);
                        if (tCurve >= 0.02f && tCurve <= 0.98f && tEdge >= 0.02f && tEdge <= 0.98f)
                        {
                            intersections.Add((edge, hit, tCurve, tEdge));
                            goto NextEdge;
                        }
                    }
                }
            }
        NextEdge:;
        }

        return intersections;
    }

    private static bool TryIntersectSegments(
        Vector2 p1, Vector2 p2, Vector2 p3, Vector2 p4,
        out Vector2 intersection, out float ua, out float ub)
    {
        intersection = Vector2.Zero;
        ua = 0f;
        ub = 0f;

        Vector2 r = p2 - p1;
        Vector2 s = p4 - p3;
        float rxs = r.X * s.Y - r.Y * s.X;
        if (Mathf.Abs(rxs) < 1e-6f) return false;

        Vector2 qp = p3 - p1;
        ua = (qp.X * s.Y - qp.Y * s.X) / rxs;
        ub = (qp.X * r.Y - qp.Y * r.X) / rxs;

        if (ua >= -1e-4f && ua <= 1.0001f && ub >= -1e-4f && ub <= 1.0001f)
        {
            ua = Mathf.Clamp(ua, 0f, 1f);
            ub = Mathf.Clamp(ub, 0f, 1f);
            intersection = p1 + ua * r;
            return true;
        }
        return false;
    }
}
