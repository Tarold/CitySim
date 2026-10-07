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
                Zones[id] = new Zone(id, ZoneType.Empty, new Vector2I(x, y));
            }
        }
    }

    public Zone GetZone(int id)
    {
        return Zones[id];
    }

    public Zone GetZone(int x, int y)
    {
        if (x < 0 || x >= Width || y < 0 || y >= Height) return null;
        return Zones[y * Width + x];
    }

    public int GetZoneId(int x, int y)
    {
        return y * Width + x;
    }

    public int ZoneCount => Width * Height;

    public Vector2 GetWorldCenter(int zoneId)
    {
        var zone = Zones[zoneId];
        return new Vector2((zone.GridPos.X + 0.5f) * CellSize, (zone.GridPos.Y + 0.5f) * CellSize);
    }

    public const int DefaultResidentialPopulation = 7000;
    public const int DefaultCommercialJobs = 4500;
    public const int DefaultCommercialCapacity = 3000;
    public const int DefaultIndustrialJobs = 3800;

    public int TotalPopulation()
    {
        int total = 0;
        for (int i = 0; i < Zones.Length; i++)
        {
            total += Zones[i].Population;
        }
        return total;
    }

    public int TotalJobs()
    {
        int total = 0;
        for (int i = 0; i < Zones.Length; i++)
        {
            total += Zones[i].Jobs;
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
                zone.Population = population > 0 ? population : DefaultResidentialPopulation;
                zone.Jobs = 0;
                zone.CommercialCap = 0;
                break;
            case ZoneType.Commercial:
                zone.Population = 0;
                zone.Jobs = jobs > 0 ? jobs : DefaultCommercialJobs;
                zone.CommercialCap = commercialCap >= 0 ? commercialCap : DefaultCommercialCapacity;
                break;
            case ZoneType.Industrial:
                zone.Population = 0;
                zone.Jobs = jobs > 0 ? jobs : DefaultIndustrialJobs;
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
                int pop = 0;
                int jobs = 0;
                int cap = 0;

                // Margins: leave border cells empty
                if (x >= 2 && x <= 17 && y >= 2 && y <= 17)
                {
                    // 1. WEST SIDE: RESIDENTIAL DISTRICT (Green sleep suburbs & apartments)
                    if (x >= 2 && x <= 7)
                    {
                        type = ZoneType.Residential;
                        bool isHighDensity = (x >= 4 && x <= 7) && (y >= 4 && y <= 15);
                        pop = isHighDensity ? 14000 : 7000;
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
                Zones[id].Population = pop;
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
