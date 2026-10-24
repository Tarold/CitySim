using Godot;
using System;
using System.Collections.Generic;
using CitySim.Simulation;

namespace CitySim.Rendering;

/// <summary>
/// Pure C# cached road ribbon geometry and dynamic vertex color buffer manager for road_heatmap.gdshader.
/// Rebuilds curve geometry only when RoadGraph topology changes, and updates only the vertex color array
/// when MSA recalculates edge congestion ratios.
/// </summary>
public sealed class RoadHeatmapMeshBuffer
{
    public const int CurveSampleCount = 16;
    public const float HeatmapRibbonHalfWidth = 4.2f;

    public struct EdgeVertexSpan
    {
        public int EdgeId;
        public int StartVertex;
        public int VertexCount;
    }

    private RoadGraph _graph;
    private Vector2[] _meshVertices = Array.Empty<Vector2>();
    private Vector2[] _meshUvs = Array.Empty<Vector2>();
    private Color[] _meshVertexColors = Array.Empty<Color>();
    private int[] _meshIndices = Array.Empty<int>();
    private readonly List<EdgeVertexSpan> _edgeVertexSpans = new List<EdgeVertexSpan>();
    private readonly Dictionary<int, Vector2[]> _cachedEdgePolylines = new Dictionary<int, Vector2[]>();

    private bool _geometryDirty = true;
    private bool _colorsDirty = true;
    private int _lastBuiltEdgeCount = -1;

    public Vector2[] MeshVertices => _meshVertices;
    public Vector2[] MeshUvs => _meshUvs;
    public Color[] MeshVertexColors => _meshVertexColors;
    public int[] MeshIndices => _meshIndices;
    public IReadOnlyList<EdgeVertexSpan> EdgeVertexSpans => _edgeVertexSpans;
    public IReadOnlyDictionary<int, Vector2[]> CachedEdgePolylines => _cachedEdgePolylines;

    public int GeometryRebuildCount { get; private set; }
    public int ColorBufferUpdateCount { get; private set; }

    public void Initialize(RoadGraph graph)
    {
        if (_graph != null)
        {
            _graph.NodeAdded -= OnTopologyNodeChanged;
            _graph.NodeRemoved -= OnTopologyIdChanged;
            _graph.EdgeAdded -= OnTopologyEdgeChanged;
            _graph.EdgeRemoved -= OnTopologyIdChanged;
            _graph.EdgeSplit -= OnTopologyEdgeSplit;
        }

        _graph = graph;
        _geometryDirty = true;
        _colorsDirty = true;

        if (_graph != null)
        {
            _graph.NodeAdded += OnTopologyNodeChanged;
            _graph.NodeRemoved += OnTopologyIdChanged;
            _graph.EdgeAdded += OnTopologyEdgeChanged;
            _graph.EdgeRemoved += OnTopologyIdChanged;
            _graph.EdgeSplit += OnTopologyEdgeSplit;
        }

        EnsureSynced();
    }

    private void OnTopologyNodeChanged(RoadNode _) => MarkTopologyDirty();
    private void OnTopologyEdgeChanged(RoadEdge _) => MarkTopologyDirty();
    private void OnTopologyIdChanged(int _) => MarkTopologyDirty();
    private void OnTopologyEdgeSplit(int _, RoadNode __) => MarkTopologyDirty();

    public void MarkTopologyDirty()
    {
        _geometryDirty = true;
        _colorsDirty = true;
    }

    public void MarkColorsDirty()
    {
        _colorsDirty = true;
    }

    /// <summary>
    /// Ensures cached road geometry and vertex colors are synchronized.
    /// Returns true if vertex colors or geometry were updated.
    /// </summary>
    public bool EnsureSynced()
    {
        if (_graph == null) return false;

        bool changed = false;
        if (_geometryDirty || _lastBuiltEdgeCount != _graph.Edges.Count)
        {
            RebuildGeometry();
            changed = true;
        }

        if (_colorsDirty)
        {
            UpdateVertexColors();
            changed = true;
        }

        return changed;
    }

    public void RebuildGeometry()
    {
        _edgeVertexSpans.Clear();
        _cachedEdgePolylines.Clear();

        if (_graph == null || _graph.Edges.Count == 0)
        {
            _meshVertices = Array.Empty<Vector2>();
            _meshUvs = Array.Empty<Vector2>();
            _meshVertexColors = Array.Empty<Color>();
            _meshIndices = Array.Empty<int>();
            _geometryDirty = false;
            _lastBuiltEdgeCount = 0;
            GeometryRebuildCount++;
            return;
        }

        var vertices = new List<Vector2>(_graph.Edges.Count * 16);
        var uvs = new List<Vector2>(_graph.Edges.Count * 16);
        var indices = new List<int>(_graph.Edges.Count * 42);

        for (int eIdx = 0; eIdx < _graph.Edges.Count; eIdx++)
        {
            var edge = _graph.Edges[eIdx];
            if (edge == null || edge.FromId == -1 || edge.ToId == -1) continue;

            var fromNode = _graph.GetNode(edge.FromId);
            var toNode = _graph.GetNode(edge.ToId);
            if (fromNode == null || toNode == null) continue;

            Vector2[] points = edge.Curve != null
                ? edge.Curve.GetSampledPoints(CurveSampleCount)
                : new Vector2[] { fromNode.WorldPosition, toNode.WorldPosition };

            _cachedEdgePolylines[eIdx] = points;
            if (points.Length < 2) continue;

            int startVert = vertices.Count;
            int pointCount = points.Length;

            for (int pIdx = 0; pIdx < pointCount; pIdx++)
            {
                float t = pointCount > 1 ? (float)pIdx / (pointCount - 1) : 0f;
                Vector2 normal;
                if (edge.Curve != null)
                {
                    normal = edge.Curve.GetNormal(t);
                }
                else
                {
                    Vector2 delta = toNode.WorldPosition - fromNode.WorldPosition;
                    Vector2 dir = delta.LengthSquared() > 1e-4f ? delta.Normalized() : Vector2.Right;
                    normal = new Vector2(-dir.Y, dir.X);
                }

                Vector2 center = points[pIdx] + normal * 1.6f;
                vertices.Add(center - normal * HeatmapRibbonHalfWidth);
                vertices.Add(center + normal * HeatmapRibbonHalfWidth);

                uvs.Add(new Vector2(t, 0f));
                uvs.Add(new Vector2(t, 1f));

                if (pIdx < pointCount - 1)
                {
                    int v0 = startVert + pIdx * 2;
                    int v1 = v0 + 1;
                    int v2 = v0 + 2;
                    int v3 = v0 + 3;

                    indices.Add(v0);
                    indices.Add(v2);
                    indices.Add(v1);

                    indices.Add(v1);
                    indices.Add(v2);
                    indices.Add(v3);
                }
            }

            _edgeVertexSpans.Add(new EdgeVertexSpan
            {
                EdgeId = eIdx,
                StartVertex = startVert,
                VertexCount = pointCount * 2
            });
        }

        _meshVertices = vertices.ToArray();
        _meshUvs = uvs.ToArray();
        _meshIndices = indices.ToArray();
        if (_meshVertexColors.Length != _meshVertices.Length)
        {
            _meshVertexColors = new Color[_meshVertices.Length];
        }

        _geometryDirty = false;
        _colorsDirty = true;
        _lastBuiltEdgeCount = _graph.Edges.Count;
        GeometryRebuildCount++;
    }

    public void UpdateVertexColors()
    {
        if (_graph == null) return;

        for (int i = 0; i < _edgeVertexSpans.Count; i++)
        {
            var span = _edgeVertexSpans[i];
            if (span.EdgeId < 0 || span.EdgeId >= _graph.Edges.Count) continue;

            var edge = _graph.Edges[span.EdgeId];
            float ratio = (edge != null && edge.FromId != -1 && edge.ToId != -1)
                ? edge.GetCongestionRatio()
                : 0f;

            // Encode R_congestion in vertex COLOR.r (normalized as ratio * 0.5 for road_heatmap.gdshader)
            float encodedRatio = Mathf.Clamp(ratio * 0.5f, 0f, 1f);
            Color heatCol = RoadRenderer.GetHeatmapColor(ratio);
            Color vertexPayload = new Color(encodedRatio, heatCol.G, heatCol.B, 0.95f);

            int endVert = Math.Min(span.StartVertex + span.VertexCount, _meshVertexColors.Length);
            for (int v = span.StartVertex; v < endVert; v++)
            {
                _meshVertexColors[v] = vertexPayload;
            }
        }

        _colorsDirty = false;
        ColorBufferUpdateCount++;
    }
}

/// <summary>
/// Renders the road network, traffic lights, and GPU shader-driven congestion heatmap.
/// Caches curved road ribbon geometry and updates only the vertex color buffer when MSA recalculates
/// instead of rebuilding Bézier curves every frame.
/// </summary>
public partial class RoadRenderer : Node2D
{
    public const int CurveSampleCount = RoadHeatmapMeshBuffer.CurveSampleCount;

    private RoadGraph _graph;
    private TrafficLightManager _trafficLights;
    private bool _heatmapEnabled = false;
    private readonly RoadHeatmapMeshBuffer _heatmapBuffer = new RoadHeatmapMeshBuffer();

    /// <summary>
    /// Gets or sets whether heatmap mode is active.
    /// </summary>
    public bool HeatmapEnabled
    {
        get => _heatmapEnabled;
        set
        {
            if (_heatmapEnabled != value)
            {
                _heatmapEnabled = value;
                if (_heatmapMeshInstance != null)
                {
                    _heatmapMeshInstance.Visible = _heatmapEnabled;
                }
                QueueRedraw();
            }
        }
    }

    /// <summary>
    /// Gets or sets whether commute infographics mode is active.
    /// </summary>
    public bool CommuteInfographicsEnabled { get; set; } = false;

    private float _maxVolume = 1f;

    private MeshInstance2D _heatmapMeshInstance;
    private ArrayMesh _heatmapArrayMesh;
    private ShaderMaterial _heatmapShaderMaterial;

    public RoadHeatmapMeshBuffer HeatmapBuffer => _heatmapBuffer;
    public Vector2[] MeshVertices => _heatmapBuffer.MeshVertices;
    public Color[] MeshVertexColors => _heatmapBuffer.MeshVertexColors;
    public int[] MeshIndices => _heatmapBuffer.MeshIndices;
    public int GeometryRebuildCount => _heatmapBuffer.GeometryRebuildCount;
    public int ColorBufferUpdateCount => _heatmapBuffer.ColorBufferUpdateCount;

    public override void _Ready()
    {
        SetupHeatmapMeshInstance();
        if (_heatmapBuffer.EnsureSynced())
        {
            UploadHeatmapMeshToGpu();
        }
    }

    public void Initialize(RoadGraph graph, TrafficLightManager trafficLights)
    {
        _graph = graph;
        _trafficLights = trafficLights;
        _heatmapBuffer.Initialize(graph);

        if (IsInsideTree())
        {
            SetupHeatmapMeshInstance();
            UploadHeatmapMeshToGpu();
        }
    }

    private void SetupHeatmapMeshInstance()
    {
        if (_heatmapMeshInstance != null) return;

        _heatmapArrayMesh = new ArrayMesh();
        if (ResourceLoader.Exists("res://Shaders/road_heatmap.gdshader"))
        {
            var shader = GD.Load<Shader>("res://Shaders/road_heatmap.gdshader");
            if (shader != null)
            {
                _heatmapShaderMaterial = new ShaderMaterial { Shader = shader };
            }
        }

        _heatmapMeshInstance = new MeshInstance2D
        {
            Name = "RoadHeatmapMesh",
            Mesh = _heatmapArrayMesh,
            Material = _heatmapShaderMaterial,
            Visible = _heatmapEnabled
        };
        AddChild(_heatmapMeshInstance);
    }

    public void UpdateMaxVolume(float maxVol)
    {
        _maxVolume = Mathf.Max(maxVol, 1f);
        _heatmapBuffer.MarkColorsDirty();
        if (_heatmapBuffer.EnsureSynced())
        {
            UploadHeatmapMeshToGpu();
        }
        if (IsInsideTree())
        {
            QueueRedraw();
        }
    }

    public void Refresh()
    {
        _heatmapBuffer.MarkColorsDirty();
        if (_heatmapBuffer.EnsureSynced())
        {
            UploadHeatmapMeshToGpu();
        }
        if (IsInsideTree())
        {
            QueueRedraw();
        }
    }

    private void UploadHeatmapMeshToGpu()
    {
        if (!IsInsideTree() || _heatmapArrayMesh == null) return;

        _heatmapArrayMesh.ClearSurfaces();
        if (_heatmapBuffer.MeshVertices.Length == 0 || _heatmapBuffer.MeshIndices.Length == 0) return;

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = _heatmapBuffer.MeshVertices;
        arrays[(int)Mesh.ArrayType.TexUV] = _heatmapBuffer.MeshUvs;
        arrays[(int)Mesh.ArrayType.Color] = _heatmapBuffer.MeshVertexColors;
        arrays[(int)Mesh.ArrayType.Index] = _heatmapBuffer.MeshIndices;

        _heatmapArrayMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        if (_heatmapMeshInstance != null)
        {
            _heatmapMeshInstance.Visible = _heatmapEnabled;
        }
    }

    public override void _Process(double delta)
    {
        if (_heatmapBuffer.EnsureSynced())
        {
            UploadHeatmapMeshToGpu();
        }
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_graph == null) return;

        if (_heatmapBuffer.EnsureSynced())
        {
            UploadHeatmapMeshToGpu();
        }

        var drawnSegments = new HashSet<(int, int)>();
        var polylines = _heatmapBuffer.CachedEdgePolylines;

        for (int eIdx = 0; eIdx < _graph.Edges.Count; eIdx++)
        {
            var edge = _graph.Edges[eIdx];
            if (edge == null || edge.FromId == -1 || edge.ToId == -1) continue;

            var fromNode = _graph.GetNode(edge.FromId);
            var toNode = _graph.GetNode(edge.ToId);
            if (fromNode == null || toNode == null) continue;

            if (!polylines.TryGetValue(eIdx, out Vector2[] points) || points == null)
            {
                points = edge.Curve != null
                    ? edge.Curve.GetSampledPoints(CurveSampleCount)
                    : new Vector2[] { fromNode.WorldPosition, toNode.WorldPosition };
            }

            if (!HeatmapEnabled)
            {
                int minId = Mathf.Min(edge.FromId, edge.ToId);
                int maxId = Mathf.Max(edge.FromId, edge.ToId);
                if (drawnSegments.Contains((minId, maxId))) continue;
                drawnSegments.Add((minId, maxId));

                DrawPolyline(points, new Color(0.12f, 0.12f, 0.15f, 0.95f), 6.5f, true);
                DrawPolyline(points, new Color(0.22f, 0.22f, 0.26f, 0.95f), 5.0f, true);

                if (edge.HasBusLane)
                {
                    Color busLaneColor = new Color(0.95f, 0.42f, 0.15f, 0.92f);
                    Vector2[] leftStripe = new Vector2[points.Length];
                    Vector2[] rightStripe = new Vector2[points.Length];

                    for (int pIdx = 0; pIdx < points.Length; pIdx++)
                    {
                        float t = points.Length > 1 ? (float)pIdx / (points.Length - 1) : 0f;
                        Vector2 normal;
                        if (edge.Curve != null)
                        {
                            normal = edge.Curve.GetNormal(t);
                        }
                        else
                        {
                            Vector2 dir = (toNode.WorldPosition - fromNode.WorldPosition).Normalized();
                            normal = new Vector2(-dir.Y, dir.X);
                        }

                        leftStripe[pIdx] = points[pIdx] - normal * 1.85f;
                        rightStripe[pIdx] = points[pIdx] + normal * 1.85f;
                    }

                    DrawPolyline(leftStripe, busLaneColor, 1.35f, true);
                    DrawPolyline(rightStripe, busLaneColor, 1.35f, true);
                }

                float curveLen = edge.Curve != null ? edge.Curve.Length : fromNode.WorldPosition.DistanceTo(toNode.WorldPosition);
                if (curveLen > 12f)
                {
                    float cycle = 11f;
                    int dashCount = Mathf.FloorToInt(curveLen / cycle);
                    Color dashColor = new Color(0.95f, 0.88f, 0.35f, 0.80f);

                    for (int d = 0; d < dashCount; d++)
                    {
                        float s1 = d * cycle + 1.0f;
                        float s2 = s1 + 5.5f;
                        if (s2 >= curveLen) break;

                        float t1 = s1 / curveLen;
                        float t2 = s2 / curveLen;

                        Vector2 p1 = edge.Curve != null ? edge.Curve.Evaluate(t1) : fromNode.WorldPosition.Lerp(toNode.WorldPosition, t1);
                        Vector2 p2 = edge.Curve != null ? edge.Curve.Evaluate(t2) : fromNode.WorldPosition.Lerp(toNode.WorldPosition, t2);

                        DrawLine(p1, p2, dashColor, 1.0f, true);
                    }
                }
            }
        }

        if (!HeatmapEnabled)
        {
            foreach (var node in _graph.Nodes)
            {
                int degree = _graph.GetNodeDegree(node.Id);
                if (degree >= 3)
                {
                    DrawCircle(node.WorldPosition, 4.5f, new Color(0.12f, 0.12f, 0.15f, 0.95f));
                    DrawCircle(node.WorldPosition, 3.6f, new Color(0.22f, 0.22f, 0.26f, 0.95f));
                }
                else if (degree == 2)
                {
                    DrawCircle(node.WorldPosition, 3.8f, new Color(0.12f, 0.12f, 0.15f, 0.95f));
                    DrawCircle(node.WorldPosition, 3.0f, new Color(0.22f, 0.22f, 0.26f, 0.95f));
                }
                else if (degree == 1)
                {
                    DrawCircle(node.WorldPosition, 3.5f, new Color(0.12f, 0.12f, 0.15f, 0.95f));
                    DrawCircle(node.WorldPosition, 2.8f, new Color(0.22f, 0.22f, 0.26f, 0.95f));
                }
            }
        }

        if (_trafficLights != null)
        {
            foreach (var kvp in _trafficLights.Intersections)
            {
                var light = kvp.Value;
                Vector2 pos = light.Position;

                DrawCircle(pos, 3.5f, new Color(0.2f, 0.2f, 0.25f, 0.8f));

                if (light.ApproachSignals != null && light.ApproachSignals.Count > 0)
                {
                    foreach (var app in light.ApproachSignals)
                    {
                        bool isGreen = app.Phase == 0
                            ? (light.CurrentState == SignalState.NorthSouthGreen)
                            : (light.CurrentState == SignalState.EastWestGreen);

                        Color signalColor = isGreen
                            ? new Color(0.1f, 0.95f, 0.2f)
                            : new Color(0.95f, 0.15f, 0.15f);

                        DrawCircle(app.Position, 2.2f, signalColor);
                    }
                }
                else
                {
                    Color nsColor = light.CurrentState == SignalState.NorthSouthGreen
                        ? new Color(0.1f, 0.95f, 0.2f)
                        : new Color(0.95f, 0.15f, 0.15f);

                    Color ewColor = light.CurrentState == SignalState.EastWestGreen
                        ? new Color(0.1f, 0.95f, 0.2f)
                        : new Color(0.95f, 0.15f, 0.15f);

                    DrawCircle(pos + new Vector2(0, -9), 2.2f, nsColor);
                    DrawCircle(pos + new Vector2(0, 9), 2.2f, nsColor);
                    DrawCircle(pos + new Vector2(-9, 0), 2.2f, ewColor);
                    DrawCircle(pos + new Vector2(9, 0), 2.2f, ewColor);
                }
            }
        }

        if (HeatmapEnabled)
        {
            foreach (var node in _graph.Nodes)
            {
                if (_graph.GetNodeDegree(node.Id) == 0) continue;
                float maxRatio = 0f;
                if (_graph.AdjacencyEdges.TryGetValue(node.Id, out var edgeIds))
                {
                    foreach (var eid in edgeIds)
                    {
                        if (eid < 0 || eid >= _graph.Edges.Count) continue;
                        var e = _graph.Edges[eid];
                        if (e.FromId == -1 || e.ToId == -1) continue;
                        float r = e.GetCongestionRatio();
                        if (r > maxRatio) maxRatio = r;
                    }
                }

                if (maxRatio > 0.85f)
                {
                    Color hotColor = GetHeatmapColor(maxRatio);
                    DrawCircle(node.WorldPosition, 5f + maxRatio * 2f, hotColor);
                }
            }
        }

        if (HeatmapEnabled || CommuteInfographicsEnabled)
        {
            var processedSegments = new HashSet<(int, int)>();

            foreach (var edge in _graph.Edges)
            {
                if (edge.FromId == -1 || edge.ToId == -1) continue;

                int u = Mathf.Min(edge.FromId, edge.ToId);
                int v = Mathf.Max(edge.FromId, edge.ToId);
                if (processedSegments.Contains((u, v))) continue;
                processedSegments.Add((u, v));

                var fromNode = _graph.GetNode(edge.FromId);
                var toNode = _graph.GetNode(edge.ToId);
                if (fromNode == null || toNode == null) continue;

                int revEdgeId = _graph.FindEdgeId(edge.ToId, edge.FromId);
                RoadEdge revEdge = revEdgeId >= 0 && revEdgeId < _graph.Edges.Count ? _graph.Edges[revEdgeId] : null;

                float forwardRatio = edge.GetCongestionRatio();
                float reverseRatio = revEdge?.GetCongestionRatio() ?? 0f;
                float peakRatio = Mathf.Max(forwardRatio, reverseRatio);
                float totalVol = edge.CurrentVolume + (revEdge?.CurrentVolume ?? 0f);

                if (totalVol < 0.5f && peakRatio < 0.01f) continue;

                Vector2 labelPos = edge.Curve != null
                    ? edge.Curve.Evaluate(0.5f)
                    : (fromNode.WorldPosition + toNode.WorldPosition) * 0.5f;

                string text = $"{peakRatio * 100f:F0}%";
                int fontSize = 9;
                float badgeWidth = text.Length >= 4 ? 30f : 24f;
                float badgeHeight = 13f;

                Rect2 bgRect = new Rect2(labelPos.X - badgeWidth * 0.5f, labelPos.Y - badgeHeight * 0.5f, badgeWidth, badgeHeight);

                Color heatColor = GetHeatmapColor(peakRatio);
                DrawRect(bgRect, new Color(0.06f, 0.06f, 0.09f, 0.90f), true);
                DrawRect(bgRect, heatColor, false, 1.0f);

                DrawString(
                    ThemeDB.FallbackFont,
                    new Vector2(bgRect.Position.X, labelPos.Y + 3.5f),
                    text,
                    HorizontalAlignment.Center,
                    badgeWidth,
                    fontSize,
                    Colors.White
                );
            }
        }
    }

    public static Color GetHeatmapColor(float ratio)
    {
        Color freeFlow = new Color(0.15f, 0.85f, 0.25f);
        Color moderate = new Color(0.95f, 0.85f, 0.15f);
        Color heavy    = new Color(0.95f, 0.50f, 0.10f);
        Color red      = new Color(0.95f, 0.15f, 0.15f);
        Color gridlock = new Color(0.65f, 0.05f, 0.40f);

        if (ratio <= 0.35f) return freeFlow;
        if (ratio <= 0.65f) return freeFlow.Lerp(moderate, (ratio - 0.35f) / 0.30f);
        if (ratio <= 0.90f) return moderate.Lerp(heavy, (ratio - 0.65f) / 0.25f);
        if (ratio <= 1.15f) return heavy.Lerp(red, (ratio - 0.90f) / 0.25f);
        return red.Lerp(gridlock, Mathf.Clamp((ratio - 1.15f) / 0.50f, 0f, 1f));
    }
}
