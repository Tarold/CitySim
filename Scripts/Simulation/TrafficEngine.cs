using Godot;
using System.Linq;

namespace CitySim.Simulation;

public class TrafficEngine
{
    public void AssignFlows(ODMatrix od, RoadGraph graph, CityGrid grid = null, TransitManager transit = null)
    {
        graph.ClearVolumes();

        // 1. Dynamic continuous assignment
        if (od != null && od.DynamicTrips != null && od.DynamicTrips.Count > 0)
        {
            for (int t = 0; t < od.DynamicTrips.Count; t++)
            {
                var trip = od.DynamicTrips[t];

                // A. Assign Private Car Trips along road shortest paths
                if (trip.CarTrips > 0.001f)
                {
                    if (trip.OriginAccessNodeId != trip.DestAccessNodeId && trip.OriginAccessNodeId != -1 && trip.DestAccessNodeId != -1)
                    {
                        if (graph.PathCache.TryGetValue((trip.OriginAccessNodeId, trip.DestAccessNodeId), out var carPath) ||
                            (carPath = graph.GetShortestPath(trip.OriginAccessNodeId, trip.DestAccessNodeId)) != null)
                        {
                            for (int p = 0; p < carPath.Count; p++)
                            {
                                int eid = carPath[p];
                                if (eid >= 0 && eid < graph.Edges.Count)
                                    graph.Edges[eid].CurrentVolume += trip.CarTrips;
                            }
                        }
                    }
                    else if (trip.EdgePath != null && trip.EdgePath.Count > 0)
                    {
                        for (int p = 0; p < trip.EdgePath.Count; p++)
                        {
                            int eid = trip.EdgePath[p];
                            if (eid >= 0 && eid < graph.Edges.Count)
                                graph.Edges[eid].CurrentVolume += trip.CarTrips;
                        }
                    }
                }

                // B. Assign Pure Walk Trips along pedestrian shortest paths
                if (trip.WalkTrips > 0.001f)
                {
                    if (trip.OriginAccessNodeId != trip.DestAccessNodeId && trip.OriginAccessNodeId != -1 && trip.DestAccessNodeId != -1)
                    {
                        if (graph.WalkingPathCache.TryGetValue((trip.OriginAccessNodeId, trip.DestAccessNodeId), out var walkPath) ||
                            graph.PathCache.TryGetValue((trip.OriginAccessNodeId, trip.DestAccessNodeId), out walkPath) ||
                            (walkPath = graph.GetShortestWalkingPath(trip.OriginAccessNodeId, trip.DestAccessNodeId)) != null)
                        {
                            for (int p = 0; p < walkPath.Count; p++)
                            {
                                int eid = walkPath[p];
                                if (eid >= 0 && eid < graph.Edges.Count)
                                    graph.Edges[eid].PedestrianVolume += trip.WalkTrips;
                            }
                        }
                    }
                    else if (trip.EdgePath != null && trip.EdgePath.Count > 0)
                    {
                        for (int p = 0; p < trip.EdgePath.Count; p++)
                        {
                            int eid = trip.EdgePath[p];
                            if (eid >= 0 && eid < graph.Edges.Count)
                                graph.Edges[eid].PedestrianVolume += trip.WalkTrips;
                        }
                    }
                }

                // C. Assign Multimodal Transit Trips: Walking access and egress legs to sidewalks
                if (trip.TransitTrips > 0.001f)
                {
                    if (transit != null && transit.Stops.Count > 0)
                    {
                        int s1 = -1;
                        float bestDist1 = float.MaxValue;
                        int s2 = -1;
                        float bestDist2 = float.MaxValue;

                        foreach (var stop in transit.Stops.Values)
                        {
                            var sNode = graph.GetNode(stop.NodeId);
                            Vector2 stopPos = sNode?.WorldPosition ?? (grid != null ? grid.GetWorldCenter(stop.ZoneId) : Vector2.Zero);

                            float d1 = trip.OriginPos.DistanceTo(stopPos);
                            if (d1 < bestDist1) { bestDist1 = d1; s1 = stop.NodeId; }

                            float d2 = trip.DestPos.DistanceTo(stopPos);
                            if (d2 < bestDist2) { bestDist2 = d2; s2 = stop.NodeId; }
                        }

                        // Access walk leg: origin access node -> boarding stop s1
                        if (s1 != -1 && s1 != trip.OriginAccessNodeId && trip.OriginAccessNodeId != -1)
                        {
                            if (graph.WalkingPathCache.TryGetValue((trip.OriginAccessNodeId, s1), out var accessPath) ||
                                (accessPath = graph.GetShortestWalkingPath(trip.OriginAccessNodeId, s1)) != null)
                            {
                                for (int p = 0; p < accessPath.Count; p++)
                                {
                                    int eid = accessPath[p];
                                    if (eid >= 0 && eid < graph.Edges.Count)
                                        graph.Edges[eid].PedestrianVolume += trip.TransitTrips;
                                }
                            }
                        }

                        // Egress walk leg: alighting stop s2 -> destination access node
                        if (s2 != -1 && s2 != trip.DestAccessNodeId && trip.DestAccessNodeId != -1)
                        {
                            if (graph.WalkingPathCache.TryGetValue((s2, trip.DestAccessNodeId), out var egressPath) ||
                                (egressPath = graph.GetShortestWalkingPath(s2, trip.DestAccessNodeId)) != null)
                            {
                                for (int p = 0; p < egressPath.Count; p++)
                                {
                                    int eid = egressPath[p];
                                    if (eid >= 0 && eid < graph.Edges.Count)
                                        graph.Edges[eid].PedestrianVolume += trip.TransitTrips;
                                }
                            }
                        }
                    }
                    else if (trip.EdgePath != null && trip.EdgePath.Count > 0)
                    {
                        int eid0 = trip.EdgePath[0];
                        if (eid0 >= 0 && eid0 < graph.Edges.Count)
                            graph.Edges[eid0].PedestrianVolume += trip.TransitTrips;

                        if (trip.EdgePath.Count > 1)
                        {
                            int eidLast = trip.EdgePath[trip.EdgePath.Count - 1];
                            if (eidLast >= 0 && eidLast < graph.Edges.Count)
                                graph.Edges[eidLast].PedestrianVolume += trip.TransitTrips;
                        }
                    }
                }
            }
            return;
        }

        // 2. Legacy zone-matrix assignment fallback
        if (grid != null && od != null && od.CarTrips != null)
        {
            var activeIds = grid.ActiveZoneIds;
            int activeCount = activeIds.Count;

            for (int a = 0; a < activeCount; a++)
            {
                int i = activeIds[a];
                var zoneI = grid.GetZone(i);

                for (int b = 0; b < activeCount; b++)
                {
                    if (a == b) continue;
                    int j = activeIds[b];
                    var zoneJ = grid.GetZone(j);

                    // 1. Assign Private Car Trips along road shortest paths
                    float carVolume = od.CarTrips[i, j];
                    if (carVolume > 0.01f)
                    {
                        if (graph.PathCache.TryGetValue((i, j), out var carPath) || (carPath = graph.GetShortestPath(i, j)) != null)
                        {
                            for (int p = 0; p < carPath.Count; p++)
                            {
                                graph.Edges[carPath[p]].CurrentVolume += carVolume;
                            }
                        }
                    }

                    // 2. Assign Pure Walk Trips along pedestrian shortest paths
                    float walkVolume = od.WalkTrips != null ? od.WalkTrips[i, j] : 0f;
                    if (walkVolume > 0.01f)
                    {
                        if (graph.WalkingPathCache.TryGetValue((i, j), out var walkPath) || 
                            graph.PathCache.TryGetValue((i, j), out walkPath) || 
                            (walkPath = graph.GetShortestWalkingPath(i, j)) != null)
                        {
                            for (int p = 0; p < walkPath.Count; p++)
                            {
                                graph.Edges[walkPath[p]].PedestrianVolume += walkVolume;
                            }
                        }
                    }

                    // 3. Assign Multimodal Transit Trips: Walking access and egress legs to sidewalks
                    float transitVolume = od.TransitTrips[i, j];
                    if (transitVolume > 0.01f)
                    {
                        if (transit != null && transit.Stops.Count > 0)
                        {
                            int s1 = -1;
                            float bestDist1 = float.MaxValue;
                            int s2 = -1;
                            float bestDist2 = float.MaxValue;

                            foreach (var stop in transit.Stops.Values)
                            {
                                var sZone = grid.GetZone(stop.ZoneId);
                                if (sZone == null) continue;

                                int d1 = Mathf.Abs(zoneI.GridPos.X - sZone.GridPos.X) + Mathf.Abs(zoneI.GridPos.Y - sZone.GridPos.Y);
                                if (d1 < bestDist1) { bestDist1 = d1; s1 = stop.NodeId; }

                                int d2 = Mathf.Abs(zoneJ.GridPos.X - sZone.GridPos.X) + Mathf.Abs(zoneJ.GridPos.Y - sZone.GridPos.Y);
                                if (d2 < bestDist2) { bestDist2 = d2; s2 = stop.NodeId; }
                            }

                            // Access walk leg: origin zone i -> boarding stop s1
                            if (s1 != -1 && s1 != i)
                            {
                                if (graph.WalkingPathCache.TryGetValue((i, s1), out var accessPath) ||
                                    (accessPath = graph.GetShortestWalkingPath(i, s1)) != null)
                                {
                                    for (int p = 0; p < accessPath.Count; p++)
                                    {
                                        graph.Edges[accessPath[p]].PedestrianVolume += transitVolume;
                                    }
                                }
                            }

                            // Egress walk leg: alighting stop s2 -> destination zone j
                            if (s2 != -1 && s2 != j)
                            {
                                if (graph.WalkingPathCache.TryGetValue((s2, j), out var egressPath) ||
                                    (egressPath = graph.GetShortestWalkingPath(s2, j)) != null)
                                {
                                    for (int p = 0; p < egressPath.Count; p++)
                                    {
                                        graph.Edges[egressPath[p]].PedestrianVolume += transitVolume;
                                    }
                                }
                            }
                        }
                        else
                        {
                            // Fallback: assign access and egress walk to connecting ends of corridor
                            if (graph.WalkingPathCache.TryGetValue((i, j), out var fallbackPath) ||
                                graph.PathCache.TryGetValue((i, j), out fallbackPath))
                            {
                                if (fallbackPath.Count > 0)
                                {
                                    graph.Edges[fallbackPath[0]].PedestrianVolume += transitVolume;
                                    if (fallbackPath.Count > 1)
                                    {
                                        graph.Edges[fallbackPath[fallbackPath.Count - 1]].PedestrianVolume += transitVolume;
                                    }
                                }
                            }
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
            var edge = graph.Edges[i];
            if (edge.FromId == -1 || edge.ToId == -1) continue;
            float v = edge.CurrentVolume;
            if (v > maxVol) maxVol = v;
        }
        return maxVol;
    }

    public float GetMaxPedestrianVolume(RoadGraph graph)
    {
        float maxVol = 0f;
        for (int i = 0; i < graph.Edges.Count; i++)
        {
            var edge = graph.Edges[i];
            if (edge.FromId == -1 || edge.ToId == -1) continue;
            float v = edge.PedestrianVolume;
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
            if (edge.FromId == -1 || edge.ToId == -1) continue;
            if (edge.CurrentVolume > 0f)
            {
                totalRatio += edge.GetCongestionRatio();
                count++;
            }
        }
        return count > 0 ? (totalRatio / count) : 0f;
    }

    public float GetAveragePedestrianCongestion(RoadGraph graph)
    {
        float totalRatio = 0f;
        int count = 0;
        for (int i = 0; i < graph.Edges.Count; i++)
        {
            var edge = graph.Edges[i];
            if (edge.FromId == -1 || edge.ToId == -1) continue;
            if (edge.PedestrianVolume > 0f)
            {
                totalRatio += edge.GetPedestrianCongestionRatio();
                count++;
            }
        }
        return count > 0 ? (totalRatio / count) : 0f;
    }
}
