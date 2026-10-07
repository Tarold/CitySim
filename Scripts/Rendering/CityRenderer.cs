using Godot;
using CitySim.Simulation;

namespace CitySim.Rendering;

public partial class CityRenderer : Node2D
{
    private CityGrid _grid;
    public int SelectedZoneId = -1;

    public void Initialize(CityGrid grid) 
    { 
        _grid = grid; 
        QueueRedraw(); 
    }

    public void SetSelectedZone(int zoneId)
    {
        SelectedZoneId = zoneId;
        QueueRedraw();
    }
    
    public override void _Draw()
    {
        if (_grid == null) return;
        
        float cellSize = _grid.CellSize;
        
        for (int x = 0; x < _grid.Width; x++)
        {
            for (int y = 0; y < _grid.Height; y++)
            {
                var zone = _grid.GetZone(x, y);
                if (zone == null) continue;
                
                Color color = zone.Type switch
                {
                    ZoneType.Residential => new Color(0.18f, 0.58f, 0.28f), // Forest Green
                    ZoneType.Commercial  => new Color(0.20f, 0.45f, 0.85f), // Corporate Blue
                    ZoneType.Industrial  => new Color(0.85f, 0.55f, 0.15f), // Amber / Industrial Orange
                    _ => new Color(0.12f, 0.12f, 0.14f)                     // Background terrain
                };
                
                Vector2 pos = new Vector2(x * cellSize + 1, y * cellSize + 1);
                Vector2 size = new Vector2(cellSize - 2, cellSize - 2);
                Rect2 rect = new Rect2(pos, size);
                
                DrawRect(rect, color, true);
                
                if (zone.Type != ZoneType.Empty)
                {
                    DrawRect(rect, new Color(0.1f, 0.1f, 0.1f, 0.7f), false, 1f);
                }

                // Selected zone golden halo
                if (zone.Id == SelectedZoneId)
                {
                    DrawRect(rect, Colors.Gold, false, 2.5f);
                }
            }
        }
    }
}
