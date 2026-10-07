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
    private float _pulseTimer = 0f;

    /// <summary>Active tool mode for preview rendering.</summary>
    public InteractionMode Mode { get; private set; } = InteractionMode.Inspect;

    /// <summary>The starting zone ID of the pending operation, or -1 if none.</summary>
    public int StartZoneId { get; private set; } = -1;

    /// <summary>The zone ID currently hovered by the mouse cursor, or -1.</summary>
    public int HoverZoneId { get; private set; } = -1;

    /// <summary>List of zone IDs that are valid adjacent targets for the pending operation.</summary>
    public List<int> ValidTargetZoneIds { get; private set; } = new List<int>();

    /// <summary>
    /// Initializes the preview renderer with the city grid.
    /// </summary>
    public void Initialize(CityGrid grid)
    {
        _grid = grid;
    }

    /// <summary>
    /// Sets the active interaction mode and clears any pending road targets.
    /// </summary>
    public void SetMode(InteractionMode mode)
    {
        Mode = mode;
        StartZoneId = -1;
        ValidTargetZoneIds.Clear();
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
        QueueRedraw();
    }

    public override void _Process(double delta)
    {
        if (StartZoneId != -1 || (IsZoningMode(Mode) && HoverZoneId != -1))
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
            if (HoverZoneId >= 0 && HoverZoneId < _grid.ZoneCount)
            {
                var hoverZone = _grid.GetZone(HoverZoneId);
                if (hoverZone != null)
                {
                    Vector2 pos = new Vector2(hoverZone.GridPos.X * cellSize + 1, hoverZone.GridPos.Y * cellSize + 1);
                    Vector2 size = new Vector2(cellSize - 2, cellSize - 2);
                    Rect2 rect = new Rect2(pos, size);

                    Color fillColor;
                    Color borderColor;

                    switch (Mode)
                    {
                        case InteractionMode.ZoneResidential:
                            fillColor = new Color(0.18f, 0.75f, 0.32f, 0.35f);
                            borderColor = new Color(0.25f, 1.0f, 0.45f, 0.95f);
                            break;
                        case InteractionMode.ZoneCommercial:
                            fillColor = new Color(0.20f, 0.55f, 0.98f, 0.35f);
                            borderColor = new Color(0.35f, 0.75f, 1.0f, 0.95f);
                            break;
                        case InteractionMode.ZoneIndustrial:
                            fillColor = new Color(0.95f, 0.60f, 0.15f, 0.35f);
                            borderColor = new Color(1.0f, 0.75f, 0.25f, 0.95f);
                            break;
                        case InteractionMode.Dezone:
                        default:
                            fillColor = new Color(0.95f, 0.20f, 0.20f, 0.35f);
                            borderColor = new Color(1.0f, 0.35f, 0.35f, 0.95f);
                            break;
                    }

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

        // 2. Road Building & Demolition Previews (Requires StartZoneId)
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
}
