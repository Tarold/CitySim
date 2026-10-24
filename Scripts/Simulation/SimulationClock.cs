using System;

namespace CitySim.Simulation;

/// <summary>
/// Simulation ticks that came due during a <see cref="SimulationClock.Advance"/> call.
/// </summary>
[Flags]
public enum SimulationTicks
{
    None = 0,

    /// <summary>Macro model (TAZ → OD → MSA) recalculation is due.</summary>
    MacroRecalculation = 1 << 0,

    /// <summary>Hourly municipal budget tick is due.</summary>
    Financial = 1 << 1,

    /// <summary>LUTI population growth step (roughly every in-game minute) is due.</summary>
    Growth = 1 << 2
}

/// <summary>
/// In-game calendar and tick scheduler. Advances the game hour from real frame time scaled by
/// <see cref="Speed"/>, handles day rollover, and reports which periodic simulation steps are due.
/// Each step fires at most once per <see cref="Advance"/> call.
/// </summary>
public sealed class SimulationClock
{
    /// <summary>Default start time: 06:30, just before the morning peak.</summary>
    public const float DefaultStartHour = 6.5f;

    public const float HoursPerDay = 24f;

    /// <summary>At 1× speed one real second is one game minute (a full day takes 24 real minutes).</summary>
    public const float GameHoursPerRealSecond = 1f / 60f;

    /// <summary>Macro model is recalculated every 30 game minutes.</summary>
    public const float MacroRecalculationIntervalHours = 0.5f;

    /// <summary>Budget is settled every game hour.</summary>
    public const float FinancialTickIntervalHours = 1f;

    /// <summary>Growth accumulates speed-scaled real seconds and fires once per accumulated unit.</summary>
    public const float GrowthTickIntervalScaledSeconds = 1f;

    /// <summary>Initial "last recalculation" hour that makes the first macro tick due immediately.</summary>
    private const float NeverRecalculatedHour = -10f;

    private float _lastMacroRecalculationHour = NeverRecalculatedHour;
    private float _lastFinancialTickHour;
    private float _growthAccumulator;
    private float _speed = 1f;

    public SimulationClock(float startHour = DefaultStartHour, int startDay = 1)
    {
        Hour = startHour;
        Day = startDay;
        _lastFinancialTickHour = startHour;
    }

    /// <summary>Current in-game hour in [0, 24).</summary>
    public float Hour { get; private set; }

    /// <summary>Current in-game day, starting at 1.</summary>
    public int Day { get; private set; }

    /// <summary>Game speed multiplier (0 pauses the clock). Negative values are clamped to 0.</summary>
    public float Speed
    {
        get => _speed;
        set => _speed = Math.Max(0f, value);
    }

    /// <summary>
    /// Advances the clock by one frame of real time and returns the steps that came due.
    /// The macro tick stays due until <see cref="MarkMacroRecalculated"/> is called, because edits
    /// also trigger recalculations that reset its timer.
    /// </summary>
    public SimulationTicks Advance(float realDeltaSeconds)
    {
        float scaledSeconds = realDeltaSeconds * _speed;
        Hour += scaledSeconds * GameHoursPerRealSecond;

        while (Hour >= HoursPerDay)
        {
            Hour -= HoursPerDay;
            Day++;
            _lastMacroRecalculationHour -= HoursPerDay;
            _lastFinancialTickHour -= HoursPerDay;
        }

        var ticks = SimulationTicks.None;

        if (Math.Abs(Hour - _lastMacroRecalculationHour) >= MacroRecalculationIntervalHours)
        {
            ticks |= SimulationTicks.MacroRecalculation;
        }

        if (Math.Abs(Hour - _lastFinancialTickHour) >= FinancialTickIntervalHours)
        {
            ticks |= SimulationTicks.Financial;
            _lastFinancialTickHour = Hour;
        }

        _growthAccumulator += scaledSeconds;
        if (_growthAccumulator >= GrowthTickIntervalScaledSeconds)
        {
            _growthAccumulator -= GrowthTickIntervalScaledSeconds;
            ticks |= SimulationTicks.Growth;
        }

        return ticks;
    }

    /// <summary>Records that the macro model was recalculated at the current hour.</summary>
    public void MarkMacroRecalculated() => _lastMacroRecalculationHour = Hour;
}
