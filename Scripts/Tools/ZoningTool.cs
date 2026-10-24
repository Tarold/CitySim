using Godot;
using CitySim.Simulation;
using CitySim.Simulation.Commands;
using CitySim.UI;

namespace CitySim.Tools;

/// <summary>
/// Zoning and dezoning of roadside parcels (preferred when the cursor is over a lot; Shift = whole
/// road side) or, as a fallback, of grid cells. Supports click-and-drag painting via repeated clicks.
/// </summary>
public sealed class ZoningTool : ITool
{
    private readonly IToolHost _host;

    public ZoningTool(IToolHost host, InteractionMode mode)
    {
        _host = host;
        Mode = mode;
        TargetType = mode.GetZoneType();
    }

    public InteractionMode Mode { get; }

    /// <summary>Zone type applied; <see cref="ZoneType.Empty"/> for dezoning.</summary>
    public ZoneType TargetType { get; }

    public bool IsDezone => Mode == InteractionMode.Dezone;

    public void OnHover(ToolInput input)
    {
        _host.Preview.SetBusLaneHoverEdge(null);

        var query = _host.World.Parcels.QueryZoningTarget(input.WorldPosition, _host.World.RoadGraph, input.Shift);
        if (query.SideParcels.Count > 0)
        {
            _host.Preview.SetHoverZone(-1);
            _host.Preview.SetHoveredParcels(query.SideParcels, TargetType, query.Side);

            string sideStr = CommandMessages.SideName(query.Side);
            string targetStr = input.Shift ? $"Entire {sideStr} Side ({query.SideParcels.Count} parcels)" : $"Parcel on {sideStr} Side";
            int changedCount = 0;
            foreach (var p in query.SideParcels)
            {
                if (p.ZoneType != TargetType) changedCount++;
            }
            float cost = changedCount * EconomyManager.ZoningCost;
            string costStr = IsDezone ? "Free" : $"${cost:F0}";

            _host.SetHint($"🏡 [{Mode}] Hovering {targetStr}. Cost: {costStr}. (Hold Shift for entire side)", new Color(0.3f, 0.95f, 0.65f));
            return;
        }

        _host.Preview.ClearHoveredParcels();
        ToolHelpers.HoverGridCell(_host, input.WorldPosition);
    }

    public void OnPrimaryClick(ToolInput input)
    {
        var query = _host.World.Parcels.QueryZoningTarget(input.WorldPosition, _host.World.RoadGraph, input.Shift);
        if (query.SideParcels.Count > 0)
        {
            IWorldCommand parcelCommand = IsDezone
                ? new DezoneParcelsCommand(query.SideParcels, query.Side)
                : new ZoneParcelsCommand(query.SideParcels, query.Side, TargetType);

            var parcelResult = _host.Execute(parcelCommand);
            if (parcelResult.IsApplied)
            {
                _host.Preview.ClearHoveredParcels();
                OnHover(input);
                _host.ShowCityOverview();
            }
            _host.ShowResult(parcelResult);
            return;
        }

        int zoneId = ToolHelpers.GetCellAt(_host.World.Grid, input.WorldPosition);
        if (zoneId < 0) return;

        bool wasZoned = _host.World.Grid.GetZone(zoneId).Type != ZoneType.Empty;
        if (IsDezone && wasZoned)
        {
            _host.ReleaseZoneSelection(zoneId);
        }

        IWorldCommand cellCommand = IsDezone
            ? new DezoneCellCommand(zoneId)
            : new ZoneCellCommand(zoneId, TargetType);

        var result = _host.Execute(cellCommand);
        if (result.IsApplied && (!IsDezone || wasZoned))
        {
            _host.ShowCityOverview();
        }
        _host.ShowResult(result);
    }

    public bool OnCancel() => false;

    public void Reset()
    {
    }
}
