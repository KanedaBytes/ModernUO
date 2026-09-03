using System;
using System.Collections.Generic;
using ModernUO.Serialization;
using Server.Items;
using Server.Mobiles;

namespace Server.Custom;

/// <summary>
///     A townsperson who walks a fixed route, speaking a line at each stop.
///     <para>
///         Routes are driven from <see cref="OnThink" /> rather than the stock <c>WayPoint</c> chain.
///         The built-in waypoint handler moves with a greedy <c>DoMove</c> and no pathfinding, so it
///         sticks on the first building corner; driving each leg through <c>MoveToPoint</c> gets A*
///         and door opening instead.
///     </para>
///     <para>
///         Ephemeral by design, like every daily-life NPC: it deletes itself on world load and the
///         owning system recreates it from config. That keeps the JSON the single source of truth
///         and makes duplicate-on-restart impossible.
///     </para>
/// </summary>
[SerializationGenerator(0)]
public partial class RoutedTownsfolk : BaseCreature
{
    /// <summary>
    ///     A* searches a 38x38 box centred on the midpoint of start and goal, so a goal much further
    ///     than this cannot be pathed at all and silently degrades to walking into scenery. Route
    ///     nodes must be closer together than this.
    /// </summary>
    public const int MaxLegDistance = 15;

    /// <summary>MoveToPoint never gives up on an unreachable tile, so every leg gets a deadline.</summary>
    private static readonly TimeSpan LegTimeout = TimeSpan.FromSeconds(45.0);

    private static readonly TimeSpan PauseAtStop = TimeSpan.FromSeconds(8.0);

    private List<TownScheduleConfig.RouteNode> _route;
    private int _nodeIndex;

    // MoveToPoint compares Path?.Goal by REFERENCE, so a fresh Point3D each tick would rebuild the
    // PathFollower every tick and destroy path persistence. Cache the boxed goal.
    private IPoint3D _cachedGoal;

    private long _legDeadline;
    private long _resumeAt;

    [Constructible]
    public RoutedTownsfolk() : base(AIType.AI_Vendor, FightMode.None, 2)
    {
        Race = Race.Human;
        Female = Utility.RandomBool();
        Body = Female ? 0x191 : 0x190;
        Hue = Race.RandomSkinHue();

        SetSpeed(0.5, 2.0);
        InitStats(70, 70, 25);

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
    }

    public override bool IsInvulnerable => true;

    /// <summary>
    ///     Without this the AI timer stops whenever no player is in the NPC's sector, and a route
    ///     walker would simply freeze the moment nobody was watching.
    /// </summary>
    public override bool PlayerRangeSensitive => false;

    public void AssignRoute(List<TownScheduleConfig.RouteNode> route)
    {
        _route = route;
        _nodeIndex = 0;
        _cachedGoal = null;
        _legDeadline = Core.TickCount + (long)LegTimeout.TotalMilliseconds;
        _resumeAt = Core.TickCount;
    }

    public override void OnThink()
    {
        base.OnThink();

        if (_route == null || _route.Count == 0 || Deleted || Map == null || Map == Map.Internal)
        {
            return;
        }

        // Pausing at a stop and the per-leg timeout are both deadline-gated in subtraction form, so
        // an extra OnThink call never advances the route.
        if (Core.TickCount - _resumeAt < 0)
        {
            return;
        }

        var node = _route[_nodeIndex];

        if (InRange(node.ToPoint3D(), 1))
        {
            ArriveAt(node);
            return;
        }

        if (Core.TickCount - _legDeadline >= 0)
        {
            // Genuinely stuck: skip this node rather than grinding against it forever.
            AdvanceNode();
            return;
        }

        EnsureGoal(node);
        AIObject?.MoveToPoint(_cachedGoal);
    }

    private void EnsureGoal(TownScheduleConfig.RouteNode node)
    {
        if (_cachedGoal != null && _cachedGoal.X == node.X && _cachedGoal.Y == node.Y)
        {
            return;
        }

        _cachedGoal = new Point3D(node.X, node.Y, Map?.GetAverageZ(node.X, node.Y) ?? Z);
    }

    private void ArriveAt(TownScheduleConfig.RouteNode node)
    {
        if (!string.IsNullOrEmpty(node.Say))
        {
            Say(node.Say);
        }

        AdvanceNode();
        _resumeAt = Core.TickCount + (long)PauseAtStop.TotalMilliseconds;
    }

    private void AdvanceNode()
    {
        _nodeIndex = (_nodeIndex + 1) % _route.Count;
        _cachedGoal = null;
        _legDeadline = Core.TickCount + (long)LegTimeout.TotalMilliseconds;
    }

    [AfterDeserialization(false)]
    private void AfterDeserialization() => Timer.DelayCall(Delete);
}
