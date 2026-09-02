using System;
using Server.Engines.MLQuests;
using Server.Engines.MLQuests.Objectives;
using Server.Gumps;
using Server.Mobiles;

namespace Server.Custom;

/// <summary>
///     Wraps an upstream <see cref="CollectObjective" /> so that matching items in the top level of
///     the player's backpack count automatically, without the "Toggle Quest Item" dance.
///     <para>
///         This <b>wraps</b> rather than replaces so that awkward cases keep working: the engine's own
///         <c>TimedCollectObjective</c>, and the private nested <c>CollectObjective</c> subclasses in
///         the quest definitions that override <see cref="CollectObjective.CheckItem" />. Those types
///         cannot be reconstructed from outside the engine, but every behaviour that matters is
///         <c>public virtual</c>, so it can simply be delegated to the original instance.
///     </para>
///     <para>
///         Installed over every registered quest by <see cref="AutoCollectInstaller" />.
///     </para>
/// </summary>
public class AutoCollectObjective : CollectObjective
{
    private readonly CollectObjective _inner;

    public AutoCollectObjective(CollectObjective inner)
        : base(inner.DesiredAmount, inner.AcceptedType, inner.Name) => _inner = inner;

    // Null-guarded: the base constructor reads ShowDetailed before _inner is assigned.
    public override bool ShowDetailed => _inner?.ShowDetailed ?? true;

    public override bool CheckItem(Item item) => _inner.CheckItem(item);

    public override bool IsTimed => _inner.IsTimed;

    public override TimeSpan Duration => _inner.Duration;

    public override bool CanOffer(IQuestGiver quester, PlayerMobile pm, bool message) =>
        _inner.CanOffer(quester, pm, message);

    public override void WriteToGump(ref DynamicGumpBuilder builder, ref int y) =>
        _inner.WriteToGump(ref builder, ref y);

    public override BaseObjectiveInstance CreateInstance(MLQuestInstance instance) =>
        new AutoCollectObjectiveInstance(this, instance);
}

/// <summary>
///     The counting half. <c>CollectObjectiveInstance.GetCurrentTotal()</c> and
///     <c>ClaimTypePredicate()</c> are private and non-virtual, so the backpack walk is
///     re-implemented here using only public API. It is reached solely from
///     <see cref="IsCompleted" /> and <see cref="WriteToGump" />, both of which are overridden, so the
///     private original is never invoked.
///     <para>
///         Do not override <c>Serialize</c> or <c>ExtraDataType</c>:
///         <c>BaseObjectiveInstance.Deserialize</c> is static with a closed switch over a fixed enum,
///         so extra fields could never be read back and would desync the save stream. Nothing needs
///         storing - collect state is derived from the backpack, and the instance is rebuilt
///         polymorphically from the quest definition on world load.
///     </para>
/// </summary>
public class AutoCollectObjectiveInstance : CollectObjectiveInstance
{
    public AutoCollectObjectiveInstance(AutoCollectObjective objective, MLQuestInstance instance)
        : base(objective, instance)
    {
    }

    private bool IsAcceptedType(Item item) => Objective.AcceptedType.IsInstanceOfType(item);

    // The manual flag still counts, so a player can deliberately hand in something the safety
    // filter rejects (a runic bow, a dyed shirt) by toggling it the old way.
    private bool Counts(Item item) =>
        (item.QuestItem || QuestItemSafety.CanAutoCount(item)) && Objective.CheckItem(item);

    private int GetCurrentTotal()
    {
        var pack = Instance.Player.Backpack;

        if (pack == null)
        {
            return 0;
        }

        var total = 0;

        // false = top level only. Anything inside a sub-container is deliberately invisible to the
        // quest, which gives players a safe place to stash items they do not want consumed.
        foreach (var item in pack.FindItems(false))
        {
            if (IsAcceptedType(item) && Counts(item))
            {
                total += item.Amount;
            }
        }

        return total;
    }

    public override bool IsCompleted() => GetCurrentTotal() >= Objective.DesiredAmount;

    // Should only be called after IsCompleted() is checked to be true
    public override void OnClaimReward()
    {
        var pack = Instance.Player.Backpack;

        if (pack == null)
        {
            return;
        }

        var left = Objective.DesiredAmount;

        using var queue = pack.EnumerateItemsByType<Item>(false, IsAcceptedType);
        foreach (var item in queue)
        {
            if (left == 0)
            {
                return;
            }

            if (!Counts(item))
            {
                continue;
            }

            // An oversized stack loses only what the objective asked for.
            if (item.Amount > left)
            {
                item.Consume(left);
                left = 0;
            }
            else
            {
                item.Delete();
                left -= item.Amount;
            }
        }
    }

    public override void WriteToGump(ref DynamicGumpBuilder builder, ref int y)
    {
        Objective.WriteToGump(ref builder, ref y);
        y -= 16;

        if (!Objective.ShowDetailed)
        {
            return;
        }

        // Replicates BaseObjectiveInstance.WriteToGump. C# cannot skip a level to reach it, and
        // calling base here would run CollectObjectiveInstance's version, which uses the private
        // QuestItem-filtered total.
        if (IsTimed)
        {
            WriteTimeRemaining(ref builder, ref y, Utility.Max(EndTime - Core.Now, TimeSpan.Zero));
        }

        builder.AddHtmlLocalized(103, y, 120, 16, 3000087, 0x5F90); // Total
        builder.AddLabel(223, y, 0x481, $"{GetCurrentTotal()}");
        y += 16;

        builder.AddHtmlLocalized(103, y, 120, 16, 1074782, 0x5F90); // Return to
        builder.AddLabel(223, y, 0x481, QuesterNameAttribute.GetQuesterNameFor(Instance.QuesterType));
        y += 16;
    }
}
