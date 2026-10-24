using Godot;
using System;
using System.Collections.Generic;
using CitySim.Simulation;

namespace CitySim.Rendering;

/// <summary>
/// Pure C# Data-Oriented buffer builder for the three MultiMeshInstance2D layers
/// (Passenger Cars, Transit Buses, and Pedestrians).
/// Packs 12 floats per instance (8 floats 2D Transform + 4 floats RGBA Color) for single-call
/// RenderingServer.MultimeshSetBuffer() GPU uploads and supports headless unit testing.
/// </summary>
public sealed class VehicleMultimeshBufferBuilder
{
    public const int FloatsPerInstance = 12;
    public const int MaxCarInstances = CarTrafficManager.PoolCapacity;
    public const int MaxBusInstances = 4096;
    public const int MaxPedestrianInstances = PedestrianManager.PoolCapacity;

    private TransitManager _transitManager;
    private CarTrafficManager _carManager;
    private PedestrianManager _pedManager;
    private RoadGraph _graph;
    private ParcelManager _parcelManager;

    private readonly float[] _carMultimeshBuffer = new float[MaxCarInstances * FloatsPerInstance];
    private readonly float[] _busMultimeshBuffer = new float[MaxBusInstances * FloatsPerInstance];
    private readonly float[] _pedMultimeshBuffer = new float[MaxPedestrianInstances * FloatsPerInstance];

    public float[] CarMultimeshBuffer => _carMultimeshBuffer;
    public float[] BusMultimeshBuffer => _busMultimeshBuffer;
    public float[] PedestrianMultimeshBuffer => _pedMultimeshBuffer;

    public int ActiveCarInstances { get; private set; }
    public int ActiveBusInstances { get; private set; }
    public int ActivePedestrianInstances { get; private set; }

    public void Initialize(TransitManager tm, CarTrafficManager carMgr, PedestrianManager pedMgr, RoadGraph graph, ParcelManager parcelMgr = null)
    {
        _transitManager = tm;
        _carManager = carMgr;
        _pedManager = pedMgr;
        _graph = graph;
        _parcelManager = parcelMgr;
    }

    public void SetParcelManager(ParcelManager parcelMgr)
    {
        _parcelManager = parcelMgr;
    }

    /// <summary>
    /// Populates the three flat 12-float Transform2D + Color buffers for Cars, Buses, and Pedestrians.
    /// </summary>
    public void BuildBuffers(float animTime = 0f)
    {
        ActiveCarInstances = 0;
        ActiveBusInstances = 0;
        ActivePedestrianInstances = 0;

        if (_graph == null) return;

        // =========================================================================
        // 1. POPULATE PASSENGER CARS MULTIMESH BUFFER (12 floats per instance)
        // =========================================================================
        int carIdx = 0;

        if (_carManager != null && _carManager.VisibleInstanceCount > 0)
        {
            int copyCount = Math.Min(_carManager.VisibleInstanceCount, MaxCarInstances);
            Array.Copy(
                _carManager.MultimeshBuffer,
                0,
                _carMultimeshBuffer,
                0,
                copyCount * FloatsPerInstance);
            carIdx = copyCount;
        }

        for (int eIdx = 0; eIdx < _graph.Edges.Count && carIdx < MaxCarInstances; eIdx++)
        {
            var edge = _graph.Edges[eIdx];
            if (edge == null || edge.FromId == -1 || edge.ToId == -1 || edge.CurrentVolume <= 0.05f) continue;

            var fromNode = _graph.GetNode(edge.FromId);
            var toNode = _graph.GetNode(edge.ToId);
            if (fromNode == null || toNode == null) continue;

            float volume = edge.CurrentVolume;
            float capacity = Mathf.Max(edge.Capacity, 1f);
            float congestion = edge.GetCongestionRatio();

            float speedFactor = 1.0f / (1.0f + 0.35f * Mathf.Pow(congestion, 3.0f));
            float streamSpeed = (edge.FreeFlowSpeed / 12f) * speedFactor;

            Color streamColor;
            if (congestion < 0.50f)
            {
                streamColor = new Color(0.95f, 0.98f, 1.0f, 0.95f);
            }
            else if (congestion < 0.85f)
            {
                float t = (congestion - 0.50f) / 0.35f;
                streamColor = new Color(1.0f, Mathf.Lerp(0.95f, 0.70f, t), 0.20f, 0.95f);
            }
            else
            {
                float t = Mathf.Clamp((congestion - 0.85f) / 0.40f, 0f, 1f);
                streamColor = new Color(1.0f, Mathf.Lerp(0.40f, 0.15f, t), 0.15f, 0.55f);
            }

            float tMax = _graph.GetEdgeTrafficExtent(edge, _parcelManager);
            int pulses = Mathf.Clamp(Mathf.RoundToInt(volume / 250f) + 1, 1, 6);
            float scaleX = Mathf.Clamp((4.0f + (volume / capacity) * 4.0f) / 7.5f, 0.75f, 1.15f);

            for (int k = 0; k < pulses && carIdx < MaxCarInstances; k++)
            {
                float phase = ((animTime * streamSpeed * 0.15f) + ((float)k / pulses)) % 1.0f;
                if (phase < 0f) phase += 1.0f;

                float tAlongEdge = phase * tMax;

                Vector2 center, tangent;
                if (edge.Curve != null)
                {
                    center = edge.Curve.Evaluate(tAlongEdge) + edge.Curve.GetNormal(tAlongEdge) * 4.5f;
                    tangent = edge.Curve.GetTangent(tAlongEdge);
                }
                else
                {
                    Vector2 dir = (toNode.WorldPosition - fromNode.WorldPosition).Normalized();
                    Vector2 normal = new Vector2(-dir.Y, dir.X);
                    center = fromNode.WorldPosition.Lerp(toNode.WorldPosition, tAlongEdge) + normal * 4.5f;
                    tangent = dir;
                }

                CarTrafficManager.WriteInstanceTransform2DAndColor(
                    _carMultimeshBuffer,
                    carIdx++,
                    center,
                    tangent,
                    streamColor,
                    scaleX,
                    1.0f);
            }
        }

        ActiveCarInstances = carIdx;

        // =========================================================================
        // 2. POPULATE TRANSIT BUSES MULTIMESH BUFFER (12 floats per instance)
        // =========================================================================
        int busIdx = 0;
        if (_transitManager != null)
        {
            for (int vIdx = 0; vIdx < _transitManager.Vehicles.Count && busIdx < MaxBusInstances; vIdx++)
            {
                var vehicle = _transitManager.Vehicles[vIdx];
                var route = _transitManager.Routes.Find(r => r.Id == vehicle.RouteId);
                if (route == null) continue;

                Vector2 pos = vehicle.GetWorldPosition(route, _graph);
                if (pos == Vector2.Zero && route.PathNodeIds.Count == 0) continue;

                float rot = vehicle.GetRotation(route, _graph);
                Vector2 tangent = new Vector2(MathF.Cos(rot), MathF.Sin(rot));

                CarTrafficManager.WriteInstanceTransform2DAndColor(
                    _busMultimeshBuffer,
                    busIdx++,
                    pos,
                    tangent,
                    route.RouteColor);
            }
        }

        ActiveBusInstances = busIdx;

        // =========================================================================
        // 3. POPULATE PEDESTRIANS MULTIMESH BUFFER (12 floats per instance)
        // =========================================================================
        int pedIdx = 0;

        if (_pedManager != null && _pedManager.VisibleInstanceCount > 0)
        {
            int copyCount = Math.Min(_pedManager.VisibleInstanceCount, MaxPedestrianInstances);
            Array.Copy(
                _pedManager.MultimeshBuffer,
                0,
                _pedMultimeshBuffer,
                0,
                copyCount * FloatsPerInstance);
            pedIdx = copyCount;
        }

        for (int eIdx = 0; eIdx < _graph.Edges.Count && pedIdx < MaxPedestrianInstances; eIdx++)
        {
            var edge = _graph.Edges[eIdx];
            if (edge == null || edge.FromId == -1 || edge.ToId == -1 || edge.PedestrianVolume <= 0.05f) continue;

            var fromNode = _graph.GetNode(edge.FromId);
            var toNode = _graph.GetNode(edge.ToId);
            if (fromNode == null || toNode == null) continue;

            float pedVol = edge.PedestrianVolume;
            float pedRatio = edge.GetPedestrianCongestionRatio();
            float walkSpeed = (edge.WalkingSpeed / 10f) * (1.0f - 0.20f * Mathf.Clamp(pedRatio, 0f, 0.7f));

            int pedDashes = Mathf.Clamp(Mathf.RoundToInt(pedVol / 200f) + 1, 1, 8);
            Color pedColor = pedRatio > 0.6f
                ? new Color(0.35f, 0.85f, 1.0f, 0.95f)
                : new Color(0.40f, 0.95f, 0.65f, 0.90f);

            for (int p = 0; p < pedDashes && pedIdx < MaxPedestrianInstances; p++)
            {
                float phase = ((animTime * walkSpeed * 0.20f) + ((float)p / pedDashes)) % 1.0f;
                if (phase < 0f) phase += 1.0f;

                Vector2 pedPosR, tangentR;
                if (edge.Curve != null)
                {
                    pedPosR = edge.Curve.Evaluate(phase) + edge.Curve.GetNormal(phase) * 7.5f;
                    tangentR = edge.Curve.GetTangent(phase);
                }
                else
                {
                    Vector2 dir = (toNode.WorldPosition - fromNode.WorldPosition).Normalized();
                    Vector2 normal = new Vector2(-dir.Y, dir.X);
                    pedPosR = fromNode.WorldPosition.Lerp(toNode.WorldPosition, phase) + normal * 7.5f;
                    tangentR = dir;
                }

                float walkBobR = Mathf.Sin((animTime * 12f) + p * 2.0f) * 0.6f;
                Vector2 bobPosR = pedPosR + tangentR * walkBobR;

                CarTrafficManager.WriteInstanceTransform2DAndColor(
                    _pedMultimeshBuffer,
                    pedIdx++,
                    bobPosR,
                    tangentR,
                    pedColor);

                if (pedVol > 400f && pedIdx < MaxPedestrianInstances)
                {
                    float phaseL = (phase + 0.5f) % 1.0f;
                    Vector2 pedPosL, tangentL;
                    if (edge.Curve != null)
                    {
                        pedPosL = edge.Curve.Evaluate(phaseL) - edge.Curve.GetNormal(phaseL) * 7.5f;
                        tangentL = edge.Curve.GetTangent(phaseL);
                    }
                    else
                    {
                        Vector2 dir = (toNode.WorldPosition - fromNode.WorldPosition).Normalized();
                        Vector2 normal = new Vector2(-dir.Y, dir.X);
                        pedPosL = fromNode.WorldPosition.Lerp(toNode.WorldPosition, phaseL) - normal * 7.5f;
                        tangentL = dir;
                    }

                    float walkBobL = Mathf.Sin((animTime * 12f) + (p + 3) * 2.0f) * 0.6f;
                    Vector2 bobPosL = pedPosL + tangentL * walkBobL;

                    CarTrafficManager.WriteInstanceTransform2DAndColor(
                        _pedMultimeshBuffer,
                        pedIdx++,
                        bobPosL,
                        tangentL,
                        pedColor);
                }
            }
        }

        ActivePedestrianInstances = pedIdx;
    }
}

/// <summary>
/// Hardware-instanced vehicle and pedestrian renderer using three MultiMeshInstance2D layers
/// (Cars, Transit Buses, and Pedestrians) and flat 12-float Transform2D + Color buffers uploaded
/// via RenderingServer.MultimeshSetBuffer().
/// </summary>
public partial class VehicleRenderer : Node2D
{
    public const int FloatsPerInstance = VehicleMultimeshBufferBuilder.FloatsPerInstance;

    private TransitManager _transitManager;
    private CarTrafficManager _carManager;
    private PedestrianManager _pedManager;
    private RoadGraph _graph;
    private ParcelManager _parcelManager;
    private float _animTime = 0f;

    private readonly VehicleMultimeshBufferBuilder _bufferBuilder = new VehicleMultimeshBufferBuilder();

    private MultiMeshInstance2D _carsMultiMeshInstance;
    private MultiMeshInstance2D _busesMultiMeshInstance;
    private MultiMeshInstance2D _pedestriansMultiMeshInstance;

    private MultiMesh _carsMultiMesh;
    private MultiMesh _busesMultiMesh;
    private MultiMesh _pedestriansMultiMesh;

    private float[] _carGpuUploadBuffer = Array.Empty<float>();
    private float[] _busGpuUploadBuffer = Array.Empty<float>();
    private float[] _pedGpuUploadBuffer = Array.Empty<float>();

    public VehicleMultimeshBufferBuilder BufferBuilder => _bufferBuilder;
    public float[] CarMultimeshBuffer => _bufferBuilder.CarMultimeshBuffer;
    public float[] BusMultimeshBuffer => _bufferBuilder.BusMultimeshBuffer;
    public float[] PedestrianMultimeshBuffer => _bufferBuilder.PedestrianMultimeshBuffer;

    public int ActiveCarInstances => _bufferBuilder.ActiveCarInstances;
    public int ActiveBusInstances => _bufferBuilder.ActiveBusInstances;
    public int ActivePedestrianInstances => _bufferBuilder.ActivePedestrianInstances;

    public MultiMeshInstance2D CarsMultiMeshInstance => _carsMultiMeshInstance;
    public MultiMeshInstance2D BusesMultiMeshInstance => _busesMultiMeshInstance;
    public MultiMeshInstance2D PedestriansMultiMeshInstance => _pedestriansMultiMeshInstance;

    public bool ShowTransitRoutes { get; set; } = true;

    public override void _Ready()
    {
        SetupMultiMeshLayers();
    }

    public void Initialize(TransitManager tm, CarTrafficManager carMgr, PedestrianManager pedMgr, RoadGraph graph, ParcelManager parcelMgr = null)
    {
        _transitManager = tm;
        _carManager = carMgr;
        _pedManager = pedMgr;
        _graph = graph;
        _parcelManager = parcelMgr;
        _bufferBuilder.Initialize(tm, carMgr, pedMgr, graph, parcelMgr);

        if (IsInsideTree() && _carsMultiMeshInstance == null)
        {
            SetupMultiMeshLayers();
        }
    }

    public void SetParcelManager(ParcelManager parcelMgr)
    {
        _parcelManager = parcelMgr;
        _bufferBuilder.SetParcelManager(parcelMgr);
    }

    private void SetupMultiMeshLayers()
    {
        if (_carsMultiMeshInstance != null) return;

        // 1. Passenger Cars MultiMeshInstance2D (7.5m x 3.8m quad)
        _carsMultiMesh = CreateMultiMesh(new Vector2(7.5f, 3.8f));
        _carsMultiMeshInstance = new MultiMeshInstance2D
        {
            Name = "CarsMultiMesh",
            Multimesh = _carsMultiMesh,
            Material = LoadShaderMaterial("res://Shaders/car_instanced.gdshader")
        };
        AddChild(_carsMultiMeshInstance);

        // 2. Transit Buses MultiMeshInstance2D (14.5m x 7.2m quad)
        _busesMultiMesh = CreateMultiMesh(new Vector2(14.5f, 7.2f));
        _busesMultiMeshInstance = new MultiMeshInstance2D
        {
            Name = "BusesMultiMesh",
            Multimesh = _busesMultiMesh,
            Material = LoadShaderMaterial("res://Shaders/bus_instanced.gdshader")
        };
        AddChild(_busesMultiMeshInstance);

        // 3. Pedestrians MultiMeshInstance2D (3.6m x 3.6m quad)
        _pedestriansMultiMesh = CreateMultiMesh(new Vector2(3.6f, 3.6f));
        _pedestriansMultiMeshInstance = new MultiMeshInstance2D
        {
            Name = "PedestriansMultiMesh",
            Multimesh = _pedestriansMultiMesh,
            Material = LoadShaderMaterial("res://Shaders/pedestrian_instanced.gdshader")
        };
        AddChild(_pedestriansMultiMeshInstance);
    }

    private static MultiMesh CreateMultiMesh(Vector2 quadSize)
    {
        var quad = new QuadMesh
        {
            Size = quadSize
        };

        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
            UseColors = true,
            UseCustomData = false,
            Mesh = quad,
            InstanceCount = 0,
            VisibleInstanceCount = 0
        };
        return mm;
    }

    private static ShaderMaterial LoadShaderMaterial(string shaderPath)
    {
        if (!ResourceLoader.Exists(shaderPath)) return null;
        var shader = GD.Load<Shader>(shaderPath);
        if (shader == null) return null;
        return new ShaderMaterial { Shader = shader };
    }

    public override void _Process(double delta)
    {
        _animTime += (float)delta * 4f;
        _bufferBuilder.BuildBuffers(_animTime);
        UploadMultimeshBuffersToGpu();
        QueueRedraw();
    }

    public void BuildMultimeshBuffers(float animTime = 0f)
    {
        _bufferBuilder.BuildBuffers(animTime);
    }

    private void UploadMultimeshBuffersToGpu()
    {
        if (!IsInsideTree()) return;
        if (_carsMultiMeshInstance == null)
        {
            SetupMultiMeshLayers();
        }

        UploadLayerBuffer(_carsMultiMesh, _bufferBuilder.CarMultimeshBuffer, _bufferBuilder.ActiveCarInstances, ref _carGpuUploadBuffer);
        UploadLayerBuffer(_busesMultiMesh, _bufferBuilder.BusMultimeshBuffer, _bufferBuilder.ActiveBusInstances, ref _busGpuUploadBuffer);
        UploadLayerBuffer(_pedestriansMultiMesh, _bufferBuilder.PedestrianMultimeshBuffer, _bufferBuilder.ActivePedestrianInstances, ref _pedGpuUploadBuffer);
    }

    private static void UploadLayerBuffer(MultiMesh multimesh, float[] sourceBuffer, int activeInstances, ref float[] gpuUploadBuffer)
    {
        if (multimesh == null) return;

        if (activeInstances <= 0)
        {
            multimesh.VisibleInstanceCount = 0;
            return;
        }

        int currentAllocated = multimesh.InstanceCount;
        if (currentAllocated < activeInstances)
        {
            int newCapacity = 64;
            while (newCapacity < activeInstances)
            {
                newCapacity *= 2;
            }
            multimesh.InstanceCount = newCapacity;
            currentAllocated = newCapacity;
        }

        int requiredFloats = currentAllocated * FloatsPerInstance;
        if (gpuUploadBuffer.Length != requiredFloats)
        {
            gpuUploadBuffer = new float[requiredFloats];
        }

        Array.Copy(sourceBuffer, 0, gpuUploadBuffer, 0, activeInstances * FloatsPerInstance);
        multimesh.VisibleInstanceCount = activeInstances;
        RenderingServer.MultimeshSetBuffer(multimesh.GetRid(), gpuUploadBuffer);
    }

    public override void _Draw()
    {
        if (_graph == null) return;

        // =========================================================================
        // 1. DRAW TRANSIT ROUTE CORRIDORS (CURVED TRACK LINES & SHOULDER STOPS)
        // =========================================================================
        if (ShowTransitRoutes && _transitManager != null)
        {
            foreach (var route in _transitManager.Routes)
            {
                Color trackColor = new Color(route.RouteColor.R, route.RouteColor.G, route.RouteColor.B, 0.65f);

                for (int i = 0; i < route.PathNodeIds.Count - 1; i++)
                {
                    int id1 = route.PathNodeIds[i];
                    int id2 = route.PathNodeIds[i + 1];
                    var n1 = _graph.GetNode(id1);
                    var n2 = _graph.GetNode(id2);
                    if (n1 == null || n2 == null) continue;

                    int edgeId = _graph.FindEdgeId(id1, id2);
                    var edge = (edgeId >= 0 && edgeId < _graph.Edges.Count) ? _graph.Edges[edgeId] : null;

                    if (edge != null && edge.Curve != null)
                    {
                        DrawPolyline(edge.Curve.GetSampledPoints(16), trackColor, 3.0f, true);
                    }
                    else
                    {
                        DrawLine(n1.WorldPosition, n2.WorldPosition, trackColor, 3.0f, true);
                    }
                }

                if (route.IsLoop && route.PathNodeIds.Count > 2)
                {
                    int lastId = route.PathNodeIds[route.PathNodeIds.Count - 1];
                    int firstId = route.PathNodeIds[0];
                    var last = _graph.GetNode(lastId);
                    var first = _graph.GetNode(firstId);
                    if (last != null && first != null)
                    {
                        int loopEdgeId = _graph.FindEdgeId(lastId, firstId);
                        var loopEdge = (loopEdgeId >= 0 && loopEdgeId < _graph.Edges.Count) ? _graph.Edges[loopEdgeId] : null;

                        if (loopEdge != null && loopEdge.Curve != null)
                        {
                            DrawPolyline(loopEdge.Curve.GetSampledPoints(16), trackColor, 3.0f, true);
                        }
                        else
                        {
                            DrawLine(last.WorldPosition, first.WorldPosition, trackColor, 3.0f, true);
                        }
                    }
                }
            }

            foreach (var route in _transitManager.Routes)
            {
                foreach (var stopId in route.StopNodeIds)
                {
                    var node = _graph.GetNode(stopId);
                    if (node == null) continue;

                    Vector2 stopPos = TransitManager.GetStopWorldPosition(stopId, route, _graph);

                    DrawRect(new Rect2(stopPos.X - 5.5f, stopPos.Y - 5.5f, 11f, 11f), route.RouteColor);
                    DrawRect(new Rect2(stopPos.X - 5.5f, stopPos.Y - 5.5f, 11f, 11f), Colors.White, false, 1.2f);

                    if (_transitManager.Stops.TryGetValue(stopId, out var stopData) && stopData.WaitingPassengers > 5f)
                    {
                        DrawCircle(stopPos + new Vector2(6, -6), 2.8f, Colors.Gold);
                    }
                }
            }
        }

        // =========================================================================
        // 2. DRAW BUS TELEMETRY OVERLAYS (DWELL HALOS & PASSENGER CAPACITY BADGES)
        // =========================================================================
        if (_transitManager != null)
        {
            foreach (var vehicle in _transitManager.Vehicles)
            {
                var route = _transitManager.Routes.Find(r => r.Id == vehicle.RouteId);
                if (route == null) continue;

                Vector2 pos = vehicle.GetWorldPosition(route, _graph);
                if (pos == Vector2.Zero) continue;

                if (vehicle.State == VehicleState.AtStop)
                {
                    float pulse = 9f + Mathf.Sin(_animTime * 2f) * 3f;
                    DrawArc(pos, pulse, 0, Mathf.Tau, 20, new Color(1f, 0.9f, 0.2f, 0.85f), 2.0f, true);
                }
                else if (vehicle.State == VehicleState.TerminusLayover)
                {
                    DrawArc(pos, 11f, 0, Mathf.Tau, 20, new Color(0.3f, 0.7f, 1f, 0.9f), 1.6f, true);
                }

                DrawString(
                    ThemeDB.FallbackFont,
                    pos + new Vector2(-12, -12),
                    $"{(int)vehicle.Passengers}/{vehicle.Capacity}",
                    HorizontalAlignment.Center,
                    -1,
                    9,
                    Colors.White
                );

                float fillRatio = (float)vehicle.Passengers / Mathf.Max(1, vehicle.Capacity);
                Rect2 barBg = new Rect2(pos.X - 8f, pos.Y - 15f, 16f, 3f);
                DrawRect(barBg, new Color(0.1f, 0.1f, 0.1f, 0.8f));

                Rect2 barFill = new Rect2(pos.X - 8f, pos.Y - 15f, 16f * fillRatio, 3f);
                Color fillColor = Colors.Green;
                if (fillRatio > 0.85f) fillColor = Colors.Red;
                else if (fillRatio > 0.5f) fillColor = Colors.Yellow;
                DrawRect(barFill, fillColor);
            }
        }
    }
}
