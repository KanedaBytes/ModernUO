using System;
using System.Collections.Generic;
using ModernUO.CodeGeneratedEvents;
using Server.Gumps;
using Server.Logging;
using Server.Mobiles;
using JailSys = Server.Systems.JailSystem.JailSystem;

namespace Server.Custom;

/// <summary>
///     Owns the restricted-zone records, their live regions, and the 30-second countdown that hands
///     offenders to <c>JailSystem</c>.
///     <para>
///         Persistence uses <c>GenericPersistence</c>, the current ModernUO mechanism for "my system
///         owns a list of records that must survive a restart" - the same base <c>JailSystem</c>
///         itself uses. Records are saved to <c>Saves/RestrictedZones/RestrictedZones.bin</c>
///         alongside the world save.
///     </para>
/// </summary>
public class RestrictedZoneSystem : GenericPersistence
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(RestrictedZoneSystem));

    public static readonly TimeSpan WarningDelay = TimeSpan.FromSeconds(30.0);

    private static RestrictedZoneSystem _instance;

    private static readonly List<RestrictedZoneRecord> _records = [];
    private static readonly Dictionary<RestrictedZoneRecord, RestrictedZoneRegion> _regions = [];

    // Transient by design: a restart clears every pending countdown, so nobody is ever jailed by a
    // timer they could not see.
    private static readonly Dictionary<PlayerMobile, ZoneCountdown> _countdowns = [];

    /// <summary>
    ///     Per-player countdown state. One 1-second repeating timer drives both the on-screen gump
    ///     and the jail at zero, so there is only ever one timer per player to cancel.
    ///     <para>
    ///         Counting an int down avoids clock arithmetic entirely - there are no tick-count
    ///         comparisons here to get wrong.
    ///     </para>
    /// </summary>
    private sealed class ZoneCountdown
    {
        public string ZoneName;
        public int Remaining;
        public Timer Timer;
    }

    /// <summary>
    ///     Must be constructed in Configure(), which runs before World.Load(). A GenericPersistence
    ///     self-registers in its constructor; miss this window and Deserialize is never called.
    /// </summary>
    public static void Configure()
    {
        _instance = new RestrictedZoneSystem();

        // OnExit does NOT fire when a client disconnects - it fires only when the logout timer
        // expires, up to five minutes later. Cancel explicitly instead.
        EventSink.Disconnected += OnDisconnected;
    }

    public RestrictedZoneSystem() : base("RestrictedZones", 10)
    {
    }

    public static IReadOnlyList<RestrictedZoneRecord> Zones => _records;

    public static RestrictedZoneRecord Find(string name)
    {
        foreach (var record in _records)
        {
            if (record.Name.InsensitiveEquals(name))
            {
                return record;
            }
        }

        return null;
    }

    public static bool Add(RestrictedZoneRecord record)
    {
        if (Find(record.Name) != null)
        {
            return false;
        }

        _records.Add(record);
        RegisterZone(record);

        return true;
    }

    public static bool Remove(RestrictedZoneRecord record)
    {
        if (!_records.Remove(record))
        {
            return false;
        }

        UnregisterZone(record);

        return true;
    }

    private static void RegisterZone(RestrictedZoneRecord record)
    {
        UnregisterZone(record);

        if (record.Map == null || record.Map == Map.Internal)
        {
            return;
        }

        var region = new RestrictedZoneRegion(record);
        _regions[record] = region;

        // Register() walks the affected sectors and re-resolves every mobile standing in them, so
        // this fires OnEnter for players already inside. No manual sweep needed.
        region.Register();
    }

    private static void UnregisterZone(RestrictedZoneRecord record)
    {
        if (_regions.Remove(record, out var region))
        {
            region.Unregister();
        }
    }

    public static void OnEnterZone(PlayerMobile pm, RestrictedZoneRegion region)
    {
        if (!ShouldWarn(pm))
        {
            return;
        }

        // Re-entering restarts the countdown rather than resuming it.
        CancelCountdown(pm);

        pm.SendMessage(
            0x22,
            $"You have entered a restricted area: {region.Record.Name}. Leave within {WarningDelay.TotalSeconds:F0} seconds or you will be jailed."
        );
        pm.PlaySound(0x1F3);

        var countdown = new ZoneCountdown
        {
            ZoneName = region.Record.Name,
            Remaining = (int)WarningDelay.TotalSeconds
        };

        _countdowns[pm] = countdown;

        RestrictedZoneCountdownGump.DisplayTo(pm, countdown.ZoneName, countdown.Remaining);

        countdown.Timer = Timer.DelayCall(
            TimeSpan.FromSeconds(1.0),
            TimeSpan.FromSeconds(1.0),
            0,
            OnCountdownTick,
            pm
        );
    }

    public static bool HasCountdown(Mobile m) => m is PlayerMobile pm && _countdowns.ContainsKey(pm);

    public static void CancelCountdown(PlayerMobile pm)
    {
        // Always stop before dropping the reference. JailSystem omits this on its own release
        // timers and the stale timer fires anyway - the same mistake here would jail someone who
        // had already left.
        if (_countdowns.Remove(pm, out var countdown))
        {
            countdown.Timer?.Stop();
            pm.CloseGump<RestrictedZoneCountdownGump>();
        }
    }

    private static void OnCountdownTick(PlayerMobile pm)
    {
        if (!_countdowns.TryGetValue(pm, out var countdown))
        {
            return;
        }

        countdown.Remaining--;

        if (countdown.Remaining > 0)
        {
            // Singleton replaces the previous instance, so this refreshes rather than stacks - and
            // it also puts the gump back if the player somehow dismissed it.
            RestrictedZoneCountdownGump.DisplayTo(pm, countdown.ZoneName, countdown.Remaining);
            return;
        }

        CancelCountdown(pm);
        OnCountdownExpired(pm);
    }

    private static bool ShouldWarn(PlayerMobile pm) =>
        pm is { Deleted: false, Alive: true } && pm.AccessLevel <= AccessLevel.Player;

    private static void OnCountdownExpired(PlayerMobile pm)
    {
        var region = GetZoneAt(pm);

        // Re-validate: 30 seconds is a long time. They may have died, gone staff, or the zone may
        // have been removed out from under them.
        if (region == null || !ShouldWarn(pm))
        {
            return;
        }

        TryJail(pm, region.Record.Name);
    }

    private static RestrictedZoneRegion GetZoneAt(PlayerMobile pm)
    {
        if (pm?.Deleted != false || pm.Map == null || pm.Map == Map.Internal)
        {
            return null;
        }

        return Region.Find(pm.Location, pm.Map).GetRegion<RestrictedZoneRegion>();
    }

    /// <summary>
    ///     JailSystem.JailPlayer does not guard against staff, already-serving prisoners, or
    ///     null/deleted mobiles - those checks live only in its [Jail command handler. Replicate
    ///     them, then verify the call actually took effect.
    /// </summary>
    private static void TryJail(PlayerMobile pm, string zoneName)
    {
        // Re-jailing an active prisoner overwrites JailSystem's release timer without stopping it,
        // which releases them early and then fires a second time. Never call into that.
        if (JailSys.IsPlayerJailed(pm))
        {
            return;
        }

        var reason = $"Restricted zone: {zoneName}";

        // from: null - there is no staff member behind an automatic jail. JailSystem's own
        // CommandLogging call throws internally on a null from and swallows it, so we log here
        // instead; the entry is better attributed this way anyway.
        JailSys.JailPlayer(null, pm, reason);

        // JailPlayer returns void and can silently no-op: a prisoner restored across a restart is
        // left in its CurrentlyBeingJailed latch forever, and every later call for them just
        // returns. JailEndTime is stamped immediately, so this assertion is valid right away.
        if (!JailSys.IsPlayerJailed(pm))
        {
            logger.Error(
                "Failed to jail {Player} for {Reason}: JailSystem silently rejected the call (likely stuck in CurrentlyBeingJailed after a restart)",
                pm.Name,
                reason
            );

            NotifyStaff($"Could not jail {pm.Name} for '{reason}' - JailSystem rejected the call. Jail them manually.");
            return;
        }

        logger.Information("Jailed {Player} for {Reason}", pm.Name, reason);
        NotifyStaff($"{pm.Name} was jailed automatically. Reason: {reason}");

        // Show the sentence gump immediately rather than waiting for the next minute sweep.
        JailStatusSystem.ShowNow(pm);
    }

    private static void NotifyStaff(string message)
    {
        foreach (var ns in Network.NetState.Instances)
        {
            if (ns.Mobile is PlayerMobile staff && staff.AccessLevel >= AccessLevel.Counselor)
            {
                staff.SendMessage(0x35, message);
            }
        }
    }

    private static void OnDisconnected(Mobile m)
    {
        if (m is PlayerMobile pm)
        {
            CancelCountdown(pm);
        }
    }

    /// <summary>
    ///     Login does fire OnEnter, but via a map change that happens while the location is still
    ///     stale, so it can fire for the wrong region and double up. The login event is the reliable
    ///     hook - HouseRegion uses it for the same reason.
    /// </summary>
    [OnEvent(nameof(PlayerMobile.PlayerLoginEvent))]
    public static void OnLogin(PlayerMobile pm)
    {
        var region = GetZoneAt(pm);

        if (region != null)
        {
            OnEnterZone(pm, region);
        }
    }

    public override void Serialize(IGenericWriter writer)
    {
        writer.WriteEncodedInt(0); // version

        writer.WriteEncodedInt(_records.Count);

        foreach (var record in _records)
        {
            record.Serialize(writer);
        }
    }

    public override void Deserialize(IGenericReader reader)
    {
        reader.ReadEncodedInt(); // version

        var count = reader.ReadEncodedInt();

        for (var i = 0; i < count; i++)
        {
            var record = new RestrictedZoneRecord();
            record.Deserialize(reader);
            _records.Add(record);
        }

        // Defer registration by a tick: regions rely on maps and parent regions being settled, and
        // this runs in the middle of world load.
        Timer.StartTimer(TimeSpan.Zero, RegisterAll);
    }

    private static void RegisterAll()
    {
        foreach (var record in _records)
        {
            RegisterZone(record);
        }

        logger.Information("Registered {Count} restricted zone(s)", _records.Count);
    }
}
