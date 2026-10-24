using Godot;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CitySim.Simulation;

/// <summary>
/// Data-Oriented value-type slot representing a vehicle in the fixed-size pool (zero GC overhead).
/// </summary>
public struct CarSlot
{
    public int EdgeId;
    public float Progress;
    public float Speed;
    public Color CarColor;
    public bool IsStopped;
    public bool Active;

    public Vector2 GetWorldPosition(RoadGraph graph, float laneOffset = CarTrafficManager.DefaultLaneOffset)
    {
        if (!Active || graph == null || EdgeId < 0 || EdgeId >= graph.Edges.Count) return Vector2.Zero;
        return CarTrafficManager.GetCarWorldPosition(graph.Edges[EdgeId], Progress, laneOffset, graph);
    }

    public float GetRotation(RoadGraph graph)
    {
        if (!Active || graph == null || EdgeId < 0 || EdgeId >= graph.Edges.Count) return 0f;
        return CarTrafficManager.GetCarRotation(graph.Edges[EdgeId], Progress, graph);
    }
}

public class VisualCar
{
    public int EdgeId;
    public float Progress; // 0.0 to 1.0
    public float Speed;
    public Color CarColor;
    public bool IsStopped;
    public int SlotIndex = -1;

    public Vector2 GetWorldPosition(RoadGraph graph, float laneOffset = 4.5f)
    {
        if (graph == null || EdgeId < 0 || EdgeId >= graph.Edges.Count) return Vector2.Zero;
        var edge = graph.Edges[EdgeId];
        return CarTrafficManager.GetCarWorldPosition(edge, Progress, laneOffset, graph);
    }

    public float GetRotation(RoadGraph graph)
    {
        if (graph == null || EdgeId < 0 || EdgeId >= graph.Edges.Count) return 0f;
        var edge = graph.Edges[EdgeId];
        return CarTrafficManager.GetCarRotation(edge, Progress, graph);
    }
}

public class CarTrafficManager
{
    public const int PoolCapacity = 32768;
    public const int MaxCars = PoolCapacity;
    public const int FloatsPerInstance = 12; // Godot 2D MultiMesh: 8 floats Transform2D + 4 floats Color
    public const float DemandThreshold = 0.05f;
    public const float DefaultLaneOffset = 4.5f;

    // Pre-allocated Data-Oriented pool of value-type structs + O(1) FreeList
    private readonly CarSlot[] _cars = new CarSlot[PoolCapacity];
    private readonly int[] _freeList = new int[PoolCapacity];
    private int _freeCount;
    private int _activeCount;
    private int _highWaterMark;
    private uint _tickSeed = 1u;

    // Flat MultiMesh buffer (12 floats per instance: 2x4 Transform2D + RGBA Color)
    private readonly float[] _multimeshBuffer = new float[PoolCapacity * FloatsPerInstance];
    private int _visibleInstanceCount;

    public CarSlot[] CarSlots => _cars;
    public int ActiveCarCount => _activeCount;
    public int FreeSlotCount => _freeCount;
    public int HighWaterMark => _highWaterMark;
    public float[] MultimeshBuffer => _multimeshBuffer;
    public int VisibleInstanceCount => _visibleInstanceCount;

    public List<VisualCar> Cars = new List<VisualCar>();
    private Random _random = new Random(42);
    private List<int> _busyEdgeIds = new List<int>();
    private ParcelManager _parcelManager;

    private static readonly Color[] Palette = new Color[]
    {
        new Color(0.95f, 0.95f, 0.95f), // White
        new Color(0.22f, 0.22f, 0.25f), // Charcoal
        new Color(0.70f, 0.70f, 0.75f), // Silver
        new Color(0.85f, 0.20f, 0.18f), // Red
        new Color(0.18f, 0.45f, 0.85f), // Blue
        new Color(0.88f, 0.75f, 0.18f), // Taxi Yellow
        new Color(0.35f, 0.60f, 0.35f)  // Green
    };

    public CarTrafficManager()
    {
        ResetPool();
    }

    /// <summary>
    /// Resets the fixed-size CarSlot pool and populates the O(1) FreeList without heap allocations.
    /// </summary>
    public void ResetPool()
    {
        Array.Clear(_cars, 0, PoolCapacity);
        for (int i = 0; i < PoolCapacity; i++)
        {
            _freeList[i] = PoolCapacity - 1 - i;
        }
        _freeCount = PoolCapacity;
        _activeCount = 0;
        _highWaterMark = 0;
        _visibleInstanceCount = 0;
    }

    /// <summary>
    /// Allocates a CarSlot in O(1) time from the FreeList with zero GC allocations.
    /// Returns the slot index in [0, PoolCapacity - 1], or -1 if the pool is at capacity.
    /// </summary>
    public int AllocateCarSlot(int edgeId, float progress = 0f, float speed = 50f, Color? color = null)
    {
        if (_freeCount <= 0) return -1;

        int slotIndex = _freeList[--_freeCount];
        _cars[slotIndex] = new CarSlot
        {
            EdgeId = edgeId,
            Progress = progress,
            Speed = speed,
            CarColor = color ?? Palette[slotIndex % Palette.Length],
            IsStopped = false,
            Active = true
        };
        _activeCount++;
        if (slotIndex + 1 > _highWaterMark)
        {
            _highWaterMark = slotIndex + 1;
        }
        return slotIndex;
    }

    /// <summary>
    /// Releases a CarSlot back to the FreeList in O(1) time.
    /// </summary>
    public void ReleaseCarSlot(int slotIndex)
    {
        if (slotIndex < 0 || slotIndex >= PoolCapacity || !_cars[slotIndex].Active) return;

        _cars[slotIndex].Active = false;
        _cars[slotIndex].EdgeId = -1;
        _freeList[_freeCount++] = slotIndex;
        _activeCount = Math.Max(0, _activeCount - 1);
    }

    /// <summary>
    /// Writes a 2D Transform + Color (12 floats) into a Godot MultiMesh buffer at the specified instance index.
    /// Layout: [x.x, y.x, 0, origin.x, x.y, y.y, 0, origin.y, r, g, b, a].
    /// </summary>
    public static void WriteInstanceTransform2DAndColor(
        float[] buffer,
        int instanceIndex,
        Vector2 position,
        Vector2 tangent,
        Color color,
        float scaleX = 1.0f,
        float scaleY = 1.0f)
    {
        int baseIdx = instanceIndex * FloatsPerInstance;
        if (baseIdx < 0 || baseIdx + FloatsPerInstance > buffer.Length) return;

        // Basis X = tangent * scaleX, Basis Y = normal * scaleY where normal = (-tangent.Y, tangent.X)
        buffer[baseIdx + 0] = tangent.X * scaleX;
        buffer[baseIdx + 1] = -tangent.Y * scaleY;
        buffer[baseIdx + 2] = 0f;
        buffer[baseIdx + 3] = position.X;
        buffer[baseIdx + 4] = tangent.Y * scaleX;
        buffer[baseIdx + 5] = tangent.X * scaleY;
        buffer[baseIdx + 6] = 0f;
        buffer[baseIdx + 7] = position.Y;
        buffer[baseIdx + 8] = color.R;
        buffer[baseIdx + 9] = color.G;
        buffer[baseIdx + 10] = color.B;
        buffer[baseIdx + 11] = color.A;
    }

    /// <summary>
    /// Computes the 2D world position for a vehicle along the specified road edge at parameter [0, 1].
    /// Applies right-hand driving lane offset perpendicular to the curve.
    /// </summary>
    public static Vector2 GetCarWorldPosition(RoadEdge edge, float progress, float laneOffset = DefaultLaneOffset, RoadGraph graph = null)
    {
        if (edge == null) return Vector2.Zero;
        float t = Mathf.Clamp(progress, 0.0f, 1.0f);

        if (edge.Curve != null)
        {
            Vector2 basePos = edge.Curve.Evaluate(t);
            Vector2 normal = edge.Curve.GetNormal(t);
            return basePos + normal * laneOffset;
        }

        if (graph != null)
        {
            var fromNode = graph.GetNode(edge.FromId);
            var toNode = graph.GetNode(edge.ToId);
            if (fromNode != null && toNode != null)
            {
                Vector2 basePos = fromNode.WorldPosition.Lerp(toNode.WorldPosition, t);
                Vector2 delta = toNode.WorldPosition - fromNode.WorldPosition;
                if (delta.LengthSquared() > 1e-4f)
                {
                    Vector2 dir = delta.Normalized();
                    Vector2 normal = new Vector2(-dir.Y, dir.X);
                    return basePos + normal * laneOffset;
                }
                return basePos;
            }
        }

        return Vector2.Zero;
    }

    /// <summary>
    /// Computes the heading rotation angle in radians for a vehicle along the specified road edge at parameter [0, 1].
    /// Accurately aligned with the curve tangent vector.
    /// </summary>
    public static float GetCarRotation(RoadEdge edge, float progress, RoadGraph graph = null)
    {
        if (edge == null) return 0f;
        float t = Mathf.Clamp(progress, 0.0f, 1.0f);

        if (edge.Curve != null)
        {
            return edge.Curve.GetTangent(t).Angle();
        }

        if (graph != null)
        {
            var fromNode = graph.GetNode(edge.FromId);
            var toNode = graph.GetNode(edge.ToId);
            if (fromNode != null && toNode != null)
            {
                Vector2 delta = toNode.WorldPosition - fromNode.WorldPosition;
                if (delta.LengthSquared() > 1e-4f)
                {
                    return delta.Angle();
                }
            }
        }

        return 0f;
    }

    /// <summary>
    /// Instantiates and registers a visual car backed by a Data-Oriented CarSlot in the pool.
    /// </summary>
    public VisualCar CreateCar(int edgeId, float progress = 0f, float speed = 50f, Color? color = null)
    {
        Color resolvedColor = color ?? Palette[_random.Next(Palette.Length)];
        int slotIdx = AllocateCarSlot(edgeId, progress, speed, resolvedColor);
        var car = new VisualCar
        {
            EdgeId = edgeId,
            Progress = progress,
            Speed = speed,
            CarColor = resolvedColor,
            IsStopped = false,
            SlotIndex = slotIdx
        };
        Cars.Add(car);
        return car;
    }

    public static bool HasDemand(RoadEdge edge)
    {
        return edge != null && edge.FromId != -1 && edge.ToId != -1 && edge.CurrentVolume > DemandThreshold;
    }

    public List<int> GetActiveDemandEdgeIds(RoadGraph graph)
    {
        var list = new List<int>();
        if (graph == null) return list;
        for (int i = 0; i < graph.Edges.Count; i++)
        {
            var edge = graph.Edges[i];
            if (HasDemand(edge))
            {
                list.Add(i);
            }
        }
        return list;
    }

    public int CalculateTargetCarCount(RoadGraph graph)
    {
        if (graph == null || graph.Edges.Count == 0) return 0;
        float totalVol = 0f;
        int activeEdgeCount = 0;
        for (int i = 0; i < graph.Edges.Count; i++)
        {
            var edge = graph.Edges[i];
            if (HasDemand(edge))
            {
                totalVol += edge.CurrentVolume;
                activeEdgeCount++;
            }
        }

        if (activeEdgeCount == 0 || totalVol <= DemandThreshold) return 0;

        // Scale dynamically with actual total traffic volume
        int countFromVolume = Mathf.RoundToInt(totalVol * 0.12f);
        int target = Mathf.Clamp(countFromVolume, 1, MaxCars);
        // Avoid overloading if very few active edges
        target = Mathf.Min(target, activeEdgeCount * 4);
        return Mathf.Clamp(target, 1, MaxCars);
    }

    public void Initialize(RoadGraph graph, ParcelManager parcelManager = null)
    {
        Cars.Clear();
        ResetPool();
        _busyEdgeIds.Clear();
        _parcelManager = parcelManager;

        if (graph == null || graph.Edges.Count == 0) return;
        RefreshBusyEdges(graph);
    }

    public void SetParcelManager(ParcelManager parcelManager)
    {
        _parcelManager = parcelManager;
    }

    public void RefreshBusyEdges(RoadGraph graph)
    {
        _busyEdgeIds.Clear();
        if (graph == null) return;
        for (int i = 0; i < graph.Edges.Count; i++)
        {
            var edge = graph.Edges[i];
            if (HasDemand(edge))
            {
                _busyEdgeIds.Add(i);
            }
        }
    }

    private void SyncVisualCarsIntoPool()
    {
        for (int i = 0; i < Cars.Count; i++)
        {
            var car = Cars[i];
            if (car.SlotIndex < 0 || car.SlotIndex >= PoolCapacity || !_cars[car.SlotIndex].Active)
            {
                car.SlotIndex = AllocateCarSlot(car.EdgeId, car.Progress, car.Speed, car.CarColor);
            }
            else
            {
                ref var slot = ref _cars[car.SlotIndex];
                slot.EdgeId = car.EdgeId;
                slot.Progress = car.Progress;
                slot.Speed = car.Speed;
                slot.CarColor = car.CarColor;
                slot.IsStopped = car.IsStopped;
            }
        }
    }

    private void SyncPoolBackToVisualCars()
    {
        for (int i = Cars.Count - 1; i >= 0; i--)
        {
            var car = Cars[i];
            if (car.SlotIndex < 0 || car.SlotIndex >= PoolCapacity || !_cars[car.SlotIndex].Active)
            {
                car.EdgeId = -1;
                Cars.RemoveAt(i);
            }
            else
            {
                ref var slot = ref _cars[car.SlotIndex];
                car.EdgeId = slot.EdgeId;
                car.Progress = slot.Progress;
                car.Speed = slot.Speed;
                car.CarColor = slot.CarColor;
                car.IsStopped = slot.IsStopped;
            }
        }
    }

    /// <summary>
    /// Respawns or cleans any cars referencing demolished, invalidated, or zero-demand edges.
    /// </summary>
    public void HandleInvalidatedEdges(RoadGraph graph)
    {
        RefreshBusyEdges(graph);
        SyncVisualCarsIntoPool();

        for (int i = 0; i < _highWaterMark; i++)
        {
            if (!_cars[i].Active) continue;
            ref var slot = ref _cars[i];

            if (graph == null || slot.EdgeId < 0 || slot.EdgeId >= graph.Edges.Count ||
                graph.Edges[slot.EdgeId].FromId == -1 || graph.Edges[slot.EdgeId].ToId == -1 ||
                !HasDemand(graph.Edges[slot.EdgeId]))
            {
                if (!TryRespawnSlot(ref slot, i, graph, _busyEdgeIds))
                {
                    slot.EdgeId = -1;
                }
            }
        }

        for (int i = 0; i < Cars.Count; i++)
        {
            var car = Cars[i];
            if (car.SlotIndex >= 0 && car.SlotIndex < PoolCapacity)
            {
                ref var slot = ref _cars[car.SlotIndex];
                car.EdgeId = slot.EdgeId;
                car.Progress = slot.Progress;
                car.Speed = slot.Speed;
                car.IsStopped = slot.IsStopped;
            }
        }
    }

    /// <summary>
    /// Updates all active CarSlots along Bézier curves B(t), evaluates intersection traffic lights,
    /// and writes 12-float Transform2D + Color entries directly into _multimeshBuffer in Parallel.For.
    /// </summary>
    public void Update(float delta, float gameSpeed, RoadGraph graph, TrafficLightManager trafficLights, ParcelManager parcelManager = null)
    {
        if (parcelManager != null) _parcelManager = parcelManager;

        if (graph == null || graph.Edges.Count == 0)
        {
            Cars.Clear();
            ResetPool();
            return;
        }

        RefreshBusyEdges(graph);
        SyncVisualCarsIntoPool();

        if (_activeCount == 0)
        {
            _visibleInstanceCount = 0;
            return;
        }

        _tickSeed++;
        float speedMult = Mathf.Clamp(gameSpeed, 0.1f, 10.0f);
        int limit = _highWaterMark;
        int renderedInstances = 0;

        void ProcessSlot(int i)
        {
            if (!_cars[i].Active) return;

            ref var slot = ref _cars[i];
            if (slot.EdgeId < 0 || slot.EdgeId >= graph.Edges.Count)
            {
                if (!TryRespawnSlot(ref slot, i, graph, _busyEdgeIds))
                {
                    slot.EdgeId = -1;
                    return;
                }
            }

            var edge = graph.Edges[slot.EdgeId];
            if (!HasDemand(edge))
            {
                if (!TryRespawnSlot(ref slot, i, graph, _busyEdgeIds))
                {
                    slot.EdgeId = -1;
                    return;
                }
                edge = graph.Edges[slot.EdgeId];
            }

            // Check traffic light when approaching intersection
            if (trafficLights != null && slot.Progress > 0.82f)
            {
                var fromNode = graph.GetNode(edge.FromId);
                Vector2 fromPos = fromNode != null ? fromNode.WorldPosition : Vector2.Zero;
                if (!trafficLights.IsGreen(edge.ToId, fromPos))
                {
                    slot.IsStopped = true;
                    WriteSlotToMultimesh(ref slot, edge, graph, ref renderedInstances);
                    return;
                }
            }

            slot.IsStopped = false;
            float edgeLen = edge.Length > 0.1f ? edge.Length : (edge.Curve != null ? edge.Curve.Length : 50f);
            float effectiveSpeed = slot.Speed > 0f ? slot.Speed : edge.FreeFlowSpeed;
            if (effectiveSpeed <= 0f) effectiveSpeed = 40f;

            float tMax = graph.GetEdgeTrafficExtent(edge, _parcelManager);
            slot.Progress += (effectiveSpeed * delta * speedMult) / Mathf.Max(edgeLen, 1f);

            if (slot.Progress >= tMax)
            {
                slot.Progress = tMax;
                TransitionSlotToNextEdge(ref slot, i, edge, graph);
            }

            if (slot.EdgeId >= 0 && slot.EdgeId < graph.Edges.Count)
            {
                var currentEdge = graph.Edges[slot.EdgeId];
                if (HasDemand(currentEdge))
                {
                    WriteSlotToMultimesh(ref slot, currentEdge, graph, ref renderedInstances);
                }
            }
        }

        // Execute curve B(t) and traffic light updates in Parallel.For with direct _multimeshBuffer writes
        Parallel.For(0, limit, ProcessSlot);

        // Reclaim slots that despawned (EdgeId == -1) back into FreeList
        for (int i = 0; i < limit; i++)
        {
            if (_cars[i].Active && _cars[i].EdgeId == -1)
            {
                ReleaseCarSlot(i);
            }
        }

        _visibleInstanceCount = renderedInstances;
        SyncPoolBackToVisualCars();
    }

    private void WriteSlotToMultimesh(ref CarSlot slot, RoadEdge edge, RoadGraph graph, ref int renderedInstances)
    {
        Vector2 pos = GetCarWorldPosition(edge, slot.Progress, DefaultLaneOffset, graph);
        float rot = GetCarRotation(edge, slot.Progress, graph);
        Vector2 tangent = new Vector2(MathF.Cos(rot), MathF.Sin(rot));

        // Encode brake light status in alpha (0.5f when stopped at red light, 1.0f when moving)
        Color instanceColor = new Color(
            slot.CarColor.R,
            slot.CarColor.G,
            slot.CarColor.B,
            slot.IsStopped ? 0.5f : 1.0f);

        int instanceIdx = Interlocked.Increment(ref renderedInstances) - 1;
        WriteInstanceTransform2DAndColor(_multimeshBuffer, instanceIdx, pos, tangent, instanceColor);
    }

    private void TransitionSlotToNextEdge(ref CarSlot slot, int slotIdx, RoadEdge currentEdge, RoadGraph graph)
    {
        if (graph.AdjacencyEdges.TryGetValue(currentEdge.ToId, out var nextEdgeIds) && nextEdgeIds.Count > 0)
        {
            int bestEdgeId = -1;
            float bestVol = DemandThreshold;

            for (int i = 0; i < nextEdgeIds.Count; i++)
            {
                int candidateId = nextEdgeIds[i];
                if (candidateId < 0 || candidateId >= graph.Edges.Count) continue;
                var candidate = graph.Edges[candidateId];
                if (!HasDemand(candidate)) continue;
                if (candidate.ToId == currentEdge.FromId) continue; // Avoid 180 u-turn

                if (candidate.CurrentVolume > bestVol)
                {
                    bestVol = candidate.CurrentVolume;
                    bestEdgeId = candidateId;
                }
            }

            if (bestEdgeId != -1)
            {
                slot.EdgeId = bestEdgeId;
                slot.Progress = 0.01f;
                return;
            }
        }

        if (!TryRespawnSlot(ref slot, slotIdx, graph, _busyEdgeIds))
        {
            slot.EdgeId = -1;
        }
    }

    private bool TryRespawnSlot(ref CarSlot slot, int slotIdx, RoadGraph graph, List<int> activeDemandEdgeIds)
    {
        if (activeDemandEdgeIds == null || activeDemandEdgeIds.Count == 0 || graph == null)
        {
            slot.EdgeId = -1;
            return false;
        }

        // Thread-safe deterministic hash so Parallel.For never contends on System.Random
        uint hash = (uint)(slotIdx + 1) * 2654435761u ^ (_tickSeed * 2246822519u);
        int candidateIdx = (int)(hash % (uint)activeDemandEdgeIds.Count);
        int candidate = activeDemandEdgeIds[candidateIdx];

        if (candidate >= 0 && candidate < graph.Edges.Count && HasDemand(graph.Edges[candidate]))
        {
            slot.EdgeId = candidate;
            float candidateTMax = graph.GetEdgeTrafficExtent(graph.Edges[candidate], _parcelManager);
            float rand01 = ((hash >> 8) & 0xFFFFu) / 65535f;
            float spawnProgress = rand01 * 0.3f;
            slot.Progress = Mathf.Min(spawnProgress, candidateTMax * 0.8f);
            slot.Speed = graph.Edges[candidate].FreeFlowSpeed;
            slot.IsStopped = false;
            return true;
        }

        slot.EdgeId = -1;
        return false;
    }
}
