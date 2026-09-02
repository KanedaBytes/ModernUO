using System;
using Server.Commands;
using Server.Engines.MLQuests;
using Server.Gumps;
using Server.Mobiles;
using Server.Targeting;

namespace Server.Custom;

/// <summary>
///     Staff tooling for the ML quest system.
///     <para>
///         A player's quest state lives entirely on a per-player <see cref="MLQuestContext" />: the
///         completed list, the in-progress <c>QuestInstances</c>, and any pending <c>ChainOffers</c>.
///         Once a <c>OneTimeOnly</c> quest is recorded as done, nothing in the game can offer it
///         again - these commands are the way back.
///     </para>
/// </summary>
public static class ResetQuestCommands
{
    public static void Configure()
    {
        CommandSystem.Register("ResetQuest", AccessLevel.GameMaster, ResetQuest_OnCommand);
        CommandSystem.Register("ResetAllQuests", AccessLevel.GameMaster, ResetAllQuests_OnCommand);
    }

    [Usage("ResetQuest <QuestTypeName>")]
    [Description("Targets a player and clears one quest, so it can be offered to them again.")]
    public static void ResetQuest_OnCommand(CommandEventArgs e)
    {
        var from = e.Mobile;

        if (e.Length != 1)
        {
            from.SendMessage("Usage: [ResetQuest <QuestTypeName>");
            return;
        }

        var name = e.GetString(0);

        // FindTypeByName matches short names only and does not fall back, so try both forms.
        var type = AssemblyHandler.FindTypeByName(name) ?? AssemblyHandler.FindTypeByFullName(name);

        // The registry lookup is also what protects us from a short name that collided with some
        // unrelated type: a wrong resolution fails here rather than acting on the wrong thing.
        if (type == null || !MLQuestSystem.Quests.TryGetValue(type, out var quest))
        {
            from.SendMessage("Invalid quest type name.");
            return;
        }

        from.SendMessage($"Select the player whose '{type.Name}' progress should be reset.");
        from.Target = new ResetQuestTarget(quest);
    }

    [Usage("ResetAllQuests")]
    [Description("Targets a player and erases all of their ML quest progress, after confirmation.")]
    public static void ResetAllQuests_OnCommand(CommandEventArgs e)
    {
        e.Mobile.SendMessage("Select the player whose quest progress should be erased.");
        e.Mobile.Target = new ResetAllQuestsTarget();
    }

    private class ResetQuestTarget : Target
    {
        private readonly MLQuest _quest;

        public ResetQuestTarget(MLQuest quest) : base(-1, false, TargetFlags.None) => _quest = quest;

        protected override void OnTarget(Mobile from, object targeted)
        {
            if (targeted is not PlayerMobile pm)
            {
                from.SendMessage("That is not a player.");
                return;
            }

            var questName = _quest.GetType().Name;

            // Null when the player has never interacted with the quest system at all.
            var context = MLQuestSystem.GetContext(pm);

            if (context == null)
            {
                from.SendMessage($"{pm.Name} has no quest progress to reset.");
                return;
            }

            // Cancel(true) rather than Remove(): it runs OnQuestCancelled on each objective, which
            // un-marks the player's quest items, and drops the matching chain offer.
            var instance = context.FindInstance(_quest);
            var wasInProgress = instance != null;

            instance?.Cancel(true);

            // Covers a pending chain offer with no live instance behind it.
            var hadChainOffer = context.ChainOffers.Remove(_quest);

            var wasCompleted = context.HasDoneQuest(_quest);
            context.RemoveDoneQuest(_quest);

            if (!wasInProgress && !hadChainOffer && !wasCompleted)
            {
                from.SendMessage($"{pm.Name} has no progress on '{questName}'.");
                return;
            }

            from.SendMessage(
                $"Reset '{questName}' for {pm.Name}. In progress: {wasInProgress}. Completed: {wasCompleted}. Chain offer: {hadChainOffer}."
            );

            CommandLogging.WriteLine(
                from,
                $"{from.AccessLevel} {CommandLogging.Format(from)} resetting quest '{questName}' for {CommandLogging.Format(pm)}"
            );
        }
    }

    private class ResetAllQuestsTarget : Target
    {
        public ResetAllQuestsTarget() : base(-1, false, TargetFlags.None)
        {
        }

        protected override void OnTarget(Mobile from, object targeted)
        {
            if (targeted is not PlayerMobile pm)
            {
                from.SendMessage("That is not a player.");
                return;
            }

            var context = MLQuestSystem.GetContext(pm);

            if (context == null)
            {
                from.SendMessage($"{pm.Name} has no quest progress to erase.");
                return;
            }

            var active = context.QuestInstances.Count;
            var offers = context.ChainOffers.Count;

            from.SendGump(
                new ResetAllQuestsWarningGump(
                    pm.Name,
                    active,
                    offers,
                    okay => OnConfirm(from, okay, pm)
                )
            );
        }

        private static void OnConfirm(Mobile from, bool okay, PlayerMobile pm)
        {
            if (!okay)
            {
                from.SendMessage("Quest reset aborted.");
                return;
            }

            if (pm.Deleted)
            {
                from.SendMessage("That player no longer exists.");
                return;
            }

            // Re-fetch: the staff member may have sat on the gump for a while.
            var context = MLQuestSystem.GetContext(pm);

            if (context == null)
            {
                from.SendMessage($"{pm.Name} has no quest progress to erase.");
                return;
            }

            // Reverse index: Cancel -> Unregister removes the instance from this very list.
            var instances = context.QuestInstances;
            var cancelled = instances.Count;

            for (var i = instances.Count - 1; i >= 0; i--)
            {
                instances[i].Cancel(true);
            }

            // MLQuestContext exposes no way to enumerate the completed list, so walk the registry
            // instead. Every surviving record points at a registered quest - records whose type no
            // longer resolves are dropped at load time by MLDoneQuestInfo.Deserialize.
            foreach (var quest in MLQuestSystem.Quests.Values)
            {
                context.RemoveDoneQuest(quest);
            }

            context.ChainOffers.Clear();

            // Quest-granted abilities (Spellweaving, SummonFey, SummonFiend, BedlamAccess) are
            // deliberately left intact - revoking a castable skill is not what "reset quests" means.

            from.SendMessage($"Erased all quest progress for {pm.Name}. Cancelled {cancelled} active quest(s).");

            CommandLogging.WriteLine(
                from,
                $"{from.AccessLevel} {CommandLogging.Format(from)} erasing ALL quest progress for {CommandLogging.Format(pm)}"
            );
        }
    }

    private class ResetAllQuestsWarningGump : StaticWarningGump<ResetAllQuestsWarningGump>
    {
        public override int Width => 420;
        public override int Height => 280;

        public override string Content { get; }

        public ResetAllQuestsWarningGump(string playerName, int active, int offers, Action<bool> callback)
            : base(callback) =>
            Content =
                $"You are about to erase <em>all</em> ML quest progress for {playerName}.<br><br>" +
                $"This cancels {active} quest(s) currently in progress, clears every completed-quest " +
                $"record, and discards {offers} pending chain offer(s). Quest-granted abilities such as " +
                "Spellweaving are not affected.<br><br>This cannot be undone without a server revert. Continue?";
    }
}
