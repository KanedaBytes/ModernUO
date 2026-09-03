using System;
using Server.Commands;

namespace Server.Custom;

/// <summary>
///     Staff control over the day cycle.
///     <para>
///         The game clock cannot be set - it is a pure function of UTC and a fixed 1997 epoch - so
///         the testing lever is a forced phase, modelled on <c>LightCycle.LevelOverride</c>. Without
///         it, watching a full cycle means waiting two real hours.
///     </para>
/// </summary>
public static class DayCycleCommands
{
    public static void Configure()
    {
        CommandSystem.Register("DayPhase", AccessLevel.GameMaster, DayPhase_OnCommand);
    }

    [Usage("DayPhase [dawn | day | dusk | night | clear]")]
    [Description("Reports the current day phase, or forces one for testing.")]
    public static void DayPhase_OnCommand(CommandEventArgs e)
    {
        var from = e.Mobile;

        if (e.Length == 0)
        {
            Report(from);
            return;
        }

        var arg = e.GetString(0);

        if (arg.InsensitiveEquals("clear"))
        {
            DayCycleSystem.ClearOverride();
            from.SendMessage("Day phase override cleared; following the game clock again.");
            Report(from);
            return;
        }

        if (!Enum.TryParse<DayPhase>(arg, true, out var phase))
        {
            from.SendMessage("Usage: [DayPhase [dawn | day | dusk | night | clear]");
            return;
        }

        DayCycleSystem.SetOverride(phase);
        from.SendMessage($"Day phase forced to {phase.ToFriendlyString()}. Use [DayPhase clear to release it.");
    }

    private static void Report(Mobile from)
    {
        var hour = DayCycleSystem.GetAnchorHour();
        var minute = DayCycleSystem.GetAnchorMinute();

        from.SendMessage(
            $"It is {DayCycleSystem.Current.ToFriendlyString()} - {hour:D2}:{minute:D2} in game at the town anchor."
        );

        if (DayCycleSystem.IsOverridden)
        {
            from.SendMessage(0x35, "A staff override is active. [DayPhase clear to release it.");
        }
    }
}
