using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CitySim.Simulation;

/// <summary>
/// Manages roadside ribbon parcel subdivision along road network edges,
/// maintains spatial indices and zoning states, and calculates city-wide
/// parcel population and employment aggregates.
/// </summary>
public class ParcelManager
{
    /// <summary>Nominal frontage width along the road edge curve in pixels (32-48 px).</summary>
    public const float DefaultFrontageWidth = 40f;

    /// <summary>Nominal lot depth extending away from the road in pixels (40-54 px).</summary>
    public const float DefaultLotDepth = 48f;

    /// <summary>Lateral clearance from road centerline to the front lot line in pixels.</summary>
    public const float RoadOffset = 8f;

    /// <summary>Clearance buffer maintained from road junction endpoints in pixels (40-48 px).</summary>
    public const float JunctionMargin = 44f;

    /// <summary>Minimal physical clearance from road segment endpoints in pixels (4-8 px).</summary>
    public const float LotEndpointClearance = 4f;

    /// <summary>Default residential capacity for newly zoned parcels.</summary>
    public const int DefaultResidentialCap = 120;

    /// <summary>Default jobs capacity for commercial parcels.</summary>
    public const int DefaultCommercialJobs = 80;

    /// <summary>Default commercial customer capacity.</summary>
    public const int DefaultCommercialCap = 100;

    /// <summary>Default jobs capacity for industrial parcels.</summary>
    public const int DefaultIndustrialJobs = 90;

    private int _nextParcelId = 1;
    private readonly List<RoadsideParcel> _parcels = new List<RoadsideParcel>();
    private readonly Dictionary<int, RoadsideParcel> _parcelMap = new Dictionary<int, RoadsideParcel>();
    private readonly Dictionary<int, List<RoadsideParcel>> _parcelsByEdge = new Dictionary<int, List<RoadsideParcel>>();
    private RoadGraph _roadGraph;
    private SpatialGridIndex _spatialIndex = new SpatialGridIndex();
    private readonly List<int> _parcelQueryBuffer = new List<int>(32);
    private readonly List<int> _edgeQueryBuffer = new List<int>(32);

    /// <summary>Spatial hash grid index for O(1) parcel and road edge spatial queries.</summary>
    public SpatialGridIndex SpatialIndex => _spatialIndex;

    /// <summary>List of all active roadside ribbon parcels.</summary>
    public IReadOnlyList<RoadsideParcel> Parcels => _parcels;

    /// <summary>Dictionary lookup of active parcels by their unique ID.</summary>
    public IReadOnlyDictionary<int, RoadsideParcel> ParcelMap => _parcelMap;

    /// <summary>All roadside ribbon parcels that are designated with a non-empty active zone.</summary>
    public IEnumerable<RoadsideParcel> ActiveParcels => _parcels.Where(p => p.ZoneType != ZoneType.Empty);

    private void BindSpatialIndex(RoadGraph roadGraph)
    {
        if (roadGraph == null) return;
        roadGraph.EnsureSpatialIndexSynced();
        if (_spatialIndex != roadGraph.SpatialIndex)
        {
            _spatialIndex = roadGraph.SpatialIndex;
            for (int i = 0; i < _parcels.Count; i++)
            {
                _spatialIndex.AddParcel(_parcels[i]);
            }
        }
    }

    private void RemoveParcelInternal(RoadsideParcel p)
    {
        if (p == null) return;
        _parcels.Remove(p);
        _parcelMap.Remove(p.Id);
        _spatialIndex.RemoveParcel(p.Id);
    }

    /// <summary>Total population living in residential roadside parcels.</summary>
    public int TotalPopulation
    {
        get
        {
            int total = 0;
            for (int i = 0; i < _parcels.Count; i++)
            {
                if (_parcels[i].ZoneType == ZoneType.Residential)
                {
                    total += _parcels[i].Population;
                }
            }
            return total;
        }
    }

    /// <summary>Total workplaces / jobs provided in commercial and industrial roadside parcels.</summary>
    public int TotalJobs
    {
        get
        {
            int total = 0;
            for (int i = 0; i < _parcels.Count; i++)
            {
                if (_parcels[i].ZoneType == ZoneType.Commercial || _parcels[i].ZoneType == ZoneType.Industrial)
                {
                    total += _parcels[i].Jobs;
                }
            }
            return total;
        }
    }

    /// <summary>Total residential housing capacity across all parcels.</summary>
    public int TotalResidentialCap
    {
        get
        {
            int total = 0;
            for (int i = 0; i < _parcels.Count; i++)
            {
                if (_parcels[i].ZoneType == ZoneType.Residential)
                {
                    total += _parcels[i].ResidentialCap;
                }
            }
            return total;
        }
    }

    /// <summary>Total commercial customer capacity across all parcels.</summary>
    public int TotalCommercialCap
    {
        get
        {
            int total = 0;
            for (int i = 0; i < _parcels.Count; i++)
            {
                if (_parcels[i].ZoneType == ZoneType.Commercial)
                {
                    total += _parcels[i].CommercialCap;
                }
            }
            return total;
        }
    }

    /// <summary>
    /// Initializes the parcel manager with an initial road graph and generates parcels.
    /// </summary>
    public void Initialize(RoadGraph roadGraph)
    {
        if (_roadGraph != null)
        {
            _roadGraph.EdgeRemoved -= HandleEdgeRemoved;
            _roadGraph.EdgeSplit -= OnRoadGraphEdgeSplit;
        }

        _roadGraph = roadGraph;
        BindSpatialIndex(roadGraph);

        if (_roadGraph != null)
        {
            _roadGraph.EdgeRemoved += HandleEdgeRemoved;
            _roadGraph.EdgeSplit += OnRoadGraphEdgeSplit;
        }

        RefreshParcels(roadGraph);
    }

    private void OnRoadGraphEdgeSplit(int edgeId, RoadNode junction)
    {
        if (_roadGraph != null)
        {
            HandleEdgeSplit(edgeId, _roadGraph);
        }
    }

    /// <summary>
    /// Synchronizes parcels with the current state of the road graph.
    /// Cleans up disconnected edges and generates parcels for any newly added road edges.
    /// </summary>
    public void RefreshParcels(RoadGraph roadGraph)
    {
        _roadGraph = roadGraph;
        if (roadGraph == null)
        {
            ClearAllParcels();
            return;
        }

        BindSpatialIndex(roadGraph);

        // 1. Identify which edge IDs currently exist and are active
        var activeEdgeIds = new HashSet<int>();
        var visitedSegments = new HashSet<(int, int)>();

        for (int i = 0; i < roadGraph.Edges.Count; i++)
        {
            var edge = roadGraph.Edges[i];
            if (edge.FromId == -1 || edge.ToId == -1) continue;

            activeEdgeIds.Add(edge.Id);

            int minNode = Math.Min(edge.FromId, edge.ToId);
            int maxNode = Math.Max(edge.FromId, edge.ToId);
            if (visitedSegments.Contains((minNode, maxNode))) continue;
            visitedSegments.Add((minNode, maxNode));

            // If parcels for this segment do not exist, generate them
            if (!_parcelsByEdge.ContainsKey(edge.Id))
            {
                GenerateParcelsForEdge(edge, roadGraph);
            }
        }

        // 2. Remove parcels belonging to edges that no longer exist or were disconnected
        var registeredEdgeIds = _parcelsByEdge.Keys.ToList();
        foreach (int eid in registeredEdgeIds)
        {
            if (!activeEdgeIds.Contains(eid))
            {
                HandleEdgeRemoved(eid);
            }
        }

        // 3. Remove parcels whose boundary collides with intersecting roads (other than their own edge) via O(1) SpatialGridIndex
        var collidedParcels = new List<RoadsideParcel>();
        for (int i = 0; i < _parcels.Count; i++)
        {
            var p = _parcels[i];
            if (p.ZoneType == ZoneType.Empty) continue;

            var (pMin, pMax) = SpatialGridIndex.ComputeParcelAabb(p, RoadOffset + 2f);
            _spatialIndex.QueryEdgesAabb(pMin, pMax, _edgeQueryBuffer);

            for (int e = 0; e < _edgeQueryBuffer.Count; e++)
            {
                var edge = roadGraph.GetEdge(_edgeQueryBuffer[e]);
                if (edge == null || edge.FromId == -1 || edge.ToId == -1) continue;
                if (edge.Id == p.EdgeId || roadGraph.GetReverseEdgeId(edge.Id) == p.EdgeId ||
                    roadGraph.GetReverseEdgeId(p.EdgeId) == edge.Id) continue;

                if (DoesParcelCollideWithRoad(p, edge))
                {
                    collidedParcels.Add(p);
                    break;
                }
            }
        }

        foreach (var p in collidedParcels)
        {
            RemoveParcelInternal(p);
            foreach (var list in _parcelsByEdge.Values)
            {
                list.Remove(p);
            }
        }
    }

    /// <summary>
    /// Clears all registered parcels.
    /// </summary>
    public void ClearAllParcels()
    {
        _parcels.Clear();
        _parcelMap.Clear();
        _parcelsByEdge.Clear();
        _spatialIndex.ClearParcels();
    }

    /// <summary>
    /// Subdivides the specified road edge into roadside ribbon parcels on both Left and Right sides.
    /// Uses the edge curve's local normal vectors to compute boundary corners.
    /// </summary>
    public List<RoadsideParcel> GenerateParcelsForEdge(RoadEdge edge, RoadGraph roadGraph = null)
    {
        if (edge == null || edge.FromId == -1 || edge.ToId == -1)
            return new List<RoadsideParcel>();

        if (roadGraph != null)
        {
            BindSpatialIndex(roadGraph);
        }

        // Ensure curve geometry is present
        CurveSegment curve = edge.Curve;
        if (curve == null && roadGraph != null)
        {
            var n1 = roadGraph.GetNode(edge.FromId);
            var n2 = roadGraph.GetNode(edge.ToId);
            if (n1 != null && n2 != null)
            {
                curve = new CurveSegment(n1.WorldPosition, n2.WorldPosition);
            }
        }

        if (curve == null)
            return new List<RoadsideParcel>();

        float curveLength = curve.Length;
        if (curveLength < 24f)
            return new List<RoadsideParcel>();

        // Remove existing empty parcels for this edge, preserving any existing zoned parcels
        var preservedZoned = new List<RoadsideParcel>();
        if (_parcelsByEdge.TryGetValue(edge.Id, out var existing))
        {
            for (int i = existing.Count - 1; i >= 0; i--)
            {
                var p = existing[i];
                if (p.ZoneType == ZoneType.Empty)
                {
                    RemoveParcelInternal(p);
                }
                else
                {
                    preservedZoned.Add(p);
                }
            }
            _parcelsByEdge.Remove(edge.Id);
        }

        var newParcels = new List<RoadsideParcel>(preservedZoned);

        // Subdivide edge along its actual usable span from near start to near end
        float startOffset = LotEndpointClearance;
        float usableLength = Math.Max(curveLength - 2f * LotEndpointClearance, 0f);

        int parcelCount = Math.Clamp((int)Math.Round(usableLength / DefaultFrontageWidth, MidpointRounding.AwayFromZero), 1, 100);
        float parcelSpan = usableLength / parcelCount;

        // Pre-collect connecting cross-roads and junction nodes for physical geometric collision checks (Criterion 2a)
        var crossRoads = new List<RoadEdge>();
        var junctionNodesToCheck = new List<RoadNode>();

        if (roadGraph != null)
        {
            int revId = roadGraph.GetReverseEdgeId(edge.Id);

            void CollectCrossRoadsForNode(int nodeId)
            {
                if (nodeId == -1) return;
                int degree = roadGraph.GetNodeDegree(nodeId);
                if (degree <= 1) return; // Dead end has no cross roads

                var node = roadGraph.GetNode(nodeId);
                if (node != null && !junctionNodesToCheck.Contains(node))
                {
                    junctionNodesToCheck.Add(node);
                }

                if (roadGraph.AdjacencyEdges.TryGetValue(nodeId, out var adj))
                {
                    for (int i = 0; i < adj.Count; i++)
                    {
                        int eid = adj[i];
                        if (eid >= 0 && eid < roadGraph.Edges.Count && eid != edge.Id && eid != revId)
                        {
                            var ce = roadGraph.Edges[eid];
                            if (ce.FromId != -1 && ce.ToId != -1 && !crossRoads.Contains(ce))
                            {
                                crossRoads.Add(ce);
                            }
                        }
                    }
                }
            }

            CollectCrossRoadsForNode(edge.FromId);
            CollectCrossRoadsForNode(edge.ToId);
        }

        ParcelSide[] sides = new ParcelSide[] { ParcelSide.Left, ParcelSide.Right };

        for (int pIdx = 0; pIdx < parcelCount; pIdx++)
        {
            float s1 = startOffset + pIdx * parcelSpan;
            float s2 = s1 + parcelSpan;

            float t1 = Mathf.Clamp(s1 / curveLength, 0f, 1f);
            float t2 = Mathf.Clamp(s2 / curveLength, 0f, 1f);
            float tMid = (t1 + t2) * 0.5f;

            Vector2 pt1 = curve.Evaluate(t1);
            Vector2 pt2 = curve.Evaluate(t2);
            Vector2 accessPt = curve.Evaluate(tMid);

            Vector2 normal1 = curve.GetNormal(t1);
            Vector2 normal2 = curve.GetNormal(t2);

            foreach (var side in sides)
            {
                // In Godot 2D (Y-down), GetNormal(t) points to the Right side of curve traversal.
                // Left side is offset by -GetNormal(t).
                Vector2 n1 = side == ParcelSide.Right ? normal1 : -normal1;
                Vector2 n2 = side == ParcelSide.Right ? normal2 : -normal2;

                // Inside curve depth adaptation:
                // On sharp inside curves, normal vectors converge (n2 - n1 has negative dot product with pt2 - pt1).
                // Scale back lot depth to keep rear corners from crossing or pinching adjacent lots.
                float effectiveDepth = DefaultLotDepth;
                Vector2 vSeg = pt2 - pt1;
                Vector2 deltaN = n2 - n1;
                float convergence = vSeg.Dot(deltaN);
                if (convergence < -1e-4f)
                {
                    Vector2 testRear = (pt2 + n2 * (RoadOffset + DefaultLotDepth)) - (pt1 + n1 * (RoadOffset + DefaultLotDepth));
                    if (vSeg.Dot(testRear) <= 0f || testRear.Length() < 0.20f * vSeg.Length())
                    {
                        float vLenSq = vSeg.LengthSquared();
                        float maxSafeDepth = (0.35f * vLenSq / Mathf.Abs(convergence)) - RoadOffset;
                        effectiveDepth = Mathf.Clamp(maxSafeDepth, 16f, DefaultLotDepth);
                    }
                }

                // Four-corner oriented quadrilateral polygon vertices:
                // [0] front-start, [1] front-end, [2] back-end, [3] back-start
                Vector2 c0 = pt1 + n1 * RoadOffset;
                Vector2 c1 = pt2 + n2 * RoadOffset;
                Vector2 c2 = pt2 + n2 * (RoadOffset + effectiveDepth);
                Vector2 c3 = pt1 + n1 * (RoadOffset + effectiveDepth);

                Vector2 frontEdge = c1 - c0;
                Vector2 rearEdge = c2 - c3;

                // Discard degenerate, inverted, or severely pinched lots (Criterion 2c)
                if (frontEdge.Length() <= 8f || rearEdge.Length() <= 8f) continue;
                if (frontEdge.Dot(rearEdge) <= 0f) continue;

                Vector2 center = (c0 + c1 + c2 + c3) * 0.25f;
                // Facing angle points directly from lot center toward road centerline access point
                float facingAngle = (accessPt - center).Angle();

                var candidate = new RoadsideParcel
                {
                    EdgeId = edge.Id,
                    NormalizedT = tMid,
                    Side = side,
                    Boundary = new Vector2[] { c0, c1, c2, c3 },
                    AccessPoint = accessPt,
                    FacingAngle = facingAngle,
                    ZoneType = ZoneType.Empty,
                    Population = 0,
                    ResidentialCap = 0,
                    Jobs = 0,
                    CommercialCap = 0
                };

                // Flexible physical fitting and collision evaluation:
                bool collides = false;

                // 2a. Junction node collision: check if candidate contains the junction node point
                for (int j = 0; j < junctionNodesToCheck.Count; j++)
                {
                    if (candidate.ContainsPoint(junctionNodesToCheck[j].WorldPosition))
                    {
                        collides = true;
                        break;
                    }
                }

                // 2a. Cross-road collision: check against connecting roads at junction nodes
                if (!collides && crossRoads.Count > 0)
                {
                    for (int c = 0; c < crossRoads.Count; c++)
                    {
                        if (DoesParcelCollideWithRoad(candidate, crossRoads[c]))
                        {
                            collides = true;
                            break;
                        }
                    }
                }

                // 2b. Parcel overlap (SAT): check against new and preserved parcels on this edge
                if (!collides)
                {
                    for (int k = 0; k < newParcels.Count; k++)
                    {
                        if (ParcelsOverlap(candidate, newParcels[k]))
                        {
                            collides = true;
                            break;
                        }
                    }
                }

                // 2b. Parcel overlap (SAT): check against nearby registered parcels via O(1) SpatialGridIndex
                if (!collides)
                {
                    var (cMin, cMax) = SpatialGridIndex.ComputeParcelAabb(candidate, 2.0f);
                    _spatialIndex.QueryParcelsAabb(cMin, cMax, _parcelQueryBuffer);
                    for (int k = 0; k < _parcelQueryBuffer.Count; k++)
                    {
                        if (!_parcelMap.TryGetValue(_parcelQueryBuffer[k], out var other)) continue;
                        if (other.EdgeId == edge.Id) continue;
                        if (ParcelsOverlap(candidate, other))
                        {
                            collides = true;
                            break;
                        }
                    }
                }

                if (collides)
                {
                    // Discard candidate parcel that physically collides or overlaps
                    continue;
                }

                candidate.Id = _nextParcelId++;
                newParcels.Add(candidate);
                _parcels.Add(candidate);
                _parcelMap[candidate.Id] = candidate;
                _spatialIndex.AddParcel(candidate);
            }
        }

        _parcelsByEdge[edge.Id] = newParcels;

        // Also register under reverse edge ID if graph is supplied
        if (roadGraph != null)
        {
            int revId = roadGraph.FindEdgeId(edge.ToId, edge.FromId);
            if (revId != -1 && revId != edge.Id)
            {
                _parcelsByEdge[revId] = newParcels;
            }
        }

        return newParcels;
    }

    private static readonly List<RoadsideParcel> _emptyParcelList = new List<RoadsideParcel>(0);

    /// <summary>
    /// Retrieves all roadside parcels associated with the specified road edge ID in O(1) time
    /// (supports both forward and reverse edge lookups in bidirectional pairs even when disconnected).
    /// </summary>
    public List<RoadsideParcel> GetParcelsForEdge(int edgeId)
    {
        if (_parcelsByEdge.TryGetValue(edgeId, out var list) && list != null && list.Count > 0)
        {
            return list;
        }

        if (_roadGraph != null)
        {
            int revId = _roadGraph.GetReverseEdgeId(edgeId);
            if (revId != -1 && _parcelsByEdge.TryGetValue(revId, out var revList) && revList != null && revList.Count > 0)
            {
                return revList;
            }
        }

        return list ?? _emptyParcelList;
    }

    /// <summary>
    /// Retrieves roadside parcels for the specified road edge filtered by side.
    /// </summary>
    public List<RoadsideParcel> GetParcelsForEdge(int edgeId, ParcelSide side)
    {
        var all = GetParcelsForEdge(edgeId);
        var result = new List<RoadsideParcel>();
        for (int i = 0; i < all.Count; i++)
        {
            if (all[i].Side == side)
            {
                result.Add(all[i]);
            }
        }
        return result;
    }

    /// <summary>
    /// Returns the parcel whose boundary contains the specified world position in O(1) time via <see cref="SpatialGridIndex"/>, or null if none.
    /// </summary>
    public RoadsideParcel GetParcelAt(Vector2 worldPosition)
    {
        _spatialIndex.QueryParcelsAtPoint(worldPosition, _parcelQueryBuffer);
        for (int i = 0; i < _parcelQueryBuffer.Count; i++)
        {
            if (_parcelMap.TryGetValue(_parcelQueryBuffer[i], out var parcel) && parcel.ContainsPoint(worldPosition))
            {
                return parcel;
            }
        }
        return null;
    }

    /// <summary>
    /// Finds the closest parcel to the specified world position within a maximum distance in O(1) time via <see cref="SpatialGridIndex"/>.
    /// </summary>
    public RoadsideParcel FindClosestParcel(Vector2 worldPosition, float maxDistance = 64f)
    {
        RoadsideParcel best = null;
        float bestDistSq = maxDistance * maxDistance;

        _spatialIndex.QueryParcelsInRadius(worldPosition, maxDistance + DefaultLotDepth, _parcelQueryBuffer);
        for (int i = 0; i < _parcelQueryBuffer.Count; i++)
        {
            if (!_parcelMap.TryGetValue(_parcelQueryBuffer[i], out var p)) continue;
            float distSq = p.Center.DistanceSquaredTo(worldPosition);
            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                best = p;
            }
        }

        return best;
    }

    /// <summary>
    /// Determines whether a 2D query point lies on the Left or Right side of a curve segment
    /// relative to its forward direction.
    /// </summary>
    public static ParcelSide GetSideOfPoint(CurveSegment curve, Vector2 point)
    {
        if (curve == null) return ParcelSide.Right;

        var (t, closestPt, _) = curve.GetClosestPoint(point);
        Vector2 normal = curve.GetNormal(t);
        Vector2 toPoint = point - closestPt;

        // In Godot (Y-down), normal points to the Right side.
        // Dot product > 0 indicates point is on the Right side; < 0 indicates Left side.
        return toPoint.Dot(normal) >= 0f ? ParcelSide.Right : ParcelSide.Left;
    }

    /// <summary>
    /// Zones an individual parcel and initializes its capacity properties.
    /// Does not split road edges on zoning; resolves access node non-destructively.
    /// </summary>
    public bool ZoneParcel(int parcelId, ZoneType type, int population = 0, int residentialCap = -1, int jobs = -1, int commercialCap = -1, bool ensureAccessNode = false)
    {
        if (!_parcelMap.TryGetValue(parcelId, out var parcel))
            return false;

        parcel.SetZone(type, population, residentialCap, jobs, commercialCap);
        if (_roadGraph != null && type != ZoneType.Empty)
        {
            if (ensureAccessNode)
            {
                parcel.EnsureAccessNode(_roadGraph);
            }
            else
            {
                parcel.GetAccessNodeId(_roadGraph);
            }
        }
        return true;
    }

    /// <summary>
    /// Clears an individual parcel back to Empty terrain.
    /// </summary>
    public bool DezoneParcel(int parcelId)
    {
        return ZoneParcel(parcelId, ZoneType.Empty);
    }

    /// <summary>
    /// Zones all parcels along an entire edge side in a single operation.
    /// Returns the number of parcels zoned.
    /// </summary>
    public int ZoneEdgeSide(int edgeId, ParcelSide side, ZoneType type)
    {
        var parcels = GetParcelsForEdge(edgeId, side);
        int count = 0;
        foreach (var p in parcels)
        {
            if (p.ZoneType != type)
            {
                ZoneParcel(p.Id, type);
                count++;
            }
        }
        return count;
    }

    /// <summary>
    /// Dezones all parcels along an entire edge side.
    /// Returns the number of parcels dezoned.
    /// </summary>
    public int DezoneEdgeSide(int edgeId, ParcelSide side)
    {
        return ZoneEdgeSide(edgeId, side, ZoneType.Empty);
    }

    /// <summary>
    /// Removes and cleans up all parcels belonging to the specified edge ID (and reverse edge).
    /// Safe against removing already migrated parcels.
    /// </summary>
    public void HandleEdgeRemoved(int edgeId)
    {
        var parcelsToRemove = new List<RoadsideParcel>();

        if (_parcelsByEdge.TryGetValue(edgeId, out var list))
        {
            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i];
                // Only mark for removal if parcel has not already been migrated to a new active edge
                if (p.EdgeId == edgeId || (_roadGraph != null && _roadGraph.GetReverseEdgeId(p.EdgeId) == edgeId))
                {
                    parcelsToRemove.Add(p);
                }
            }
            _parcelsByEdge.Remove(edgeId);
        }

        // Also check if any parcel directly references this edgeId
        for (int i = _parcels.Count - 1; i >= 0; i--)
        {
            if (_parcels[i].EdgeId == edgeId && !parcelsToRemove.Contains(_parcels[i]))
            {
                parcelsToRemove.Add(_parcels[i]);
            }
        }

        foreach (var p in parcelsToRemove)
        {
            RemoveParcelInternal(p);
        }

        // Clean up reverse edge entry if empty
        var reverseKeysToRemove = new List<int>();
        foreach (var kvp in _parcelsByEdge)
        {
            kvp.Value.RemoveAll(p => parcelsToRemove.Contains(p));
            if (kvp.Value.Count == 0)
            {
                reverseKeysToRemove.Add(kvp.Key);
            }
        }
        foreach (int k in reverseKeysToRemove)
        {
            _parcelsByEdge.Remove(k);
        }
    }

    /// <summary>
    /// Handles edge splitting: migrates and preserves existing zoned parcels to the corresponding
    /// split sub-edges while respecting JunctionMargin, and generates parcels for the split segments.
    /// </summary>
    public void HandleEdgeSplit(int originalEdgeId, RoadGraph roadGraph)
    {
        HandleEdgeSplitInternal(originalEdgeId, -1, -1, roadGraph);
    }

    /// <summary>
    /// Explicit overload to handle edge splitting with explicit new edge IDs.
    /// </summary>
    public void HandleEdgeSplit(int originalEdgeId, int newEdge1Id, int newEdge2Id, RoadGraph roadGraph)
    {
        HandleEdgeSplitInternal(originalEdgeId, newEdge1Id, newEdge2Id, roadGraph);
    }

    private void HandleEdgeSplitInternal(int originalEdgeId, int newEdge1Id, int newEdge2Id, RoadGraph roadGraph)
    {
        if (roadGraph == null)
        {
            HandleEdgeRemoved(originalEdgeId);
            return;
        }

        BindSpatialIndex(roadGraph);
        int revEdgeId = roadGraph.GetReverseEdgeId(originalEdgeId);

        // 1. Collect parcels currently registered for originalEdgeId and its reverse edge
        var originalParcelsSet = new HashSet<RoadsideParcel>();
        if (_parcelsByEdge.TryGetValue(originalEdgeId, out var fwdList))
        {
            foreach (var p in fwdList) originalParcelsSet.Add(p);
        }
        if (revEdgeId != -1 && _parcelsByEdge.TryGetValue(revEdgeId, out var revList))
        {
            foreach (var p in revList) originalParcelsSet.Add(p);
        }
        for (int i = 0; i < _parcels.Count; i++)
        {
            var p = _parcels[i];
            if (p.EdgeId == originalEdgeId || (revEdgeId != -1 && p.EdgeId == revEdgeId))
            {
                originalParcelsSet.Add(p);
            }
        }

        var originalParcels = originalParcelsSet.ToList();
        if (originalParcels.Count == 0)
        {
            // Already processed or no parcels on this edge pair
            _parcelsByEdge.Remove(originalEdgeId);
            if (revEdgeId != -1) _parcelsByEdge.Remove(revEdgeId);
            return;
        }

        var zonedParcels = originalParcels.Where(p => p.ZoneType != ZoneType.Empty).ToList();
        if (zonedParcels.Count == 0)
        {
            foreach (var p in originalParcels)
            {
                RemoveParcelInternal(p);
            }
            _parcelsByEdge.Remove(originalEdgeId);
            if (revEdgeId != -1) _parcelsByEdge.Remove(revEdgeId);
            RefreshParcels(roadGraph);
            return;
        }

        // 2. Identify junction node and replacement candidate sub-edges
        RoadNode junction = null;
        RoadEdge subEdge1 = null;
        RoadEdge subEdge2 = null;

        if (newEdge1Id >= 0 && newEdge1Id < roadGraph.Edges.Count &&
            newEdge2Id >= 0 && newEdge2Id < roadGraph.Edges.Count)
        {
            subEdge1 = roadGraph.Edges[newEdge1Id];
            subEdge2 = roadGraph.Edges[newEdge2Id];
            int jId = (subEdge1.FromId == subEdge2.FromId || subEdge1.FromId == subEdge2.ToId) ? subEdge1.FromId : subEdge1.ToId;
            junction = roadGraph.GetNode(jId);
        }
        else
        {
            var splitInfo = roadGraph.GetSplitInfo(originalEdgeId) ?? (revEdgeId != -1 ? roadGraph.GetSplitInfo(revEdgeId) : null);
            if (splitInfo.HasValue)
            {
                junction = splitInfo.Value.Junction;
                int e1Id = roadGraph.FindEdgeId(splitInfo.Value.FromId, junction.Id);
                int e2Id = roadGraph.FindEdgeId(junction.Id, splitInfo.Value.ToId);
                if (e1Id >= 0 && e1Id < roadGraph.Edges.Count) subEdge1 = roadGraph.Edges[e1Id];
                if (e2Id >= 0 && e2Id < roadGraph.Edges.Count) subEdge2 = roadGraph.Edges[e2Id];
            }
        }

        var candidateEdges = new List<RoadEdge>();
        if (subEdge1 != null && subEdge1.FromId != -1 && subEdge1.ToId != -1) candidateEdges.Add(subEdge1);
        if (subEdge2 != null && subEdge2.FromId != -1 && subEdge2.ToId != -1) candidateEdges.Add(subEdge2);

        if (candidateEdges.Count == 0 && junction != null)
        {
            if (roadGraph.AdjacencyEdges.TryGetValue(junction.Id, out var adjEdges))
            {
                for (int i = 0; i < adjEdges.Count; i++)
                {
                    int eid = adjEdges[i];
                    if (eid >= 0 && eid < roadGraph.Edges.Count)
                    {
                        var e = roadGraph.Edges[eid];
                        if (e.FromId != -1 && e.ToId != -1)
                        {
                            candidateEdges.Add(e);
                        }
                    }
                }
            }
        }

        // 3. Remove original edge registrations (both forward and reverse) and unzoned empty parcels
        _parcelsByEdge.Remove(originalEdgeId);
        if (revEdgeId != -1) _parcelsByEdge.Remove(revEdgeId);

        var edgeKeysToRemove = new List<int>();
        foreach (var kvp in _parcelsByEdge)
        {
            if (kvp.Key == originalEdgeId || kvp.Key == revEdgeId || kvp.Value.Any(p => originalParcels.Contains(p)))
            {
                edgeKeysToRemove.Add(kvp.Key);
            }
        }
        foreach (int k in edgeKeysToRemove)
        {
            _parcelsByEdge.Remove(k);
        }

        // Remove only empty parcels from global lists
        for (int i = 0; i < originalParcels.Count; i++)
        {
            var p = originalParcels[i];
            if (p.ZoneType == ZoneType.Empty)
            {
                RemoveParcelInternal(p);
            }
        }

        // 4. Migrate surviving zoned parcels
        int nodeDegree = junction != null ? roadGraph.GetNodeDegree(junction.Id) : 0;
        bool isDegree2 = nodeDegree <= 2;

        var intersectingEdges = new List<RoadEdge>();
        if (!isDegree2 && junction != null && roadGraph.AdjacencyEdges.TryGetValue(junction.Id, out var adjList))
        {
            for (int i = 0; i < adjList.Count; i++)
            {
                int eid = adjList[i];
                if (eid >= 0 && eid < roadGraph.Edges.Count)
                {
                    var e = roadGraph.Edges[eid];
                    if (e.FromId != -1 && e.ToId != -1 && !candidateEdges.Contains(e))
                    {
                        intersectingEdges.Add(e);
                    }
                }
            }
        }

        for (int i = 0; i < zonedParcels.Count; i++)
        {
            var p = zonedParcels[i];

            // Junction collision check:
            // Degree-2 nodes (such as mid-road transit stops) are exempt from JunctionMargin deletion.
            // Genuine intersections (degree > 2) only remove parcels whose physical boundary collides with intersecting road geometry.
            if (!isDegree2 && junction != null)
            {
                if (DoesParcelCollideWithIntersection(p, junction, intersectingEdges))
                {
                    RemoveParcelInternal(p);
                    continue;
                }
            }

            // Find closest candidate sub-edge
            RoadEdge bestEdge = null;
            float bestDist = float.MaxValue;
            float bestT = 0.5f;
            Vector2 bestPt = p.AccessPoint;

            for (int e = 0; e < candidateEdges.Count; e++)
            {
                var ce = candidateEdges[e];
                if (ce.Curve != null)
                {
                    var (t, pt, d) = ce.Curve.GetClosestPoint(p.AccessPoint);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        bestEdge = ce;
                        bestT = t;
                        bestPt = pt;
                    }
                }
            }

            if (bestEdge != null)
            {
                p.EdgeId = bestEdge.Id;
                p.NormalizedT = bestT;
                p.AccessPoint = bestPt;
                p.GetAccessNodeId(roadGraph);

                if (!_parcelsByEdge.TryGetValue(bestEdge.Id, out var edgeParcels))
                {
                    edgeParcels = new List<RoadsideParcel>();
                    _parcelsByEdge[bestEdge.Id] = edgeParcels;
                }
                if (!edgeParcels.Contains(p))
                {
                    edgeParcels.Add(p);
                }

                int revSubId = roadGraph.GetReverseEdgeId(bestEdge.Id);
                if (revSubId != -1 && revSubId != bestEdge.Id)
                {
                    if (!_parcelsByEdge.TryGetValue(revSubId, out var revEdgeParcels))
                    {
                        revEdgeParcels = new List<RoadsideParcel>();
                        _parcelsByEdge[revSubId] = revEdgeParcels;
                    }
                    if (!revEdgeParcels.Contains(p))
                    {
                        revEdgeParcels.Add(p);
                    }
                }
            }
            else
            {
                RemoveParcelInternal(p);
            }
        }

        // 5. Generate fresh parcels for the new segments
        if (subEdge1 != null) GenerateParcelsForEdge(subEdge1, roadGraph);
        if (subEdge2 != null) GenerateParcelsForEdge(subEdge2, roadGraph);
        RefreshParcels(roadGraph);
    }

    /// <summary>
    /// Checks whether a roadside parcel's physical boundary polygon collides with a road edge curve.
    /// </summary>
    public static bool DoesParcelCollideWithRoad(RoadsideParcel parcel, RoadEdge edge)
    {
        if (parcel?.Boundary == null || parcel.Boundary.Length < 3 || edge?.Curve == null)
            return false;

        // Quick distance check from parcel center to edge curve
        var (t, closestPt, dist) = edge.Curve.GetClosestPoint(parcel.Center);
        if (dist > parcel.BoundingRadius + RoadOffset)
            return false;

        if (parcel.ContainsPoint(closestPt))
            return true;

        float effectiveRadius = RoadOffset - 0.5f;

        // Sample points along the curve near the parcel
        int samples = Math.Clamp((int)Math.Ceiling(edge.Curve.Length / 8f), 16, 64);
        for (int s = 0; s <= samples; s++)
        {
            float st = s / (float)samples;
            Vector2 pt = edge.Curve.Evaluate(st);
            if (pt.DistanceSquaredTo(parcel.Center) > (parcel.BoundingRadius + RoadOffset) * (parcel.BoundingRadius + RoadOffset))
                continue;

            if (parcel.ContainsPoint(pt))
                return true;

            for (int b = 0; b < parcel.Boundary.Length; b++)
            {
                Vector2 p1 = parcel.Boundary[b];
                Vector2 p2 = parcel.Boundary[(b + 1) % parcel.Boundary.Length];
                if (DistanceToSegment(pt, p1, p2) < effectiveRadius)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float lenSq = ab.LengthSquared();
        if (lenSq < 1e-6f) return p.DistanceTo(a);
        float t = Mathf.Clamp((p - a).Dot(ab) / lenSq, 0f, 1f);
        Vector2 proj = a + t * ab;
        return p.DistanceTo(proj);
    }

    /// <summary>
    /// Checks whether a roadside parcel's physical boundary polygon collides with intersecting road geometry at an intersection node.
    /// </summary>
    public static bool DoesParcelCollideWithIntersection(RoadsideParcel parcel, RoadNode junction, List<RoadEdge> intersectingEdges)
    {
        if (parcel?.Boundary == null || parcel.Boundary.Length < 3 || junction == null) return false;

        // Direct hit on the junction center point
        if (parcel.ContainsPoint(junction.WorldPosition)) return true;

        if (intersectingEdges == null || intersectingEdges.Count == 0)
        {
            return false;
        }

        for (int i = 0; i < intersectingEdges.Count; i++)
        {
            if (DoesParcelCollideWithRoad(parcel, intersectingEdges[i]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Computes the active traffic extent [0, T_max] along a road edge.
    /// </summary>
    public float GetEdgeTrafficExtent(RoadEdge edge, RoadGraph roadGraph = null)
    {
        var graph = roadGraph ?? _roadGraph;
        if (graph != null && edge != null)
        {
            return graph.GetEdgeTrafficExtent(edge, this);
        }
        return 1.0f;
    }

    /// <summary>
    /// Computes the active traffic extent [0, T_max] along a road edge by ID.
    /// </summary>
    public float GetEdgeTrafficExtent(int edgeId, RoadGraph roadGraph = null)
    {
        var graph = roadGraph ?? _roadGraph;
        if (graph != null)
        {
            return graph.GetEdgeTrafficExtent(edgeId, this);
        }
        return 1.0f;
    }

    /// <summary>
    /// Queries the roadside zoning target at a given world position in O(1) time via <see cref="SpatialGridIndex"/>,
    /// detecting whether a specific parcel or an entire edge side is targeted.
    /// </summary>
    public (RoadsideParcel Parcel, RoadEdge Edge, ParcelSide Side, List<RoadsideParcel> SideParcels) QueryZoningTarget(
        Vector2 worldPosition,
        RoadGraph roadGraph,
        bool selectFullSide = false)
    {
        if (roadGraph != null)
        {
            BindSpatialIndex(roadGraph);
        }

        // 1. Direct hit on parcel polygon via O(1) SpatialGridIndex lookup
        var hitParcel = GetParcelAt(worldPosition);
        if (hitParcel != null)
        {
            RoadEdge edge = null;
            if (roadGraph != null && hitParcel.EdgeId >= 0 && hitParcel.EdgeId < roadGraph.Edges.Count)
            {
                edge = roadGraph.Edges[hitParcel.EdgeId];
            }

            var sideParcels = selectFullSide
                ? GetParcelsForEdge(hitParcel.EdgeId, hitParcel.Side)
                : new List<RoadsideParcel> { hitParcel };

            return (hitParcel, edge, hitParcel.Side, sideParcels);
        }

        // 2. Proximity search near active road edges in adjacent SpatialGridIndex cells
        if (roadGraph != null)
        {
            float searchRadius = RoadOffset + DefaultLotDepth + 20f;
            float bestDist = searchRadius;
            RoadEdge bestEdge = null;
            float bestT = 0f;
            ParcelSide bestSide = ParcelSide.Right;

            roadGraph.SpatialIndex.QueryEdgesInRadius(worldPosition, searchRadius, _edgeQueryBuffer);
            for (int i = 0; i < _edgeQueryBuffer.Count; i++)
            {
                var edge = roadGraph.GetEdge(_edgeQueryBuffer[i]);
                if (edge == null || edge.FromId == -1 || edge.ToId == -1 || edge.Curve == null) continue;

                var (t, closestPt, dist) = edge.Curve.GetClosestPoint(worldPosition);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestEdge = edge;
                    bestT = t;
                    bestSide = GetSideOfPoint(edge.Curve, worldPosition);
                }
            }

            if (bestEdge != null)
            {
                var edgeParcels = GetParcelsForEdge(bestEdge.Id, bestSide);
                if (edgeParcels.Count > 0)
                {
                    if (selectFullSide)
                    {
                        return (edgeParcels[0], bestEdge, bestSide, edgeParcels);
                    }

                    // Find closest parcel along curve t
                    RoadsideParcel closestP = edgeParcels[0];
                    float closestTDiff = Math.Abs(closestP.NormalizedT - bestT);
                    for (int p = 1; p < edgeParcels.Count; p++)
                    {
                        float diff = Math.Abs(edgeParcels[p].NormalizedT - bestT);
                        if (diff < closestTDiff)
                        {
                            closestTDiff = diff;
                            closestP = edgeParcels[p];
                        }
                    }

                    return (closestP, bestEdge, bestSide, new List<RoadsideParcel> { closestP });
                }
            }
        }

        return (null, null, ParcelSide.Right, new List<RoadsideParcel>());
    }

    /// <summary>
    /// Checks whether two roadside parcels collide or overlap in their lot boundaries.
    /// Performs quick-rejection via bounding circle clearance before precise SAT testing.
    /// </summary>
    public static bool ParcelsOverlap(RoadsideParcel a, RoadsideParcel b, float overlapTolerance = 0.5f)
    {
        if (a == null || b == null) return false;
        if (a.Boundary == null || b.Boundary == null || a.Boundary.Length < 3 || b.Boundary.Length < 3) return false;

        // Quick rejection via bounding radii
        float radA = a.BoundingRadius;
        float radB = b.BoundingRadius;
        float distSq = a.Center.DistanceSquaredTo(b.Center);
        float maxDist = radA + radB;
        if (distSq > maxDist * maxDist)
            return false;

        return PolygonsOverlap(a.Boundary, b.Boundary, overlapTolerance);
    }

    /// <summary>
    /// Determines whether two oriented convex 2D polygons overlap in their interiors.
    /// Uses the Separating Axis Theorem (SAT) with an overlap tolerance to allow shared boundary edges.
    /// </summary>
    public static bool PolygonsOverlap(Vector2[] polyA, Vector2[] polyB, float overlapTolerance = 0.5f)
    {
        if (polyA == null || polyB == null || polyA.Length < 3 || polyB.Length < 3)
            return false;

        // Check edge normals of polygon A
        if (HasSeparatingAxis(polyA, polyB, overlapTolerance))
            return false;

        // Check edge normals of polygon B
        if (HasSeparatingAxis(polyB, polyA, overlapTolerance))
            return false;

        return true;
    }

    private static bool HasSeparatingAxis(Vector2[] polyA, Vector2[] polyB, float overlapTolerance)
    {
        int countA = polyA.Length;
        for (int i = 0; i < countA; i++)
        {
            Vector2 p1 = polyA[i];
            Vector2 p2 = polyA[(i + 1) % countA];
            Vector2 edge = p2 - p1;
            if (edge.LengthSquared() < 1e-6f) continue;

            // Normal perpendicular to edge
            Vector2 axis = new Vector2(-edge.Y, edge.X).Normalized();

            // Project polyA onto axis
            float minA = polyA[0].Dot(axis);
            float maxA = minA;
            for (int a = 1; a < polyA.Length; a++)
            {
                float proj = polyA[a].Dot(axis);
                if (proj < minA) minA = proj;
                if (proj > maxA) maxA = proj;
            }

            // Project polyB onto axis
            float minB = polyB[0].Dot(axis);
            float maxB = minB;
            for (int b = 1; b < polyB.Length; b++)
            {
                float proj = polyB[b].Dot(axis);
                if (proj < minB) minB = proj;
                if (proj > maxB) maxB = proj;
            }

            // If intervals do not overlap by more than overlapTolerance, this axis separates them
            float overlap = Mathf.Min(maxA, maxB) - Mathf.Max(minA, minB);
            if (overlap <= overlapTolerance)
            {
                return true;
            }
        }

        return false;
    }

    // =========================================================================
    // PHASE 4: LUTI (Land-Use & Transport Interaction) DENSITY EVOLUTION
    // =========================================================================

    /// <summary>Congestion ratio threshold (R_congestion &gt; 1.5) above which parcels stagnate.</summary>
    public const float CongestionStagnationThreshold = 1.5f;

    /// <summary>Minimum normalized Hansen AccessibilityScore required to upgrade from Low to Medium density.</summary>
    public const float MediumDensityAccessibilityThreshold = 0.85f;

    /// <summary>Minimum normalized Hansen AccessibilityScore required to upgrade from Medium to High density.</summary>
    public const float HighDensityAccessibilityThreshold = 1.25f;

    /// <summary>
    /// Evaluates LUTI population growth and automatic density evolution (Low -&gt; Medium -&gt; High) across all active parcels.
    /// Parcels with high <see cref="RoadsideParcel.AccessibilityScore"/> fill faster and promote to higher density tiers,
    /// while parcels gridlocked in severe traffic congestion (R_congestion &gt; 1.5) stagnate.
    /// </summary>
    public (bool populationChanged, bool densityUpgraded) ProcessLutiEvolution(
        RoadGraph roadGraph,
        TazManager tazManager = null,
        HashSet<int> reachableEntranceNodes = null)
    {
        if (roadGraph == null) return (false, false);

        bool popChanged = false;
        bool densityUpgraded = false;
        bool checkReachability = reachableEntranceNodes != null && reachableEntranceNodes.Count > 0;

        for (int i = 0; i < _parcels.Count; i++)
        {
            var parcel = _parcels[i];
            if (parcel == null || parcel.ZoneType == ZoneType.Empty) continue;
            if (parcel.EdgeId < 0 || parcel.EdgeId >= roadGraph.Edges.Count) continue;

            var edge = roadGraph.Edges[parcel.EdgeId];
            if (edge == null || edge.FromId == -1 || edge.ToId == -1) continue;

            if (checkReachability)
            {
                bool reachable = reachableEntranceNodes.Contains(edge.FromId) ||
                                 reachableEntranceNodes.Contains(edge.ToId) ||
                                 (parcel.AccessNodeId >= 0 && reachableEntranceNodes.Contains(parcel.AccessNodeId));
                if (!reachable) continue;
            }

            // 1. Synchronize LUTI accessibility score and local congestion ratio from TAZ and fronting road
            if (tazManager != null)
            {
                int tazId = tazManager.GetTazIdForParcel(parcel.Id);
                var taz = tazManager.GetTaz(tazId);
                if (taz != null)
                {
                    parcel.AccessibilityScore = taz.AccessibilityScore;
                    float edgeCong = OptimizedODMatrix.GetParcelEdgeCongestion(parcel, roadGraph);
                    parcel.LocalCongestion = MathF.Max(edgeCong, taz.AverageCongestion);
                }
                else
                {
                    parcel.LocalCongestion = OptimizedODMatrix.GetParcelEdgeCongestion(parcel, roadGraph);
                }
            }
            else
            {
                float edgeCong = OptimizedODMatrix.GetParcelEdgeCongestion(parcel, roadGraph);
                if (edgeCong > 0f)
                {
                    parcel.LocalCongestion = MathF.Max(parcel.LocalCongestion, edgeCong);
                }
            }

            // 2. Stagnation check: districts blocked in severe traffic jams (R_congestion > 1.5) stagnate
            if (parcel.LocalCongestion > CongestionStagnationThreshold)
            {
                continue;
            }

            float accessScore = Math.Clamp(parcel.AccessibilityScore, 0.0f, 2.0f);
            if (accessScore <= 0.01f) continue;

            if (parcel.ZoneType == ZoneType.Residential)
            {
                float congDamp = parcel.LocalCongestion > 1.0f
                    ? (1.0f - 0.5f * (parcel.LocalCongestion - 1.0f))
                    : 1.0f;

                int growthStep = Math.Max(1, (int)MathF.Round(4.0f * (0.4f + 1.2f * accessScore) * congDamp));

                if (parcel.Population < parcel.ResidentialCap)
                {
                    parcel.Population = Math.Min(parcel.ResidentialCap, parcel.Population + growthStep);
                    popChanged = true;
                }

                // Automatic density upgrade (Low -> Medium -> High) when occupancy >= 85% and accessibility is sufficient
                if (parcel.ResidentialCap > 0 && parcel.Population >= (int)(parcel.ResidentialCap * 0.85f))
                {
                    if (parcel.Density == DensityLevel.Low && accessScore >= MediumDensityAccessibilityThreshold)
                    {
                        if (parcel.UpgradeDensity())
                        {
                            densityUpgraded = true;
                            popChanged = true;
                        }
                    }
                    else if (parcel.Density == DensityLevel.Medium && accessScore >= HighDensityAccessibilityThreshold)
                    {
                        if (parcel.UpgradeDensity())
                        {
                            densityUpgraded = true;
                            popChanged = true;
                        }
                    }
                }
            }
            else if (parcel.ZoneType == ZoneType.Commercial || parcel.ZoneType == ZoneType.Industrial)
            {
                if (parcel.Density == DensityLevel.Low && accessScore >= MediumDensityAccessibilityThreshold)
                {
                    if (parcel.UpgradeDensity())
                    {
                        densityUpgraded = true;
                    }
                }
                else if (parcel.Density == DensityLevel.Medium && accessScore >= HighDensityAccessibilityThreshold)
                {
                    if (parcel.UpgradeDensity())
                    {
                        densityUpgraded = true;
                    }
                }
            }
        }

        return (popChanged, densityUpgraded);
    }
}
