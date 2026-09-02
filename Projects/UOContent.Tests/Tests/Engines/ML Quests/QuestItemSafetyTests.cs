using Server;
using Server.Custom;
using Server.Items;
using Xunit;

namespace UOContent.Tests;

[Collection("Sequential UOContent Tests")]
public class QuestItemSafetyTests
{
    [Fact]
    public void PlainStackableResource_IsAutoCounted()
    {
        var fish = new Fish(5);

        try
        {
            Assert.True(QuestItemSafety.CanAutoCount(fish));
        }
        finally
        {
            fish.Delete();
        }
    }

    [Fact]
    public void PlainWeapon_IsAutoCounted()
    {
        var bow = new Bow();

        try
        {
            Assert.True(QuestItemSafety.CanAutoCount(bow));
        }
        finally
        {
            bow.Delete();
        }
    }

    [Fact]
    public void NullOrDeleted_IsNotAutoCounted()
    {
        Assert.False(QuestItemSafety.CanAutoCount(null));

        var fish = new Fish();
        fish.Delete();

        Assert.False(QuestItemSafety.CanAutoCount(fish));
    }

    // Consumption uses Item.Delete(), which bypasses insurance entirely - so without these
    // checks, insuring an item would offer no protection at all.
    [Theory]
    [InlineData(LootType.Newbied)]
    [InlineData(LootType.Blessed)]
    [InlineData(LootType.Cursed)]
    public void NonRegularLootType_IsNotAutoCounted(LootType lootType)
    {
        var fish = new Fish(5);

        try
        {
            fish.LootType = lootType;
            Assert.False(QuestItemSafety.CanAutoCount(fish));
        }
        finally
        {
            fish.Delete();
        }
    }

    [Fact]
    public void InsuredItem_IsNotAutoCounted()
    {
        var bow = new Bow();

        try
        {
            bow.Insured = true;
            Assert.False(QuestItemSafety.CanAutoCount(bow));
        }
        finally
        {
            bow.Delete();
        }
    }

    [Fact]
    public void RenamedItem_IsNotAutoCounted()
    {
        var bow = new Bow();

        try
        {
            bow.Name = "Steve";
            Assert.False(QuestItemSafety.CanAutoCount(bow));
        }
        finally
        {
            bow.Delete();
        }
    }

    [Fact]
    public void HuedItem_IsNotAutoCounted()
    {
        var shirt = new FancyShirt();

        try
        {
            shirt.Hue = 1157;
            Assert.False(QuestItemSafety.CanAutoCount(shirt));
        }
        finally
        {
            shirt.Delete();
        }
    }

    [Fact]
    public void PlayerCraftedItem_IsNotAutoCounted()
    {
        var bow = new Bow();

        try
        {
            bow.PlayerConstructed = true;
            Assert.False(QuestItemSafety.CanAutoCount(bow));
        }
        finally
        {
            bow.Delete();
        }
    }

    // A magic bow is the same CLR type as a plain one - this is the case that would otherwise
    // let "collect 5 Bow" eat a runic bow.
    [Fact]
    public void WeaponWithMagicProperties_IsNotAutoCounted()
    {
        var bow = new Bow();

        try
        {
            bow.Attributes.WeaponDamage = 25;
            Assert.False(QuestItemSafety.CanAutoCount(bow));
        }
        finally
        {
            bow.Delete();
        }
    }

    [Fact]
    public void ExceptionalWeapon_IsNotAutoCounted()
    {
        var bow = new Bow();

        try
        {
            bow.Quality = WeaponQuality.Exceptional;
            Assert.False(QuestItemSafety.CanAutoCount(bow));
        }
        finally
        {
            bow.Delete();
        }
    }

    [Fact]
    public void CraftedWeaponWithMakersMark_IsNotAutoCounted()
    {
        var bow = new Bow();

        try
        {
            bow.Crafter = "Some Smith";
            Assert.False(QuestItemSafety.CanAutoCount(bow));
        }
        finally
        {
            bow.Delete();
        }
    }

    // Pre-AOS magic weapons carry no AosAttributes - the enchantment lives in these levels.
    [Fact]
    public void LegacyMagicWeapon_IsNotAutoCounted()
    {
        var bow = new Bow();

        try
        {
            bow.DamageLevel = WeaponDamageLevel.Power;
            Assert.False(QuestItemSafety.CanAutoCount(bow));
        }
        finally
        {
            bow.Delete();
        }
    }

    [Fact]
    public void SlayerWeapon_IsNotAutoCounted()
    {
        var bow = new Bow();

        try
        {
            bow.Slayer = SlayerName.OrcSlaying;
            Assert.False(QuestItemSafety.CanAutoCount(bow));
        }
        finally
        {
            bow.Delete();
        }
    }

    [Fact]
    public void NonStandardResource_IsNotAutoCounted()
    {
        var sword = new Broadsword();

        try
        {
            sword.Resource = CraftResource.Valorite;
            Assert.False(QuestItemSafety.CanAutoCount(sword));
        }
        finally
        {
            sword.Delete();
        }
    }
}
