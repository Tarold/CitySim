using Godot;
using System;

namespace CitySim.Simulation;

/// <summary>
/// Specifies which side of a road edge a parcel is positioned on,
/// relative to the edge's forward traversal direction.
/// </summary>
public enum ParcelSide
{
    /// <summary>Left side of the road edge curve.</summary>
    Left,
    /// <summary>Right side of the road edge curve.</summary>
    Right
}

/// <summary>
/// Represents a non-tile ribbon parcel subdivided along a continuous road edge curve.
/// Stores geometric boundaries, facade orientation, access points, zoning designation,
/// and population/employment capacities.
/// </summary>
public class RoadsideParcel
{
    /// <summary>Unique identifier for this parcel.</summary>
    public int Id { get; set; }

    /// <summary>The ID of the road edge this parcel fronts.</summary>
    public int EdgeId { get; set; }

    /// <summary>Center normalized curve parameter in [0, 1] along the road edge curve.</summary>
    public float NormalizedT { get; set; }

    /// <summary>Which side of the road edge this parcel is on (relative to forward direction).</summary>
    public ParcelSide Side { get; set; }

    private Vector2[] _boundary;
    private float _boundingRadius = -1f;

    /// <summary>
    /// Four-corner oriented quadrilateral polygon vertices representing the lot boundary:
    /// [0] front-start (near road), [1] front-end (near road),
    /// [2] back-end (away from road), [3] back-start (away from road).
    /// </summary>
    public Vector2[] Boundary
    {
        get => _boundary;
        set
        {
            _boundary = value;
            _boundingRadius = -1f;
        }
    }

    /// <summary>
    /// Circumscribed bounding radius from lot center to furthest boundary corner for fast spatial rejection.
    /// </summary>
    public float BoundingRadius
    {
        get
        {
            if (_boundingRadius < 0f && _boundary != null && _boundary.Length > 0)
            {
                Vector2 c = Center;
                float maxDistSq = 0f;
                for (int i = 0; i < _boundary.Length; i++)
                {
                    float dSq = c.DistanceSquaredTo(_boundary[i]);
                    if (dSq > maxDistSq) maxDistSq = dSq;
                }
                _boundingRadius = Mathf.Sqrt(maxDistSq);
            }
            return _boundingRadius >= 0f ? _boundingRadius : 0f;
        }
    }

    /// <summary>Connection point along the road centerline for driveways and citizen access.</summary>
    public Vector2 AccessPoint { get; set; }

    /// <summary>Facade facing angle in radians, oriented toward the road centerline.</summary>
    public float FacingAngle { get; set; }

    /// <summary>Active zoning type for this lot.</summary>
    public ZoneType ZoneType { get; set; } = ZoneType.Empty;

    /// <summary>Current population living in this parcel.</summary>
    public int Population { get; set; }

    /// <summary>Maximum residential capacity for this parcel.</summary>
    public int ResidentialCap { get; set; }

    /// <summary>Current number of workplaces/jobs filled in this parcel.</summary>
    public int Jobs { get; set; }

    /// <summary>Maximum commercial capacity for this parcel.</summary>
    public int CommercialCap { get; set; }

    /// <summary>Calculated geometric center of the parcel lot boundary.</summary>
    public Vector2 Center
    {
        get
        {
            if (Boundary != null && Boundary.Length >= 4)
            {
                return (Boundary[0] + Boundary[1] + Boundary[2] + Boundary[3]) * 0.25f;
            }
            return AccessPoint;
        }
    }

    /// <summary>Frontage width along the road curb line in pixels.</summary>
    public float FrontageWidth => (Boundary != null && Boundary.Length >= 2) ? Boundary[0].DistanceTo(Boundary[1]) : 0f;

    /// <summary>Lot depth extending away from the road in pixels.</summary>
    public float LotDepth => (Boundary != null && Boundary.Length >= 4) ? Boundary[0].DistanceTo(Boundary[3]) : 0f;

    /// <summary>
    /// Checks whether the given 2D world position lies inside this parcel's boundary polygon.
    /// </summary>
    public bool ContainsPoint(Vector2 point)
    {
        if (Boundary == null || Boundary.Length < 3) return false;

        bool inside = false;
        for (int i = 0, j = Boundary.Length - 1; i < Boundary.Length; j = i++)
        {
            if ((Boundary[i].Y > point.Y) != (Boundary[j].Y > point.Y) &&
                point.X < (Boundary[j].X - Boundary[i].X) * (point.Y - Boundary[i].Y) / (Boundary[j].Y - Boundary[i].Y) + Boundary[i].X)
            {
                inside = !inside;
            }
        }
        return inside;
    }

    /// <summary>
    /// Designates the zone type and sets capacity metrics according to defaults or overrides.
    /// </summary>
    public void SetZone(ZoneType type, int population = 0, int residentialCap = -1, int jobs = -1, int commercialCap = -1)
    {
        ZoneType = type;
        switch (type)
        {
            case ZoneType.Residential:
                ResidentialCap = residentialCap >= 0 ? residentialCap : 120;
                Population = population;
                Jobs = 0;
                CommercialCap = 0;
                break;
            case ZoneType.Commercial:
                ResidentialCap = 0;
                Population = 0;
                Jobs = jobs >= 0 ? jobs : 80;
                CommercialCap = commercialCap >= 0 ? commercialCap : 100;
                break;
            case ZoneType.Industrial:
                ResidentialCap = 0;
                Population = 0;
                Jobs = jobs >= 0 ? jobs : 90;
                CommercialCap = 0;
                break;
            case ZoneType.Empty:
            default:
                ZoneType = ZoneType.Empty;
                Population = 0;
                ResidentialCap = 0;
                Jobs = 0;
                CommercialCap = 0;
                break;
        }
    }

    /// <summary>Dedicated access road node ID for driveways and citizen access.</summary>
    public int AccessNodeId { get; set; } = -1;

    /// <summary>
    /// Ensures that this parcel connects to a dedicated access node at its access point on the road network.
    /// If an existing road node is already at the access point, connects to it. Otherwise, splits the road edge
    /// at the access point using <see cref="RoadGraph.SplitEdgeAtPoint"/>.
    /// </summary>
    public RoadNode EnsureAccessNode(RoadGraph graph)
    {
        if (graph == null) return null;

        if (AccessNodeId >= 0 && graph.NodeMap.TryGetValue(AccessNodeId, out var existing))
        {
            return existing;
        }

        // Check if an existing node is already within 10 px of AccessPoint
        foreach (var node in graph.Nodes)
        {
            if (node.WorldPosition.DistanceTo(AccessPoint) <= 10f)
            {
                AccessNodeId = node.Id;
                return node;
            }
        }

        // Not at an existing node: split edge at AccessPoint
        if (EdgeId >= 0 && EdgeId < graph.Edges.Count)
        {
            var edge = graph.Edges[EdgeId];
            if (edge.FromId != -1 && edge.ToId != -1)
            {
                var junction = graph.SplitEdgeAtPoint(EdgeId, AccessPoint);
                if (junction != null)
                {
                    AccessNodeId = junction.Id;
                    return junction;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves the dedicated access road node on the network for this parcel based on its access point.
    /// If not already connected to a node, ensures connection (splitting the edge if necessary).
    /// </summary>
    public int GetAccessNodeId(RoadGraph graph)
    {
        if (graph == null) return -1;
        if (AccessNodeId >= 0 && graph.NodeMap.ContainsKey(AccessNodeId))
            return AccessNodeId;

        // Check proximity to existing nodes (within 16 px)
        foreach (var node in graph.Nodes)
        {
            if (node.WorldPosition.DistanceTo(AccessPoint) <= 16f)
            {
                AccessNodeId = node.Id;
                return node.Id;
            }
        }

        // Fallback: closest endpoint of the edge
        if (EdgeId >= 0 && EdgeId < graph.Edges.Count)
        {
            var edge = graph.Edges[EdgeId];
            if (edge.FromId != -1 && edge.ToId != -1)
            {
                var n1 = graph.GetNode(edge.FromId);
                var n2 = graph.GetNode(edge.ToId);
                if (n1 == null) { AccessNodeId = edge.ToId; return edge.ToId; }
                if (n2 == null) { AccessNodeId = edge.FromId; return edge.FromId; }
                int chosen = AccessPoint.DistanceSquaredTo(n1.WorldPosition) <= AccessPoint.DistanceSquaredTo(n2.WorldPosition)
                    ? edge.FromId
                    : edge.ToId;
                AccessNodeId = chosen;
                return chosen;
            }
        }

        return -1;
    }
}

