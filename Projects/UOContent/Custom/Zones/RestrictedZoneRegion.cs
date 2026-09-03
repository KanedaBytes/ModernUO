using Server.Mobiles;
using Server.Regions;

namespace Server.Custom;

/// <summary>
///     The live region for one <see cref="RestrictedZoneRecord" />. Created and registered at
///     runtime by <see cref="RestrictedZoneSystem" />; never loaded from JSON.
/// </summary>
public class RestrictedZoneRegion : BaseRegion
{
    public RestrictedZoneRecord Record { get; }

    /// <summary>
    ///     Constructed with the region found at the zone's centre as its parent, so the zone layers
    ///     on top of whatever it overlays (town, dungeon, wilderness) instead of replacing those
    ///     rules - nearly every Region hook delegates to Parent by default. This mirrors
    ///     ChampionSpawnRegion.
    ///     <para>
    ///         The name is deliberately null: a named region is added to <c>Map.Regions</c> and logs
    ///         a duplicate-name warning on collision. Zones are tracked by
    ///         <see cref="RestrictedZoneSystem" /> instead.
    ///     </para>
    /// </summary>
    public RestrictedZoneRegion(RestrictedZoneRecord record)
        : base(null, record.Map, FindParent(record), record.Bounds)
    {
        Record = record;
    }

    private static Region FindParent(RestrictedZoneRecord record)
    {
        var bounds = record.Bounds;
        var map = record.Map;

        var x = bounds.X + bounds.Width / 2;
        var y = bounds.Y + bounds.Height / 2;

        return Find(new Point3D(x, y, map.GetAverageZ(x, y)), map);
    }

    public override void OnEnter(Mobile m)
    {
        base.OnEnter(m);

        if (m is PlayerMobile pm)
        {
            RestrictedZoneSystem.OnEnterZone(pm, this);
        }
    }

    public override void OnExit(Mobile m)
    {
        base.OnExit(m);

        // Also reached while a mobile is being deleted, with items mid-strip - so this must not
        // assume the mobile is in a usable state.
        if (m is not PlayerMobile pm)
        {
            return;
        }

        // Capture before cancelling: only someone who was actually being counted down should be
        // told they got out in time. The message lives here rather than in CancelCountdown because
        // that also runs on disconnect, on re-entry, and after a jail - none of which "left".
        var escaped = !pm.Deleted && RestrictedZoneSystem.HasCountdown(pm);

        RestrictedZoneSystem.CancelCountdown(pm);

        if (escaped)
        {
            pm.SendMessage(0x40, $"You have left {Record.Name}.");
        }
    }

    /// <summary>
    ///     Death fires no region change at all, so ghosts simply keep whatever countdown state they
    ///     had (and <see cref="OnEnter" /> ignores them). Resurrecting inside the zone has to count
    ///     as a fresh entry, otherwise dying at 29 seconds and resurrecting at a shrine inside the
    ///     zone is a way to loiter indefinitely.
    /// </summary>
    public override bool OnResurrect(Mobile m)
    {
        var allowed = base.OnResurrect(m);

        if (allowed && m is PlayerMobile pm)
        {
            RestrictedZoneSystem.OnEnterZone(pm, this);
        }

        return allowed;
    }
}
