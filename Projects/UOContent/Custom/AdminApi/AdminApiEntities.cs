using System.Collections.Generic;
using Server.Mobiles;
using Server.Network;

namespace Server.Custom.AdminApi;

/// <summary>
///     The live view: where the players and the daily-life NPCs actually are right now.
///     <para>
///         Everything here runs on the game loop (posted by <see cref="AdminApiLoop" />) and reads
///         no more than name, type and position.
///     </para>
///     <para>
///         Nothing iterates <c>World.Mobiles</c>. Players come from the connected net states, and
///         the daily-life NPCs from the lists their own systems already keep - so the cost of a
///         one-second poll is proportional to what is on screen, not to the size of the world.
///     </para>
/// </summary>
internal static class AdminApiEntities
{
    internal sealed class Entity
    {
        public string Name { get; init; }

        public string Type { get; init; }

        /// <summary>"player", "staff" or "npc" - what the editor colours by.</summary>
        public string Kind { get; init; }

        public int X { get; init; }

        public int Y { get; init; }

        public int Z { get; init; }

        public string Map { get; init; }
    }

    public static List<Entity> All()
    {
        var entities = new List<Entity>();

        foreach (var state in NetState.Instances)
        {
            if (state.Mobile is PlayerMobile player && player.Deleted == false)
            {
                Add(entities, player, player.AccessLevel > AccessLevel.Player ? "staff" : "player");
            }
        }

        foreach (var patron in TavernSystem.Patrons)
        {
            Add(entities, patron, "npc");
        }

        foreach (var watchman in NightWatchSystem.Watchmen)
        {
            Add(entities, watchman, "npc");
        }

        foreach (var walker in RoutedTownsfolkSystem.Walkers)
        {
            Add(entities, walker, "npc");
        }

        AddShopkeepers(entities);

        return entities;
    }

    /// <summary>
    ///     Online staff, so the editor can offer "jump to my character". There is no way to tell
    ///     which character belongs to the person holding the API token - the token authenticates the
    ///     shard owner, not a character - so this returns everyone with access and lets the editor
    ///     choose. On a one-operator shard that is a list of one.
    /// </summary>
    public static List<Entity> Staff()
    {
        var staff = new List<Entity>();

        foreach (var state in NetState.Instances)
        {
            if (state.Mobile is PlayerMobile player && player.AccessLevel > AccessLevel.Player)
            {
                Add(staff, player, "staff");
            }
        }

        return staff;
    }

    /// <summary>
    ///     Shopkeepers are upstream spawner-owned vendors that <c>ShopScheduleSystem</c> only
    ///     drives, so there is no list of them to read. Find them the way that system does: by type
    ///     name, near the configured shop point.
    /// </summary>
    private static void AddShopkeepers(List<Entity> entities)
    {
        var shops = TownScheduleConfig.Current?.Shops;

        if (shops?.Shops == null)
        {
            return;
        }

        var map = shops.GetMap();

        if (map == null || map == Map.Internal)
        {
            return;
        }

        foreach (var shop in shops.Shops)
        {
            foreach (var vendor in map.GetMobilesInRange<BaseVendor>(shop.ToPoint3D(), 24))
            {
                if (!vendor.Deleted && vendor.GetType().Name.InsensitiveEquals(shop.Vendor))
                {
                    Add(entities, vendor, "npc");
                    break;
                }
            }
        }
    }

    private static void Add(List<Entity> entities, Mobile mobile, string kind)
    {
        if (mobile?.Deleted != false || mobile.Map == null || mobile.Map == Map.Internal)
        {
            return;
        }

        entities.Add(
            new Entity
            {
                Name = mobile.Name,
                Type = mobile.GetType().Name,
                Kind = kind,
                X = mobile.X,
                Y = mobile.Y,
                Z = mobile.Z,
                Map = mobile.Map.Name
            }
        );
    }
}
