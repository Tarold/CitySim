using Godot;
using System.Linq;

namespace CitySim.Simulation;

public class TrafficEngine
{
    public void AssignFlows(ODMatrix od, RoadGraph graph, CityGrid grid)
    {
        graph.ClearVolumes();

        var activeIds = grid.ActiveZoneIds;
        int activeCount = activeIds.Count;

        for (int a = 0; a < activeCount; a++)
        {
            int i = activeIds[a];
            for (int b = 0; b < activeCount; b++)
            {
                if (a == b) continue;
                int j = activeIds[b];

                float carVolume = od.CarTrips[i, j];
                float transitVolume = od.TransitTrips[i, j];
                float totalVehVolume = carVolume + (transitVolume * 0.15f);

                if (totalVehVolume > 0.05f)
                {
                    if (graph.PathCache.TryGetValue((i, j), out var path))
                    {
                        for (int p = 0; p < path.Count; p++)
                        {
                            graph.Edges[path[p]].CurrentVolume += totalVehVolume;
                        }
                    }
                }
            }
        }
    }

    public float GetMaxVolume(RoadGraph graph)
    {
        float maxVol = 0f;
        for (int i = 0; i < graph.Edges.Count; i++)
        {
            float v = graph.Edges[i].CurrentVolume;
            if (v > maxVol) maxVol = v;
        }
        return maxVol;
    }

    public float GetAverageCongestion(RoadGraph graph)
    {
        float totalRatio = 0f;
        int count = 0;
        for (int i = 0; i < graph.Edges.Count; i++)
        {
            var edge = graph.Edges[i];
            if (edge.CurrentVolume > 0f)
            {
                totalRatio += edge.GetCongestionRatio();
                count++;
            }
        }
        return count > 0 ? (totalRatio / count) : 0f;
    }
}
