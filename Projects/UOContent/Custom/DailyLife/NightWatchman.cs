using ModernUO.Serialization;
using Server.Items;

namespace Server.Custom;

/// <summary>
///     A lantern-carrying watchman who patrols after dark.
///     <para>
///         Visual only, as specified - the watch has no guard behaviour. Note that Britain has no
///         spawned day guards to "replace": real town guards are conjured on demand by
///         <c>GuardedRegion.MakeGuard</c> and delete themselves when idle, so this is an addition to
///         the town rather than a swap.
///     </para>
/// </summary>
[SerializationGenerator(0)]
public partial class NightWatchman : RoutedTownsfolk
{
    [Constructible]
    public NightWatchman()
    {
        Title = "the night watch";

        AddItem(new Lantern { Movable = false, Layer = Layer.TwoHanded });
        AddItem(new LeatherChest());
        AddItem(new LeatherLegs());
    }

    public override string DefaultName => Female ? NameList.RandomName("female") : NameList.RandomName("male");
}
