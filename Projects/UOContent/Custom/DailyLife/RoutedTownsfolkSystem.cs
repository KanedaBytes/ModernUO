using System.Collections.Generic;
using Server.Logging;

namespace Server.Custom;

/// <summary>
///     Creates the all-day route walkers - a courier, a farmer - from config.
///     <para>
///         Unlike the tavern crowd and the watch these are not phase-driven; they exist from startup
///         and walk their loop continuously. They are still ephemeral (deleted on world load and
///         recreated here), so the JSON stays the single source of truth.
///     </para>
/// </summary>
public static class RoutedTownsfolkSystem
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(RoutedTownsfolkSystem));

    private static readonly List<RoutedTownsfolk> _walkers = [];

    public static int WalkerCount => _walkers.Count;

    /// <summary>
    ///     Rebuilds the route walkers from the current config. The teardown is essential - without
    ///     it a reload would leave the previous courier and farmer walking alongside the new ones.
    /// </summary>
    public static void Reload()
    {
        for (var i = _walkers.Count - 1; i >= 0; i--)
        {
            _walkers[i]?.Delete();
        }

        _walkers.Clear();

        Initialize();
    }

    public static void Initialize()
    {
        var config = TownScheduleConfig.Current;

        if (config?.Townsfolk == null || config.Townsfolk.Count == 0)
        {
            return;
        }

        foreach (var entry in config.Townsfolk)
        {
            var route = config.GetRoute(entry.Route);

            if (route == null || route.Count == 0)
            {
                logger.Warning("Townsfolk '{Name}' references unknown or empty route '{Route}'", entry.Name, entry.Route);
                continue;
            }

            var map = entry.GetMap();

            if (map == null || map == Map.Internal)
            {
                continue;
            }

            var start = route[0].ToPoint3D();

            var walker = new RoutedTownsfolk();

            if (entry.Body.InsensitiveEquals("male"))
            {
                walker.Female = false;
                walker.Body = 0x190;
            }
            else if (entry.Body.InsensitiveEquals("female"))
            {
                walker.Female = true;
                walker.Body = 0x191;
            }

            if (!string.IsNullOrEmpty(entry.Name))
            {
                walker.Name = entry.Name;
            }

            if (!string.IsNullOrEmpty(entry.Title))
            {
                walker.Title = entry.Title;
            }

            // No home tether: the route is the plan, and a Home would have WalkRandomInHome fighting
            // MoveToPoint whenever the route paused.
            walker.Home = Point3D.Zero;

            walker.MoveToWorld(start, map);
            walker.AssignRoute(route);

            _walkers.Add(walker);
        }

        logger.Information("Created {Count} routed townsfolk", _walkers.Count);
    }
}
