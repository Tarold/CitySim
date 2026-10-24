using Godot;
using NUnit.Framework;
using System;
using System.IO;
using CitySim.Simulation;
using CitySim.Rendering;

namespace CitySim.Tests.Simulation;

[TestFixture]
public class Phase3GpuInstancingAndDodTests
{
    [Test]
    public void CarTrafficManager_StructPoolAndFreeList_AllocatesAndRecycles32768SlotsInO1()
    {
        var carMgr = new CarTrafficManager();
        Assert.That(CarTrafficManager.PoolCapacity, Is.EqualTo(32768));
        Assert.That(carMgr.CarSlots.Length, Is.EqualTo(32768));
        Assert.That(carMgr.FreeSlotCount, Is.EqualTo(32768));
        Assert.That(carMgr.ActiveCarCount, Is.EqualTo(0));

        // Allocate all 32,768 slots
        for (int i = 0; i < CarTrafficManager.PoolCapacity; i++)
        {
            int idx = carMgr.AllocateCarSlot(edgeId: 0, progress: 0.25f, speed: 45f);
            Assert.That(idx, Is.EqualTo(i));
        }

        Assert.That(carMgr.ActiveCarCount, Is.EqualTo(32768));
        Assert.That(carMgr.FreeSlotCount, Is.EqualTo(0));
        Assert.That(carMgr.AllocateCarSlot(0), Is.EqualTo(-1), "Pool must return -1 when all 32,768 slots are active");

        // Release a slot in the middle and verify O(1) FreeList reuse
        carMgr.ReleaseCarSlot(12345);
        Assert.That(carMgr.ActiveCarCount, Is.EqualTo(32767));
        Assert.That(carMgr.FreeSlotCount, Is.EqualTo(1));
        Assert.That(carMgr.CarSlots[12345].Active, Is.False);

        int reusedIdx = carMgr.AllocateCarSlot(edgeId: 2, progress: 0.5f, speed: 60f);
        Assert.That(reusedIdx, Is.EqualTo(12345), "FreeList must immediately recycle the released slot index");
        Assert.That(carMgr.CarSlots[12345].Active, Is.True);
        Assert.That(carMgr.CarSlots[12345].EdgeId, Is.EqualTo(2));
    }

    [Test]
    public void CarTrafficManager_ParallelFor_Updates10000CarsAndWrites12FloatMultimeshBuffer()
    {
        var graph = new RoadGraph();
        var n0 = graph.CreateNode(new Vector2(0, 0));
        var n1 = graph.CreateNode(new Vector2(200, 0));
        var curve = CurveSegment.CreateFromThreePoints(n0.WorldPosition, new Vector2(100, 40), n1.WorldPosition);
        graph.AddCurvedRoadSegment(n0.Id, n1.Id, curve);

        int edgeId = graph.FindEdgeId(n0.Id, n1.Id);
        graph.Edges[edgeId].CurrentVolume = 800f;

        var carMgr = new CarTrafficManager();
        carMgr.Initialize(graph);

        const int simCarCount = 10000;
        Color testColor = new Color(0.85f, 0.20f, 0.18f, 1.0f);
        for (int i = 0; i < simCarCount; i++)
        {
            float progress = (i % 700) / 1000f; // [0.0, 0.70]
            carMgr.AllocateCarSlot(edgeId, progress, speed: 40f, color: testColor);
        }

        Assert.That(carMgr.ActiveCarCount, Is.EqualTo(simCarCount));

        // Run Parallel.For update
        carMgr.Update(delta: 0.05f, gameSpeed: 1.0f, graph, trafficLights: null);

        Assert.That(carMgr.VisibleInstanceCount, Is.EqualTo(simCarCount));
        Assert.That(carMgr.MultimeshBuffer.Length, Is.EqualTo(CarTrafficManager.PoolCapacity * 12));

        // Verify 12-float Transform2D + Color entries in MultimeshBuffer
        for (int inst = 0; inst < 20; inst++)
        {
            int baseIdx = inst * 12;
            float xx = carMgr.MultimeshBuffer[baseIdx + 0];
            float yx = carMgr.MultimeshBuffer[baseIdx + 1];
            float ox = carMgr.MultimeshBuffer[baseIdx + 3];
            float xy = carMgr.MultimeshBuffer[baseIdx + 4];
            float yy = carMgr.MultimeshBuffer[baseIdx + 5];
            float oy = carMgr.MultimeshBuffer[baseIdx + 7];
            float r = carMgr.MultimeshBuffer[baseIdx + 8];
            float g = carMgr.MultimeshBuffer[baseIdx + 9];
            float b = carMgr.MultimeshBuffer[baseIdx + 10];
            float a = carMgr.MultimeshBuffer[baseIdx + 11];

            // Orthonormal 2D rotation matrix check: xx^2 + xy^2 == 1, yy == xx, yx == -xy
            float lenSq = xx * xx + xy * xy;
            Assert.That(lenSq, Is.EqualTo(1.0f).Within(1e-3f));
            Assert.That(yy, Is.EqualTo(xx).Within(1e-4f));
            Assert.That(yx, Is.EqualTo(-xy).Within(1e-4f));

            // World position is within curve bounds
            Assert.That(ox, Is.InRange(-10f, 210f));
            Assert.That(oy, Is.InRange(-10f, 60f));

            // Color matches testColor
            Assert.That(r, Is.EqualTo(testColor.R).Within(1e-4f));
            Assert.That(g, Is.EqualTo(testColor.G).Within(1e-4f));
            Assert.That(b, Is.EqualTo(testColor.B).Within(1e-4f));
            Assert.That(a, Is.EqualTo(1.0f).Within(1e-4f));
        }
    }

    [Test]
    public void PedestrianManager_StructPoolAndParallelFor_Updates10000PedestriansAndWritesMultimeshBuffer()
    {
        var graph = new RoadGraph();
        var n0 = graph.CreateNode(new Vector2(0, 0));
        var n1 = graph.CreateNode(new Vector2(150, 0));
        graph.AddRoadSegment(n0.Id, n1.Id);

        int edgeId = graph.FindEdgeId(n0.Id, n1.Id);
        graph.Edges[edgeId].PedestrianVolume = 350f;

        var pedMgr = new PedestrianManager();
        pedMgr.Initialize(graph);
        Assert.That(PedestrianManager.PoolCapacity, Is.EqualTo(32768));

        const int simPedCount = 10000;
        Color clothColor = new Color(0.25f, 0.75f, 0.35f, 1.0f);
        for (int i = 0; i < simPedCount; i++)
        {
            float progress = (i % 800) / 1000f;
            pedMgr.AllocatePedestrianSlot(edgeId, progress, speed: 2.0f, side: 1, color: clothColor);
        }

        Assert.That(pedMgr.ActivePedestrianCount, Is.EqualTo(simPedCount));

        pedMgr.Update(delta: 0.1f, gameSpeed: 1.0f, graph);

        Assert.That(pedMgr.VisibleInstanceCount, Is.EqualTo(simPedCount));

        // Check first instance transform & color in PedestrianManager.MultimeshBuffer
        float oy = pedMgr.MultimeshBuffer[7];
        Assert.That(oy, Is.EqualTo(PedestrianManager.DefaultSidewalkOffset).Within(0.05f));
        Assert.That(pedMgr.MultimeshBuffer[8], Is.EqualTo(clothColor.R).Within(1e-4f));
        Assert.That(pedMgr.MultimeshBuffer[9], Is.EqualTo(clothColor.G).Within(1e-4f));
        Assert.That(pedMgr.MultimeshBuffer[10], Is.EqualTo(clothColor.B).Within(1e-4f));
    }

    [Test]
    public void VehicleRenderer_BuildMultimeshBuffers_PopulatesAllThreeLayersWith12FloatStride()
    {
        var grid = new CityGrid(6, 6, 64f);
        grid.GenerateDefaultCity();
        var graph = new RoadGraph();
        graph.BuildFromGrid(grid);

        // Activate road volume & pedestrian volume on edge 0
        graph.Edges[0].CurrentVolume = 600f;
        graph.Edges[0].PedestrianVolume = 500f;

        var transitMgr = new TransitManager();
        transitMgr.CreateDefaultRoutes(grid, graph);

        var carMgr = new CarTrafficManager();
        carMgr.Initialize(graph);
        carMgr.AllocateCarSlot(0, progress: 0.3f, speed: 40f, color: Colors.Red);
        carMgr.Update(0.05f, 1.0f, graph, null);

        var pedMgr = new PedestrianManager();
        pedMgr.Initialize(graph, grid, transitMgr);
        pedMgr.AllocatePedestrianSlot(0, progress: 0.4f, speed: 2f, side: 1, color: Colors.Cyan);
        pedMgr.Update(0.05f, 1.0f, graph, grid, transitMgr);

        var bufferBuilder = new VehicleMultimeshBufferBuilder();
        bufferBuilder.Initialize(transitMgr, carMgr, pedMgr, graph);
        bufferBuilder.BuildBuffers(animTime: 1.5f);

        Assert.That(bufferBuilder.ActiveCarInstances, Is.GreaterThanOrEqualTo(2), "Must include discrete CarSlots + flow stream vehicles");
        Assert.That(bufferBuilder.ActiveBusInstances, Is.EqualTo(transitMgr.Vehicles.Count), "Must include all transit buses");
        Assert.That(bufferBuilder.ActivePedestrianInstances, Is.GreaterThanOrEqualTo(2), "Must include discrete PedestrianSlots + sidewalk flow");
    }

    [Test]
    public void RoadRenderer_HeatmapShaderBuffer_UpdatesOnlyVertexColorsOnMsaWithoutRebuildingGeometry()
    {
        var graph = new RoadGraph();
        var n0 = graph.CreateNode(new Vector2(0, 0));
        var n1 = graph.CreateNode(new Vector2(200, 0));
        var curve = CurveSegment.CreateFromThreePoints(n0.WorldPosition, new Vector2(100, 30), n1.WorldPosition);
        graph.AddCurvedRoadSegment(n0.Id, n1.Id, curve, capacity: 1000f);

        int edgeId = graph.FindEdgeId(n0.Id, n1.Id);
        graph.Edges[edgeId].CurrentVolume = 200f; // Congestion ratio = 0.20

        var heatmapBuffer = new RoadHeatmapMeshBuffer();
        heatmapBuffer.Initialize(graph);

        Assert.That(heatmapBuffer.GeometryRebuildCount, Is.EqualTo(1), "Geometry built once on Initialize");
        Assert.That(heatmapBuffer.ColorBufferUpdateCount, Is.EqualTo(1));
        Assert.That(heatmapBuffer.MeshVertices.Length, Is.GreaterThan(0));

        var span = heatmapBuffer.EdgeVertexSpans[0];
        float initialEncodedRatio = heatmapBuffer.MeshVertexColors[span.StartVertex].R;
        Assert.That(initialEncodedRatio, Is.EqualTo(0.20f * 0.5f).Within(1e-3f));

        // Simulate MSA traffic volume update (congestion rises from 0.20 to 1.20)
        graph.Edges[edgeId].CurrentVolume = 1200f;
        heatmapBuffer.MarkColorsDirty();
        heatmapBuffer.EnsureSynced();

        Assert.That(heatmapBuffer.GeometryRebuildCount, Is.EqualTo(1),
            "Bézier curve geometry MUST NOT be rebuilt when only MSA traffic volumes change");
        Assert.That(heatmapBuffer.ColorBufferUpdateCount, Is.EqualTo(2),
            "Vertex color buffer MUST be updated when MSA traffic volumes change");

        float updatedEncodedRatio = heatmapBuffer.MeshVertexColors[span.StartVertex].R;
        Assert.That(updatedEncodedRatio, Is.EqualTo(1.20f * 0.5f).Within(1e-3f));

        // Now add a new road segment -> topology change SHOULD trigger geometry rebuild
        var n2 = graph.CreateNode(new Vector2(400, 0));
        graph.AddRoadSegment(n1.Id, n2.Id);
        heatmapBuffer.EnsureSynced();

        Assert.That(heatmapBuffer.GeometryRebuildCount, Is.EqualTo(2),
            "Topology modification must trigger geometry rebuild");
    }

    [Test]
    public void Phase3Shaders_ExistAndContainValidCanvasItemShaderDefinitions()
    {
        string projectRoot = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."));
        string[] shaderFiles = new[]
        {
            Path.Combine(projectRoot, "Shaders", "car_instanced.gdshader"),
            Path.Combine(projectRoot, "Shaders", "bus_instanced.gdshader"),
            Path.Combine(projectRoot, "Shaders", "pedestrian_instanced.gdshader"),
            Path.Combine(projectRoot, "Shaders", "road_heatmap.gdshader")
        };

        foreach (string path in shaderFiles)
        {
            Assert.That(File.Exists(path), Is.True, $"Missing shader file: {path}");
            string content = File.ReadAllText(path);
            Assert.That(content, Does.Contain("shader_type canvas_item;"));
        }
    }
}
