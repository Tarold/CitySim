using Godot;
using System.Collections.Generic;
using CitySim.Simulation;
using CitySim.Simulation.Commands;
using CitySim.Tools;
using CitySim.UI;

namespace CitySim.Tests.Tools;

/// <summary>
/// Records preview calls made by tools.
/// </summary>
internal sealed class FakeToolPreview : IToolPreview
{
    public int RoadBuildPreviewStep = -1;
    public float RoadBuildPreviewCost;
    public int RoadBuildPreviewClears;
    public int DemolishStartZoneId = -1;
    public List<int> DemolishTargets = new List<int>();
    public int PreviewClears;
    public int HoverZoneId = -2;
    public List<RoadsideParcel> HoveredParcels = new List<RoadsideParcel>();
    public RoadEdge BusLaneHoverEdge;
    public List<int> TransitDraftStops = new List<int>();
    public List<int> TransitDraftPath = new List<int>();
    public bool TransitDraftIsLoop;
    public RoadSnapResult? TransitHoverSnap;

    public void SetRoadBuildPreview(int step, Vector2 startPos, Vector2 endPos, Vector2 apexPos, CurveSegment curve, RoadSnapResult snap, float cost, bool canAfford)
    {
        RoadBuildPreviewStep = step;
        RoadBuildPreviewCost = cost;
    }

    public void ClearRoadBuildPreview()
    {
        RoadBuildPreviewStep = -1;
        RoadBuildPreviewClears++;
    }

    public void SetPreview(InteractionMode mode, int startZoneId, List<int> validTargets)
    {
        DemolishStartZoneId = startZoneId;
        DemolishTargets = new List<int>(validTargets);
    }

    public void ClearPreview()
    {
        DemolishStartZoneId = -1;
        DemolishTargets.Clear();
        PreviewClears++;
    }

    public void SetHoverZone(int hoverZoneId) => HoverZoneId = hoverZoneId;

    public void SetHoveredParcels(List<RoadsideParcel> parcels, ZoneType zoneType, ParcelSide side) =>
        HoveredParcels = new List<RoadsideParcel>(parcels);

    public void ClearHoveredParcels() => HoveredParcels.Clear();

    public void SetBusLaneHoverEdge(RoadEdge edge) => BusLaneHoverEdge = edge;

    public void SetTransitDraft(List<int> stops, List<int> path, Color color, bool isLoop)
    {
        TransitDraftStops = new List<int>(stops);
        TransitDraftPath = new List<int>(path);
        TransitDraftIsLoop = isLoop;
    }

    public void ClearTransitDraft()
    {
        TransitDraftStops.Clear();
        TransitDraftPath.Clear();
        TransitDraftIsLoop = false;
    }

    public void SetTransitHoverSnap(RoadSnapResult snap) => TransitHoverSnap = snap;
}

/// <summary>
/// Headless <see cref="IToolHost"/> over a real <see cref="SimulationWorld"/> that records UI calls.
/// </summary>
internal sealed class FakeToolHost : IToolHost
{
    public FakeToolHost(SimulationWorld world)
    {
        World = world;
    }

    public SimulationWorld World { get; }
    public FakeToolPreview FakePreview { get; } = new FakeToolPreview();
    public IToolPreview Preview => FakePreview;

    public string LastHint = string.Empty;
    public List<CommandResult> Results = new List<CommandResult>();
    public int ReturnToInspectCalls;
    public List<int> ReleasedZoneSelections = new List<int>();
    public int CityOverviewCalls;
    public int TransitRoutesViewUpdates;
    public (int Stops, int Path, bool Loop) RouteDesignerStatus;

    public Color RouteDesignerColor => Colors.Orange;

    public CommandResult Execute(IWorldCommand command)
    {
        var result = command.Execute(World);
        Results.Add(result);
        return result;
    }

    public void ShowResult(CommandResult result)
    {
        if (!string.IsNullOrEmpty(result.Message)) LastHint = result.Message;
    }

    public void SetHint(string text, Color color) => LastHint = text;

    public void ReturnToInspect() => ReturnToInspectCalls++;

    public void ReleaseZoneSelection(int zoneId) => ReleasedZoneSelections.Add(zoneId);

    public void ShowCityOverview() => CityOverviewCalls++;

    public void UpdateRouteDesignerStatus(int stopsCount, int pathNodeCount, bool isLoop) =>
        RouteDesignerStatus = (stopsCount, pathNodeCount, isLoop);

    public void UpdateTransitRoutesView() => TransitRoutesViewUpdates++;
}
