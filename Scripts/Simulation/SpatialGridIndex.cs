using System;
using System.Collections.Generic;
using Godot;

namespace CitySim.Simulation;

/// <summary>
/// Uniform 2D spatial hash grid index (default cell size 128x128 meters/pixels)
/// for O(1) spatial queries over RoadNodes, RoadEdges (by AABB), and RoadsideParcels (by AABB).
/// Eliminates O(V), O(E), and O(P) linear scans during road construction, cursor snapping,
/// roadside parcel zoning, collision detection, and TAZ centroid connector binding.
/// </summary>
public sealed class SpatialGridIndex
{
    /// <summary>Default spatial cell size in world units (128x128 meters/pixels).</summary>
    public const float DefaultCellSize = 128.0f;

    private sealed class SpatialBucket
    {
        public readonly List<int> NodeIds = new(8);
        public readonly List<int> EdgeIds = new(8);
        public readonly List<int> ParcelIds = new(16);

        public bool IsEmpty => NodeIds.Count == 0 && EdgeIds.Count == 0 && ParcelIds.Count == 0;
    }

    private readonly Dictionary<Vector2I, SpatialBucket> _buckets = new();
    private readonly Dictionary<int, Vector2I> _nodeToCell = new();
    private readonly Dictionary<int, (Vector2I Min, Vector2I Max)> _edgeToCellRange = new();
    private readonly Dictionary<int, (Vector2I Min, Vector2I Max)> _parcelToCellRange = new();

    [ThreadStatic]
    private static HashSet<int> _tlsDedupSet;

    /// <summary>Side length of each uniform grid cell in world units.</summary>
    public float CellSize { get; }

    /// <summary>Total number of currently indexed road nodes.</summary>
    public int IndexedNodeCount => _nodeToCell.Count;

    /// <summary>Total number of currently indexed road edges.</summary>
    public int IndexedEdgeCount => _edgeToCellRange.Count;

    /// <summary>Total number of currently indexed roadside parcels.</summary>
    public int IndexedParcelCount => _parcelToCellRange.Count;

    /// <summary>Total number of active non-empty spatial cells.</summary>
    public int ActiveBucketCount => _buckets.Count;

    public SpatialGridIndex(float cellSize = DefaultCellSize)
    {
        CellSize = cellSize > 1.0f ? cellSize : DefaultCellSize;
    }

    /// <summary>
    /// Converts a 2D world coordinate into discrete grid cell coordinates.
    /// </summary>
    public Vector2I WorldToCell(Vector2 worldPos)
    {
        return new Vector2I(
            Mathf.FloorToInt(worldPos.X / CellSize),
            Mathf.FloorToInt(worldPos.Y / CellSize)
        );
    }

    /// <summary>
    /// Converts a world-space AABB (min, max) into inclusive grid cell coordinates (minCell, maxCell).
    /// </summary>
    public (Vector2I Min, Vector2I Max) WorldAabbToCellRange(Vector2 minWorld, Vector2 maxWorld)
    {
        float minX = Mathf.Min(minWorld.X, maxWorld.X);
        float minY = Mathf.Min(minWorld.Y, maxWorld.Y);
        float maxX = Mathf.Max(minWorld.X, maxWorld.X);
        float maxY = Mathf.Max(minWorld.Y, maxWorld.Y);

        return (
            new Vector2I(Mathf.FloorToInt(minX / CellSize), Mathf.FloorToInt(minY / CellSize)),
            new Vector2I(Mathf.FloorToInt(maxX / CellSize), Mathf.FloorToInt(maxY / CellSize))
        );
    }

    /// <summary>
    /// Clears all indexed nodes, edges, and parcels from the spatial grid.
    /// </summary>
    public void Clear()
    {
        _buckets.Clear();
        _nodeToCell.Clear();
        _edgeToCellRange.Clear();
        _parcelToCellRange.Clear();
    }

    /// <summary>
    /// Clears only road nodes and edges while preserving indexed parcels.
    /// </summary>
    public void ClearNodesAndEdges()
    {
        foreach (var kvp in _nodeToCell)
        {
            if (_buckets.TryGetValue(kvp.Value, out var bucket))
            {
                bucket.NodeIds.Clear();
                if (bucket.IsEmpty) _buckets.Remove(kvp.Value);
            }
        }
        _nodeToCell.Clear();

        foreach (var kvp in _edgeToCellRange)
        {
            var (min, max) = kvp.Value;
            for (int y = min.Y; y <= max.Y; y++)
            {
                for (int x = min.X; x <= max.X; x++)
                {
                    var cell = new Vector2I(x, y);
                    if (_buckets.TryGetValue(cell, out var bucket))
                    {
                        bucket.EdgeIds.Clear();
                        if (bucket.IsEmpty) _buckets.Remove(cell);
                    }
                }
            }
        }
        _edgeToCellRange.Clear();
    }

    /// <summary>
    /// Clears all indexed parcels while preserving road nodes and edges.
    /// </summary>
    public void ClearParcels()
    {
        foreach (var kvp in _parcelToCellRange)
        {
            var (min, max) = kvp.Value;
            for (int y = min.Y; y <= max.Y; y++)
            {
                for (int x = min.X; x <= max.X; x++)
                {
                    var cell = new Vector2I(x, y);
                    if (_buckets.TryGetValue(cell, out var bucket))
                    {
                        bucket.ParcelIds.Clear();
                        if (bucket.IsEmpty) _buckets.Remove(cell);
                    }
                }
            }
        }
        _parcelToCellRange.Clear();
    }

    // =========================================================================
    // NODE OPERATIONS
    // =========================================================================

    /// <summary>
    /// Inserts or updates a RoadNode in the spatial grid index.
    /// </summary>
    public void AddNode(RoadNode node)
    {
        if (node == null) return;
        AddNode(node.Id, node.WorldPosition);
    }

    /// <summary>
    /// Inserts or updates a node ID at the given world position.
    /// </summary>
    public void AddNode(int nodeId, Vector2 worldPosition)
    {
        if (nodeId < 0) return;

        var newCell = WorldToCell(worldPosition);
        if (_nodeToCell.TryGetValue(nodeId, out var oldCell))
        {
            if (oldCell == newCell) return;
            RemoveNode(nodeId);
        }

        _nodeToCell[nodeId] = newCell;
        var bucket = GetOrCreateBucket(newCell);
        if (!bucket.NodeIds.Contains(nodeId))
        {
            bucket.NodeIds.Add(nodeId);
        }
    }

    /// <summary>
    /// Removes a node ID from the spatial grid index in O(1) time.
    /// </summary>
    public bool RemoveNode(int nodeId)
    {
        if (!_nodeToCell.TryGetValue(nodeId, out var cell))
            return false;

        _nodeToCell.Remove(nodeId);
        if (_buckets.TryGetValue(cell, out var bucket))
        {
            bucket.NodeIds.Remove(nodeId);
            if (bucket.IsEmpty)
            {
                _buckets.Remove(cell);
            }
        }
        return true;
    }

    // =========================================================================
    // EDGE OPERATIONS
    // =========================================================================

    /// <summary>
    /// Computes the world-space Axis-Aligned Bounding Box (AABB) of a road edge from its Bézier curve or endpoints.
    /// </summary>
    public static (Vector2 Min, Vector2 Max) ComputeEdgeAabb(RoadEdge edge, RoadGraph graph = null, float margin = 2.0f)
    {
        if (edge?.Curve != null)
        {
            var c = edge.Curve;
            float minX = Mathf.Min(Mathf.Min(c.P0.X, c.P1.X), Mathf.Min(c.P2.X, c.P3.X)) - margin;
            float minY = Mathf.Min(Mathf.Min(c.P0.Y, c.P1.Y), Mathf.Min(c.P2.Y, c.P3.Y)) - margin;
            float maxX = Mathf.Max(Mathf.Max(c.P0.X, c.P1.X), Mathf.Max(c.P2.X, c.P3.X)) + margin;
            float maxY = Mathf.Max(Mathf.Max(c.P0.Y, c.P1.Y), Mathf.Max(c.P2.Y, c.P3.Y)) + margin;
            return (new Vector2(minX, minY), new Vector2(maxX, maxY));
        }

        if (graph != null && edge != null)
        {
            var n1 = graph.GetNode(edge.FromId);
            var n2 = graph.GetNode(edge.ToId);
            if (n1 != null && n2 != null)
            {
                float minX = Mathf.Min(n1.WorldPosition.X, n2.WorldPosition.X) - margin;
                float minY = Mathf.Min(n1.WorldPosition.Y, n2.WorldPosition.Y) - margin;
                float maxX = Mathf.Max(n1.WorldPosition.X, n2.WorldPosition.X) + margin;
                float maxY = Mathf.Max(n1.WorldPosition.Y, n2.WorldPosition.Y) + margin;
                return (new Vector2(minX, minY), new Vector2(maxX, maxY));
            }
        }

        return (Vector2.Zero, Vector2.Zero);
    }

    /// <summary>
    /// Inserts or updates a RoadEdge in all spatial cells overlapped by its AABB.
    /// </summary>
    public void AddEdge(RoadEdge edge, RoadGraph graph = null)
    {
        if (edge == null || edge.Id < 0 || edge.FromId == -1 || edge.ToId == -1)
            return;

        var (minWorld, maxWorld) = ComputeEdgeAabb(edge, graph);
        AddEdge(edge.Id, minWorld, maxWorld);
    }

    /// <summary>
    /// Inserts or updates an edge ID across all spatial cells overlapped by [minWorld, maxWorld].
    /// </summary>
    public void AddEdge(int edgeId, Vector2 minWorld, Vector2 maxWorld)
    {
        if (edgeId < 0) return;

        if (_edgeToCellRange.ContainsKey(edgeId))
        {
            RemoveEdge(edgeId);
        }

        var (minCell, maxCell) = WorldAabbToCellRange(minWorld, maxWorld);
        _edgeToCellRange[edgeId] = (minCell, maxCell);

        for (int y = minCell.Y; y <= maxCell.Y; y++)
        {
            for (int x = minCell.X; x <= maxCell.X; x++)
            {
                var bucket = GetOrCreateBucket(new Vector2I(x, y));
                if (!bucket.EdgeIds.Contains(edgeId))
                {
                    bucket.EdgeIds.Add(edgeId);
                }
            }
        }
    }

    /// <summary>
    /// Removes an edge ID from all spatial cells it was registered in.
    /// </summary>
    public bool RemoveEdge(int edgeId)
    {
        if (!_edgeToCellRange.TryGetValue(edgeId, out var range))
            return false;

        _edgeToCellRange.Remove(edgeId);
        for (int y = range.Min.Y; y <= range.Max.Y; y++)
        {
            for (int x = range.Min.X; x <= range.Max.X; x++)
            {
                var cell = new Vector2I(x, y);
                if (_buckets.TryGetValue(cell, out var bucket))
                {
                    bucket.EdgeIds.Remove(edgeId);
                    if (bucket.IsEmpty)
                    {
                        _buckets.Remove(cell);
                    }
                }
            }
        }
        return true;
    }

    /// <summary>
    /// Incrementally updates the spatial index when an edge is split at a new junction node.
    /// Removes the disconnected edge and registers the new junction node.
    /// </summary>
    public void SplitEdge(int disconnectedEdgeId, RoadNode junction)
    {
        RemoveEdge(disconnectedEdgeId);
        if (junction != null)
        {
            AddNode(junction);
        }
    }

    // =========================================================================
    // PARCEL OPERATIONS
    // =========================================================================

    /// <summary>
    /// Computes the world-space AABB of a RoadsideParcel from its boundary vertices.
    /// </summary>
    public static (Vector2 Min, Vector2 Max) ComputeParcelAabb(RoadsideParcel parcel, float margin = 1.0f)
    {
        if (parcel?.Boundary != null && parcel.Boundary.Length > 0)
        {
            float minX = parcel.Boundary[0].X;
            float minY = parcel.Boundary[0].Y;
            float maxX = minX;
            float maxY = minY;

            for (int i = 1; i < parcel.Boundary.Length; i++)
            {
                Vector2 pt = parcel.Boundary[i];
                if (pt.X < minX) minX = pt.X;
                if (pt.Y < minY) minY = pt.Y;
                if (pt.X > maxX) maxX = pt.X;
                if (pt.Y > maxY) maxY = pt.Y;
            }

            return (new Vector2(minX - margin, minY - margin), new Vector2(maxX + margin, maxY + margin));
        }

        Vector2 fallback = parcel?.AccessPoint ?? Vector2.Zero;
        return (fallback - new Vector2(margin, margin), fallback + new Vector2(margin, margin));
    }

    /// <summary>
    /// Inserts or updates a RoadsideParcel in all spatial cells overlapped by its boundary AABB.
    /// </summary>
    public void AddParcel(RoadsideParcel parcel)
    {
        if (parcel == null || parcel.Id <= 0)
            return;

        var (minWorld, maxWorld) = ComputeParcelAabb(parcel);
        AddParcel(parcel.Id, minWorld, maxWorld);
    }

    /// <summary>
    /// Inserts or updates a parcel ID across all spatial cells overlapped by [minWorld, maxWorld].
    /// </summary>
    public void AddParcel(int parcelId, Vector2 minWorld, Vector2 maxWorld)
    {
        if (parcelId <= 0) return;

        if (_parcelToCellRange.ContainsKey(parcelId))
        {
            RemoveParcel(parcelId);
        }

        var (minCell, maxCell) = WorldAabbToCellRange(minWorld, maxWorld);
        _parcelToCellRange[parcelId] = (minCell, maxCell);

        for (int y = minCell.Y; y <= maxCell.Y; y++)
        {
            for (int x = minCell.X; x <= maxCell.X; x++)
            {
                var bucket = GetOrCreateBucket(new Vector2I(x, y));
                if (!bucket.ParcelIds.Contains(parcelId))
                {
                    bucket.ParcelIds.Add(parcelId);
                }
            }
        }
    }

    /// <summary>
    /// Removes a parcel ID from all spatial cells it was registered in.
    /// </summary>
    public bool RemoveParcel(int parcelId)
    {
        if (!_parcelToCellRange.TryGetValue(parcelId, out var range))
            return false;

        _parcelToCellRange.Remove(parcelId);
        for (int y = range.Min.Y; y <= range.Max.Y; y++)
        {
            for (int x = range.Min.X; x <= range.Max.X; x++)
            {
                var cell = new Vector2I(x, y);
                if (_buckets.TryGetValue(cell, out var bucket))
                {
                    bucket.ParcelIds.Remove(parcelId);
                    if (bucket.IsEmpty)
                    {
                        _buckets.Remove(cell);
                    }
                }
            }
        }
        return true;
    }

    // =========================================================================
    // SPATIAL QUERIES (O(1) CELL LOOKUPS)
    // =========================================================================

    /// <summary>
    /// Queries all unique NodeIds, EdgeIds, and ParcelIds overlapping the specified world-space AABB Rect2.
    /// Pass null for any output list that is not needed.
    /// </summary>
    public void QueryAabb(Rect2 bbox, List<int> nodeIds = null, List<int> edgeIds = null, List<int> parcelIds = null)
    {
        Vector2 min = bbox.Position;
        Vector2 max = bbox.Position + bbox.Size;
        if (nodeIds != null) QueryNodesAabb(min, max, nodeIds);
        if (edgeIds != null) QueryEdgesAabb(min, max, edgeIds);
        if (parcelIds != null) QueryParcelsAabb(min, max, parcelIds);
    }

    /// <summary>
    /// Populates <paramref name="results"/> with all node IDs inside the cells overlapping [minWorld, maxWorld].
    /// Since each node resides in exactly one cell, no deduplication set is needed.
    /// </summary>
    public void QueryNodesAabb(Vector2 minWorld, Vector2 maxWorld, List<int> results)
    {
        results.Clear();
        var (minCell, maxCell) = WorldAabbToCellRange(minWorld, maxWorld);

        for (int y = minCell.Y; y <= maxCell.Y; y++)
        {
            for (int x = minCell.X; x <= maxCell.X; x++)
            {
                if (_buckets.TryGetValue(new Vector2I(x, y), out var bucket))
                {
                    results.AddRange(bucket.NodeIds);
                }
            }
        }
    }

    /// <summary>
    /// Populates <paramref name="results"/> with all node IDs in thecells overlapping a circle of <paramref name="radius"/> around <paramref name="center"/>.
    /// </summary>
    public void QueryNodesInRadius(Vector2 center, float radius, List<int> results)
    {
        Vector2 ext = new Vector2(radius, radius);
        QueryNodesAabb(center - ext, center + ext, results);
    }

    /// <summary>
    /// Populates <paramref name="results"/> with all node IDs in the (2*cellRadius + 1) x (2*cellRadius + 1) neighborhood
    /// around the cell containing <paramref name="worldPos"/> (e.g., cellRadius = 1 queries the 3x3 cell block).
    /// </summary>
    public void QueryNodesInCellNeighborhood(Vector2 worldPos, int cellRadius, List<int> results)
    {
        results.Clear();
        Vector2I centerCell = WorldToCell(worldPos);
        for (int dy = -cellRadius; dy <= cellRadius; dy++)
        {
            for (int dx = -cellRadius; dx <= cellRadius; dx++)
            {
                if (_buckets.TryGetValue(new Vector2I(centerCell.X + dx, centerCell.Y + dy), out var bucket))
                {
                    results.AddRange(bucket.NodeIds);
                }
            }
        }
    }

    /// <summary>
    /// Populates <paramref name="results"/> with unique edge IDs overlapping the cell range of [minWorld, maxWorld].
    /// </summary>
    public void QueryEdgesAabb(Vector2 minWorld, Vector2 maxWorld, List<int> results)
    {
        results.Clear();
        var (minCell, maxCell) = WorldAabbToCellRange(minWorld, maxWorld);

        if (minCell == maxCell)
        {
            if (_buckets.TryGetValue(minCell, out var singleBucket))
            {
                results.AddRange(singleBucket.EdgeIds);
            }
            return;
        }

        var seen = GetDedupSet();
        for (int y = minCell.Y; y <= maxCell.Y; y++)
        {
            for (int x = minCell.X; x <= maxCell.X; x++)
            {
                if (_buckets.TryGetValue(new Vector2I(x, y), out var bucket))
                {
                    var edgeIds = bucket.EdgeIds;
                    for (int i = 0; i < edgeIds.Count; i++)
                    {
                        int eid = edgeIds[i];
                        if (seen.Add(eid))
                        {
                            results.Add(eid);
                        }
                    }
                }
            }
        }
        seen.Clear();
    }

    /// <summary>
    /// Populates <paramref name="results"/> with unique edge IDs overlapping a square of <paramref name="radius"/> around <paramref name="center"/>.
    /// </summary>
    public void QueryEdgesInRadius(Vector2 center, float radius, List<int> results)
    {
        Vector2 ext = new Vector2(radius, radius);
        QueryEdgesAabb(center - ext, center + ext, results);
    }

    /// <summary>
    /// Populates <paramref name="results"/> with unique parcel IDs overlapping the cell range of [minWorld, maxWorld].
    /// </summary>
    public void QueryParcelsAabb(Vector2 minWorld, Vector2 maxWorld, List<int> results)
    {
        results.Clear();
        var (minCell, maxCell) = WorldAabbToCellRange(minWorld, maxWorld);

        if (minCell == maxCell)
        {
            if (_buckets.TryGetValue(minCell, out var singleBucket))
            {
                results.AddRange(singleBucket.ParcelIds);
            }
            return;
        }

        var seen = GetDedupSet();
        for (int y = minCell.Y; y <= maxCell.Y; y++)
        {
            for (int x = minCell.X; x <= maxCell.X; x++)
            {
                if (_buckets.TryGetValue(new Vector2I(x, y), out var bucket))
                {
                    var parcelIds = bucket.ParcelIds;
                    for (int i = 0; i < parcelIds.Count; i++)
                    {
                        int pid = parcelIds[i];
                        if (seen.Add(pid))
                        {
                            results.Add(pid);
                        }
                    }
                }
            }
        }
        seen.Clear();
    }

    /// <summary>
    /// Populates <paramref name="results"/> with unique parcel IDs in the cell containing <paramref name="worldPosition"/>.
    /// </summary>
    public void QueryParcelsAtPoint(Vector2 worldPosition, List<int> results)
    {
        results.Clear();
        var cell = WorldToCell(worldPosition);
        if (_buckets.TryGetValue(cell, out var bucket))
        {
            results.AddRange(bucket.ParcelIds);
        }
    }

    /// <summary>
    /// Populates <paramref name="results"/> with unique parcel IDs overlapping a square of <paramref name="radius"/> around <paramref name="center"/>.
    /// </summary>
    public void QueryParcelsInRadius(Vector2 center, float radius, List<int> results)
    {
        Vector2 ext = new Vector2(radius, radius);
        QueryParcelsAabb(center - ext, center + ext, results);
    }

    /// <summary>
    /// Full rebuild of nodes, edges, and optionally parcels from the given RoadGraph and ParcelManager.
    /// </summary>
    public void Rebuild(RoadGraph roadGraph, ParcelManager parcelManager = null)
    {
        Clear();

        if (roadGraph != null)
        {
            for (int i = 0; i < roadGraph.Nodes.Count; i++)
            {
                var node = roadGraph.Nodes[i];
                if (node != null)
                {
                    AddNode(node);
                }
            }

            for (int i = 0; i < roadGraph.Edges.Count; i++)
            {
                var edge = roadGraph.Edges[i];
                if (edge != null && edge.FromId != -1 && edge.ToId != -1)
                {
                    AddEdge(edge, roadGraph);
                }
            }
        }

        if (parcelManager != null)
        {
            var parcels = parcelManager.Parcels;
            for (int i = 0; i < parcels.Count; i++)
            {
                AddParcel(parcels[i]);
            }
        }
    }

    private SpatialBucket GetOrCreateBucket(Vector2I cell)
    {
        if (!_buckets.TryGetValue(cell, out var bucket))
        {
            bucket = new SpatialBucket();
            _buckets[cell] = bucket;
        }
        return bucket;
    }

    private static HashSet<int> GetDedupSet()
    {
        var set = _tlsDedupSet ??= new HashSet<int>(64);
        set.Clear();
        return set;
    }
}
