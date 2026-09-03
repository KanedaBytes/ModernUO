using System.Collections.Generic;
using ModernUO.CodeGeneratedEvents;
using Server.Logging;

namespace Server.Custom;

/// <summary>
///     Puts the night watch on the streets at dusk and stands them down at dawn.
///     Same ephemeral pattern as <see cref="TavernSystem" />: nothing persisted, rebuilt from config.
/// </summary>
public static class NightWatchSystem
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(NightWatchSystem));

    private static readonly List<NightWatchman> _watch = [];

    public static int WatchCount => _watch.Count;

    public static void Initialize() => ApplyPhase(DayCycleSystem.Current);

    [OnEvent(nameof(DayCycleSystem.DayPhaseChangedEvent))]
    public static void OnDayPhaseChanged(DayPhase oldPhase, DayPhase newPhase) => ApplyPhase(newPhase);

    /// <summary>
    ///     Rebuilds the watch from the current config. Stands down first because
    ///     <see cref="Deploy" /> early-returns outright when the watch is already out, so changed
    ///     posts or routes would otherwise be ignored.
    /// </summary>
    public static void Reload()
    {
        StandDown();
        ApplyPhase(DayCycleSystem.Current);
    }

    public static void ApplyPhase(DayPhase phase)
    {
        if (phase.IsAfterDark())
        {
            Deploy();
        }
        else
        {
            StandDown();
        }
    }

    private static void Deploy()
    {
        var config = TownScheduleConfig.Current;
        var watch = config?.Watch;

        if (watch?.Posts == null || watch.Posts.Count == 0)
        {
            return;
        }

        var map = watch.GetMap();

        if (map == null || map == Map.Internal)
        {
            return;
        }

        Prune();

        if (_watch.Count > 0)
        {
            return; // already deployed
        }

        foreach (var post in watch.Posts)
        {
            var watchman = new NightWatchman
            {
                Home = post.ToPoint3D(),
                HomeMap = map,
                RangeHome = 4
            };

            watchman.MoveToWorld(post.ToPoint3D(), map);

            var route = config.GetRoute(post.Route);

            if (route is { Count: > 0 })
            {
                watchman.AssignRoute(route);
            }

            _watch.Add(watchman);
        }

        logger.Information("Night watch deployed: {Count}", _watch.Count);
    }

    private static void StandDown()
    {
        if (_watch.Count == 0)
        {
            return;
        }

        for (var i = _watch.Count - 1; i >= 0; i--)
        {
            _watch[i]?.Delete();
        }

        _watch.Clear();

        logger.Information("Night watch stood down");
    }

    private static void Prune()
    {
        for (var i = _watch.Count - 1; i >= 0; i--)
        {
            if (_watch[i]?.Deleted != false)
            {
                _watch.RemoveAt(i);
            }
        }
    }
}
