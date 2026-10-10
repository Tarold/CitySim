using Godot;
using CitySim.Simulation;

namespace CitySim.Rendering;

/// <summary>
/// Procedurally renders city ground zones, roadside ribbon parcels,
/// and oriented frontage building footprints with architectural accents.
/// </summary>
public partial class CityRenderer : Node2D
{
    private CityGrid _grid;
    private ParcelManager _parcelManager;

    /// <summary>The ID of the currently selected zone for inspection highlight.</summary>
    public int SelectedZoneId = -1;

    /// <summary>The ID of the currently selected parcel for inspection highlight.</summary>
    public int SelectedParcelId = -1;

    /// <summary>
    /// Initializes the city renderer with the grid and optional roadside parcel manager.
    /// </summary>
    public void Initialize(CityGrid grid, ParcelManager parcelManager = null) 
    { 
        _grid = grid; 
        _parcelManager = parcelManager;
        QueueRedraw(); 
    }

    /// <summary>
    /// Sets or updates the parcel manager reference.
    /// </summary>
    public void SetParcelManager(ParcelManager parcelManager)
    {
        _parcelManager = parcelManager;
        QueueRedraw();
    }

    /// <summary>
    /// Sets the inspection selection on a grid zone.
    /// </summary>
    public void SetSelectedZone(int zoneId)
    {
        SelectedZoneId = zoneId;
        QueueRedraw();
    }

    /// <summary>
    /// Sets the inspection selection on a roadside parcel.
    /// </summary>
    public void SetSelectedParcel(int parcelId)
    {
        SelectedParcelId = parcelId;
        QueueRedraw();
    }

    /// <summary>
    /// Forces a redraw of the city scene.
    /// </summary>
    public void Refresh()
    {
        QueueRedraw();
    }
    
    public override void _Draw()
    {
        // 1. Draw Legacy Grid Zones (Background / Base Map)
        if (_grid != null)
        {
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
                        ZoneType.Entrance    => new Color(0.6f, 0.2f, 0.8f),    // Purple Entrance
                        _                    => new Color(0.12f, 0.12f, 0.14f)  // Background terrain
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

        // 2. Draw Roadside Ribbon Parcels & Frontage Building Footprints
        if (_parcelManager != null && _parcelManager.Parcels != null)
        {
            var parcels = _parcelManager.Parcels;
            for (int i = 0; i < parcels.Count; i++)
            {
                var parcel = parcels[i];
                if (parcel.Boundary == null || parcel.Boundary.Length < 4) continue;

                Vector2[] closedBoundary = new Vector2[]
                {
                    parcel.Boundary[0],
                    parcel.Boundary[1],
                    parcel.Boundary[2],
                    parcel.Boundary[3],
                    parcel.Boundary[0]
                };

                // A. Draw Lot Ground Polygon
                if (parcel.ZoneType == ZoneType.Empty)
                {
                    // Subtle gray lot boundary for empty parcels
                    DrawPolyline(closedBoundary, new Color(0.30f, 0.32f, 0.36f, 0.45f), 1.0f, true);
                }
                else
                {
                    Color lotColor = parcel.ZoneType switch
                    {
                        ZoneType.Residential => new Color(0.18f, 0.58f, 0.28f, 0.85f), // Forest Green
                        ZoneType.Commercial  => new Color(0.20f, 0.45f, 0.85f, 0.85f), // Corporate Blue
                        ZoneType.Industrial  => new Color(0.85f, 0.55f, 0.15f, 0.85f), // Amber
                        _                    => new Color(0.20f, 0.20f, 0.22f, 0.50f)
                    };

                    Color lotBorderColor = parcel.ZoneType switch
                    {
                        ZoneType.Residential => new Color(0.12f, 0.42f, 0.20f, 0.95f),
                        ZoneType.Commercial  => new Color(0.12f, 0.30f, 0.65f, 0.95f),
                        ZoneType.Industrial  => new Color(0.60f, 0.38f, 0.10f, 0.95f),
                        _                    => new Color(0.25f, 0.25f, 0.28f, 0.80f)
                    };

                    DrawColoredPolygon(parcel.Boundary, lotColor);
                    DrawPolyline(closedBoundary, lotBorderColor, 1.2f, true);

                    // Selected parcel golden halo
                    if (parcel.Id == SelectedParcelId)
                    {
                        DrawPolyline(closedBoundary, Colors.Gold, 2.5f, true);
                    }

                    // B. Draw Driveway Marker & Inset Building Footprint
                    // Compute oriented coordinate frame based on FacingAngle (facing road)
                    float angle = parcel.FacingAngle;
                    Vector2 facingDir = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                    Vector2 lateralDir = new Vector2(-facingDir.Y, facingDir.X);

                    // Building center placed inside lot, slightly setback
                    Vector2 bldgCenter = parcel.Center - facingDir * 2.5f;

                    // Footprint dimensions: 25px facade width, 21px depth
                    float hw = 12.5f; // half lateral width
                    float hd = 10.5f; // half depth

                    Vector2 fl = bldgCenter + facingDir * hd - lateralDir * hw; // front-left
                    Vector2 fr = bldgCenter + facingDir * hd + lateralDir * hw; // front-right
                    Vector2 rr = bldgCenter - facingDir * hd + lateralDir * hw; // rear-right
                    Vector2 rl = bldgCenter - facingDir * hd - lateralDir * hw; // rear-left

                    // Driveway marker from front facade center to road access point
                    Vector2 facadeCenter = (fl + fr) * 0.5f;
                    DrawLine(facadeCenter, parcel.AccessPoint, new Color(0.28f, 0.28f, 0.32f, 0.95f), 3.2f, true);
                    DrawLine(facadeCenter, parcel.AccessPoint, new Color(0.42f, 0.42f, 0.48f, 0.85f), 1.5f, true);

                    // C. 2D Building Footprint Shape
                    Vector2[] footprint = new Vector2[] { fl, fr, rr, rl };
                    Vector2[] closedFootprint = new Vector2[] { fl, fr, rr, rl, fl };

                    Color bldgColor = parcel.ZoneType switch
                    {
                        ZoneType.Residential => new Color(0.22f, 0.68f, 0.34f),
                        ZoneType.Commercial  => new Color(0.25f, 0.55f, 0.95f),
                        ZoneType.Industrial  => new Color(0.92f, 0.62f, 0.20f),
                        _                    => Colors.Gray
                    };
                    DrawColoredPolygon(footprint, bldgColor);
                    DrawPolyline(closedFootprint, new Color(0.08f, 0.08f, 0.10f, 0.95f), 1.6f, true);

                    // D. Roof Cap (inset polygon shape)
                    float rhw = hw - 2.8f;
                    float rhd = hd - 2.8f;
                    Vector2 rfl = bldgCenter + facingDir * rhd - lateralDir * rhw;
                    Vector2 rfr = bldgCenter + facingDir * rhd + lateralDir * rhw;
                    Vector2 rrr = bldgCenter - facingDir * rhd + lateralDir * rhw;
                    Vector2 rrl = bldgCenter - facingDir * rhd - lateralDir * rhw;

                    Vector2[] roofCap = new Vector2[] { rfl, rfr, rrr, rrl };
                    Vector2[] closedRoofCap = new Vector2[] { rfl, rfr, rrr, rrl, rfl };

                    Color roofColor = parcel.ZoneType switch
                    {
                        ZoneType.Residential => new Color(0.14f, 0.46f, 0.24f),
                        ZoneType.Commercial  => new Color(0.16f, 0.36f, 0.68f),
                        ZoneType.Industrial  => new Color(0.68f, 0.44f, 0.12f),
                        _                    => Colors.DarkGray
                    };
                    DrawColoredPolygon(roofCap, roofColor);
                    DrawPolyline(closedRoofCap, new Color(0.06f, 0.06f, 0.08f, 0.75f), 1.0f, true);

                    // E. Facade Border Line (crisp architectural highlight facing road)
                    Color facadeAccent = parcel.ZoneType switch
                    {
                        ZoneType.Residential => new Color(0.85f, 1.0f, 0.85f, 0.95f),
                        ZoneType.Commercial  => new Color(0.85f, 0.95f, 1.0f, 0.95f),
                        ZoneType.Industrial  => new Color(1.0f, 0.90f, 0.75f, 0.95f),
                        _                    => Colors.White
                    };
                    DrawLine(fl, fr, facadeAccent, 2.2f, true);
                }
            }
        }
    }
}
