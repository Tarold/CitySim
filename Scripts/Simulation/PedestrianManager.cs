using Godot;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CitySim.Simulation;

/// <summary>
/// Data-Oriented value-type slot representing a pedestrian in the fixed-size pool (zero GC overhead).
/// </summary>
public struct PedestrianSlot
{
    public int EdgeId;
    public float Progress;
    public float Speed;
    public Color ClothingColor;
    public int Side; // 1 for right sidewalk, -1 for left sidewalk
    public bool Active;

    public Vector2 GetWorldPosition(RoadGraph graph, float sidewalkOffset = PedestrianManager.DefaultSidewalkOffset)
    {
        if (!Active || graph == null || EdgeId < 0 || EdgeId >= graph.Edges.Count) return Vector2.Zero;
        return PedestrianManager.GetSidewalkPosition(graph.Edges[EdgeId], Progress, Side, sidewalkOffset, graph);
    }

    public float GetRotation(RoadGraph graph)
    {
        if (!Active || graph == null || EdgeId < 0 || EdgeId >= graph.Edges.Count) return 0f;
        return PedestrianManager.GetSidewalkRotation(graph.Edges[EdgeId], Progress, graph);
    }
}

public class VisualPedestrian
{
    public int EdgeId;
    public float Progress;
    public float Speed;
    public Color ClothingColor;
    public int Side; // 1 for right sidewalk, -1 for left sidewalk
    public int SlotIndex = -1;

    /// <summary>
    /// Computes the 2D world position of this pedestrian along the curved road sidewalk.
    /// </summary>
    public Vector2 GetWorldPosition(RoadGraph graph, float sidewalkOffset = 7.5f)
    {
        if (graph == null || EdgeId < 0 || EdgeId >= graph.Edges.Count) return Vector2.Zero;
        var edge = graph.Edges[EdgeId];
        return PedestrianManager.GetSidewalkPosition(edge, Progress, Side, sidewalkOffset, graph);
    }

    /// <summary>
    /// Computes the heading rotation angle in radians of this pedestrian facing along the curve tangent.
    /// </summary>
    public float GetRotation(RoadGraph graph)
    {
        if (graph == null || EdgeId < 0 || EdgeId >= graph.Edges.Count) return 0f;
        var edge = graph.Edges[EdgeId];
        return PedestrianManager.GetSidewalkRotation(edge, Progress, graph);
    }
}

public class PedestrianManager
{
    public const int PoolCapacity = 32768;
    public const int MaxPedestrians = PoolCapacity;
    public const int FloatsPerInstance = 12; // Godot 2D MultiMesh: 8 floats Transform2D + 4 floats Color
    public const float DefaultSidewalkOffset = 7.5f;

    // Pre-allocated Data-Oriented pool of value-type structs + O(1) FreeList
    private readonly PedestrianSlot[] _pedestrians = new PedestrianSlot[PoolCapacity];
    private readonly int[] _freeList = new int[PoolCapacity];
    private int _freeCount;
    private int _activeCount;
    private int _highWaterMark;
    private uint _tickSeed = 1u;

    // Flat MultiMesh buffer (12 floats per instance: 2x4 Transform2D + RGBA Color)
    private readonly float[] _multimeshBuffer = new float[PoolCapacity * FloatsPerInstance];
    private int _visibleInstanceCount;

    public PedestrianSlot[] PedestrianSlots => _pedestrians;
    public int ActivePedestrianCount => _activeCount;
    public int FreeSlotCount => _freeCount;
    public int HighWaterMark => _highWaterMark;
    public float[] MultimeshBuffer => _multimeshBuffer;
    public int VisibleInstanceCount => _visibleInstanceCount;

    public List<VisualPedestrian> Pedestrians = new List<VisualPedestrian>();
    private Random _random = new Random(1234);
    private List<int> _activeEdgeIds = new List<int>();

    private static readonly Color[] ClothingPalette = new Color[]
    {
        new Color(0.85f, 0.25f, 0.25f), // Red
        new Color(0.25f, 0.45f, 0.85f), // Blue
        new Color(0.25f, 0.75f, 0.35f), // Green
        new Color(0.95f, 0.95f, 0.95f), // White
        new Color(0.15f, 0.15f, 0.15f), // Black
        new Color(0.85f, 0.85f, 0.25f), // Yellow
        new Color(0.65f, 0.35f, 0.75f)  // Purple
    };

    public PedestrianManager()
    {
        ResetPool();
    }

    /// <summary>
    /// Resets the fixed-size PedestrianSlot pool and populates the O(1) FreeList without heap allocations.
    /// </summary>
    public void ResetPool()
    {
        Array.Clear(_pedestrians, 0, PoolCapacity);
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
    /// Allocates a PedestrianSlot in O(1) time from the FreeList with zero GC allocations.
    /// Returns the slot index in [0, PoolCapacity - 1], or -1 if the pool is at capacity.
    /// </summary>
    public int AllocatePedestrianSlot(int edgeId, float progress = 0f, float speed = 2.0f, int side = 1, Color? color = null)
    {
        if (_freeCount <= 0) return -1;

        int slotIndex = _freeList[--_freeCount];
        _pedestrians[slotIndex] = new PedestrianSlot
        {
            EdgeId = edgeId,
            Progress = progress,
            Speed = speed,
            Side = side == 0 ? 1 : side,
            ClothingColor = color ?? ClothingPalette[slotIndex % ClothingPalette.Length],
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
    /// Releases a PedestrianSlot back to the FreeList in O(1) time.
    /// </summary>
    public void ReleasePedestrianSlot(int slotIndex)
    {
        if (slotIndex < 0 || slotIndex >= PoolCapacity || !_pedestrians[slotIndex].Active) return;

        _pedestrians[slotIndex].Active = false;
        _pedestrians[slotIndex].EdgeId = -1;
        _freeList[_freeCount++] = slotIndex;
        _activeCount = Math.Max(0, _activeCount - 1);
    }

    /// <summary>
    /// Computes the 2D world position along a road edge sidewalk at progress parameter [0, 1].
    /// Offsets perpendicularly using the curve's normal vector.
    /// </summary>
    public static Vector2 GetSidewalkPosition(RoadEdge edge, float progress, int side = 1, float sidewalkOffset = DefaultSidewalkOffset, RoadGraph graph = null)
    {
        if (edge == null) return Vector2.Zero;
        float t = Mathf.Clamp(progress, 0.0f, 1.0f);

        if (edge.Curve != null)
        {
            Vector2 basePos = edge.Curve.Evaluate(t);
            Vector2 normal = edge.Curve.GetNormal(t);
            return basePos + normal * (sidewalkOffset * side);
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
                    return basePos + normal * (sidewalkOffset * side);
                }
                return basePos;
            }
        }

        return Vector2.Zero;
    }

    /// <summary>
    /// Computes the heading rotation angle in radians along the curved sidewalk tangent.
    /// </summary>
    public static float GetSidewalkRotation(RoadEdge edge, float progress, RoadGraph graph = null)
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
    /// Instantiates and registers a visual pedestrian backed by a Data-Oriented PedestrianSlot in the pool.
    /// </summary>
    public VisualPedestrian CreatePedestrian(int edgeId, float progress = 0f, float speed = 2.0f, int side = 1, Color? color = null)
    {
        Color resolvedColor = color ?? ClothingPalette[_random.Next(ClothingPalette.Length)];
        int slotIdx = AllocatePedestrianSlot(edgeId, progress, speed, side, resolvedColor);
        var ped = new VisualPedestrian
        {
            EdgeId = edgeId,
            Progress = progress,
            Speed = speed,
            Side = side,
            ClothingColor = resolvedColor,
            SlotIndex = slotIdx
        };
        Pedestrians.Add(ped);
        return ped;
    }

    /// <summary>
    /// Determines whether a road edge has actual pedestrian/urban activity.
    /// Activity is defined by non-zero traffic demand, connection to populated residential or workplace zones,
    /// or transit stops with waiting passengers. Roads in empty/inactive areas return false.
    /// </summary>
    public static bool IsEdgeActiveUrban(RoadEdge edge, RoadGraph graph, CityGrid grid = null, TransitManager transit = null)
    {
        if (edge == null || edge.FromId == -1 || edge.ToId == -1) return false;

        // 1. Edge has actual traffic volume or pedestrian volume
        if (edge.CurrentVolume > 0.05f || edge.PedestrianVolume > 0.05f) return true;

        // 2. Edge connects to populated residential or workplace zones
        if (grid != null && graph != null)
        {
            var fromNode = graph.GetNode(edge.FromId);
            var toNode = graph.GetNode(edge.ToId);

            if (fromNode != null && fromNode.ZoneId >= 0 && fromNode.ZoneId < grid.ZoneCount)
            {
                var z = grid.GetZone(fromNode.ZoneId);
                if (z != null)
                {
                    if (z.Type == ZoneType.Residential && z.Population > 0) return true;
                    if ((z.Type == ZoneType.Commercial || z.Type == ZoneType.Industrial) && z.Jobs > 0) return true;
                }
            }

            if (toNode != null && toNode.ZoneId >= 0 && toNode.ZoneId < grid.ZoneCount)
            {
                var z = grid.GetZone(toNode.ZoneId);
                if (z != null)
                {
                    if (z.Type == ZoneType.Residential && z.Population > 0) return true;
                    if ((z.Type == ZoneType.Commercial || z.Type == ZoneType.Industrial) && z.Jobs > 0) return true;
                }
            }
        }

        // 3. Transit stops with waiting passengers
        if (transit != null)
        {
            if (transit.Stops.TryGetValue(edge.FromId, out var s1) && s1.WaitingPassengers > 0.5f) return true;
            if (transit.Stops.TryGetValue(edge.ToId, out var s2) && s2.WaitingPassengers > 0.5f) return true;
        }

        return false;
    }

    public List<int> GetActiveUrbanEdgeIds(RoadGraph graph, CityGrid grid = null, TransitManager transit = null)
    {
        var list = new List<int>();
        if (graph == null) return list;
        for (int i = 0; i < graph.Edges.Count; i++)
        {
            if (IsEdgeActiveUrban(graph.Edges[i], graph, grid, transit))
            {
                list.Add(i);
            }
        }
        return list;
    }

    public int CalculateTargetPedestrianCount(RoadGraph graph, CityGrid grid = null, TransitManager transit = null)
    {
        if (graph == null || graph.Edges.Count == 0) return 0;
        var active = GetActiveUrbanEdgeIds(graph, grid, transit);
        if (active.Count == 0) return 0;

        // Scale dynamically with active urban activity
        int baseCount = active.Count * 2;
        int popBonus = grid != null ? Mathf.RoundToInt(grid.TotalPopulation() * 0.00015f) : 0;
        int target = Mathf.Clamp(Mathf.Max(baseCount, popBonus), 1, MaxPedestrians);
        target = Mathf.Min(target, active.Count * 6);
        return Mathf.Clamp(target, 1, MaxPedestrians);
    }

    public void Initialize(RoadGraph graph, CityGrid grid = null, TransitManager transit = null)
    {
        Pedestrians.Clear();
        ResetPool();
        _activeEdgeIds = GetActiveUrbanEdgeIds(graph, grid, transit);
    }

    private void SyncVisualPedestriansIntoPool()
    {
        for (int i = 0; i < Pedestrians.Count; i++)
        {
            var ped = Pedestrians[i];
            if (ped.SlotIndex < 0 || ped.SlotIndex >= PoolCapacity || !_pedestrians[ped.SlotIndex].Active)
            {
                ped.SlotIndex = AllocatePedestrianSlot(ped.EdgeId, ped.Progress, ped.Speed, ped.Side, ped.ClothingColor);
            }
            else
            {
                ref var slot = ref _pedestrians[ped.SlotIndex];
                slot.EdgeId = ped.EdgeId;
                slot.Progress = ped.Progress;
                slot.Speed = ped.Speed;
                slot.Side = ped.Side;
                slot.ClothingColor = ped.ClothingColor;
            }
        }
    }

    private void SyncPoolBackToVisualPedestrians()
    {
        for (int i = Pedestrians.Count - 1; i >= 0; i--)
        {
            var ped = Pedestrians[i];
            if (ped.SlotIndex < 0 || ped.SlotIndex >= PoolCapacity || !_pedestrians[ped.SlotIndex].Active)
            {
                ped.EdgeId = -1;
                Pedestrians.RemoveAt(i);
            }
            else
            {
                ref var slot = ref _pedestrians[ped.SlotIndex];
                ped.EdgeId = slot.EdgeId;
                ped.Progress = slot.Progress;
                ped.Speed = slot.Speed;
                ped.Side = slot.Side;
                ped.ClothingColor = slot.ClothingColor;
            }
        }
    }

    public void HandleInvalidatedEdges(RoadGraph graph, CityGrid grid = null, TransitManager transit = null)
    {
        _activeEdgeIds = GetActiveUrbanEdgeIds(graph, grid, transit);
        SyncVisualPedestriansIntoPool();

        for (int i = 0; i < _highWaterMark; i++)
        {
            if (!_pedestrians[i].Active) continue;
            ref var slot = ref _pedestrians[i];

            if (graph == null || slot.EdgeId < 0 || slot.EdgeId >= graph.Edges.Count ||
                !IsEdgeActiveUrban(graph.Edges[slot.EdgeId], graph, grid, transit))
            {
                if (!TryRespawnSlot(ref slot, i, graph, _activeEdgeIds))
                {
                    slot.EdgeId = -1;
                }
            }
        }

        for (int i = 0; i < _highWaterMark; i++)
        {
            if (_pedestrians[i].Active && _pedestrians[i].EdgeId == -1)
            {
                ReleasePedestrianSlot(i);
            }
        }

        SyncPoolBackToVisualPedestrians();
    }

    /// <summary>
    /// Updates all active PedestrianSlots along curved sidewalks B(t) and writes 12-float Transform2D + Color
    /// entries directly into _multimeshBuffer in Parallel.For.
    /// </summary>
    public void Update(float delta, float gameSpeed, RoadGraph graph, CityGrid grid = null, TransitManager transit = null)
    {
        if (graph == null || graph.Edges.Count == 0)
        {
            Pedestrians.Clear();
            ResetPool();
            return;
        }

        _activeEdgeIds = GetActiveUrbanEdgeIds(graph, grid, transit);
        SyncVisualPedestriansIntoPool();

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
            if (!_pedestrians[i].Active) return;

            ref var slot = ref _pedestrians[i];
            if (slot.EdgeId < 0 || slot.EdgeId >= graph.Edges.Count ||
                !IsEdgeActiveUrban(graph.Edges[slot.EdgeId], graph, grid, transit))
            {
                if (!TryRespawnSlot(ref slot, i, graph, _activeEdgeIds))
                {
                    slot.EdgeId = -1;
                    return;
                }
            }

            var edge = graph.Edges[slot.EdgeId];
            float edgeLen = edge.Length > 0.1f ? edge.Length : (edge.Curve != null ? edge.Curve.Length : 50f);
            float speed = slot.Speed > 0f ? slot.Speed : 2.0f;

            slot.Progress += (speed * delta * speedMult) / Mathf.Max(edgeLen, 1f);

            if (slot.Progress >= 1.0f)
            {
                TransitionSlotToNextEdge(ref slot, i, edge, graph, grid, transit);
            }

            if (slot.EdgeId >= 0 && slot.EdgeId < graph.Edges.Count)
            {
                var currentEdge = graph.Edges[slot.EdgeId];
                Vector2 pos = GetSidewalkPosition(currentEdge, slot.Progress, slot.Side, DefaultSidewalkOffset, graph);
                float rot = GetSidewalkRotation(currentEdge, slot.Progress, graph);
                Vector2 tangent = new Vector2(MathF.Cos(rot), MathF.Sin(rot));

                int instanceIdx = Interlocked.Increment(ref renderedInstances) - 1;
                CarTrafficManager.WriteInstanceTransform2DAndColor(
                    _multimeshBuffer,
                    instanceIdx,
                    pos,
                    tangent,
                    slot.ClothingColor);
            }
        }

        Parallel.For(0, limit, ProcessSlot);

        for (int i = 0; i < limit; i++)
        {
            if (_pedestrians[i].Active && _pedestrians[i].EdgeId == -1)
            {
                ReleasePedestrianSlot(i);
            }
        }

        _visibleInstanceCount = renderedInstances;
        SyncPoolBackToVisualPedestrians();
    }

    private void TransitionSlotToNextEdge(ref PedestrianSlot slot, int slotIdx, RoadEdge currentEdge, RoadGraph graph, CityGrid grid, TransitManager transit)
    {
        if (graph.AdjacencyEdges.TryGetValue(currentEdge.ToId, out var nextEdgeIds) && nextEdgeIds.Count > 0)
        {
            // Zero-allocation two-pass selection over valid outgoing edges
            int validCount = 0;
            for (int i = 0; i < nextEdgeIds.Count; i++)
            {
                int candidateId = nextEdgeIds[i];
                if (candidateId >= 0 && candidateId < graph.Edges.Count &&
                    IsEdgeActiveUrban(graph.Edges[candidateId], graph, grid, transit))
                {
                    validCount++;
                }
            }

            if (validCount > 0)
            {
                uint hash = (uint)(slotIdx + 1) * 2654435761u ^ (_tickSeed * 2246822519u);
                int targetPick = (int)(hash % (uint)validCount);
                int currentPick = 0;

                for (int i = 0; i < nextEdgeIds.Count; i++)
                {
                    int candidateId = nextEdgeIds[i];
                    if (candidateId >= 0 && candidateId < graph.Edges.Count &&
                        IsEdgeActiveUrban(graph.Edges[candidateId], graph, grid, transit))
                    {
                        if (currentPick == targetPick)
                        {
                            slot.EdgeId = candidateId;
                            slot.Progress = 0.0f;
                            if ((hash & 0x7u) > 5u)
                            {
                                slot.Side *= -1;
                            }
                            return;
                        }
                        currentPick++;
                    }
                }
            }
        }

        if (!TryRespawnSlot(ref slot, slotIdx, graph, _activeEdgeIds))
        {
            slot.EdgeId = -1;
        }
    }

    private bool TryRespawnSlot(ref PedestrianSlot slot, int slotIdx, RoadGraph graph, List<int> activeUrbanEdgeIds)
    {
        if (activeUrbanEdgeIds == null || activeUrbanEdgeIds.Count == 0 || graph == null)
        {
            slot.EdgeId = -1;
            return false;
        }

        uint hash = (uint)(slotIdx + 1) * 2654435761u ^ (_tickSeed * 3266489917u);
        int candidateIdx = (int)(hash % (uint)activeUrbanEdgeIds.Count);
        int candidate = activeUrbanEdgeIds[candidateIdx];

        if (candidate >= 0 && candidate < graph.Edges.Count)
        {
            float rand01 = ((hash >> 8) & 0xFFFFu) / 65535f;
            slot.EdgeId = candidate;
            slot.Progress = rand01;
            slot.Speed = 1.5f + rand01 * 1.5f;
            slot.ClothingColor = ClothingPalette[slotIdx % ClothingPalette.Length];
            slot.Side = (hash & 1u) == 0u ? 1 : -1;
            return true;
        }

        slot.EdgeId = -1;
        return false;
    }
}
