using System;
using System.Collections.Generic;
using System.IO;
using ModernUO.CodeGeneratedEvents;
using Server.Gumps;
using Server.Json;
using Server.Logging;
using Server.Mobiles;
using JailSys = Server.Systems.JailSystem.JailSystem;

namespace Server.Custom;

/// <summary>
///     Owns the restricted-zone records, their live regions, and the 30-second countdown that hands
///     offenders to <c>JailSystem</c>.
///     <para>
///         Zones live in <c>Data/Custom/restricted-zones.json</c>, not in the world save. A text
///         file is diffable, hand-editable and editable by the shard editor, and it means drawing a
///         zone no longer has to ride along on a world save to survive a restart. The trade is that
///         a zone edit is no longer atomic with the rest of the world state, which for a
///         staff-drawn box is not a property worth paying for.
///     </para>
/// </summary>
public static class RestrictedZoneSystem
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(RestrictedZoneSystem));

    public const string ConfigPath = "Data/Custom/restricted-zones.json";

    public static readonly TimeSpan WarningDelay = TimeSpan.FromSeconds(30.0);

    private static readonly List<RestrictedZoneRecord> _records = [];
    private static readonly Dictionary<RestrictedZoneRecord, RestrictedZoneRegion> _regions = [];

    // False until Initialize(): loading during Configure() must not build regions yet, because
    // their parents do not exist at that point in the boot sequence.
    private static bool _regionsLive;

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
    ///     Records are read here, but the regions they describe are not built until
    ///     <see cref="Initialize" />. A region resolves its parent with <c>Region.Find</c>, and the
    ///     town and dungeon regions it needs to find are loaded by <c>RegionJsonSerializer</c>
    ///     after the Configure sweep has run.
    /// </summary>
    public static void Configure()
    {
        if (!TryLoad(out var error))
        {
            logger.Error("Restricted zones are INACTIVE - {Path} was not loaded: {Error}", ConfigPath, error);
        }

        // OnExit does NOT fire when a client disconnects - it fires only when the logout timer
        // expires, up to five minutes later. Cancel explicitly instead.
        EventSink.Disconnected += OnDisconnected;
    }

    /// <summary>Runs after the world and the region tree are loaded, so parents resolve.</summary>
    public static void Initialize()
    {
        _regionsLive = true;
        RegisterAll();
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
        Save();

        return true;
    }

    public static bool Remove(RestrictedZoneRecord record)
    {
        if (!_records.Remove(record))
        {
            return false;
        }

        UnregisterZone(record);
        Save();

        return true;
    }

    /// <summary>
    ///     Replaces a zone in place. The region is rebuilt either way, and
    ///     <c>Region.Register()</c> re-resolves everyone standing in the affected sectors, so a
    ///     resized zone applies to its current occupants immediately.
    /// </summary>
    public static bool Replace(RestrictedZoneRecord existing, RestrictedZoneRecord replacement)
    {
        var index = _records.IndexOf(existing);

        if (index < 0)
        {
            return false;
        }

        // A rename must not collide with a different zone.
        var clash = Find(replacement.Name);

        if (clash != null && !ReferenceEquals(clash, existing))
        {
            return false;
        }

        UnregisterZone(existing);
        _records[index] = replacement;
        RegisterZone(replacement);
        Save();

        return true;
    }

    /// <summary>
    ///     Reads and validates the zone file, replacing the live set only on success. A bad file
    ///     leaves the running zones in place and reports why, exactly as the daily life config does.
    /// </summary>
    public static bool TryLoad(out string error)
    {
        var path = Path.Combine(Core.BaseDirectory, ConfigPath);

        try
        {
            var store = JsonConfig.Deserialize<RestrictedZoneStore>(path);

            if (store == null)
            {
                error = $"No zone file found at {ConfigPath}.";
                return false;
            }

            if (store.Zones == null)
            {
                error = "'zones' section is missing";
                return false;
            }

            if (!Validate(store.Zones, out error))
            {
                logger.Error("Invalid restricted zone file at {Path}: {Error}", ConfigPath, error);
                return false;
            }

            UnregisterAll();
            _records.Clear();
            _records.AddRange(store.Zones);

            if (_regionsLive)
            {
                RegisterAll();
            }

            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            logger.Error(ex, "Failed to parse restricted zone file at {Path}; keeping the previous zones", ConfigPath);
            return false;
        }
    }

    /// <summary>
    ///     Re-reads the zone file and rebuilds every region. Single entry point for the staff
    ///     command and the admin API, so a bad file is reported the same way whichever asked.
    /// </summary>
    public static bool TryReload(out string error)
    {
        if (!TryLoad(out error))
        {
            World.BroadcastStaff($"Restricted zones NOT reloaded: {error}");
            return false;
        }

        logger.Information("Reloaded {Count} restricted zone(s)", _records.Count);
        return true;
    }

    public static void Save()
    {
        var path = Path.Combine(Core.BaseDirectory, ConfigPath);

        JsonConfig.Serialize(path, new RestrictedZoneStore { Zones = new List<RestrictedZoneRecord>(_records) });
    }

    internal static bool Validate(List<RestrictedZoneRecord> records, out string error)
    {
        var errors = new List<string>();

        // Find() matches case-insensitively, so two zones differing only in case would make one of
        // them unreachable by name.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < records.Count; i++)
        {
            var record = records[i];
            var where = $"zones[{i}]";

            if (string.IsNullOrWhiteSpace(record.Name))
            {
                errors.Add($"{where} has no name");
            }
            else if (!seen.Add(record.Name))
            {
                errors.Add($"{where} duplicates the name '{record.Name}'");
            }

            if (record.Map == null || record.Map == Map.Internal)
            {
                errors.Add($"{where} map '{record.MapName}' is not a valid facet");
            }

            if (record.Width <= 0 || record.Height <= 0)
            {
                errors.Add(
                    $"{where} bounds must have a positive width and height (found {record.Width}x{record.Height})"
                );
            }
        }

        error = errors.Count > 0 ? string.Join("; ", errors) : null;
        return errors.Count == 0;
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

    private static void UnregisterAll()
    {
        foreach (var record in _records)
        {
            UnregisterZone(record);
        }

        _regions.Clear();
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
