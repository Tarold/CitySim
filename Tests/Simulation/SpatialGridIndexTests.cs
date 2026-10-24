using System.Collections.Generic;
using System.Diagnostics;
using Godot;
using NUnit.Framework;
using CitySim.Simulation;

namespace CitySim.Tests.Simulation;

[TestFixture]
public class SpatialGridIndexTests
{
    [Test]
    public void SpatialGridIndex_IncrementalNodeEdgeParcelLifecycle_WorksCorrectly()
    {
        var graph = new RoadGraph();
        var parcelManager = new ParcelManager();
        parcelManager.Initialize(graph);

        Assert.That(graph.SpatialIndex.CellSize, Is.EqualTo(128.0f));

        // 1. Add two nodes and a road segment spanning multiple 128x128m cells
        var n1 = graph.CreateNode(new Vector2(32f, 64f));
        var n2 = graph.CreateNode(new Vector2(450f, 64f));
        bool added = graph.AddRoadSegment(n1.Id, n2.Id);

        Assert.That(added, Is.True);
        Assert.That(graph.SpatialIndex.IndexedNodeCount, Is.EqualTo(2));
        Assert.That(graph.SpatialIndex.IndexedEdgeCount, Is.EqualTo(2)); // forward + reverse

        // Query AABB near middle of the road (x = 240, y = 64) where no node exists, but edge passes through
        var edgeIds = new List<int>();
        var nodeIds = new List<int>();
        graph.SpatialIndex.QueryAabb(new Rect2(220f, 40f, 40f, 40f), nodeIds: nodeIds, edgeIds: edgeIds);
        Assert.That(nodeIds, Is.Empty);
        Assert.That(edgeIds.Count, Is.EqualTo(2));

        // 2. Refresh parcels and verify parcels are indexed in SpatialGridIndex
        parcelManager.RefreshParcels(graph);
        Assert.That(parcelManager.Parcels.Count, Is.GreaterThan(4));
        Assert.That(graph.SpatialIndex.IndexedParcelCount, Is.EqualTo(parcelManager.Parcels.Count));

        // Verify GetParcelAt and FindClosestParcel via SpatialGridIndex
        var sampleParcel = parcelManager.Parcels[0];
        var hitParcel = parcelManager.GetParcelAt(sampleParcel.Center);
        Assert.That(hitParcel, Is.Not.Null);
        Assert.That(hitParcel.Id, Is.EqualTo(sampleParcel.Id));

        var closestParcel = parcelManager.FindClosestParcel(sampleParcel.Center + new Vector2(5f, 5f), 32f);
        Assert.That(closestParcel, Is.Not.Null);
        Assert.That(closestParcel.Id, Is.EqualTo(sampleParcel.Id));

        // 3. Split edge at midpoint -> original 2 edges removed from index, junction node + 4 sub-edges added
        int fwdEdgeId = graph.FindEdgeId(n1.Id, n2.Id);
        var junction = graph.SplitEdgeAtPoint(fwdEdgeId, new Vector2(240f, 64f));
        Assert.That(junction, Is.Not.Null);
        Assert.That(graph.SpatialIndex.IndexedNodeCount, Is.EqualTo(3));
        Assert.That(graph.SpatialIndex.IndexedEdgeCount, Is.EqualTo(4)); // 2 left + 2 right

        // Query at junction position should now find the new junction node
        graph.SpatialIndex.QueryNodesInRadius(junction.WorldPosition, 16f, nodeIds);
        Assert.That(nodeIds, Contains.Item(junction.Id));

        // 4. Remove one sub-segment and verify incremental cleanup
        bool removed = graph.RemoveRoadSegment(n1.Id, junction.Id);
        Assert.That(removed, Is.True);
        Assert.That(graph.SpatialIndex.IndexedEdgeCount, Is.EqualTo(2));
    }

    [Test]
    public void RoadSnappingEngine_FindCurveIntersections_DetectsCrossingsViaSpatialIndex()
    {
        var graph = new RoadGraph();
        var n1 = graph.CreateNode(new Vector2(200f, 0f));
        var n2 = graph.CreateNode(new Vector2(200f, 400f));
        graph.AddRoadSegment(n1.Id, n2.Id);

        // Candidate horizontal curve crossing x = 200 at y = 200
        var crossingCurve = new CurveSegment(new Vector2(0f, 200f), new Vector2(400f, 200f));
        var intersections = RoadSnappingEngine.FindCurveIntersections(crossingCurve, graph);

        Assert.That(intersections.Count, Is.EqualTo(1));
        Assert.That(intersections[0].Point.X, Is.EqualTo(200f).Within(2f));
        Assert.That(intersections[0].Point.Y, Is.EqualTo(200f).Within(2f));

        // Candidate curve far away should have 0 intersections
        var farCurve = new CurveSegment(new Vector2(800f, 800f), new Vector2(1000f, 800f));
        var farIntersections = RoadSnappingEngine.FindCurveIntersections(farCurve, graph);
        Assert.That(farIntersections, Is.Empty);
    }

    [Test]
    public void TazManager_BindRoadConnectorsAndTransitCatchment_UsesSpatialIndexAccurately()
    {
        var grid = new CityGrid(10, 10, 64f);
        var graph = new RoadGraph();
        var parcelManager = new ParcelManager();
        var transit = new TransitManager();

        int zRes = grid.GetZoneId(1, 1);
        int zCom = grid.GetZoneId(7, 7);
        grid.ZoneCell(zRes, ZoneType.Residential);
        grid.GetZone(zRes).Population = 600;
        grid.ZoneCell(zCom, ZoneType.Commercial);
        grid.GetZone(zCom).Jobs = 600;

        for (int x = 1; x <= 7; x++)
        {
            int id = grid.GetZoneId(x, 1);
            graph.EnsureNode(id, grid.GetWorldCenter(id));
            if (x > 1) graph.AddRoadSegment(grid.GetZoneId(x - 1, 1), id);
        }
        for (int y = 2; y <= 7; y++)
        {
            int id = grid.GetZoneId(7, y);
            graph.EnsureNode(id, grid.GetWorldCenter(id));
            graph.AddRoadSegment(grid.GetZoneId(7, y - 1), id);
        }

        var path = graph.GetShortestNodePath(zRes, zCom);
        transit.CreateRoute("Metro Line 1", path, new[] { zRes, zCom }, Colors.Cyan, isLoop: false, fleetSize: 4);

        var tazManager = new TazManager();
        tazManager.Rebuild(grid, parcelManager, graph, transit);

        Assert.That(tazManager.ActiveZones.Count, Is.EqualTo(2));
        foreach (var taz in tazManager.ActiveZones)
        {
            Assert.That(taz.PrimaryRoadNodeId, Is.GreaterThanOrEqualTo(0));
            Assert.That(taz.Connectors.Count, Is.GreaterThanOrEqualTo(1));
            Assert.That(taz.AccessibleTransitRoutes.Count, Is.EqualTo(1));
        }
    }

    [Test]
    public void SpatialGridIndex_LargeNetworkQueries_ExecuteInSubMillisecondTime()
    {
        var graph = new RoadGraph();
        var parcelManager = new ParcelManager();
        parcelManager.Initialize(graph);

        // Build a 30x30 grid (900 nodes, ~3480 directed edges, ~6900 parcels)
        int gridSide = 30;
        float spacing = 128f;
        for (int y = 0; y < gridSide; y++)
        {
            for (int x = 0; x < gridSide; x++)
            {
                int id = y * gridSide + x;
                graph.EnsureNode(id, new Vector2(x * spacing, y * spacing));
                if (x > 0)
                {
                    graph.AddRoadSegment(id - 1, id);
                }
                if (y > 0)
                {
                    graph.AddRoadSegment((y - 1) * gridSide + x, id);
                }
            }
        }

        parcelManager.RefreshParcels(graph);
        Assert.That(graph.Nodes.Count, Is.EqualTo(900));
        Assert.That(graph.Edges.Count, Is.GreaterThan(3000));
        Assert.That(parcelManager.Parcels.Count, Is.GreaterThan(2000));

        // Measure 1000 cursor snap + zoning queries across the map
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++)
        {
            Vector2 pos = new Vector2((i % 25) * spacing + 40f, ((i / 25) % 25) * spacing + 25f);
            var snap = RoadSnappingEngine.FindSnap(pos, graph);
            var zoning = parcelManager.QueryZoningTarget(pos, graph, selectFullSide: false);
            _ = snap.Type;
            _ = zoning.Parcel;
        }
        sw.Stop();

        // 1000 combined FindSnap + QueryZoningTarget queries should complete well under 150ms (< 0.15ms per frame)
        Assert.That(sw.ElapsedMilliseconds, Is.LessThan(150),
            $"1000 spatial queries took {sw.ElapsedMilliseconds}ms, expected < 150ms");
    }
}
