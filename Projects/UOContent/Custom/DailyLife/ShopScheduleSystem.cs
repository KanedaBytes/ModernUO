using System;
using System.Collections.Generic;
using ModernUO.CodeGeneratedEvents;
using Server.Logging;
using Server.Mobiles;

namespace Server.Custom;

/// <summary>
///     Walks the configured shopkeepers home at dusk and back at dawn.
///     <para>
///         These are upstream vendors spawned by upstream spawners, so we drive them from outside
///         rather than subclassing. Two facts make that safe: the spawner has no distance check at
///         all (<c>Defrag</c> only forgets a spawn that is deleted, orphaned, tamed or stabled), so
///         a vendor who walks away still counts and no duplicate appears at the shop; and vendors
///         are <c>FightMode.None</c>, which exempts them from <c>BaseCreature</c>'s return-home
///         path.
///     </para>
///     <para>
///         "Closed" is enforced by absence, not by a hard block. <c>CheckVendorAccess</c> only greys
///         the context-menu entry - <c>VendorBuyEntry.OnClick</c> calls <c>VendorBuy</c> without
///         rechecking it, and the "vendor buy" speech command bypasses the menu entirely. A player
///         who catches a shopkeeper mid-walk can still trade with them.
///     </para>
/// </summary>
public static class ShopScheduleSystem
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(ShopScheduleSystem));

    /// <summary>How far from the configured shop point to look for the vendor.</summary>
    private const int VendorSearchRange = 8;

    private static readonly TimeSpan DriveInterval = TimeSpan.FromSeconds(1.0);
    private static readonly TimeSpan LegTimeout = TimeSpan.FromSeconds(45.0);

    private static readonly List<ShopWalk> _walks = [];

    public static bool ShopsAreClosed => DayCycleSystem.Current.IsAfterDark();

    public static void Initialize()
    {
        Timer.DelayCall(DriveInterval, DriveInterval, 0, Drive);

        // Apply the current phase as end state rather than walking: a restart at midnight should
        // find the shopkeepers already at home, not strolling there.
        ApplyPhase(DayCycleSystem.Current, true);
    }

    [OnEvent(nameof(DayCycleSystem.DayPhaseChangedEvent))]
    public static void OnDayPhaseChanged(DayPhase oldPhase, DayPhase newPhase) => ApplyPhase(newPhase, false);

    /// <summary>
    ///     Re-applies the shop schedule from the current config.
    ///     <para>
    ///         Deliberately does NOT call <see cref="Initialize" />: that arms the repeating drive
    ///         timer, so re-running it would leave a second timer stepping the shopkeepers and leak
    ///         another one on every reload.
    ///     </para>
    ///     <para>
    ///         Snaps rather than walks - a reload should put shopkeepers where the new config says
    ///         they belong straight away, not send them strolling across town.
    ///     </para>
    /// </summary>
    public static void Reload() => ApplyPhase(DayCycleSystem.Current, true);

    private static void ApplyPhase(DayPhase phase, bool snap)
    {
        var shops = TownScheduleConfig.Current?.Shops;

        if (shops?.Shops == null || shops.Shops.Count == 0)
        {
            return;
        }

        var map = shops.GetMap();

        if (map == null || map == Map.Internal)
        {
            return;
        }

        var goingHome = phase.IsAfterDark();

        _walks.Clear();

        foreach (var shop in shops.Shops)
        {
            if (shops.IsExcluded(shop.Vendor))
            {
                logger.Information("Shop '{Vendor}' is on the never-closes list; skipping", shop.Vendor);
                continue;
            }

            if (shop.HomeRoute == null || shop.HomeRoute.Count == 0)
            {
                logger.Warning("Shop '{Vendor}' has no home route; skipping", shop.Vendor);
                continue;
            }

            var vendor = FindVendor(shop, map);

            if (vendor == null)
            {
                // Normal enough: the spawner may not have produced one yet.
                continue;
            }

            var destination = goingHome
                ? shop.HomeRoute[^1].ToPoint3D()
                : shop.ToPoint3D();

            // Point the vendor's own wander AI at the same destination we are driving it toward, so
            // WalkRandomInHome pulls the same way instead of dragging it back to the shop.
            vendor.Home = destination;
            vendor.HomeMap = map;
            vendor.RangeHome = goingHome ? 0 : GetShopRange(vendor);

            if (snap)
            {
                vendor.MoveToWorld(destination, map);
                continue;
            }

            var route = new List<TownScheduleConfig.RouteNode>(shop.HomeRoute);

            if (!goingHome)
            {
                route.Reverse();
                route.Add(new TownScheduleConfig.RouteNode { X = shop.X, Y = shop.Y, Z = shop.Z });
            }

            _walks.Add(new ShopWalk(vendor, route));
        }

        logger.Information(
            "Shops {State}: {Count} shopkeeper(s) on the move",
            goingHome ? "closing" : "opening",
            _walks.Count
        );
    }

    /// <summary>
    ///     Drives each in-progress walk one step. This runs on our own timer rather than the vendor's
    ///     OnThink deliberately: BaseCreature.PlayerRangeSensitive stops a vendor's AI timer entirely
    ///     when no player is in its sector, so a vendor driven from its own think would simply freeze
    ///     the moment nobody was watching. MoveToPoint still respects the movement budget, so pacing
    ///     is unaffected.
    /// </summary>
    private static void Drive()
    {
        for (var i = _walks.Count - 1; i >= 0; i--)
        {
            if (!_walks[i].Step())
            {
                _walks.RemoveAt(i);
            }
        }
    }

    private static BaseVendor FindVendor(TownScheduleConfig.ShopConfig shop, Map map)
    {
        var typeName = shop.Vendor;

        foreach (var vendor in map.GetMobilesInRange<BaseVendor>(shop.ToPoint3D(), VendorSearchRange))
        {
            if (!vendor.Deleted && vendor.GetType().Name.InsensitiveEquals(typeName))
            {
                return vendor;
            }
        }

        return null;
    }

    /// <summary>
    ///     Restore the daytime wander radius from the spawner that owns the vendor, rather than
    ///     guessing - this is the same re-anchor the pet-release order does.
    /// </summary>
    private static int GetShopRange(BaseVendor vendor) => vendor.Spawner?.WalkingRange ?? 5;

    /// <summary>One shopkeeper's journey, one leg at a time.</summary>
    private sealed class ShopWalk
    {
        private readonly BaseVendor _vendor;
        private readonly List<TownScheduleConfig.RouteNode> _route;

        private int _index;
        private IPoint3D _cachedGoal;
        private long _legDeadline;

        public ShopWalk(BaseVendor vendor, List<TownScheduleConfig.RouteNode> route)
        {
            _vendor = vendor;
            _route = route;
            ResetLeg();
        }

        /// <summary>Returns false when the walk is finished or should be abandoned.</summary>
        public bool Step()
        {
            if (_vendor?.Deleted != false || _vendor.Map == null || _vendor.Map == Map.Internal)
            {
                return false;
            }

            if (_index >= _route.Count)
            {
                return false;
            }

            var node = _route[_index];

            if (_vendor.InRange(node.ToPoint3D(), 1))
            {
                _index++;
                ResetLeg();
                return _index < _route.Count;
            }

            if (Core.TickCount - _legDeadline >= 0)
            {
                // Unreachable node - MoveToPoint never gives up on its own. Skip rather than grind.
                _index++;
                ResetLeg();
                return _index < _route.Count;
            }

            // Cache the boxed goal: MoveToPoint compares Path?.Goal by reference, so a fresh Point3D
            // each tick would rebuild the PathFollower every tick and lose the path.
            if (_cachedGoal == null || _cachedGoal.X != node.X || _cachedGoal.Y != node.Y)
            {
                _cachedGoal = new Point3D(node.X, node.Y, _vendor.Map.GetAverageZ(node.X, node.Y));
            }

            _vendor.AIObject?.MoveToPoint(_cachedGoal);

            return true;
        }

        private void ResetLeg()
        {
            _cachedGoal = null;
            _legDeadline = Core.TickCount + (long)LegTimeout.TotalMilliseconds;
        }
    }
}
