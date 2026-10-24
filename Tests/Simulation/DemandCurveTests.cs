using NUnit.Framework;
using CitySim.Simulation;

namespace CitySim.Tests.Simulation;

[TestFixture]
public class DemandCurveTests
{
    [Test]
    public void GetDemandMultiplier_RushHours_ExceedNightAndMidday()
    {
        float morning = DemandCurve.GetDemandMultiplier(7.5f);
        float evening = DemandCurve.GetDemandMultiplier(17f);
        float midday = DemandCurve.GetDemandMultiplier(14f);
        float night = DemandCurve.GetDemandMultiplier(2f);

        Assert.That(morning, Is.GreaterThan(midday));
        Assert.That(evening, Is.GreaterThan(midday));
        Assert.That(midday, Is.GreaterThan(night));
        Assert.That(DemandCurve.GetDemandMultiplier(8f), Is.GreaterThan(night * 5f), "Peak demand is several times the night level");
    }

    [Test]
    public void GetDirectionalBias_MorningInboundEveningOutbound()
    {
        Assert.That(DemandCurve.GetDirectionalBias(7f), Is.GreaterThan(0.8f), "Morning rush heads to work");
        Assert.That(DemandCurve.GetDirectionalBias(17f), Is.LessThan(0.2f), "Evening rush heads home");
        Assert.That(DemandCurve.GetDirectionalBias(3f), Is.EqualTo(0.5f), "Night traffic is balanced");
    }

    [Test]
    public void Curves_InterpolateBetweenHoursAndWrapAroundMidnight()
    {
        float at7 = DemandCurve.GetDemandMultiplier(7f);
        float at8 = DemandCurve.GetDemandMultiplier(8f);
        float half = DemandCurve.GetDemandMultiplier(7.5f);

        Assert.That(half, Is.EqualTo((at7 + at8) * 0.5f).Within(1e-5f));
        Assert.That(DemandCurve.GetDemandMultiplier(25f), Is.EqualTo(DemandCurve.GetDemandMultiplier(1f)).Within(1e-6f));
        Assert.That(DemandCurve.GetDirectionalBias(-1f), Is.EqualTo(DemandCurve.GetDirectionalBias(23f)).Within(1e-6f));
    }
}
