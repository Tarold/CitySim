using Godot;
using System.Collections.Generic;

namespace CitySim.Simulation;

public class CityGrid
{
    public int Width;
    public int Height;
    public Zone[] Zones;
    public float CellSize;
    public List<int> ActiveZoneIds = new List<int>();

    public CityGrid(int width, int height, float cellSize = 64f)
    {
        Width = width;
        Height = height;
        CellSize = cellSize;
        Zones = new Zone[Width * Height];
        
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int id = GetZoneId(x, y);
                ZoneType type = ZoneType.Empty;

                if ((x == 0 && y == height / 2) || (x == width - 1 && y == height / 2))
                {
                    type = ZoneType.Entrance;
                }

                Zones[id] = new Zone(id, type, new Vector2I(x, y));
                if (type != ZoneType.Empty)
                {
                    ActiveZoneIds.Add(id);
                }
            }
        }
    }

    /// <summary>
    /// Safely retrieves the zone by index, returning null if id is negative or out of bounds.
    /// </summary>
    public Zone GetZone(int id)
    {
        if (Zones == null || id < 0 || id >= Zones.Length) return null;
        return Zones[id];
    }

    /// <summary>
    /// Safely retrieves the zone at grid coordinate (x, y), returning null if out of bounds.
    /// </summary>
    public Zone GetZone(int x, int y)
    {
        if (Zones == null || x < 0 || x >= Width || y < 0 || y >= Height) return null;
        int id = y * Width + x;
        if (id < 0 || id >= Zones.Length) return null;
        return Zones[id];
    }

    public int GetZoneId(int x, int y)
    {
        return y * Width + x;
    }

    public int ZoneCount => Width * Height;

    /// <summary>
    /// Computes the 2D world center position of a zone. Returns Vector2.Zero safely if zoneId is out of bounds or zone is null.
    /// </summary>
    public Vector2 GetWorldCenter(int zoneId)
    {
        if (Zones == null || zoneId < 0 || zoneId >= Zones.Length) return Vector2.Zero;
        var zone = Zones[zoneId];
        if (zone == null) return Vector2.Zero;
        return new Vector2((zone.GridPos.X + 0.5f) * CellSize, (zone.GridPos.Y + 0.5f) * CellSize);
    }

    public const int DefaultResidentialPopulation = 7000;
    public const int DefaultCommercialJobs = 4500;
    public const int DefaultCommercialCapacity = 3000;
    public const int DefaultIndustrialJobs = 3800;

    public int TotalPopulation()
    {
        if (Zones == null) return 0;
        int total = 0;
        for (int i = 0; i < Zones.Length; i++)
        {
            if (Zones[i] != null)
            {
                total += Zones[i].Population;
            }
        }
        return total;
    }

    public int TotalJobs()
    {
        if (Zones == null) return 0;
        int total = 0;
        for (int i = 0; i < Zones.Length; i++)
        {
            if (Zones[i] != null)
            {
                total += Zones[i].Jobs;
            }
        }
        return total;
    }

    /// <summary>
    /// Designates a zone type for the specified grid cell coordinates and initializes baseline capacity metrics.
    /// </summary>
    public bool ZoneCell(int x, int y, ZoneType type, int population = DefaultResidentialPopulation, int jobs = -1, int commercialCap = -1)
    {
        if (x < 0 || x >= Width || y < 0 || y >= Height) return false;
        return ZoneCell(GetZoneId(x, y), type, population, jobs, commercialCap);
    }

    /// <summary>
    /// Designates a zone type for the specified zone ID and initializes baseline capacity metrics.
    /// </summary>
    public bool ZoneCell(int zoneId, ZoneType type, int population = DefaultResidentialPopulation, int jobs = -1, int commercialCap = -1)
    {
        if (zoneId < 0 || zoneId >= ZoneCount) return false;
        var zone = Zones[zoneId];
        if (zone == null) return false;

        if (type == ZoneType.Empty)
        {
            return DezoneCell(zoneId);
        }

        zone.Type = type;
        switch (type)
        {
            case ZoneType.Residential:
                zone.ResidentialCap = population > 0 ? population : DefaultResidentialPopulation;
                zone.Population = 0; // Starts at 0, grows gradually
                zone.Jobs = 0;
                zone.CommercialCap = 0;
                break;
            case ZoneType.Commercial:
                zone.ResidentialCap = 0;
                zone.Population = 0;
                zone.Jobs = jobs > 0 ? jobs : DefaultCommercialJobs;
                zone.CommercialCap = commercialCap >= 0 ? commercialCap : DefaultCommercialCapacity;
                break;
            case ZoneType.Industrial:
                zone.ResidentialCap = 0;
                zone.Population = 0;
                zone.Jobs = jobs > 0 ? jobs : DefaultIndustrialJobs;
                zone.CommercialCap = 0;
                break;
            case ZoneType.Entrance:
                zone.ResidentialCap = 0;
                zone.Population = 0;
                zone.Jobs = 0;
                zone.CommercialCap = 0;
                break;
        }

        if (!ActiveZoneIds.Contains(zoneId))
        {
            ActiveZoneIds.Add(zoneId);
        }

        return true;
    }

    /// <summary>
    /// Clears a grid cell back to empty terrain and resets all capacity metrics.
    /// </summary>
    public bool DezoneCell(int x, int y)
    {
        if (x < 0 || x >= Width || y < 0 || y >= Height) return false;
        return DezoneCell(GetZoneId(x, y));
    }

    /// <summary>
    /// Clears a zone ID back to empty terrain and resets all capacity metrics.
    /// </summary>
    public bool DezoneCell(int zoneId)
    {
        if (zoneId < 0 || zoneId >= ZoneCount) return false;
        var zone = Zones[zoneId];
        if (zone == null) return false;

        zone.Type = ZoneType.Empty;
        zone.Population = 0;
        zone.ResidentialCap = 0;
        zone.Jobs = 0;
        zone.CommercialCap = 0;
        ActiveZoneIds.Remove(zoneId);
        return true;
    }

    public void GenerateDefaultCity()
    {
        ActiveZoneIds.Clear();
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int id = GetZoneId(x, y);
                ZoneType type = ZoneType.Empty;
                int resCap = 0;
                int jobs = 0;
                int cap = 0;

                // Add City Entrances at the edges of the grid
                if ((x == 1 && y == 10) || (x == 18 && y == 10) || (x == 10 && y == 1) || (x == 10 && y == 18))
                {
                    type = ZoneType.Entrance;
                }
                // Margins: leave border cells empty
                else if (x >= 2 && x <= 17 && y >= 2 && y <= 17)
                {
                    // 1. WEST SIDE: RESIDENTIAL DISTRICT (Green sleep suburbs & apartments)
                    if (x >= 2 && x <= 7)
                    {
                        type = ZoneType.Residential;
                        bool isHighDensity = (x >= 4 && x <= 7) && (y >= 4 && y <= 15);
                        resCap = isHighDensity ? 14000 : 7000;
                    }
                    // 2. CENTRAL SECTOR: COMMERCIAL DOWNTOWN (Blue office towers & shops)
                    else if (x >= 8 && x <= 11)
                    {
                        type = ZoneType.Commercial;
                        jobs = 4500;
                        cap = 3000;
                    }
                    // 3. EAST SIDE: INDUSTRIAL FACTORY COMPLEX (Amber heavy plants & manufacturing)
                    else if (x >= 12 && x <= 17)
                    {
                        type = ZoneType.Industrial;
                        jobs = 3800;
                    }
                }

                Zones[id].Type = type;
                Zones[id].ResidentialCap = resCap;
                Zones[id].Population = 0; // Starts empty
                Zones[id].Jobs = jobs;
                Zones[id].CommercialCap = cap;

                if (type != ZoneType.Empty)
                {
                    ActiveZoneIds.Add(id);
                }
            }
        }
    }
}
