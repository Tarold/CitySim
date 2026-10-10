using Godot;
using System.Collections.Generic;
using CitySim.Simulation;
using CitySim.UI;

namespace CitySim.Rendering;

/// <summary>
/// Renders interactive visual feedback and previews for road construction and demolition tooling.
/// </summary>
public partial class ToolPreviewRenderer : Node2D
{
    private CityGrid _grid;
    private RoadGraph _roadGraph;
    private float _pulseTimer = 0f;

    // Roadside parcel zoning preview state
    private ParcelManager _parcelManager;
    private List<RoadsideParcel> _hoveredParcels = new List<RoadsideParcel>();
    private ZoneType _hoveredParcelZoneType = ZoneType.Empty;
    private ParcelSide _hoveredParcelSide = ParcelSide.Right;

    // Transit route designer draft state
    private List<int> _draftTransitStops = new List<int>();
    private List<int> _draftTransitPath = new List<int>();
    private Color _draftRouteColor = new Color(0.65f, 0.25f, 0.95f);
    private bool _draftIsLoop = false;
    private RoadSnapResult _transitHoverSnap;

    /// <summary>Active tool mode for preview rendering.</summary>
    public InteractionMode Mode { get; private set; } = InteractionMode.Inspect;

    /// <summary>Whether a continuous vector road preview is currently active.</summary>
    public bool HasRoadBuildPreview { get; private set; } = false;

    /// <summary>The construction step: 0=hovering start, 1=hovering end/straight, 2=bending curvature.</summary>
    public int RoadBuildStep { get; private set; } = 0;

    /// <summary>Locked start position of the road segment being built.</summary>
    public Vector2 RoadStartPos { get; private set; }

    /// <summary>Candidate end position of the road segment.</summary>
    public Vector2 RoadEndPos { get; private set; }

    /// <summary>Curvature bend apex position (for 3-point curved roads).</summary>
    public Vector2 RoadApexPos { get; private set; }

    /// <summary>The active candidate Bézier curve preview.</summary>
    public CurveSegment RoadPreviewCurve { get; private set; }

    /// <summary>The active snapping query result.</summary>
    public RoadSnapResult RoadSnap { get; private set; }

    /// <summary>Estimated construction cost in dollars.</summary>
    public float RoadCost { get; private set; }

    /// <summary>Whether the player currently has enough funds to build this road.</summary>
    public bool CanAffordRoad { get; private set; } = true;

    /// <summary>The starting zone ID of the pending operation, or -1 if none.</summary>
    public int StartZoneId { get; private set; } = -1;

    /// <summary>The zone ID currently hovered by the mouse cursor, or -1.</summary>
    public int HoverZoneId { get; private set; } = -1;

    /// <summary>List of zone IDs that are valid adjacent targets for the pending operation.</summary>
    public List<int> ValidTargetZoneIds { get; private set; } = new List<int>();

    /// <summary>
    /// Initializes the preview renderer with the city grid and road graph.
    /// </summary>
    public void Initialize(CityGrid grid, RoadGraph roadGraph = null)
    {
        _grid = grid;
        _roadGraph = roadGraph;
    }

    /// <summary>
    /// Updates the road graph reference used for route path previews.
    /// </summary>
    public void SetRoadGraph(RoadGraph roadGraph)
    {
        _roadGraph = roadGraph;
    }

    /// <summary>
    /// Sets the parcel manager reference.
    /// </summary>
    public void SetParcelManager(ParcelManager parcelManager)
    {
        _parcelManager = parcelManager;
    }

    /// <summary>
    /// Sets candidate roadside ribbon parcels for zoning hover preview.
    /// </summary>
    public void SetHoveredParcels(List<RoadsideParcel> parcels, ZoneType zoneType, ParcelSide side)
    {
        _hoveredParcels = parcels != null ? new List<RoadsideParcel>(parcels) : new List<RoadsideParcel>();
        _hoveredParcelZoneType = zoneType;
        _hoveredParcelSide = side;
        QueueRedraw();
    }

    /// <summary>
    /// Clears any hovered roadside ribbon parcels.
    /// </summary>
    public void ClearHoveredParcels()
    {
        if (_hoveredParcels.Count > 0)
        {
            _hoveredParcels.Clear();
            QueueRedraw();
        }
    }

    /// <summary>
    /// Updates the draft transit route stops and path for live rendering.
    /// </summary>
    public void SetTransitDraft(List<int> stops, List<int> path, Color color, bool isLoop)
    {
        _draftTransitStops = stops != null ? new List<int>(stops) : new List<int>();
        _draftTransitPath = path != null ? new List<int>(path) : new List<int>();
        _draftRouteColor = color;
        _draftIsLoop = isLoop;
        QueueRedraw();
    }

    /// <summary>
    /// Clears the active draft transit route.
    /// </summary>
    public void ClearTransitDraft()
    {
        _draftTransitStops.Clear();
        _draftTransitPath.Clear();
        _draftIsLoop = false;
        _transitHoverSnap = default;
        QueueRedraw();
    }

    /// <summary>
    /// Updates the live cursor snap result for transit route stop placement preview.
    /// </summary>
    public void SetTransitHoverSnap(RoadSnapResult snap)
    {
        _transitHoverSnap = snap;
        QueueRedraw();
    }

    /// <summary>
    /// Configures the live vector road construction preview.
    /// </summary>
    public void SetRoadBuildPreview(
        int step,
        Vector2 startPos,
        Vector2 endPos,
        Vector2 apexPos,
        CurveSegment curve,
        RoadSnapResult snap,
        float cost,
        bool canAfford)
    {
        HasRoadBuildPreview = true;
        RoadBuildStep = step;
        RoadStartPos = startPos;
        RoadEndPos = endPos;
        RoadApexPos = apexPos;
        RoadPreviewCurve = curve;
        RoadSnap = snap;
        RoadCost = cost;
        CanAffordRoad = canAfford;
        QueueRedraw();
    }

    /// <summary>
    /// Clears any active vector road construction preview.
    /// </summary>
    public void ClearRoadBuildPreview()
    {
        HasRoadBuildPreview = false;
        RoadBuildStep = 0;
        RoadPreviewCurve = null;
        QueueRedraw();
    }

    /// <summary>
    /// Sets the active interaction mode and clears any pending road targets.
    /// </summary>
    public void SetMode(InteractionMode mode)
    {
        Mode = mode;
        StartZoneId = -1;
        ValidTargetZoneIds.Clear();
        ClearTransitDraft();
        ClearRoadBuildPreview();
        ClearHoveredParcels();
        QueueRedraw();
    }

    /// <summary>
    /// Checks whether the specified mode is a zoning or dezoning interaction mode.
    /// </summary>
    public static bool IsZoningMode(InteractionMode mode) =>
        mode == InteractionMode.ZoneResidential ||
        mode == InteractionMode.ZoneCommercial ||
        mode == InteractionMode.ZoneIndustrial ||
        mode == InteractionMode.Dezone;

    /// <summary>
    /// Configures the active preview for a tool operation.
    /// </summary>
    public void SetPreview(InteractionMode mode, int startZoneId, List<int> validTargets)
    {
        Mode = mode;
        StartZoneId = startZoneId;
        ValidTargetZoneIds = validTargets ?? new List<int>();
        HoverZoneId = -1;
        QueueRedraw();
    }

    /// <summary>
    /// Updates the hovered zone ID for live preview drawing.
    /// </summary>
    public void SetHoverZone(int hoverZoneId)
    {
        if (HoverZoneId != hoverZoneId)
        {
            HoverZoneId = hoverZoneId;
            QueueRedraw();
        }
    }

    /// <summary>
    /// Clears any active tool preview.
    /// </summary>
    public void ClearPreview()
    {
        Mode = InteractionMode.Inspect;
        StartZoneId = -1;
        HoverZoneId = -1;
        ValidTargetZoneIds.Clear();
        ClearTransitDraft();
        ClearRoadBuildPreview();
        ClearHoveredParcels();
        QueueRedraw();
    }

    public override void _Process(double delta)
    {
        if (StartZoneId != -1 || (IsZoningMode(Mode) && (HoverZoneId != -1 || _hoveredParcels.Count > 0)) || Mode == InteractionMode.CreateTransitRoute || HasRoadBuildPreview)
        {
            _pulseTimer += (float)delta * 4f;
            QueueRedraw();
        }
    }

    public override void _Draw()
    {
        if (_grid == null || Mode == InteractionMode.Inspect)
            return;

        float cellSize = _grid.CellSize;
        float pulseWidth = 3.2f + Mathf.Sin(_pulseTimer) * 1.0f;

        // 1. Zoning Modes Live Hover Preview (Residential, Commercial, Industrial, Dezone)
        if (IsZoningMode(Mode))
        {
            Color fillColor;
            Color borderColor;

            switch (Mode)
            {
                case InteractionMode.ZoneResidential:
                    fillColor = new Color(0.18f, 0.75f, 0.32f, 0.40f);
                    borderColor = new Color(0.25f, 1.0f, 0.45f, 0.95f);
                    break;
                case InteractionMode.ZoneCommercial:
                    fillColor = new Color(0.20f, 0.55f, 0.98f, 0.40f);
                    borderColor = new Color(0.35f, 0.75f, 1.0f, 0.95f);
                    break;
                case InteractionMode.ZoneIndustrial:
                    fillColor = new Color(0.95f, 0.60f, 0.15f, 0.40f);
                    borderColor = new Color(1.0f, 0.75f, 0.25f, 0.95f);
                    break;
                case InteractionMode.Dezone:
                default:
                    fillColor = new Color(0.95f, 0.20f, 0.20f, 0.40f);
                    borderColor = new Color(1.0f, 0.35f, 0.35f, 0.95f);
                    break;
            }

            // 1a. Roadside Ribbon Parcel(s) Hover Preview
            if (_hoveredParcels.Count > 0)
            {
                for (int i = 0; i < _hoveredParcels.Count; i++)
                {
                    var parcel = _hoveredParcels[i];
                    if (parcel.Boundary == null || parcel.Boundary.Length < 4) continue;

                    Vector2[] closed = new Vector2[]
                    {
                        parcel.Boundary[0], parcel.Boundary[1], parcel.Boundary[2], parcel.Boundary[3], parcel.Boundary[0]
                    };

                    DrawColoredPolygon(parcel.Boundary, fillColor);
                    DrawPolyline(closed, borderColor, pulseWidth, true);

                    // Central indicator marker
                    Vector2 c = parcel.Center;
                    if (Mode == InteractionMode.Dezone)
                    {
                        DrawLine(c + new Vector2(-6, -6), c + new Vector2(6, 6), Colors.White, 3.0f, true);
                        DrawLine(c + new Vector2(-6, -6), c + new Vector2(6, 6), borderColor, 1.8f, true);
                        DrawLine(c + new Vector2(6, -6), c + new Vector2(-6, 6), Colors.White, 3.0f, true);
                        DrawLine(c + new Vector2(6, -6), c + new Vector2(-6, 6), borderColor, 1.8f, true);
                    }
                    else
                    {
                        DrawLine(c + new Vector2(-6, 0), c + new Vector2(6, 0), Colors.White, 2.5f, true);
                        DrawLine(c + new Vector2(0, -6), c + new Vector2(0, 6), Colors.White, 2.5f, true);
                    }

                    // Roadway access guide line
                    DrawLine(c, parcel.AccessPoint, new Color(borderColor.R, borderColor.G, borderColor.B, 0.45f), 1.5f, true);
                }
                return;
            }

            // 1b. Legacy Grid Cell Hover Preview
            if (HoverZoneId >= 0 && HoverZoneId < _grid.ZoneCount)
            {
                var hoverZone = _grid.GetZone(HoverZoneId);
                if (hoverZone != null)
                {
                    Vector2 pos = new Vector2(hoverZone.GridPos.X * cellSize + 1, hoverZone.GridPos.Y * cellSize + 1);
                    Vector2 size = new Vector2(cellSize - 2, cellSize - 2);
                    Rect2 rect = new Rect2(pos, size);

                    // Fill highlight
                    DrawRect(rect, fillColor, true);
                    // Pulsing outline
                    DrawRect(rect, borderColor, false, pulseWidth);

                    // For Dezone mode: draw red X crosshair marker
                    if (Mode == InteractionMode.Dezone)
                    {
                        Vector2 p1 = pos + new Vector2(12, 12);
                        Vector2 p2 = pos + size - new Vector2(12, 12);
                        Vector2 p3 = pos + new Vector2(size.X - 12, 12);
                        Vector2 p4 = pos + new Vector2(12, size.Y - 12);
                        DrawLine(p1, p2, Colors.White, 4.0f, true);
                        DrawLine(p1, p2, new Color(1f, 0.25f, 0.25f, 0.95f), 2.2f, true);
                        DrawLine(p3, p4, Colors.White, 4.0f, true);
                        DrawLine(p3, p4, new Color(1f, 0.25f, 0.25f, 0.95f), 2.2f, true);
                    }
                    else
                    {
                        // Draw central '+' symbol indicating expansion
                        Vector2 center = _grid.GetWorldCenter(HoverZoneId);
                        DrawLine(center + new Vector2(-7, 0), center + new Vector2(7, 0), Colors.White, 2.5f, true);
                        DrawLine(center + new Vector2(0, -7), center + new Vector2(0, 7), Colors.White, 2.5f, true);
                    }
                }
            }
            return;
        }

        // 2. Transit Route Designer Live Preview
        if (Mode == InteractionMode.CreateTransitRoute)
        {
            // 2a. Draw draft route path corridor along curves
            if (_draftTransitPath.Count > 1 && _roadGraph != null)
            {
                for (int i = 0; i < _draftTransitPath.Count - 1; i++)
                {
                    int id1 = _draftTransitPath[i];
                    int id2 = _draftTransitPath[i + 1];
                    var n1 = _roadGraph.GetNode(id1);
                    var n2 = _roadGraph.GetNode(id2);
                    if (n1 == null || n2 == null) continue;

                    int eid = _roadGraph.FindEdgeId(id1, id2);
                    var edge = (eid >= 0 && eid < _roadGraph.Edges.Count) ? _roadGraph.Edges[eid] : null;

                    if (edge != null && edge.Curve != null)
                    {
                        var pts = edge.Curve.GetSampledPoints(16);
                        DrawPolyline(pts, new Color(_draftRouteColor.R, _draftRouteColor.G, _draftRouteColor.B, 0.85f), 5.5f, true);
                        DrawPolyline(pts, Colors.White, 2.0f, true);
                    }
                    else
                    {
                        DrawLine(n1.WorldPosition, n2.WorldPosition, new Color(_draftRouteColor.R, _draftRouteColor.G, _draftRouteColor.B, 0.85f), 5.5f, true);
                        DrawLine(n1.WorldPosition, n2.WorldPosition, Colors.White, 2.0f, true);
                    }
                }

                if (_draftIsLoop && _draftTransitPath.Count > 2)
                {
                    int firstId = _draftTransitPath[0];
                    int lastId = _draftTransitPath[_draftTransitPath.Count - 1];
                    var first = _roadGraph.GetNode(firstId);
                    var last = _roadGraph.GetNode(lastId);
                    if (first != null && last != null)
                    {
                        int eid = _roadGraph.FindEdgeId(lastId, firstId);
                        var loopEdge = (eid >= 0 && eid < _roadGraph.Edges.Count) ? _roadGraph.Edges[eid] : null;

                        if (loopEdge != null && loopEdge.Curve != null)
                        {
                            var pts = loopEdge.Curve.GetSampledPoints(16);
                            DrawPolyline(pts, new Color(_draftRouteColor.R, _draftRouteColor.G, _draftRouteColor.B, 0.85f), 5.5f, true);
                            DrawPolyline(pts, Colors.White, 2.0f, true);
                        }
                        else
                        {
                            DrawLine(last.WorldPosition, first.WorldPosition, new Color(_draftRouteColor.R, _draftRouteColor.G, _draftRouteColor.B, 0.85f), 5.5f, true);
                            DrawLine(last.WorldPosition, first.WorldPosition, Colors.White, 2.0f, true);
                        }
                    }
                }
            }

            // 2b. Draw hover target indicator over snapped road node or mid-road edge
            if (_transitHoverSnap.Type == SnapType.Node || _transitHoverSnap.Type == SnapType.Edge)
            {
                Vector2 hoverPos = _transitHoverSnap.Position;
                float ringRadius = 11f + Mathf.Sin(_pulseTimer) * 2.5f;
                Color snapColor = _transitHoverSnap.Type == SnapType.Edge
                    ? new Color(0.2f, 0.95f, 0.65f, 0.95f)
                    : _draftRouteColor;

                // Pulsing hover target circle
                DrawArc(hoverPos, ringRadius, 0, Mathf.Tau, 24, snapColor, 2.8f, true);
                DrawCircle(hoverPos, 5.5f, snapColor);
                DrawCircle(hoverPos, 2.5f, Colors.White);

                // Connector guide line to last stop
                if (_draftTransitStops.Count > 0 && !_draftIsLoop)
                {
                    int lastStopId = _draftTransitStops[_draftTransitStops.Count - 1];
                    var lastNode = _roadGraph?.GetNode(lastStopId);
                    if (lastNode != null)
                    {
                        DrawLine(lastNode.WorldPosition, hoverPos, new Color(_draftRouteColor.R, _draftRouteColor.G, _draftRouteColor.B, 0.45f), 2.2f, true);
                    }
                }

                // Hover badge label
                string badgeLabel = _transitHoverSnap.Type == SnapType.Edge
                    ? $"🚏 Mid-Road Stop (New #{_draftTransitStops.Count + 1})"
                    : $"🚏 Stop #{_draftTransitStops.Count + 1}";

                DrawRect(new Rect2(hoverPos.X + 10, hoverPos.Y - 18, 140, 22), new Color(0.05f, 0.08f, 0.12f, 0.85f), true);
                DrawRect(new Rect2(hoverPos.X + 10, hoverPos.Y - 18, 140, 22), snapColor, false, 1.0f);
                DrawString(
                    ThemeDB.FallbackFont,
                    hoverPos + new Vector2(16, -3),
                    badgeLabel,
                    HorizontalAlignment.Left,
                    -1,
                    10,
                    Colors.White
                );
            }
            else if (HoverZoneId != -1 && _roadGraph != null && _roadGraph.NodeMap.ContainsKey(HoverZoneId))
            {
                var hoverNode = _roadGraph.GetNode(HoverZoneId);
                if (hoverNode != null)
                {
                    Vector2 hoverPos = hoverNode.WorldPosition;
                    float ringRadius = 10f + Mathf.Sin(_pulseTimer) * 2.5f;

                    DrawArc(hoverPos, ringRadius, 0, Mathf.Tau, 24, _draftRouteColor, 2.5f, true);
                    DrawCircle(hoverPos, 4.0f, Colors.White);

                    if (_draftTransitStops.Count > 0 && !_draftIsLoop)
                    {
                        int lastStopId = _draftTransitStops[_draftTransitStops.Count - 1];
                        if (lastStopId != HoverZoneId)
                        {
                            var lastNode = _roadGraph.GetNode(lastStopId);
                            if (lastNode != null)
                            {
                                DrawLine(lastNode.WorldPosition, hoverPos, new Color(_draftRouteColor.R, _draftRouteColor.G, _draftRouteColor.B, 0.40f), 2.2f, true);
                            }
                        }
                    }
                }
            }

            // 2c. Draw draft stop markers
            if (_roadGraph != null)
            {
                for (int s = 0; s < _draftTransitStops.Count; s++)
                {
                    int stopId = _draftTransitStops[s];
                    var stopNode = _roadGraph.GetNode(stopId);
                    if (stopNode != null)
                    {
                        Vector2 sPos = stopNode.WorldPosition;
                        DrawCircle(sPos, 9.5f, _draftRouteColor);
                        DrawArc(sPos, 9.5f, 0, Mathf.Tau, 20, Colors.White, 2.0f, true);

                        if (s == 0)
                        {
                            // Gold origin station ring
                            DrawArc(sPos, 14.0f, 0, Mathf.Tau, 22, Colors.Gold, 2.5f, true);
                        }

                        // Stop number
                        DrawString(
                            ThemeDB.FallbackFont,
                            sPos + new Vector2(-4, 4),
                            (s + 1).ToString(),
                            HorizontalAlignment.Center,
                            -1,
                            10,
                            Colors.White
                        );
                    }
                }
            }

            return;
        }

        // 3. Vector Road Construction Live Preview (BuildRoad & BuildCurvedRoad)
        if ((Mode == InteractionMode.BuildRoad || Mode == InteractionMode.BuildCurvedRoad) && HasRoadBuildPreview)
        {
            DrawVectorRoadPreview(pulseWidth);
            return;
        }

        // 4. Demolition Previews (Requires StartZoneId)
        if (StartZoneId == -1)
            return;

        // Draw Valid Target Cells Outlines
        Color targetOutlineColor = (Mode == InteractionMode.BuildRoad)
            ? new Color(0.2f, 0.85f, 1f, 0.65f)
            : new Color(1f, 0.4f, 0.3f, 0.65f);

        foreach (int targetId in ValidTargetZoneIds)
        {
            var tZone = _grid.GetZone(targetId);
            if (tZone == null) continue;

            Rect2 tRect = new Rect2(
                new Vector2(tZone.GridPos.X * cellSize + 2, tZone.GridPos.Y * cellSize + 2),
                new Vector2(cellSize - 4, cellSize - 4)
            );

            DrawRect(tRect, targetOutlineColor, false, 2.0f);
        }

        // 2. Draw Start Cell Highlight
        var startZone = _grid.GetZone(StartZoneId);
        if (startZone != null)
        {
            Vector2 startPos = new Vector2(startZone.GridPos.X * cellSize + 1, startZone.GridPos.Y * cellSize + 1);
            Rect2 startRect = new Rect2(startPos, new Vector2(cellSize - 2, cellSize - 2));

            Color startColor = (Mode == InteractionMode.BuildRoad)
                ? new Color(0.2f, 1f, 0.65f, 0.95f)
                : new Color(1f, 0.25f, 0.2f, 0.95f);

            DrawRect(startRect, startColor, false, pulseWidth);
        }

        // 3. Draw Hover Indicator & Preview Connection Line
        if (HoverZoneId != -1 && HoverZoneId != StartZoneId)
        {
            var hoverZone = _grid.GetZone(HoverZoneId);
            if (hoverZone != null && startZone != null)
            {
                bool isValidTarget = ValidTargetZoneIds.Contains(HoverZoneId);
                Vector2 startCenter = _grid.GetWorldCenter(StartZoneId);
                Vector2 hoverCenter = _grid.GetWorldCenter(HoverZoneId);

                Rect2 hoverRect = new Rect2(
                    new Vector2(hoverZone.GridPos.X * cellSize + 1, hoverZone.GridPos.Y * cellSize + 1),
                    new Vector2(cellSize - 2, cellSize - 2)
                );

                if (isValidTarget)
                {
                    if (Mode == InteractionMode.BuildRoad)
                    {
                        // Road preview line
                        DrawLine(startCenter, hoverCenter, new Color(0.2f, 1f, 0.5f, 0.95f), 6.0f, true);
                        DrawLine(startCenter, hoverCenter, Colors.White, 1.8f, true);
                        DrawRect(hoverRect, new Color(0.2f, 1f, 0.5f, 0.95f), false, 3.0f);
                    }
                    else if (Mode == InteractionMode.Demolish)
                    {
                        // Demolition preview line
                        DrawLine(startCenter, hoverCenter, new Color(1f, 0.2f, 0.2f, 0.95f), 6.5f, true);
                        DrawLine(startCenter, hoverCenter, Colors.Yellow, 1.8f, true);

                        // Demolition cross marker at the midpoint
                        Vector2 mid = (startCenter + hoverCenter) * 0.5f;
                        DrawLine(mid + new Vector2(-7, -7), mid + new Vector2(7, 7), Colors.White, 3.5f, true);
                        DrawLine(mid + new Vector2(-7, 7), mid + new Vector2(7, -7), Colors.White, 3.5f, true);

                        DrawRect(hoverRect, new Color(1f, 0.2f, 0.2f, 0.95f), false, 3.0f);
                    }
                }
            }
        }
    }

    private void DrawVectorRoadPreview(float pulseWidth)
    {
        Vector2 snapPos = RoadSnap.Position;

        // 1. Draw Snapping Indicators
        if (RoadSnap.Type == SnapType.Node)
        {
            float ringR = 10f + Mathf.Sin(_pulseTimer) * 2.5f;
            DrawArc(snapPos, ringR, 0, Mathf.Tau, 24, new Color(0.2f, 1.0f, 0.65f, 0.95f), 2.5f, true);
            DrawCircle(snapPos, 4.0f, Colors.White);
        }
        else if (RoadSnap.Type == SnapType.Edge)
        {
            float ringR = 8f + Mathf.Sin(_pulseTimer) * 2f;
            DrawCircle(snapPos, 5.0f, new Color(1.0f, 0.85f, 0.2f, 0.95f));
            DrawCircle(snapPos, 2.5f, Colors.White);
            DrawArc(snapPos, ringR, 0, Mathf.Tau, 20, new Color(1.0f, 0.85f, 0.2f, 0.85f), 2.0f, true);
        }
        else if (RoadSnap.Type == SnapType.Angle && RoadBuildStep >= 1)
        {
            Vector2 dir = RoadSnap.GuideDirection;
            DrawLine(RoadStartPos, RoadStartPos + dir * 350f, new Color(0.3f, 0.85f, 1f, 0.45f), 1.8f, true);
        }
        else if (RoadSnap.Type == SnapType.Tangent && RoadBuildStep >= 1)
        {
            Vector2 dir = RoadSnap.GuideDirection;
            DrawLine(RoadStartPos - dir * 40f, RoadStartPos + dir * 250f, new Color(0.2f, 1.0f, 0.85f, 0.50f), 2.2f, true);
        }

        // 2. Draw Start Anchor (locked in Step 1 or 2)
        if (RoadBuildStep >= 1)
        {
            DrawCircle(RoadStartPos, 4.5f, new Color(0.2f, 1f, 0.6f, 0.95f));
            DrawArc(RoadStartPos, 8.5f, 0, Mathf.Tau, 16, Colors.White, 2.0f, true);
        }

        // 3. Draw Road Preview Curve Ribbon & Dashed Lane Line
        if (RoadPreviewCurve != null && RoadPreviewCurve.Length > 2f)
        {
            var pts = RoadPreviewCurve.GetSampledPoints(24);
            Color baseColor = CanAffordRoad ? new Color(0.20f, 0.85f, 0.45f, 0.85f) : new Color(1.0f, 0.25f, 0.25f, 0.85f);
            Color borderColor = CanAffordRoad ? new Color(0.12f, 0.65f, 0.35f, 0.95f) : new Color(0.7f, 0.15f, 0.15f, 0.95f);

            // Outer outline
            DrawPolyline(pts, borderColor, 6.5f, true);
            // Asphalt surface
            DrawPolyline(pts, baseColor, 4.5f, true);

            // Center dashed line
            float cLen = RoadPreviewCurve.Length;
            if (cLen > 12f)
            {
                float cyc = 11f;
                int dCount = Mathf.FloorToInt(cLen / cyc);
                for (int i = 0; i < dCount; i++)
                {
                    float s1 = i * cyc + 1f;
                    float s2 = s1 + 5.5f;
                    if (s2 >= cLen) break;
                    Vector2 p1 = RoadPreviewCurve.Evaluate(s1 / cLen);
                    Vector2 p2 = RoadPreviewCurve.Evaluate(s2 / cLen);
                    DrawLine(p1, p2, Colors.White, 1.2f, true);
                }
            }
        }

        // 4. Draw Curvature Apex Handle (when in Step 2)
        if (RoadBuildStep == 2)
        {
            DrawCircle(RoadApexPos, 5.0f, Colors.Gold);
            DrawArc(RoadApexPos, 8.5f, 0, Mathf.Tau, 16, Colors.White, 1.8f, true);
        }

        // 5. Draw Live HUD Box near cursor
        Vector2 hudPos = snapPos + new Vector2(16, -30);
        float roadLen = RoadPreviewCurve?.Length ?? 0f;
        float minRadius = RoadPreviewCurve?.GetMinimumRadiusOfCurvature(16) ?? float.MaxValue;

        string line1 = $"📏 {roadLen:F0} m";
        if (minRadius < 30f)
        {
            line1 += $"  ⚠️ Sharp Turn (R:{minRadius:F0}m)";
        }
        else if (Mode == InteractionMode.BuildCurvedRoad && RoadBuildStep == 2)
        {
            line1 += $"  〰️ Smooth (R:{minRadius:F0}m)";
        }

        string line2 = CanAffordRoad
            ? $"💰 ${RoadCost:F0}"
            : $"💰 ${RoadCost:F0} (⚠️ Insufficient funds)";

        string line3 = RoadSnap.Type != SnapType.None ? $"📍 {RoadSnap.Description}" : null;

        float boxWidth = 240f;
        float boxHeight = line3 != null ? 52f : 36f;

        DrawRect(new Rect2(hudPos.X - 6, hudPos.Y - 14, boxWidth, boxHeight), new Color(0.04f, 0.06f, 0.10f, 0.88f), true);
        DrawRect(new Rect2(hudPos.X - 6, hudPos.Y - 14, boxWidth, boxHeight), new Color(0.25f, 0.45f, 0.65f, 0.75f), false, 1.0f);

        DrawString(ThemeDB.FallbackFont, hudPos + new Vector2(0, 0), line1, HorizontalAlignment.Left, -1, 11, Colors.White);
        Color costColor = CanAffordRoad ? new Color(0.3f, 1f, 0.5f) : new Color(1f, 0.35f, 0.35f);
        DrawString(ThemeDB.FallbackFont, hudPos + new Vector2(0, 15), line2, HorizontalAlignment.Left, -1, 11, costColor);

        if (line3 != null)
        {
            DrawString(ThemeDB.FallbackFont, hudPos + new Vector2(0, 30), line3, HorizontalAlignment.Left, -1, 10, new Color(0.4f, 0.85f, 1f));
        }
    }
}
