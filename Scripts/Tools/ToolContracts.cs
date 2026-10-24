using Godot;
using System.Collections.Generic;
using CitySim.Simulation;
using CitySim.Simulation.Commands;
using CitySim.UI;

namespace CitySim.Tools;

/// <summary>
/// Pointer state handed to a tool: cursor position in world space and modifier keys.
/// Tools never read Godot <c>Input</c> directly, so they stay headless and testable.
/// </summary>
public readonly struct ToolInput
{
    public ToolInput(Vector2 worldPosition, bool shift = false)
    {
        WorldPosition = worldPosition;
        Shift = shift;
    }

    public Vector2 WorldPosition { get; }

    /// <summary>Shift held: angle snap for roads, whole road side for zoning.</summary>
    public bool Shift { get; }
}

/// <summary>
/// Map preview surface used by tools; implemented by <c>ToolPreviewRenderer</c> (same signatures).
/// </summary>
public interface IToolPreview
{
    void SetRoadBuildPreview(int step, Vector2 startPos, Vector2 endPos, Vector2 apexPos, CurveSegment curve, RoadSnapResult snap, float cost, bool canAfford);
    void ClearRoadBuildPreview();
    void SetPreview(InteractionMode mode, int startZoneId, List<int> validTargets);
    void ClearPreview();
    void SetHoverZone(int hoverZoneId);
    void SetHoveredParcels(List<RoadsideParcel> parcels, ZoneType zoneType, ParcelSide side);
    void ClearHoveredParcels();
    void SetBusLaneHoverEdge(RoadEdge edge);
    void SetTransitDraft(List<int> stops, List<int> path, Color color, bool isLoop);
    void ClearTransitDraft();
    void SetTransitHoverSnap(RoadSnapResult snap);
}

/// <summary>
/// Services the Godot layer provides to tools: the world, command execution, hints and UI follow-ups.
/// </summary>
public interface IToolHost
{
    SimulationWorld World { get; }
    IToolPreview Preview { get; }

    /// <summary>Executes a command and refreshes map layers (see <c>Main.Execute</c>).</summary>
    CommandResult Execute(IWorldCommand command);

    /// <summary>Shows a command result message as the tool hint.</summary>
    void ShowResult(CommandResult result);

    void SetHint(string text, Color color);

    /// <summary>Switches the UI back to Inspect mode (resets every tool).</summary>
    void ReturnToInspect();

    /// <summary>Drops the inspect selection if it points at <paramref name="zoneId"/> (about to be removed).</summary>
    void ReleaseZoneSelection(int zoneId);

    /// <summary>Shows the city-wide overview panel after an edit changed land use.</summary>
    void ShowCityOverview();

    /// <summary>Color picked in the route designer palette.</summary>
    Color RouteDesignerColor { get; }

    void UpdateRouteDesignerStatus(int stopsCount, int pathNodeCount, bool isLoop);

    void UpdateTransitRoutesView();
}

/// <summary>
/// An interaction mode's state machine: hover feedback, primary click and right-click cancel.
/// </summary>
public interface ITool
{
    /// <summary>Updates hover previews and hints for the cursor.</summary>
    void OnHover(ToolInput input);

    /// <summary>Primary (left) click on the map.</summary>
    void OnPrimaryClick(ToolInput input);

    /// <summary>
    /// Right-click: cancels the current step. Returns false when there is nothing to cancel, in which
    /// case the host leaves the tool and returns to Inspect.
    /// </summary>
    bool OnCancel();

    /// <summary>Discards any in-progress operation and its previews (mode switch).</summary>
    void Reset();
}
