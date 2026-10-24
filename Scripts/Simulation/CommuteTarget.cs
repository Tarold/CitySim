using Godot;
using System.Collections.Generic;

namespace CitySim.Simulation;

/// <summary>
/// One commute destination (or origin) of a selected zone/parcel: position, trip volume, travel time and
/// the road path, as computed by <see cref="CommuteAnalytics"/> and drawn by the commute overlay.
/// </summary>
public class CommuteTarget
{
    public int ZoneId = -1;
    public int ParcelId = -1;
    public Vector2 Position;
    public float Volume;
    public ZoneType Type;
    public float TravelTime;
    public List<int> EdgePath = new List<int>();
}
