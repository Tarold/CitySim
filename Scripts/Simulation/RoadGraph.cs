using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CitySim.Simulation;

public class RoadNode
{
    public int Id;
    public Vector2 WorldPosition;
    public int ZoneId;
}

public class RoadEdge
{
    public int Id;
    public int FromId;
    public int ToId;
    public float Length;
    public float Capacity;
    public float FreeFlowSpeed;
    public float CurrentVolume;
    public int Lanes;
    public CurveSegment Curve;

    // Pedestrian flow attributes
    public float PedestrianVolume;
    public float PedestrianCapacity = 1800f; // ped/hour
    public float WalkingSpeed = 5.0f; // km/h (1.39 m/s, matching FreeFlowSpeed unit scale)

    public float GetTravelTime()
    {
        return (Length / FreeFlowSpeed) * (1f + 0.15f * Mathf.Pow(CurrentVolume / Mathf.Max(Capacity, 1f), 4f));
    }

    public float GetCongestionRatio()
    {
        return CurrentVolume / Mathf.Max(Capacity, 1f);
    }

    public float GetWalkingTravelTime()
    {
        return (Length / WalkingSpeed) * (1f + 0.10f * Mathf.Pow(PedestrianVolume / Mathf.Max(PedestrianCapacity, 1f), 2f));
    }

    public float GetPedestrianCongestionRatio()
    {
        return PedestrianVolume / Mathf.Max(PedestrianCapacity, 1f);
    }
}

public class RoadGraph
{
    public List<RoadNode> Nodes = new List<RoadNode>();
    public List<RoadEdge> Edges = new List<RoadEdge>();
    public Dictionary<int, RoadNode> NodeMap = new Dictionary<int, RoadNode>();
    public Dictionary<int, List<int>> AdjacencyEdges = new Dictionary<int, List<int>>();
    public Dictionary<(int, int), List<int>> PathCache = new Dictionary<(int, int), List<int>>();
    public Dictionary<(int, int), List<int>> WalkingPathCache = new Dictionary<(int, int), List<int>>();
    private readonly Dictionary<int, (int FromId, int ToId, RoadNode Junction)> _splitInfo =
        new Dictionary<int, (int, int, RoadNode)>();
    private readonly Dictionary<int, int> _splitReverseEdges = new Dictionary<int, int>();

    public event Action<RoadEdge> EdgeAdded;
    public event Action<int> EdgeRemoved;
    public event Action<int, RoadNode> EdgeSplit;

    public RoadNode GetNode(int id) => NodeMap.TryGetValue(id, out var n) ? n : null;

    /// <summary>
    /// Retrieves the split information (fromId, toId, junction) for a split edge, if any.
    /// </summary>
    public (int FromId, int ToId, RoadNode Junction)? GetSplitInfo(int edgeId)
    {
        return _splitInfo.TryGetValue(edgeId, out var info) ? info : null;
    }

    /// <summary>
    /// Retrieves the junction node created by splitting an edge, if any.
    /// </summary>
    public RoadNode GetSplitJunction(int edgeId)
    {
        return _splitInfo.TryGetValue(edgeId, out var info) ? info.Junction : null;
    }

    /// <summary>
    /// Returns the reverse edge ID for the given edge, even if the edge was disconnected by a split.
    /// </summary>
    public int GetReverseEdgeId(int edgeId)
    {
        if (edgeId >= 0 && edgeId < Edges.Count)
        {
            var edge = Edges[edgeId];
            if (edge.FromId != -1 && edge.ToId != -1)
            {
                return FindEdgeId(edge.ToId, edge.FromId);
            }
        }
        if (_splitReverseEdges.TryGetValue(edgeId, out int revId))
        {
            return revId;
        }
        return -1;
    }

    public void BuildFromGrid(CityGrid grid)
    {
        Nodes.Clear();
        Edges.Clear();
        NodeMap.Clear();
        AdjacencyEdges.Clear();
        PathCache.Clear();
        _splitInfo.Clear();
        _splitReverseEdges.Clear();

        for (int i = 0; i < grid.ZoneCount; i++)
        {
            var zone = grid.GetZone(i);
            if (zone.Type != ZoneType.Empty)
            {
                var node = new RoadNode
                {
                    Id = zone.Id,
                    WorldPosition = grid.GetWorldCenter(zone.Id),
                    ZoneId = zone.Id
                };
                Nodes.Add(node);
                NodeMap[node.Id] = node;
                AdjacencyEdges[zone.Id] = new List<int>();
            }
        }

        int edgeIdCounter = 0;
        for (int y = 0; y < grid.Height; y++)
        {
            for (int x = 0; x < grid.Width; x++)
            {
                int id1 = grid.GetZoneId(x, y);
                var z1 = grid.GetZone(id1);
                if (z1.Type == ZoneType.Empty) continue;

                if (x < grid.Width - 1)
                {
                    int id2 = grid.GetZoneId(x + 1, y);
                    var z2 = grid.GetZone(id2);
                    if (z2.Type != ZoneType.Empty)
                    {
                        AddBidirectionalEdge(ref edgeIdCounter, z1, z2, grid.CellSize);
                    }
                }

                if (y < grid.Height - 1)
                {
                    int id2 = grid.GetZoneId(x, y + 1);
                    var z2 = grid.GetZone(id2);
                    if (z2.Type != ZoneType.Empty)
                    {
                        AddBidirectionalEdge(ref edgeIdCounter, z1, z2, grid.CellSize);
                    }
                }
            }
        }
    }

    private void AddBidirectionalEdge(ref int edgeIdCounter, Zone z1, Zone z2, float length)
    {
        bool isMain = z1.Type == ZoneType.Commercial || z1.Type == ZoneType.Industrial ||
                      z2.Type == ZoneType.Commercial || z2.Type == ZoneType.Industrial;
        float cap = isMain ? 2000f : 1000f;

        var n1 = NodeMap[z1.Id];
        var n2 = NodeMap[z2.Id];
        var curveFwd = new CurveSegment(n1.WorldPosition, n2.WorldPosition);
        var curveRev = curveFwd.GetReversed();

        var e1 = new RoadEdge
        {
            Id = edgeIdCounter++,
            FromId = z1.Id,
            ToId = z2.Id,
            Length = curveFwd.Length,
            Capacity = cap,
            FreeFlowSpeed = 50f,
            Lanes = 2,
            CurrentVolume = 0f,
            PedestrianCapacity = 1800f,
            WalkingSpeed = 5.0f,
            PedestrianVolume = 0f,
            Curve = curveFwd
        };

        var e2 = new RoadEdge
        {
            Id = edgeIdCounter++,
            FromId = z2.Id,
            ToId = z1.Id,
            Length = curveRev.Length,
            Capacity = cap,
            FreeFlowSpeed = 50f,
            Lanes = 2,
            CurrentVolume = 0f,
            PedestrianCapacity = 1800f,
            WalkingSpeed = 5.0f,
            PedestrianVolume = 0f,
            Curve = curveRev
        };

        Edges.Add(e1);
        Edges.Add(e2);
        AdjacencyEdges[z1.Id].Add(e1.Id);
        AdjacencyEdges[z2.Id].Add(e2.Id);
    }

    private int GetMaxNodeIndex()
    {
        int maxId = 0;
        foreach (var k in AdjacencyEdges.Keys) if (k >= maxId) maxId = k + 1;
        foreach (var k in NodeMap.Keys) if (k >= maxId) maxId = k + 1;
        return maxId;
    }

    public float[] Dijkstra(int sourceNodeId)
    {
        int maxId = GetMaxNodeIndex();
        float[] dist = new float[maxId];
        Array.Fill(dist, float.MaxValue);
        
        if (!AdjacencyEdges.ContainsKey(sourceNodeId) || sourceNodeId >= maxId) return dist;

        dist[sourceNodeId] = 0f;
        var pq = new PriorityQueue<int, float>();
        pq.Enqueue(sourceNodeId, 0f);

        while (pq.Count > 0)
        {
            pq.TryDequeue(out int u, out float d);
            if (d > dist[u]) continue;

            foreach (var edgeId in AdjacencyEdges[u])
            {
                var edge = Edges[edgeId];
                int v = edge.ToId;
                float weight = edge.GetTravelTime();

                if (dist[u] + weight < dist[v])
                {
                    dist[v] = dist[u] + weight;
                    pq.Enqueue(v, dist[v]);
                }
            }
        }

        return dist;
    }

    public float[,] ComputeDistanceMatrix(int zoneCount)
    {
        float[,] matrix = new float[zoneCount, zoneCount];
        for (int i = 0; i < zoneCount; i++)
        {
            if (AdjacencyEdges.ContainsKey(i))
            {
                float[] dists = Dijkstra(i);
                for (int j = 0; j < zoneCount; j++)
                {
                    matrix[i, j] = j < dists.Length ? dists[j] : float.MaxValue;
                }
            }
            else
            {
                for (int j = 0; j < zoneCount; j++)
                {
                    matrix[i, j] = float.MaxValue;
                }
            }
        }
        return matrix;
    }

    public List<int> GetShortestPath(int fromNodeId, int toNodeId)
    {
        if (!AdjacencyEdges.ContainsKey(fromNodeId) || !AdjacencyEdges.ContainsKey(toNodeId)) return null;

        int maxId = GetMaxNodeIndex();
        if (fromNodeId >= maxId || toNodeId >= maxId) return null;

        float[] dist = new float[maxId];
        int[] edgeTo = new int[maxId];
        Array.Fill(dist, float.MaxValue);
        Array.Fill(edgeTo, -1);

        dist[fromNodeId] = 0f;
        var pq = new PriorityQueue<int, float>();
        pq.Enqueue(fromNodeId, 0f);

        while (pq.Count > 0)
        {
            pq.TryDequeue(out int u, out float d);
            if (u == toNodeId) break;
            if (d > dist[u]) continue;

            foreach (var edgeId in AdjacencyEdges[u])
            {
                var edge = Edges[edgeId];
                int v = edge.ToId;
                float weight = edge.GetTravelTime();

                if (dist[u] + weight < dist[v])
                {
                    dist[v] = dist[u] + weight;
                    edgeTo[v] = edgeId;
                    pq.Enqueue(v, dist[v]);
                }
            }
        }

        if (dist[toNodeId] == float.MaxValue) return null;

        var path = new List<int>();
        int curr = toNodeId;
        while (curr != fromNodeId)
        {
            int eId = edgeTo[curr];
            if (eId == -1) return null;
            path.Add(eId);
            curr = Edges[eId].FromId;
        }
        path.Reverse();
        return path;
    }

    /// <summary>
    /// Finds the shortest road path between two nodes and returns the sequential list of node IDs.
    /// If both nodes are the same, returns a single-element list. If no path exists, returns null.
    /// </summary>
    public List<int> GetShortestNodePath(int fromNodeId, int toNodeId)
    {
        if (!NodeMap.ContainsKey(fromNodeId) || !NodeMap.ContainsKey(toNodeId)) return null;
        if (fromNodeId == toNodeId) return new List<int> { fromNodeId };

        var edgePath = GetShortestPath(fromNodeId, toNodeId);
        if (edgePath == null || edgePath.Count == 0) return null;

        var nodePath = new List<int> { fromNodeId };
        foreach (int eid in edgePath)
        {
            if (eid >= 0 && eid < Edges.Count && Edges[eid].ToId != -1)
            {
                nodePath.Add(Edges[eid].ToId);
            }
            else
            {
                return null;
            }
        }
        return nodePath;
    }

    public float[] DijkstraWalking(int sourceNodeId)
    {
        int maxId = GetMaxNodeIndex();
        float[] dist = new float[maxId];
        Array.Fill(dist, float.MaxValue);
        
        if (!AdjacencyEdges.ContainsKey(sourceNodeId) || sourceNodeId >= maxId) return dist;

        dist[sourceNodeId] = 0f;
        var pq = new PriorityQueue<int, float>();
        pq.Enqueue(sourceNodeId, 0f);

        while (pq.Count > 0)
        {
            pq.TryDequeue(out int u, out float d);
            if (d > dist[u]) continue;

            foreach (var edgeId in AdjacencyEdges[u])
            {
                var edge = Edges[edgeId];
                if (edge.FromId == -1 || edge.ToId == -1) continue;
                int v = edge.ToId;
                float weight = edge.GetWalkingTravelTime();

                if (dist[u] + weight < dist[v])
                {
                    dist[v] = dist[u] + weight;
                    pq.Enqueue(v, dist[v]);
                }
            }
        }

        return dist;
    }

    public float[,] ComputeWalkingDistanceMatrix(int zoneCount)
    {
        float[,] matrix = new float[zoneCount, zoneCount];
        for (int i = 0; i < zoneCount; i++)
        {
            if (AdjacencyEdges.ContainsKey(i))
            {
                float[] dists = DijkstraWalking(i);
                for (int j = 0; j < zoneCount; j++)
                {
                    matrix[i, j] = j < dists.Length ? dists[j] : float.MaxValue;
                }
            }
            else
            {
                for (int j = 0; j < zoneCount; j++)
                {
                    matrix[i, j] = float.MaxValue;
                }
            }
        }
        return matrix;
    }

    public List<int> GetShortestWalkingPath(int fromNodeId, int toNodeId)
    {
        if (!AdjacencyEdges.ContainsKey(fromNodeId) || !AdjacencyEdges.ContainsKey(toNodeId)) return null;

        int maxId = GetMaxNodeIndex();
        if (fromNodeId >= maxId || toNodeId >= maxId) return null;

        float[] dist = new float[maxId];
        int[] edgeTo = new int[maxId];
        Array.Fill(dist, float.MaxValue);
        Array.Fill(edgeTo, -1);

        dist[fromNodeId] = 0f;
        var pq = new PriorityQueue<int, float>();
        pq.Enqueue(fromNodeId, 0f);

        while (pq.Count > 0)
        {
            pq.TryDequeue(out int u, out float d);
            if (u == toNodeId) break;
            if (d > dist[u]) continue;

            foreach (var edgeId in AdjacencyEdges[u])
            {
                var edge = Edges[edgeId];
                if (edge.FromId == -1 || edge.ToId == -1) continue;
                int v = edge.ToId;
                float weight = edge.GetWalkingTravelTime();

                if (dist[u] + weight < dist[v])
                {
                    dist[v] = dist[u] + weight;
                    edgeTo[v] = edgeId;
                    pq.Enqueue(v, dist[v]);
                }
            }
        }

        if (dist[toNodeId] == float.MaxValue) return null;

        var path = new List<int>();
        int curr = toNodeId;
        while (curr != fromNodeId)
        {
            int eId = edgeTo[curr];
            if (eId == -1) return null;
            path.Add(eId);
            curr = Edges[eId].FromId;
        }
        path.Reverse();
        return path;
    }

    public List<int> GetShortestWalkingNodePath(int fromNodeId, int toNodeId)
    {
        if (!NodeMap.ContainsKey(fromNodeId) || !NodeMap.ContainsKey(toNodeId)) return null;
        if (fromNodeId == toNodeId) return new List<int> { fromNodeId };

        var edgePath = GetShortestWalkingPath(fromNodeId, toNodeId);
        if (edgePath == null || edgePath.Count == 0) return null;

        var nodePath = new List<int> { fromNodeId };
        foreach (int eid in edgePath)
        {
            if (eid >= 0 && eid < Edges.Count && Edges[eid].ToId != -1)
            {
                nodePath.Add(Edges[eid].ToId);
            }
            else
            {
                return null;
            }
        }
        return nodePath;
    }

    public void BuildPathCache(int zoneCount)
    {
        PathCache.Clear();
        WalkingPathCache.Clear();
        foreach (var u in Nodes)
        {
            foreach (var v in Nodes)
            {
                if (u.Id != v.Id)
                {
                    var path = GetShortestPath(u.Id, v.Id);
                    if (path != null)
                    {
                        PathCache[(u.Id, v.Id)] = path;
                    }

                    var walkingPath = GetShortestWalkingPath(u.Id, v.Id);
                    if (walkingPath != null)
                    {
                        WalkingPathCache[(u.Id, v.Id)] = walkingPath;
                    }
                }
            }
        }
    }

    public void ClearVolumes()
    {
        foreach (var e in Edges)
        {
            e.CurrentVolume = 0f;
            e.PedestrianVolume = 0f;
        }
    }

    /// <summary>
    /// Returns the edge ID for a directed edge from <paramref name="fromId"/> to
    /// <paramref name="toId"/>, or -1 if no such edge exists.
    /// </summary>
    public int FindEdgeId(int fromId, int toId)
    {
        if (!AdjacencyEdges.TryGetValue(fromId, out var edgeIds)) return -1;
        foreach (var eid in edgeIds)
        {
            if (Edges[eid].ToId == toId) return eid;
        }
        return -1;
    }

    /// <summary>
    /// Returns true when a directed edge from <paramref name="fromId"/> to
    /// <paramref name="toId"/> already exists.
    /// </summary>
    public bool HasEdge(int fromId, int toId) => FindEdgeId(fromId, toId) != -1;

    /// <summary>
    /// Computes the next unique node identifier.
    /// </summary>
    public int GetNextNodeId()
    {
        int maxId = 0;
        foreach (var k in NodeMap.Keys)
        {
            if (k >= maxId) maxId = k + 1;
        }
        return maxId;
    }

    /// <summary>
    /// Creates and registers a new RoadNode at the specified continuous 2D world position.
    /// </summary>
    public RoadNode CreateNode(Vector2 worldPosition, int zoneId = -1)
    {
        int id = zoneId >= 0 && !NodeMap.ContainsKey(zoneId) ? zoneId : GetNextNodeId();
        var node = new RoadNode
        {
            Id = id,
            ZoneId = zoneId,
            WorldPosition = worldPosition
        };
        Nodes.Add(node);
        NodeMap[id] = node;
        if (!AdjacencyEdges.ContainsKey(id))
            AdjacencyEdges[id] = new List<int>();
        return node;
    }

    /// <summary>
    /// Establishes a bidirectional curved road connection between two road nodes.
    /// Sets true arc length and symmetrical curve geometry for both directions.
    /// </summary>
    public bool AddCurvedRoadSegment(int fromNodeId, int toNodeId, CurveSegment curve, float capacity = 1000f)
    {
        if (!NodeMap.ContainsKey(fromNodeId) || !NodeMap.ContainsKey(toNodeId))
            return false;

        if (HasEdge(fromNodeId, toNodeId))
            return false;

        var fromNode = NodeMap[fromNodeId];
        var toNode = NodeMap[toNodeId];

        if (curve == null)
        {
            curve = new CurveSegment(fromNode.WorldPosition, toNode.WorldPosition);
        }

        int nextId = Math.Max(Edges.Count, Edges.Count > 0 ? Edges.Max(e => e.Id) + 1 : 0);

        var e1 = new RoadEdge
        {
            Id = nextId,
            FromId = fromNodeId,
            ToId = toNodeId,
            Length = curve.Length,
            Capacity = capacity,
            FreeFlowSpeed = 50f,
            Lanes = 2,
            CurrentVolume = 0f,
            PedestrianCapacity = 1800f,
            WalkingSpeed = 5.0f,
            PedestrianVolume = 0f,
            Curve = curve
        };

        var e2 = new RoadEdge
        {
            Id = nextId + 1,
            FromId = toNodeId,
            ToId = fromNodeId,
            Length = curve.Length,
            Capacity = capacity,
            FreeFlowSpeed = 50f,
            Lanes = 2,
            CurrentVolume = 0f,
            PedestrianCapacity = 1800f,
            WalkingSpeed = 5.0f,
            PedestrianVolume = 0f,
            Curve = curve.GetReversed()
        };

        Edges.Add(e1);
        Edges.Add(e2);

        if (!AdjacencyEdges.ContainsKey(fromNodeId))
            AdjacencyEdges[fromNodeId] = new List<int>();
        if (!AdjacencyEdges.ContainsKey(toNodeId))
            AdjacencyEdges[toNodeId] = new List<int>();

        AdjacencyEdges[fromNodeId].Add(e1.Id);
        AdjacencyEdges[toNodeId].Add(e2.Id);

        EdgeAdded?.Invoke(e1);
        EdgeAdded?.Invoke(e2);

        return true;
    }

    /// <summary>
    /// Establishes a bidirectional straight <see cref="RoadEdge"/> between two adjacent
    /// zone nodes. Does nothing if the connection already exists or either node is unknown.
    /// </summary>
    /// <param name="fromZoneId">Origin zone ID.</param>
    /// <param name="toZoneId">Destination zone ID.</param>
    /// <param name="capacity">Capacity for the road edges (defaults to 1000).</param>
    /// <returns><c>true</c> if the segment was created; <c>false</c> otherwise.</returns>
    public bool AddRoadSegment(int fromZoneId, int toZoneId, float capacity = 1000f)
    {
        if (!NodeMap.ContainsKey(fromZoneId) || !NodeMap.ContainsKey(toZoneId))
            return false;

        var fromNode = NodeMap[fromZoneId];
        var toNode = NodeMap[toZoneId];
        var curve = new CurveSegment(fromNode.WorldPosition, toNode.WorldPosition);

        return AddCurvedRoadSegment(fromZoneId, toZoneId, curve, capacity);
    }

    /// <summary>
    /// Splits an existing road edge at the closest point to <paramref name="point"/>,
    /// cleanly inserting a new junction node and two consecutive bidirectional edge pairs.
    /// Preserves graph connectivity and invalidates affected path caches.
    /// </summary>
    public RoadNode SplitEdgeAtPoint(int edgeId, Vector2 point)
    {
        if (edgeId < 0 || edgeId >= Edges.Count) return null;
        var edge = Edges[edgeId];
        if (edge.FromId == -1 || edge.ToId == -1) return null;

        var curve = edge.Curve ?? new CurveSegment(
            NodeMap[edge.FromId].WorldPosition,
            NodeMap[edge.ToId].WorldPosition
        );

        var (t, _, _) = curve.GetClosestPoint(point);
        return SplitEdge(edgeId, t);
    }

    /// <summary>
    /// Splits an existing edge at parameter <paramref name="t"/> along its curve,
    /// inserting a new junction node and preserving graph topology.
    /// </summary>
    public RoadNode SplitEdge(int edgeId, float t)
    {
        if (edgeId < 0 || edgeId >= Edges.Count) return null;
        var edge = Edges[edgeId];
        if (edge.FromId == -1 || edge.ToId == -1) return null;

        t = Mathf.Clamp(t, 0.01f, 0.99f);

        int fromId = edge.FromId;
        int toId = edge.ToId;
        float capacity = edge.Capacity;

        var curve = edge.Curve ?? new CurveSegment(
            NodeMap[fromId].WorldPosition,
            NodeMap[toId].WorldPosition
        );

        var (leftCurve, rightCurve) = curve.Split(t);
        Vector2 splitPos = curve.Evaluate(t);

        // 1. Create junction node at split location
        var junction = CreateNode(splitPos, -1);

        // 2. Identify reverse edge ID if it exists
        int revId = FindEdgeId(toId, fromId);

        // Register split info and reverse edge mapping BEFORE disconnection
        _splitInfo[edgeId] = (fromId, toId, junction);
        if (revId != -1)
        {
            _splitInfo[revId] = (toId, fromId, junction);
            _splitReverseEdges[edgeId] = revId;
            _splitReverseEdges[revId] = edgeId;
        }

        // 3. Disconnect original forward edge
        if (AdjacencyEdges.TryGetValue(fromId, out var fwdList))
            fwdList.Remove(edgeId);

        Edges[edgeId].FromId = -1;
        Edges[edgeId].ToId = -1;
        Edges[edgeId].Capacity = 0f;
        Edges[edgeId].CurrentVolume = 0f;

        // 4. Disconnect original reverse edge
        if (revId != -1)
        {
            if (AdjacencyEdges.TryGetValue(toId, out var revList))
                revList.Remove(revId);

            Edges[revId].FromId = -1;
            Edges[revId].ToId = -1;
            Edges[revId].Capacity = 0f;
            Edges[revId].CurrentVolume = 0f;
        }

        // 5. Invalidate path caches referencing the split edges
        InvalidatePathsWithEdges(edgeId, revId);

        // 6. Connect fromId <-> junction
        AddCurvedRoadSegment(fromId, junction.Id, leftCurve, capacity);

        // 7. Connect junction <-> toId
        AddCurvedRoadSegment(junction.Id, toId, rightCurve, capacity);

        EdgeSplit?.Invoke(edgeId, junction);
        if (revId != -1)
        {
            EdgeSplit?.Invoke(revId, junction);
        }

        return junction;
    }

    private void InvalidatePathsWithEdges(int eid1, int eid2)
    {
        var keysToRemove = new List<(int, int)>();
        foreach (var kvp in PathCache)
        {
            foreach (var pid in kvp.Value)
            {
                if (pid == eid1 || (eid2 != -1 && pid == eid2))
                {
                    keysToRemove.Add(kvp.Key);
                    break;
                }
            }
        }
        foreach (var key in keysToRemove)
            PathCache.Remove(key);

        var walkingKeysToRemove = new List<(int, int)>();
        foreach (var kvp in WalkingPathCache)
        {
            foreach (var pid in kvp.Value)
            {
                if (pid == eid1 || (eid2 != -1 && pid == eid2))
                {
                    walkingKeysToRemove.Add(kvp.Key);
                    break;
                }
            }
        }
        foreach (var key in walkingKeysToRemove)
            WalkingPathCache.Remove(key);
    }

    /// <summary>
    /// Removes both directed edges between two zone nodes, cleaning up
    /// <see cref="AdjacencyEdges"/> and <see cref="PathCache"/>.
    /// </summary>
    /// <returns><c>true</c> if edges were removed; <c>false</c> otherwise.</returns>
    public bool RemoveRoadSegment(int fromZoneId, int toZoneId)
    {
        int eidForward = FindEdgeId(fromZoneId, toZoneId);
        int eidReverse = FindEdgeId(toZoneId, fromZoneId);

        if (eidForward == -1 && eidReverse == -1)
            return false;

        // Remove from adjacency lists first
        if (eidForward != -1 && AdjacencyEdges.TryGetValue(fromZoneId, out var fwdList))
            fwdList.Remove(eidForward);
        if (eidReverse != -1 && AdjacencyEdges.TryGetValue(toZoneId, out var revList))
            revList.Remove(eidReverse);

        // Mark edges as disconnected (set length to 0, capacity to 0) rather
        // than physically removing from the list to keep existing IDs stable.
        if (eidForward != -1)
        {
            Edges[eidForward].FromId = -1;
            Edges[eidForward].ToId = -1;
            Edges[eidForward].Capacity = 0f;
            Edges[eidForward].CurrentVolume = 0f;
            Edges[eidForward].PedestrianCapacity = 0f;
            Edges[eidForward].PedestrianVolume = 0f;
        }
        if (eidReverse != -1)
        {
            Edges[eidReverse].FromId = -1;
            Edges[eidReverse].ToId = -1;
            Edges[eidReverse].Capacity = 0f;
            Edges[eidReverse].CurrentVolume = 0f;
            Edges[eidReverse].PedestrianCapacity = 0f;
            Edges[eidReverse].PedestrianVolume = 0f;
        }

        // Invalidate affected path cache entries
        InvalidatePathsWithEdges(eidForward, eidReverse);

        if (eidForward != -1) EdgeRemoved?.Invoke(eidForward);
        if (eidReverse != -1) EdgeRemoved?.Invoke(eidReverse);

        return true;
    }

    /// <summary>
    /// Ensures that a <see cref="RoadNode"/> exists for the given zone ID and world position.
    /// Used when newly zoning an empty cell so road segments can be connected to it.
    /// </summary>
    public RoadNode EnsureNode(int zoneId, Vector2 worldPosition)
    {
        if (NodeMap.TryGetValue(zoneId, out var existing))
        {
            existing.WorldPosition = worldPosition;
            if (!AdjacencyEdges.ContainsKey(zoneId))
                AdjacencyEdges[zoneId] = new List<int>();
            return existing;
        }

        var node = new RoadNode
        {
            Id = zoneId,
            ZoneId = zoneId,
            WorldPosition = worldPosition
        };

        Nodes.Add(node);
        NodeMap[zoneId] = node;
        if (!AdjacencyEdges.ContainsKey(zoneId))
            AdjacencyEdges[zoneId] = new List<int>();

        return node;
    }

    /// <summary>
    /// Safely detaches and removes all road connections to and from the given zone node,
    /// cleans up path caches, and removes the node from the active graph.
    /// </summary>
    /// <returns><c>true</c> if graph state was modified; <c>false</c> otherwise.</returns>
    public bool DetachAndRemoveNode(int zoneId)
    {
        bool changed = false;

        // 1. Remove all outgoing edges from this node
        if (AdjacencyEdges.TryGetValue(zoneId, out var outgoingEdges))
        {
            var neighbors = new List<int>();
            foreach (int eid in outgoingEdges)
            {
                if (eid >= 0 && eid < Edges.Count && Edges[eid].ToId != -1)
                {
                    neighbors.Add(Edges[eid].ToId);
                }
            }

            foreach (int toId in neighbors)
            {
                if (RemoveRoadSegment(zoneId, toId))
                    changed = true;
            }
        }

        // 2. Also check for any remaining incoming edges from other nodes to this node
        for (int i = 0; i < Edges.Count; i++)
        {
            var edge = Edges[i];
            if (edge.ToId == zoneId && edge.FromId != -1)
            {
                if (RemoveRoadSegment(edge.FromId, zoneId))
                    changed = true;
            }
        }

        // 3. Remove node from graph structures
        if (NodeMap.TryGetValue(zoneId, out var node))
        {
            Nodes.Remove(node);
            NodeMap.Remove(zoneId);
            AdjacencyEdges.Remove(zoneId);
            changed = true;
        }

        // 4. Invalidate any remaining path cache keys referencing this zone
        var keysToRemove = new List<(int, int)>();
        foreach (var key in PathCache.Keys)
        {
            if (key.Item1 == zoneId || key.Item2 == zoneId)
                keysToRemove.Add(key);
        }
        foreach (var key in keysToRemove)
            PathCache.Remove(key);

        var walkingKeysToRemove = new List<(int, int)>();
        foreach (var key in WalkingPathCache.Keys)
        {
            if (key.Item1 == zoneId || key.Item2 == zoneId)
                walkingKeysToRemove.Add(key);
        }
        foreach (var key in walkingKeysToRemove)
            WalkingPathCache.Remove(key);

        return changed;
    }

    /// <summary>
    /// Recomputes the distance matrix and path cache after a graph topology change.
    /// </summary>
    public float[,] RebuildAfterTopologyChange(int zoneCount)
    {
        PathCache.Clear();
        WalkingPathCache.Clear();
        var matrix = ComputeDistanceMatrix(zoneCount);
        BuildPathCache(zoneCount);
        return matrix;
    }

    /// <summary>
    /// Returns the degree (number of active connecting edges) of a node.
    /// </summary>
    public int GetNodeDegree(int nodeId)
    {
        if (!AdjacencyEdges.TryGetValue(nodeId, out var edges)) return 0;
        int count = 0;
        foreach (int eid in edges)
        {
            if (eid >= 0 && eid < Edges.Count && Edges[eid].FromId != -1 && Edges[eid].ToId != -1)
                count++;
        }
        return count;
    }

    /// <summary>
    /// Computes the active traffic extent [0, T_max] along a road edge.
    /// If the edge's destination node connects to other roads (degree &gt; 1), T_max = 1.0f.
    /// If the destination node is a dead end (degree &lt;= 1 with no further outgoing roads),
    /// clamps flow to the furthest active parcel parameter along that edge:
    /// T_max = clamp(max({p.NormalizedT | p in Parcels(e), p.ZoneType != Empty} U {0.15f}) + 0.05f, 0.15f, 1.0f).
    /// </summary>
    public float GetEdgeTrafficExtent(RoadEdge edge, ParcelManager parcelManager = null)
    {
        if (edge == null || edge.FromId == -1 || edge.ToId == -1) return 1.0f;

        // Check if destination node connects to other roads
        int degree = GetNodeDegree(edge.ToId);
        bool isDeadEnd = degree <= 1;

        if (!isDeadEnd && AdjacencyEdges.TryGetValue(edge.ToId, out var outgoingEdges))
        {
            bool hasOtherDestination = false;
            for (int i = 0; i < outgoingEdges.Count; i++)
            {
                int outEid = outgoingEdges[i];
                if (outEid >= 0 && outEid < Edges.Count)
                {
                    var outEdge = Edges[outEid];
                    if (outEdge.FromId != -1 && outEdge.ToId != -1 && outEdge.ToId != edge.FromId)
                    {
                        hasOtherDestination = true;
                        break;
                    }
                }
            }
            if (!hasOtherDestination)
            {
                isDeadEnd = true;
            }
        }

        if (!isDeadEnd)
        {
            return 1.0f;
        }

        float maxT = 0.15f;
        if (parcelManager != null)
        {
            var parcels = parcelManager.GetParcelsForEdge(edge.Id);
            for (int i = 0; i < parcels.Count; i++)
            {
                var p = parcels[i];
                if (p.ZoneType != ZoneType.Empty)
                {
                    float t = (p.EdgeId == edge.Id) ? p.NormalizedT : (1.0f - p.NormalizedT);
                    if (t > maxT)
                    {
                        maxT = t;
                    }
                }
            }
        }

        return Mathf.Clamp(maxT + 0.05f, 0.15f, 1.0f);
    }

    /// <summary>
    /// Computes the active traffic extent [0, T_max] along a road edge by edge ID.
    /// </summary>
    public float GetEdgeTrafficExtent(int edgeId, ParcelManager parcelManager = null)
    {
        if (edgeId < 0 || edgeId >= Edges.Count) return 1.0f;
        return GetEdgeTrafficExtent(Edges[edgeId], parcelManager);
    }
}
