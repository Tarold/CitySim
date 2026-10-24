using System;
using System.Collections.Generic;

namespace CitySim.Simulation;

/// <summary>
/// Dense zone×zone distance matrices and all-pairs path caches used by the legacy <see cref="ODMatrix"/>
/// pipeline and its tests. They are O(N²) in memory and time and must not be used by production code
/// (the macro model works on TAZ-level shortest-path trees, see <see cref="RoutingSnapshot"/>).
/// </summary>
public static class RoadGraphLegacyExtensions
{
    public static float[,] ComputeDistanceMatrix(this RoadGraph graph, int zoneCount)
    {
        float[,] matrix = new float[zoneCount, zoneCount];
        for (int i = 0; i < zoneCount; i++)
        {
            if (graph.AdjacencyEdges.ContainsKey(i))
            {
                float[] dists = graph.Dijkstra(i);
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

    public static float[,] ComputeWalkingDistanceMatrix(this RoadGraph graph, int zoneCount)
    {
        float[,] matrix = new float[zoneCount, zoneCount];
        for (int i = 0; i < zoneCount; i++)
        {
            if (graph.AdjacencyEdges.ContainsKey(i))
            {
                float[] dists = graph.DijkstraWalking(i);
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

    public static void BuildPathCache(this RoadGraph graph, int zoneCount)
    {
        graph.PathCache.Clear();
        graph.WalkingPathCache.Clear();
        foreach (var u in graph.Nodes)
        {
            foreach (var v in graph.Nodes)
            {
                if (u.Id != v.Id)
                {
                    var path = graph.GetShortestPath(u.Id, v.Id);
                    if (path != null)
                    {
                        graph.PathCache[(u.Id, v.Id)] = path;
                    }

                    var walkingPath = graph.GetShortestWalkingPath(u.Id, v.Id);
                    if (walkingPath != null)
                    {
                        graph.WalkingPathCache[(u.Id, v.Id)] = walkingPath;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Recomputes the distance matrix and path cache after a graph topology change.
    /// </summary>
    public static float[,] RebuildAfterTopologyChange(this RoadGraph graph, int zoneCount)
    {
        graph.PathCache.Clear();
        graph.WalkingPathCache.Clear();
        var matrix = graph.ComputeDistanceMatrix(zoneCount);
        graph.BuildPathCache(zoneCount);
        return matrix;
    }
}
