using System;
using ModernUO.Serialization;
using Server.Items;
using Server.Mobiles;

namespace Server.Custom;

/// <summary>
///     A drinker who shows up at the tavern after dark and leaves at dawn.
///     <para>
///         Deliberately ephemeral: patrons are created at dusk and deleted at dawn, so nothing about
///         them needs persisting. If a world save catches them mid-evening they delete themselves on
///         the next load and the scheduler recreates them if the phase still calls for it - which
///         also means a restart can never leave a crowd of orphans behind.
///     </para>
/// </summary>
[SerializationGenerator(0)]
public partial class TavernPatron : BaseCreature
{
    private static readonly TimeSpan MinChatterDelay = TimeSpan.FromSeconds(25.0);
    private static readonly TimeSpan MaxChatterDelay = TimeSpan.FromSeconds(60.0);

    private long _nextChatter;

    [Constructible]
    public TavernPatron() : base(AIType.AI_Vendor, FightMode.None, 2)
    {
        Race = Race.Human;
        Female = Utility.RandomBool();
        Body = Female ? 0x191 : 0x190;
        Hue = Race.RandomSkinHue();
        Title = "the patron";

        SetSpeed(0.5, 2.0);
        InitStats(60, 60, 25);

        Utility.AssignRandomHair(this);

        AddItem(new Backpack());
        AddItem(new Shoes(Utility.RandomNeutralHue()));

        if (Female)
        {
            AddItem(new Skirt(Utility.RandomNeutralHue()));
            AddItem(new FancyShirt(Utility.RandomNeutralHue()));
        }
        else
        {
            AddItem(new LongPants(Utility.RandomNeutralHue()));
            AddItem(new Shirt(Utility.RandomNeutralHue()));
        }

        ScheduleNextChatter();
    }

    public override bool IsInvulnerable => true;

    public override string DefaultName => Female ? NameList.RandomName("female") : NameList.RandomName("male");

    /// <summary>
    ///     Patrons stay put in the tavern; RangeHome is set to a small wander radius by the system
    ///     that places them.
    /// </summary>
    public override bool ClickTitle => false;

    /// <summary>
    ///     OnThink fires at least once per think cadence but can fire more often - a player command
    ///     wakes the AI immediately. Gate the chatter on its own deadline so extra calls never buy
    ///     an extra line, and compare in subtraction form for wraparound safety.
    /// </summary>
    public override void OnThink()
    {
        base.OnThink();

        if (Core.TickCount - _nextChatter < 0)
        {
            return;
        }

        ScheduleNextChatter();
        Chatter();
    }

    private void ScheduleNextChatter() =>
        _nextChatter = Core.TickCount + (int)Utility
            .RandomMinMax((int)MinChatterDelay.TotalMilliseconds, (int)MaxChatterDelay.TotalMilliseconds);

    private void Chatter()
    {
        var lines = TownScheduleConfig.Current?.Tavern?.Chatter;

        if (lines == null || lines.Count == 0)
        {
            return;
        }

        Say(lines[Utility.Random(lines.Count)]);
    }

    /// <summary>
    ///     Clean up after a restart. Patrons carry no state worth restoring, and the scheduler will
    ///     put out a fresh crowd if it is still dark.
    /// </summary>
    [AfterDeserialization(false)]
    private void AfterDeserialization() => Timer.DelayCall(Delete);
}
