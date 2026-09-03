namespace Server.Custom;

/// <summary>
///     The four parts of a game day that town schedules key off.
/// </summary>
public enum DayPhase
{
    Night,
    Dawn,
    Day,
    Dusk
}

public static class DayPhaseExtensions
{
    /// <summary>
    ///     Maps an in-game hour to a phase.
    ///     <para>
    ///         The boundaries deliberately match <c>LightCycle.ComputeLevelFor</c> exactly - night
    ///         below 4, a dawn ramp to 6, day to 22, then a dusk ramp - so NPC behaviour and the
    ///         light level players actually see always agree. Change one and you must change both.
    ///     </para>
    /// </summary>
    public static DayPhase FromHour(int hour)
    {
        // Clock hours can go negative if the host clock is set before the 1997 epoch: C# '%' keeps
        // the sign, so normalize rather than trusting the input.
        hour = (hour % 24 + 24) % 24;

        return hour switch
        {
            < 4  => DayPhase.Night,
            < 6  => DayPhase.Dawn,
            < 22 => DayPhase.Day,
            _    => DayPhase.Dusk
        };
    }

    /// <summary>
    ///     True when shops are shut and the night crowd is out - dusk and night.
    /// </summary>
    public static bool IsAfterDark(this DayPhase phase) => phase is DayPhase.Dusk or DayPhase.Night;

    public static string ToFriendlyString(this DayPhase phase) =>
        phase switch
        {
            DayPhase.Night => "night",
            DayPhase.Dawn  => "dawn",
            DayPhase.Day   => "day",
            DayPhase.Dusk  => "dusk",
            _              => "unknown"
        };
}
