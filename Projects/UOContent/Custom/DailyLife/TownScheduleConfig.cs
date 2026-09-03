using System;
using System.Collections.Generic;
using System.IO;
using Server.Json;
using Server.Logging;

namespace Server.Custom;

/// <summary>
///     Everything town-specific lives here rather than in code, so a second town is a second JSON
///     file and no new C#.
/// </summary>
public class TownScheduleConfig
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(TownScheduleConfig));

    public const string ConfigPath = "Data/Custom/britain-daily-life.json";

    public static TownScheduleConfig Current { get; private set; }

    /// <summary>The point whose local in-game time drives this town's schedule.</summary>
    public AnchorPoint Anchor { get; set; }

    public TavernConfig Tavern { get; set; }

    /// <summary>Named routes, referenced by watchmen and routed townsfolk.</summary>
    public Dictionary<string, List<RouteNode>> Routes { get; set; } = [];

    public WatchConfig Watch { get; set; }

    /// <summary>Townsfolk who walk a route all day - a courier, a farmer.</summary>
    public List<RoutedTownsfolkConfig> Townsfolk { get; set; } = [];

    public ShopsConfig Shops { get; set; }

    public List<RouteNode> GetRoute(string name)
    {
        if (string.IsNullOrEmpty(name) || Routes == null)
        {
            return null;
        }

        return Routes.GetValueOrDefault(name);
    }

    public static void Configure() => TryLoad(out _);

    /// <summary>
    ///     Reads the config file, replacing <see cref="Current" /> only on success.
    ///     <para>
    ///         A malformed file must not take the town down: <c>JsonConfig.Deserialize</c> throws
    ///         <c>JsonException</c> on bad input, so a failed reload leaves the running config in
    ///         place and reports why. That is the difference between a typo costing a retry and a
    ///         typo emptying the town.
    ///     </para>
    /// </summary>
    public static bool TryLoad(out string error)
    {
        var path = Path.Combine(Core.BaseDirectory, ConfigPath);

        try
        {
            var loaded = JsonConfig.Deserialize<TownScheduleConfig>(path);

            if (loaded == null)
            {
                error = $"No config found at {ConfigPath}.";
                logger.Warning("No town schedule config found at {Path}; daily life is inactive", ConfigPath);
                return false;
            }

            Current = loaded;
            error = null;

            logger.Information("Loaded town schedule config from {Path}", ConfigPath);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            logger.Error(ex, "Failed to parse town schedule config at {Path}; keeping the previous config", ConfigPath);
            return false;
        }
    }

    public class AnchorPoint
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Z { get; set; }
        public string Map { get; set; } = "Trammel";

        public Map GetMap() => Server.Map.Parse(Map);

        public Point3D ToPoint3D() => new(X, Y, Z);
    }

    public class TavernConfig
    {
        /// <summary>Rectangle patrons are placed inside, in world coordinates.</summary>
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int Z { get; set; }
        public string Map { get; set; } = "Trammel";

        /// <summary>How many patrons show up after dark.</summary>
        public int PatronCount { get; set; } = 4;

        /// <summary>Lines patrons say to themselves while drinking.</summary>
        public List<string> Chatter { get; set; } = [];

        public Map GetMap() => Server.Map.Parse(Map);

        public Rectangle2D ToBounds() => new(X, Y, Width, Height);
    }

    /// <summary>
    ///     One stop on a route. Consecutive nodes must be within
    ///     <see cref="RoutedTownsfolk.MaxLegDistance" /> tiles of each other with a walkable line
    ///     between them - the pathfinder searches a box around the straight line, so a long leg
    ///     around a building is unfindable.
    /// </summary>
    public class RouteNode
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Z { get; set; }

        /// <summary>Optional line spoken on arrival.</summary>
        public string Say { get; set; }

        public Point3D ToPoint3D() => new(X, Y, Z);
    }

    public class WatchConfig
    {
        public string Map { get; set; } = "Trammel";

        /// <summary>Where each watchman is placed at dusk. One watchman per entry.</summary>
        public List<WatchPost> Posts { get; set; } = [];

        public Map GetMap() => Server.Map.Parse(Map);
    }

    public class WatchPost
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Z { get; set; }

        /// <summary>Name of a route in <see cref="Routes" />; omit for a stationary post.</summary>
        public string Route { get; set; }

        public Point3D ToPoint3D() => new(X, Y, Z);
    }

    public class ShopsConfig
    {
        public string Map { get; set; } = "Trammel";

        /// <summary>
        ///     Vendor types that must never close, whatever else the config says. Players need these
        ///     at all hours and OSI's own towns keep them open. Listing them here rather than
        ///     hard-coding makes the rule visible when this file is copied for another town, and
        ///     guards against someone later adding one to <see cref="Shops" /> by mistake.
        /// </summary>
        public List<string> NeverCloses { get; set; } = [];

        public List<ShopConfig> Shops { get; set; } = [];

        /// <summary>Area the "we're closed" reply and the greyed buy menu apply to.</summary>
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }

        public Map GetMap() => Server.Map.Parse(Map);

        public Rectangle2D ToBounds() => new(X, Y, Width, Height);

        public bool IsExcluded(string vendorType) =>
            NeverCloses?.Exists(n => n.InsensitiveEquals(vendorType)) == true;
    }

    public class ShopConfig
    {
        /// <summary>Class name of the vendor, e.g. "Baker".</summary>
        public string Vendor { get; set; }

        /// <summary>Where the vendor stands during the day - used to find them at dusk.</summary>
        public int X { get; set; }
        public int Y { get; set; }
        public int Z { get; set; }

        /// <summary>
        ///     Route from the shop to their lodgings. Reversed at dawn. The last node should be far
        ///     enough from the shop that the shop reads as visibly empty.
        /// </summary>
        public List<RouteNode> HomeRoute { get; set; } = [];

        public Point3D ToPoint3D() => new(X, Y, Z);
    }

    public class RoutedTownsfolkConfig
    {
        public string Name { get; set; }
        public string Title { get; set; }
        public string Route { get; set; }
        public string Map { get; set; } = "Trammel";

        /// <summary>"male", "female", or anything else for random.</summary>
        public string Body { get; set; } = "random";

        public Map GetMap() => Server.Map.Parse(Map);
    }
}
