using System;
using System.Collections.Generic;
using ModernUO.CodeGeneratedEvents;
using Server.Gumps;
using Server.Mobiles;
using Server.Network;
using JailSys = Server.Systems.JailSystem.JailSystem;

namespace Server.Custom;

/// <summary>
///     Keeps <see cref="JailStatusGump" /> on screen for every jailed player and takes it away on
///     release.
///     <para>
///         One shard-wide repeating timer rather than one per prisoner. Each tick walks
///         <see cref="NetState.Instances" /> - the online-client list, bounded by player count and
///         not a <c>World.Mobiles</c> iteration - so it needs no event from JailSystem. That matters
///         because a staff member typing <c>[Jail</c> raises no event we could subscribe to; the
///         sweep notices within a minute either way.
///     </para>
/// </summary>
public static class JailStatusSystem
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1.0);

    // Who currently has the gump, so it can be closed exactly once on release.
    private static readonly HashSet<PlayerMobile> _showing = [];

    public static void Configure()
    {
        Timer.DelayCall(RefreshInterval, RefreshInterval, 0, Sweep);
    }

    public static bool IsJailed(PlayerMobile pm) => pm?.Deleted == false && JailSys.IsPlayerJailed(pm);

    public static TimeSpan GetRemaining(PlayerMobile pm)
    {
        var remaining = JailSys.GetJailEndTime(pm) - Core.Now;

        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>
    ///     Shows the gump straight away instead of waiting up to a minute. Called at the two moments
    ///     that matter: login, and immediately after an automatic jail.
    /// </summary>
    public static void ShowNow(PlayerMobile pm)
    {
        if (!IsJailed(pm))
        {
            return;
        }

        _showing.Add(pm);
        JailStatusGump.DisplayTo(pm);
    }

    private static void Sweep()
    {
        // Track who is still jailed this pass so the rest can have their gump closed.
        var stillJailed = new HashSet<PlayerMobile>();

        foreach (var ns in NetState.Instances)
        {
            if (ns.Mobile is not PlayerMobile pm || !IsJailed(pm))
            {
                continue;
            }

            stillJailed.Add(pm);

            // Singleton means this replaces the previous instance rather than stacking, and it also
            // restores the gump if the player dismissed it since the last tick.
            JailStatusGump.DisplayTo(pm);
        }

        if (_showing.Count > 0)
        {
            foreach (var pm in _showing)
            {
                if (!stillJailed.Contains(pm))
                {
                    // Released, logged out, or deleted. Closing an absent gump is harmless.
                    pm.CloseGump<JailStatusGump>();
                }
            }
        }

        _showing.Clear();
        _showing.UnionWith(stillJailed);
    }

    [OnEvent(nameof(PlayerMobile.PlayerLoginEvent))]
    public static void OnLogin(PlayerMobile pm)
    {
        ShowNow(pm);
    }
}
