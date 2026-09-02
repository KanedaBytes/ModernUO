using ModernUO.Serialization;
using Server.Engines.MLQuests;
using Server.Items;
using Server.Mobiles;

namespace Server.Custom;

/// <summary>
///     A fishwife in Britain who greets passers-by, answers "help", and offers
///     <see cref="MartasFishRequest" />.
///     <para>
///         She contains no quest code: <see cref="BaseCreature" /> already implements
///         <c>IQuestGiver</c>, so the offer gump, the "Quest Giver" tooltip and all progress tracking
///         come from the registration in <see cref="CustomQuestRegistry" />.
///     </para>
/// </summary>
[QuesterName("Old Marta (Britain)")]
[SerializationGenerator(0)]
public partial class OldMarta : BaseCreature
{
    [Constructible]
    public OldMarta() : base(AIType.AI_Vendor, FightMode.None, 2)
    {
        Title = "the fishwife";
        Race = Race.Human;
        Body = 0x191;
        Female = true;
        Hue = Race.RandomSkinHue();

        SetSpeed(0.5, 2.0);
        InitStats(100, 100, 25);

        Utility.AssignRandomHair(this);

        AddItem(new Backpack());
        AddItem(new Shoes(0x74A));
        AddItem(new Skirt(0x8AB));
        AddItem(new FancyShirt(0x483));
    }

    public override bool IsInvulnerable => true;
    public override string DefaultName => "Old Marta";

    // BaseCreature.OnMovement calls Shout() when a player walks into ShoutRange and a starter
    // quest is available; it handles the range, line-of-sight and cooldown itself.
    public override bool CanShout => true;

    public override void Shout(PlayerMobile pm)
    {
        MLQuestSystem.Tell(
            this,
            pm,
            Utility.RandomList(
                "Good day to you! Have you a moment for an old woman?",
                "You there - you've the look of someone who isn't afraid of a bit of water.",
                "Ah, a traveller. Come closer, I'll not bite."
            )
        );
    }

    // Opt in to hearing speech. This only narrows the engine's 15-tile broadcast; it cannot widen it.
    public override bool HandlesOnSpeech(Mobile from) => from.InRange(Location, 3) || base.HandlesOnSpeech(from);

    public override void OnSpeech(SpeechEventArgs e)
    {
        // Re-check the range: HandlesOnSpeech only decides who joins the listener list, and the
        // engine applies no line-of-sight test to non-player listeners.
        if (!e.Handled && e.Mobile.InRange(Location, 3) && e.Speech.InsensitiveContains("help"))
        {
            // Client-side speech keywords are unreliable (empty on ASCII clients, and there is no
            // stock "*help*" id), so match the text directly.
            SayTo(e.Mobile, "Help, is it? Double-click me and I'll tell you what I need. It's fish, mostly.");
            e.Handled = true;
        }

        // Let BaseCreature forward to the AI - swallowing this breaks vendor and pet speech.
        base.OnSpeech(e);
    }
}
