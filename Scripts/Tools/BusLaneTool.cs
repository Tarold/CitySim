using Godot;
using CitySim.Simulation.Commands;

namespace CitySim.Tools;

/// <summary>
/// Toggles dedicated bus lanes on the road segment under the cursor.
/// </summary>
public sealed class BusLaneTool : ITool
{
    private readonly IToolHost _host;

    public BusLaneTool(IToolHost host)
    {
        _host = host;
    }

    public void OnHover(ToolInput input)
    {
        var hoveredEdge = ToolHelpers.PickRoadEdge(_host.World.RoadGraph, input.WorldPosition);
        _host.Preview.SetBusLaneHoverEdge(hoveredEdge);

        if (hoveredEdge != null)
        {
            string status = hoveredEdge.HasBusLane ? "УВІМКНЕНО (Клік — прибрати)" : "ВИМКНЕНО (Клік — виділити смугу ГТ)";
            _host.SetHint($"🚌 [Bus Lane] Дорога #{hoveredEdge.Id}: {status} | Ємність авто: {hoveredEdge.Capacity:F0} авт/год", new Color(1.0f, 0.75f, 0.25f));
        }
        else
        {
            _host.SetHint("🚌 [Bus Lane] Наведіть курсор на дорогу та клікніть, щоб перемкнути виділену смугу ГТ.", new Color(1.0f, 0.65f, 0.2f));
        }
    }

    public void OnPrimaryClick(ToolInput input)
    {
        var targetEdge = ToolHelpers.PickRoadEdge(_host.World.RoadGraph, input.WorldPosition);

        var result = _host.Execute(new ToggleBusLaneCommand(targetEdge?.Id ?? -1));
        if (result.IsApplied)
        {
            OnHover(input);
        }
        _host.ShowResult(result);
    }

    public bool OnCancel() => false;

    public void Reset()
    {
    }
}
