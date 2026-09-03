using Server.Logging;
using Server.Mobiles;
using Server.Regions;

namespace Server.Custom;

/// <summary>
///     A dynamic region over the shop district that adds closing-time flavour.
///     <para>
///         Both effects here are cosmetic. <c>CheckVendorAccess</c> only sets the Enabled flag on
///         the buy/sell context-menu entries - it does not gate the trade itself - so this greys the
///         menu but does not block anything. The real "closed" is the shopkeeper not being there.
///     </para>
/// </summary>
public class ShopDistrictRegion : GuardedRegion
{
    private static ShopDistrictRegion _instance;

    // GuardedRegion has no parent-taking Rectangle2D overload, so convert explicitly. ConvertTo3D
    // spans the full Z column, which is what a district-wide rule wants.
    private ShopDistrictRegion(Map map, Rectangle2D bounds, Region parent)
        : base(null, map, parent, ConvertTo3D(bounds))
    {
    }

    public static void Initialize()
    {
        var shops = TownScheduleConfig.Current?.Shops;

        if (shops == null || shops.Width <= 0 || shops.Height <= 0)
        {
            return;
        }

        var map = shops.GetMap();

        if (map == null || map == Map.Internal)
        {
            return;
        }

        _instance?.Unregister();

        var bounds = shops.ToBounds();
        var centre = new Point3D(
            bounds.X + bounds.Width / 2,
            bounds.Y + bounds.Height / 2,
            map.GetAverageZ(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2)
        );

        // Parented to whatever region already covers the district, so the town's own guard and
        // spell rules keep applying - nearly every Region hook delegates to Parent by default.
        _instance = new ShopDistrictRegion(map, bounds, Find(centre, map));
        _instance.Register();

        LogFactory.GetLogger(typeof(ShopDistrictRegion))
            .Information("Shop district region registered over {Bounds}", bounds);
    }

    /// <summary>
    ///     Greys the buy/sell menu entries while shops are shut. Cosmetic only - see the class note.
    /// </summary>
    public override bool CheckVendorAccess(BaseVendor vendor, Mobile from)
    {
        if (ShopScheduleSystem.ShopsAreClosed && from.AccessLevel == AccessLevel.Player)
        {
            return false;
        }

        return base.CheckVendorAccess(vendor, from);
    }

    /// <summary>
    ///     If a player speaks near a shopkeeper after hours, have the shopkeeper say they are shut.
    ///     Catches the case where someone corners a vendor still walking home.
    /// </summary>
    public override void OnSpeech(SpeechEventArgs args)
    {
        base.OnSpeech(args);

        var from = args.Mobile;

        if (!ShopScheduleSystem.ShopsAreClosed || from?.Deleted != false || from.AccessLevel > AccessLevel.Player)
        {
            return;
        }

        foreach (var vendor in from.Map.GetMobilesInRange<BaseVendor>(from.Location, 3))
        {
            if (!vendor.Deleted)
            {
                vendor.Say("We're closed. Come back at dawn.");
                return;
            }
        }
    }
}
