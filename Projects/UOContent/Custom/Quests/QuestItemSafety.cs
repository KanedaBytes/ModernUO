using Server.Items;

namespace Server.Custom;

/// <summary>
///     Decides whether an item is unremarkable enough to be counted toward an ML collect
///     objective without the player having flagged it via "Toggle Quest Item".
///     <para>
///         This exists because collect objectives are consumed with <see cref="Item.Delete" />, which
///         bypasses insurance, and because magic/exceptional/artifact gear shares the same CLR type as
///         the plain version - "collect 5 Bow" would otherwise happily eat a runic bow. Anything this
///         predicate rejects still works through the manual toggle, so a rejection costs friction, never
///         functionality.
///     </para>
///     <para>
///         Pure function over a single item: no world state, no allocation, no side effects.
///     </para>
/// </summary>
public static class QuestItemSafety
{
    /// <summary>
    ///     True when <paramref name="item" /> may be auto-counted and auto-consumed.
    /// </summary>
    public static bool CanAutoCount(Item item)
    {
        if (item == null || item.Deleted)
        {
            return false;
        }

        // Deliberately protected by the player, or otherwise not ordinary loot.
        if (item.LootType != LootType.Regular || item.Insured || item.BlessedFor != null)
        {
            return false;
        }

        // Personalized: renamed, or dyed/hued away from the default.
        // Note: types whose default hue is non-zero never auto-count. That is a false negative in
        // the safe direction - the player can still toggle them by hand.
        if (item.Name != null || item.Hue != 0)
        {
            return false;
        }

        // Player-crafted items carry a maker's mark and an exceptional bonus worth keeping.
        if (item.PlayerConstructed)
        {
            return false;
        }

        return item switch
        {
            BaseWeapon weapon   => IsPlainWeapon(weapon),
            BaseArmor armor     => IsPlainArmor(armor),
            BaseClothing clothes => IsPlainClothing(clothes),
            BaseJewel jewel     => IsPlainJewel(jewel),
            _                   => true
        };
    }

    private static bool IsPlainWeapon(BaseWeapon weapon) =>
        weapon.Attributes.IsEmpty &&
        weapon.WeaponAttributes.IsEmpty &&
        weapon.SkillBonuses.IsEmpty &&
        weapon.Quality == WeaponQuality.Regular &&
        string.IsNullOrEmpty(weapon.Crafter) &&
        weapon.Slayer == SlayerName.None &&
        weapon.Slayer2 == SlayerName.None &&
        weapon.Poison == null &&
        // Pre-AOS magic weapons carry no AosAttributes at all - they use these levels instead.
        weapon.DamageLevel == WeaponDamageLevel.Regular &&
        weapon.AccuracyLevel == WeaponAccuracyLevel.Regular &&
        weapon.DurabilityLevel == WeaponDurabilityLevel.Regular &&
        CraftResources.IsStandard(weapon.Resource);

    private static bool IsPlainArmor(BaseArmor armor) =>
        armor.Attributes.IsEmpty &&
        armor.ArmorAttributes.IsEmpty &&
        armor.SkillBonuses.IsEmpty &&
        armor.Quality == ArmorQuality.Regular &&
        string.IsNullOrEmpty(armor.Crafter) &&
        armor.ProtectionLevel == ArmorProtectionLevel.Regular &&
        armor.Durability == ArmorDurabilityLevel.Regular &&
        CraftResources.IsStandard(armor.Resource);

    private static bool IsPlainClothing(BaseClothing clothes) =>
        clothes.Attributes.IsEmpty &&
        clothes.ClothingAttributes.IsEmpty &&
        clothes.SkillBonuses.IsEmpty &&
        clothes.Resistances.IsEmpty &&
        clothes.Quality == ClothingQuality.Regular &&
        string.IsNullOrEmpty(clothes.Crafter) &&
        CraftResources.IsStandard(clothes.Resource);

    private static bool IsPlainJewel(BaseJewel jewel) =>
        jewel.Attributes.IsEmpty &&
        jewel.Resistances.IsEmpty &&
        jewel.SkillBonuses.IsEmpty &&
        CraftResources.IsStandard(jewel.Resource);
}
