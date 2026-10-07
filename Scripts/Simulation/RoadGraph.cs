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

    public float[] Dijkstra(int sourceNodeId)
    {
        int maxId = AdjacencyEdges.Keys.Count > 0 ? AdjacencyEdges.Keys.Max() + 1 : 0;
        float[] dist = new float[maxId];
        Array.Fill(dist, float.MaxValue);
        
        if (!AdjacencyEdges.ContainsKey(sourceNodeId)) return dist;

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

        int maxId = AdjacencyEdges.Keys.Max() + 1;
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
}
