using Godot;

namespace CitySim.Simulation;

public enum ZoneType { Empty, Residential, Commercial, Industrial, Entrance }

/// <summary>
/// Urban development density tier for zones and roadside parcels (LUTI model).
/// </summary>
public enum DensityLevel
{
    Low = 0,
    Medium = 1,
    High = 2
}

public class Zone
{
    public int Id;
    public ZoneType Type;
    public Vector2I GridPos;
    public int Population;       // people living here
    public int ResidentialCap;   // max people living here
    public int Jobs;             // workplaces
    public int CommercialCap;    // commercial capacity
    public float Attractiveness; // for gravity model

    // LUTI (Land-Use & Transport Interaction) state
    public DensityLevel Density { get; set; } = DensityLevel.Low;
    public float AccessibilityScore { get; set; } = 1.0f;
    public float LocalCongestion { get; set; } = 0.0f;
    public bool IsStagnant => LocalCongestion > 1.5f;
    
    public Zone(int id, ZoneType type, Vector2I gridPos)
    {
        Id = id;
        Type = type;
        GridPos = gridPos;
        Population = 0;
        ResidentialCap = 0;
        Jobs = 0;
        CommercialCap = 0;
        Attractiveness = 1.0f;
        Density = DensityLevel.Low;
        AccessibilityScore = 1.0f;
        LocalCongestion = 0.0f;
    }

    public bool UpgradeDensity()
    {
        if (Type == ZoneType.Empty || Type == ZoneType.Entrance || Density == DensityLevel.High)
            return false;

        Density = Density == DensityLevel.Low ? DensityLevel.Medium : DensityLevel.High;
        if (Type == ZoneType.Residential)
        {
            ResidentialCap = Density == DensityLevel.Medium ? 14000 : 28000;
        }
        else if (Type == ZoneType.Commercial)
        {
            Jobs = Density == DensityLevel.Medium ? 9000 : 18000;
            CommercialCap = Density == DensityLevel.Medium ? 6000 : 12000;
        }
        else if (Type == ZoneType.Industrial)
        {
            Jobs = Density == DensityLevel.Medium ? 7600 : 15200;
        }
        return true;
    }
}
