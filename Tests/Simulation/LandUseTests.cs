using Godot;
using NUnit.Framework;
using CitySim.Simulation;

namespace CitySim.Tests.Simulation;

[TestFixture]
public class LandUseTests
{
    [Test]
    public void Zone_AsLandUseUnit_ExposesItsFields()
    {
        var zone = new Zone(7, ZoneType.Commercial, new Vector2I(1, 2)) { Jobs = 40, CommercialCap = 60 };
        ILandUseUnit unit = zone;

        Assert.That(unit.LandUseType, Is.EqualTo(ZoneType.Commercial));
        Assert.That(unit.Jobs, Is.EqualTo(40));
        Assert.That(LandUse.TotalCapacity(unit), Is.EqualTo(zone.ResidentialCap + 60));
    }

    [Test]
    public void Parcel_AsLandUseUnit_ExposesItsFields()
    {
        var parcel = new RoadsideParcel { ZoneType = ZoneType.Residential, Population = 12, ResidentialCap = 100 };
        ILandUseUnit unit = parcel;

        Assert.That(unit.LandUseType, Is.EqualTo(ZoneType.Residential));
        Assert.That(unit.Population, Is.EqualTo(12));
        Assert.That(LandUse.TotalCapacity(unit), Is.EqualTo(100));
    }

    [Test]
    public void ContributesToTaz_RequiresZoningAndActivityOrCapacity()
    {
        Assert.That(LandUse.ContributesToTaz(null), Is.False);
        Assert.That(LandUse.ContributesToTaz(new RoadsideParcel { ZoneType = ZoneType.Empty, Population = 50 }), Is.False);
        Assert.That(LandUse.ContributesToTaz(new RoadsideParcel { ZoneType = ZoneType.Residential }), Is.False);
        Assert.That(LandUse.ContributesToTaz(new RoadsideParcel { ZoneType = ZoneType.Residential, ResidentialCap = 10 }), Is.True);
        Assert.That(LandUse.ContributesToTaz(new RoadsideParcel { ZoneType = ZoneType.Industrial, Jobs = 5 }), Is.True);
    }

    [Test]
    public void IsWorkplace_CommercialAndIndustrialOnly()
    {
        Assert.That(LandUse.IsWorkplace(ZoneType.Commercial), Is.True);
        Assert.That(LandUse.IsWorkplace(ZoneType.Industrial), Is.True);
        Assert.That(LandUse.IsWorkplace(ZoneType.Residential), Is.False);
        Assert.That(LandUse.IsWorkplace(ZoneType.Entrance), Is.False);
    }
}
