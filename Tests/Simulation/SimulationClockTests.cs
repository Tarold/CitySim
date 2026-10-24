using NUnit.Framework;
using CitySim.Simulation;

namespace CitySim.Tests.Simulation;

[TestFixture]
public class SimulationClockTests
{
    private const float FrameDelta = 1f / 60f;

    private static SimulationClock CreateRecalculatedClock(float startHour = SimulationClock.DefaultStartHour)
    {
        var clock = new SimulationClock(startHour);
        clock.MarkMacroRecalculated();
        return clock;
    }

    [Test]
    public void Constructor_Defaults_StartsAt0630OnDayOneAtNormalSpeed()
    {
        var clock = new SimulationClock();

        Assert.That(clock.Hour, Is.EqualTo(6.5f));
        Assert.That(clock.Day, Is.EqualTo(1));
        Assert.That(clock.Speed, Is.EqualTo(1f));
    }

    [Test]
    public void Advance_OneRealSecondAtNormalSpeed_AdvancesOneGameMinute()
    {
        var clock = CreateRecalculatedClock();

        clock.Advance(1f);

        Assert.That(clock.Hour, Is.EqualTo(6.5f + 1f / 60f).Within(1e-5f));
    }

    [Test]
    public void Advance_SpeedMultiplier_ScalesGameTime()
    {
        var clock = CreateRecalculatedClock();
        clock.Speed = 50f;

        clock.Advance(1f);

        Assert.That(clock.Hour, Is.EqualTo(6.5f + 50f / 60f).Within(1e-4f));
    }

    [Test]
    public void Advance_ZeroOrNegativeSpeed_PausesClockAndTicks()
    {
        var clock = CreateRecalculatedClock();
        clock.Speed = -3f;

        var ticks = clock.Advance(100f);

        Assert.That(clock.Speed, Is.EqualTo(0f));
        Assert.That(clock.Hour, Is.EqualTo(6.5f));
        Assert.That(ticks, Is.EqualTo(SimulationTicks.None));
    }

    [Test]
    public void Advance_PastMidnight_RollsOverToNextDay()
    {
        var clock = CreateRecalculatedClock(startHour: 23.9f);

        clock.Advance(12f); // +0.2 h

        Assert.That(clock.Day, Is.EqualTo(2));
        Assert.That(clock.Hour, Is.EqualTo(0.1f).Within(1e-4f));
    }

    [Test]
    public void Advance_LargeDeltaSpanningDays_RollsOverMultipleDays()
    {
        var clock = CreateRecalculatedClock(startHour: 0f);

        clock.Advance(60f * 50f); // 50 game hours

        Assert.That(clock.Day, Is.EqualTo(3));
        Assert.That(clock.Hour, Is.EqualTo(2f).Within(1e-3f));
    }

    [Test]
    public void Advance_NeverRecalculated_MacroTickDueImmediately()
    {
        var clock = new SimulationClock();

        var ticks = clock.Advance(FrameDelta);

        Assert.That(ticks.HasFlag(SimulationTicks.MacroRecalculation), Is.True);
    }

    [Test]
    public void Advance_MacroTick_FiresEveryThirtyGameMinutesAfterMark()
    {
        var clock = CreateRecalculatedClock();

        Assert.That(clock.Advance(29f).HasFlag(SimulationTicks.MacroRecalculation), Is.False);
        Assert.That(clock.Advance(1f).HasFlag(SimulationTicks.MacroRecalculation), Is.True);
    }

    [Test]
    public void Advance_MacroTickNotMarked_StaysDueUntilMarked()
    {
        var clock = CreateRecalculatedClock();
        clock.Advance(30f);

        Assert.That(clock.Advance(FrameDelta).HasFlag(SimulationTicks.MacroRecalculation), Is.True);

        clock.MarkMacroRecalculated();
        Assert.That(clock.Advance(FrameDelta).HasFlag(SimulationTicks.MacroRecalculation), Is.False);
    }

    [Test]
    public void Advance_MacroTickAcrossMidnight_FiresOnSchedule()
    {
        var clock = CreateRecalculatedClock(startHour: 23.8f);

        Assert.That(clock.Advance(12f).HasFlag(SimulationTicks.MacroRecalculation), Is.False, "0.2 h after mark");
        Assert.That(clock.Advance(18f).HasFlag(SimulationTicks.MacroRecalculation), Is.True, "0.5 h after mark, past midnight");
    }

    [Test]
    public void Advance_FinancialTick_FiresHourlyAndResetsItself()
    {
        var clock = CreateRecalculatedClock();

        Assert.That(clock.Advance(59f).HasFlag(SimulationTicks.Financial), Is.False);
        Assert.That(clock.Advance(1f).HasFlag(SimulationTicks.Financial), Is.True);
        Assert.That(clock.Advance(FrameDelta).HasFlag(SimulationTicks.Financial), Is.False, "no double fire");
        Assert.That(clock.Advance(60f).HasFlag(SimulationTicks.Financial), Is.True);
    }

    [Test]
    public void Advance_FinancialTickAcrossMidnight_FiresOnSchedule()
    {
        var clock = CreateRecalculatedClock(startHour: 23.5f);

        Assert.That(clock.Advance(30f).HasFlag(SimulationTicks.Financial), Is.False);
        Assert.That(clock.Advance(30f).HasFlag(SimulationTicks.Financial), Is.True);
        Assert.That(clock.Day, Is.EqualTo(2));
    }

    [Test]
    public void Advance_NormalSpeedFrames_GrowthFiresOncePerScaledSecond()
    {
        var clock = CreateRecalculatedClock();
        int growthTicks = 0;

        for (int frame = 0; frame < 600; frame++) // 10 real seconds at 60 fps
        {
            if (clock.Advance(FrameDelta).HasFlag(SimulationTicks.Growth)) growthTicks++;
        }

        Assert.That(growthTicks, Is.InRange(9, 10));
    }

    [Test]
    public void Advance_HighSpeedLongFrame_GrowthFiresAtMostOncePerAdvance()
    {
        var clock = CreateRecalculatedClock();
        clock.Speed = 50f;

        var ticks = clock.Advance(0.1f); // 5 scaled seconds in one frame

        Assert.That(ticks.HasFlag(SimulationTicks.Growth), Is.True);
        Assert.That(clock.Advance(0f).HasFlag(SimulationTicks.Growth), Is.True, "backlog is drained one step per frame");
    }
}
