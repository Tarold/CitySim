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

    public float GetTravelTime()
    {
        return (Length / FreeFlowSpeed) * (1f + 0.15f * Mathf.Pow(CurrentVolume / Mathf.Max(Capacity, 1f), 4f));
    }

    public float GetCongestionRatio()
    {
        return CurrentVolume / Mathf.Max(Capacity, 1f);
    }
}

public class RoadGraph
{
    public List<RoadNode> Nodes = new List<RoadNode>();
    public List<RoadEdge> Edges = new List<RoadEdge>();
    public Dictionary<int, RoadNode> NodeMap = new Dictionary<int, RoadNode>();
    public Dictionary<int, List<int>> AdjacencyEdges = new Dictionary<int, List<int>>();
    public Dictionary<(int, int), List<int>> PathCache = new Dictionary<(int, int), List<int>>();

    public RoadNode GetNode(int id) => NodeMap.TryGetValue(id, out var n) ? n : null;

    public void BuildFromGrid(CityGrid grid)
    {
        Nodes.Clear();
        Edges.Clear();
        NodeMap.Clear();
        AdjacencyEdges.Clear();
        PathCache.Clear();

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

        var e1 = new RoadEdge
        {
            Id = edgeIdCounter++,
            FromId = z1.Id,
            ToId = z2.Id,
            Length = length,
            Capacity = cap,
            FreeFlowSpeed = 50f,
            Lanes = 2,
            CurrentVolume = 0f
        };

        var e2 = new RoadEdge
        {
            Id = edgeIdCounter++,
            FromId = z2.Id,
            ToId = z1.Id,
            Length = length,
            Capacity = cap,
            FreeFlowSpeed = 50f,
            Lanes = 2,
            CurrentVolume = 0f
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

    public void BuildPathCache(int zoneCount)
    {
        PathCache.Clear();
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
                }
            }
        }
    }

    public void ClearVolumes()
    {
        foreach (var e in Edges)
        {
            e.CurrentVolume = 0f;
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
    /// Establishes a bidirectional <see cref="RoadEdge"/> between two adjacent
    /// zone nodes. Does nothing if the connection already exists or either node
    /// is unknown.
    /// </summary>
    /// <param name="fromZoneId">Origin zone ID.</param>
    /// <param name="toZoneId">Destination zone ID.</param>
    /// <param name="capacity">Capacity for the road edges (defaults to 1000).</param>
    /// <returns><c>true</c> if the segment was created; <c>false</c> otherwise.</returns>
    public bool AddRoadSegment(int fromZoneId, int toZoneId, float capacity = 1000f)
    {
        // Both nodes must be present in the graph
        if (!NodeMap.ContainsKey(fromZoneId) || !NodeMap.ContainsKey(toZoneId))
            return false;

        // No duplicate edges
        if (HasEdge(fromZoneId, toZoneId))
            return false;

        var fromNode = NodeMap[fromZoneId];
        var toNode = NodeMap[toZoneId];
        float length = fromNode.WorldPosition.DistanceTo(toNode.WorldPosition);

        int nextId = Math.Max(Edges.Count, Edges.Count > 0 ? Edges.Max(e => e.Id) + 1 : 0);

        var e1 = new RoadEdge
        {
            Id = nextId,
            FromId = fromZoneId,
            ToId = toZoneId,
            Length = length,
            Capacity = capacity,
            FreeFlowSpeed = 50f,
            Lanes = 2,
            CurrentVolume = 0f
        };

        var e2 = new RoadEdge
        {
            Id = nextId + 1,
            FromId = toZoneId,
            ToId = fromZoneId,
            Length = length,
            Capacity = capacity,
            FreeFlowSpeed = 50f,
            Lanes = 2,
            CurrentVolume = 0f
        };

        Edges.Add(e1);
        Edges.Add(e2);

        if (!AdjacencyEdges.ContainsKey(fromZoneId))
            AdjacencyEdges[fromZoneId] = new List<int>();
        if (!AdjacencyEdges.ContainsKey(toZoneId))
            AdjacencyEdges[toZoneId] = new List<int>();

        AdjacencyEdges[fromZoneId].Add(e1.Id);
        AdjacencyEdges[toZoneId].Add(e2.Id);

        return true;
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
        }
        if (eidReverse != -1)
        {
            Edges[eidReverse].FromId = -1;
            Edges[eidReverse].ToId = -1;
            Edges[eidReverse].Capacity = 0f;
            Edges[eidReverse].CurrentVolume = 0f;
        }

        // Invalidate affected path cache entries
        var keysToRemove = new List<(int, int)>();
        foreach (var kvp in PathCache)
        {
            foreach (var pid in kvp.Value)
            {
                if (pid == eidForward || pid == eidReverse)
                {
                    keysToRemove.Add(kvp.Key);
                    break;
                }
            }
        }
        foreach (var key in keysToRemove)
            PathCache.Remove(key);

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

        return changed;
    }

    /// <summary>
    /// Recomputes the distance matrix and path cache after a graph topology change.
    /// </summary>
    public float[,] RebuildAfterTopologyChange(int zoneCount)
    {
        PathCache.Clear();
        var matrix = ComputeDistanceMatrix(zoneCount);
        BuildPathCache(zoneCount);
        return matrix;
    }

    /// <summary>
    /// Returns the degree (number of outgoing edges) of a node.
    /// </summary>
    public int GetNodeDegree(int nodeId)
    {
        return AdjacencyEdges.TryGetValue(nodeId, out var edges) ? edges.Count : 0;
    }
}
