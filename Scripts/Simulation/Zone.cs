using Godot;

namespace CitySim.Simulation;

public enum ZoneType { Empty, Residential, Commercial, Industrial, Entrance }

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
    }
}
