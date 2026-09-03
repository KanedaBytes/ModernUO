using System;
using ModernUO.CodeGeneratedEvents;
using Server.Items;
using Server.Logging;

namespace Server.Custom;

/// <summary>
///     Shard-level day schedule. Polls the game clock, works out which phase the town is in, and
///     raises an event when it changes. Other systems subscribe rather than polling themselves.
///     <para>
///         In-game time is a pure function of <c>DateTime.UtcNow</c> and a fixed 1997 epoch:
///         5 real seconds per UO minute, so a UO hour is 5 real minutes and a UO day is 2 real
///         hours. Nothing here is persisted - the phase is always recomputed from the clock.
///     </para>
/// </summary>
public static partial class DayCycleSystem
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(DayCycleSystem));

    /// <summary>
    ///     Mirrors LightCycle's own 5-second poll, which is exactly one UO minute. The dawn and dusk
    ///     ramps are only 10 real minutes each, so this is fine-grained enough to land inside them.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5.0);

    private static DayPhase _current = DayPhase.Day;
    private static bool _started;

    private static DayPhase? _override;

    /// <summary>
    ///     Raised when the phase changes, including when a staff override forces one. Subscribe with
    ///     <c>[OnEvent(nameof(DayCycleSystem.DayPhaseChangedEvent))]</c>.
    /// </summary>
    [GeneratedEvent(nameof(DayPhaseChangedEvent))]
    public static partial void DayPhaseChangedEvent(DayPhase oldPhase, DayPhase newPhase);

    public static DayPhase Current => _current;

    public static bool IsOverridden => _override != null;

    /// <summary>
    ///     CallPriority 10 (default is 50, lower runs first) so the phase is settled before the
    ///     tavern, watch, townsfolk and shop systems read <see cref="Current" /> in their own
    ///     Initialize.
    /// </summary>
    [CallPriority(10)]
    public static void Initialize()
    {
        // Initialize rather than Configure: the town config and the world must both be up before
        // the first phase is applied.
        _current = ComputePhase();
        _started = true;

        logger.Information("Day cycle started in {Phase} phase", _current.ToFriendlyString());

        Timer.DelayCall(PollInterval, PollInterval, 0, Poll);
    }

    /// <summary>
    ///     The in-game hour at the town anchor.
    ///     <para>
    ///         Clock.GetTime adds <c>map.MapIndex * 320</c> minutes per facet and <c>x / 16</c>
    ///         minutes for longitude, so "the hour" differs by over seven hours across a single
    ///         map. Sampling at the town's own anchor is what makes the schedule agree with the
    ///         light level players standing there actually see.
    ///     </para>
    /// </summary>
    public static int GetAnchorHour()
    {
        var anchor = TownScheduleConfig.Current?.Anchor;

        if (anchor == null)
        {
            Clock.GetTime(null, 0, 0, out var fallbackHours, out int _);
            return fallbackHours;
        }

        Clock.GetTime(anchor.GetMap(), anchor.X, anchor.Y, out var hours, out int _);

        return hours;
    }

    public static int GetAnchorMinute()
    {
        var anchor = TownScheduleConfig.Current?.Anchor;

        if (anchor == null)
        {
            Clock.GetTime(null, 0, 0, out _, out int fallbackMinutes);
            return fallbackMinutes;
        }

        Clock.GetTime(anchor.GetMap(), anchor.X, anchor.Y, out _, out int minutes);

        return minutes;
    }

    private static DayPhase ComputePhase() => _override ?? DayPhaseExtensions.FromHour(GetAnchorHour());

    private static void Poll()
    {
        if (!_started)
        {
            return;
        }

        // Always recompute and compare. Never advance by increment: Core.Now is DateTime.UtcNow, so
        // an NTP step moves in-game time by 12 minutes per real minute and can run backwards, and
        // downtime is not paused - a three-hour outage advances the world a day and a half.
        var phase = ComputePhase();

        if (phase == _current)
        {
            return;
        }

        var old = _current;
        _current = phase;

        logger.Information("Day phase {Old} -> {New}", old.ToFriendlyString(), phase.ToFriendlyString());

        DayPhaseChangedEvent(old, phase);
    }

    /// <summary>
    ///     Forces a phase for testing. The game clock cannot be set - it is derived from UTC - so a
    ///     forced phase is the only way to watch a full cycle without waiting two real hours.
    ///     Also pins the global light level so the sky matches what the NPCs are doing.
    /// </summary>
    public static void SetOverride(DayPhase phase)
    {
        _override = phase;
        LightCycle.LevelOverride = GetLightLevelFor(phase);
        Poll();
    }

    public static void ClearOverride()
    {
        _override = null;
        LightCycle.LevelOverride = int.MinValue;
        Poll();
    }

    /// <summary>
    ///     The steady-state light level for a phase. The ramps are mid-way values, since an override
    ///     freezes time rather than animating through the ramp.
    /// </summary>
    private static int GetLightLevelFor(DayPhase phase) =>
        phase switch
        {
            DayPhase.Night => LightCycle.NightLevel,
            DayPhase.Dawn  => LightCycle.NightLevel / 2,
            DayPhase.Day   => LightCycle.DayLevel,
            DayPhase.Dusk  => LightCycle.NightLevel / 2,
            _              => LightCycle.DayLevel
        };
}
