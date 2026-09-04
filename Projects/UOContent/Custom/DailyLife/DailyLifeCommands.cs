using Server.Commands;
using Server.Logging;

namespace Server.Custom;

/// <summary>
///     Staff command to re-read <c>britain-daily-life.json</c> and rebuild the town without a
///     restart, so coordinates can be tuned by watching rather than by guessing.
/// </summary>
public static class DailyLifeCommands
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(DailyLifeCommands));

    public static void Configure()
    {
        CommandSystem.Register("DailyLifeReload", AccessLevel.GameMaster, DailyLifeReload_OnCommand);
    }

    [Usage("DailyLifeReload")]
    [Description("Re-reads the daily life config and rebuilds the tavern, watch, townsfolk and shops.")]
    public static void DailyLifeReload_OnCommand(CommandEventArgs e)
    {
        var from = e.Mobile;

        if (!TryReload(out var error))
        {
            // The previous config is still live - nothing was torn down.
            from.SendMessage(0x35, $"Daily life config NOT reloaded: {error}");
            from.SendMessage(0x35, "The town is still running on the previously loaded config.");
            return;
        }

        from.SendMessage("Daily life config reloaded.");
        from.SendMessage(
            $"Tavern patrons: {TavernSystem.PatronCount}. Night watch: {NightWatchSystem.WatchCount}. Route walkers: {RoutedTownsfolkSystem.WalkerCount}."
        );

        CommandLogging.WriteLine(
            from,
            $"{from.AccessLevel} {CommandLogging.Format(from)} reloading the daily life config"
        );
    }

    /// <summary>
    ///     Re-reads the config and, if it is valid, rebuilds the town. The single entry point for
    ///     both the staff command and the admin API, so a bad config is reported the same way
    ///     whichever asked for the reload.
    ///     <para>
    ///         A failure is announced to every staff member online, not just the caller. An invalid
    ///         config is a builder mistake whose only other symptom is a town that quietly stops
    ///         filling up, which is exactly the kind of thing nobody notices for a week.
    ///     </para>
    /// </summary>
    public static bool TryReload(out string error)
    {
        if (!TownScheduleConfig.TryLoad(out error))
        {
            World.BroadcastStaff($"Daily life config NOT reloaded: {error}");
            return false;
        }

        Reload();
        return true;
    }

    /// <summary>
    ///     Rebuilds every daily-life system from the config currently in memory.
    ///     <para>
    ///         Order matters only in that the shop district region should be registered before the
    ///         shop schedule re-applies, so vendors are placed under the right region. Note that
    ///         neither the day-cycle poll nor the shop drive timer is re-armed here - both are
    ///         started once at boot, and re-arming them would double them up.
    ///     </para>
    /// </summary>
    public static void Reload()
    {
        TavernSystem.Reload();
        NightWatchSystem.Reload();
        RoutedTownsfolkSystem.Reload();
        ShopDistrictRegion.Initialize(); // already unregisters the previous instance
        ShopScheduleSystem.Reload();

        logger.Information("Daily life systems reloaded");
    }
}
