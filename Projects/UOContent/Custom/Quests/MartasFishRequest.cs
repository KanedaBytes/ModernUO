using Server.Engines.MLQuests;
using Server.Engines.MLQuests.Objectives;
using Server.Engines.MLQuests.Rewards;
using Server.Items;

namespace Server.Custom;

/// <summary>
///     Old Marta's fetch quest: bring her 5 fish for a small purse of gold.
///     Registered against <see cref="OldMarta" /> by <see cref="CustomQuestRegistry" />.
/// </summary>
public class MartasFishRequest : MLQuest
{
    public MartasFishRequest()
    {
        Activated = true;

        // Records the completion on the player so CanOffer can refuse a second run.
        OneTimeOnly = true;

        // TextDefinition converts implicitly from a cliloc number or a plain string.
        // Upstream quests use clilocs; custom text has none, so we pass strings.
        Title = "A Basket of Fish";

        Description =
            "Ah, a kind face at last. These old hands aren't what they were, and the walk to the docks is longer every year. " +
            "Could you spare an afternoon and bring me five fish? I'll not ask you to work for nothing - there's coin in it for you.";

        RefusalMessage = "No matter, dear. The river will still be there tomorrow, and so will I.";

        InProgressMessage = "Five fish is all I ask. Any honest fish will do - the docks south of here are the place for it.";

        CompletionMessage = "Bless you, child. Here, take this for your trouble - and mind you don't spend it all at the tavern.";

        Objectives.Add(new CollectObjective(5, typeof(Fish), "fish"));

        Rewards.Add(new ItemReward("250 gold", typeof(Gold), 250));
    }
}
