using Server.Engines.MLQuests;
using Server.Engines.MLQuests.Objectives;
using Server.Logging;

namespace Server.Custom;

/// <summary>
///     Replaces every registered <see cref="CollectObjective" /> with an
///     <see cref="AutoCollectObjective" /> wrapper, so auto-counting applies to all ML collect quests
///     - the ~100 upstream ones included - without editing a single quest definition.
/// </summary>
public static class AutoCollectInstaller
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(AutoCollectInstaller));

    /// <summary>
    ///     <c>CallPriority</c> defaults to 50 and lower runs first, so 100 guarantees this runs after
    ///     every other <c>Configure()</c> - including <see cref="CustomQuestRegistry" />, so this
    ///     shard's own quests are wrapped too.
    ///     <para>
    ///         Reading <see cref="MLQuestSystem.Quests" /> triggers the static constructor that loads
    ///         <c>MLQuests.cfg</c>, so the upstream registry is fully populated by this point. All
    ///         <c>Configure()</c> methods run before <c>World.Load()</c>, so no
    ///         <see cref="MLQuestInstance" /> exists yet and the swap is invisible downstream.
    ///     </para>
    /// </summary>
    [CallPriority(100)]
    public static void Configure()
    {
        var wrapped = 0;

        foreach (var quest in MLQuestSystem.Quests.Values)
        {
            var objectives = quest.Objectives;

            for (var i = 0; i < objectives.Count; i++)
            {
                if (objectives[i] is CollectObjective collect and not AutoCollectObjective)
                {
                    objectives[i] = new AutoCollectObjective(collect);
                    wrapped++;
                }
            }
        }

        logger.Information("Auto-counting enabled for {Count} collect objectives", wrapped);
    }
}
