using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;
using Server.Json;
using Server.Logging;
using Server.Mobiles;

namespace Server.Custom;

/// <summary>
///     Everything town-specific lives here rather than in code, so a second town is a second JSON
///     file and no new C#.
///     <para>
///         Every member carries an explicit <see cref="JsonPropertyNameAttribute" />. This is not
///         decoration: <c>JsonConfig</c>'s options set neither <c>PropertyNameCaseInsensitive</c>
///         nor a naming policy, so binding is case-SENSITIVE and a PascalCase property silently
///         binds nothing against a camelCase key. The failure is invisible - deserialization
///         "succeeds" and every section comes back null.
///     </para>
/// </summary>
public class TownScheduleConfig
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(TownScheduleConfig));

    public const string ConfigPath = "Data/Custom/britain-daily-life.json";

    public static TownScheduleConfig Current { get; private set; }

    /// <summary>The point whose local in-game time drives this town's schedule.</summary>
    [JsonPropertyName("anchor")]
    public AnchorPoint Anchor { get; set; }

    [JsonPropertyName("tavern")]
    public TavernConfig Tavern { get; set; }

    /// <summary>
    ///     Named routes, referenced by watchmen and routed townsfolk. Null rather than empty by
    ///     default so an absent section is distinguishable from an explicitly empty one - the first
    ///     is a mistake, the second is a choice.
    /// </summary>
    [JsonPropertyName("routes")]
    public Dictionary<string, List<RouteNode>> Routes { get; set; }

    [JsonPropertyName("watch")]
    public WatchConfig Watch { get; set; }

    /// <summary>Townsfolk who walk a route all day - a courier, a farmer.</summary>
    [JsonPropertyName("townsfolk")]
    public List<RoutedTownsfolkConfig> Townsfolk { get; set; }

    [JsonPropertyName("shops")]
    public ShopsConfig Shops { get; set; }

    public List<RouteNode> GetRoute(string name)
    {
        if (string.IsNullOrEmpty(name) || Routes == null)
        {
            return null;
        }

        return Routes.GetValueOrDefault(name);
    }

    /// <summary>
    ///     Boot-time load. A failure here is loud: the town is a visible feature, and an empty
    ///     Britain that logs nothing but "loaded" is far worse than a startup error nobody can miss.
    /// </summary>
    public static void Configure()
    {
        if (!TryLoad(out var error))
        {
            logger.Error("Daily life is INACTIVE - {Path} was not loaded: {Error}", ConfigPath, error);
        }
    }

    /// <summary>
    ///     Reads and validates the config file, replacing <see cref="Current" /> only on success.
    ///     <para>
    ///         A malformed file must not take the town down: a failed reload leaves the running
    ///         config in place and reports why. That is the difference between a typo costing a
    ///         retry and a typo emptying the town.
    ///     </para>
    ///     <para>
    ///         Validation is deliberately strict, because every check here replaces a silent runtime
    ///         failure: an unknown map name threw a <c>FormatException</c> from deep inside a spawn
    ///         call, a mistyped vendor class was an invisible no-op, a body of "Male " fell through
    ///         to random, and a route leg longer than the pathfinder's search box degraded to
    ///         walking into scenery. None of those were reported anywhere a builder would look.
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

            if (!loaded.Validate(out error))
            {
                logger.Error("Invalid town schedule config at {Path}: {Error}", ConfigPath, error);
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

    /// <summary>
    ///     Collects every problem rather than stopping at the first, so one reload reports the whole
    ///     list instead of making a builder fix typos one restart at a time.
    /// </summary>
    internal bool Validate(out string error)
    {
        var errors = new List<string>();

        // Absent sections are checked first and short-circuit: every later check dereferences them.
        AddIfNull(Anchor, "anchor", errors);
        AddIfNull(Tavern, "tavern", errors);
        AddIfNull(Routes, "routes", errors);
        AddIfNull(Watch, "watch", errors);
        AddIfNull(Townsfolk, "townsfolk", errors);
        AddIfNull(Shops, "shops", errors);

        if (errors.Count > 0)
        {
            error = string.Join("; ", errors);
            return false;
        }

        ValidateMap("anchor.map", Anchor.Map, errors);
        ValidateMap("tavern.map", Tavern.Map, errors);
        var watchMap = ValidateMap("watch.map", Watch.Map, errors);
        ValidateMap("shops.map", Shops.Map, errors);

        ValidateBounds("tavern", Tavern.Width, Tavern.Height, errors);
        ValidateBounds("shops", Shops.Width, Shops.Height, errors);

        if (Tavern.PatronCount <= 0)
        {
            errors.Add($"tavern.patronCount must be greater than zero (found {Tavern.PatronCount})");
        }

        ValidateRoutes(errors);
        ValidateRouteConsumers(watchMap, errors);
        ValidateShops(errors);

        error = errors.Count > 0 ? string.Join("; ", errors) : null;
        return errors.Count == 0;
    }

    private void ValidateRoutes(List<string> errors)
    {
        foreach (var (name, nodes) in Routes)
        {
            if (nodes == null || nodes.Count == 0)
            {
                errors.Add($"route '{name}' is empty");
                continue;
            }

            // Routes cycle - RoutedTownsfolk advances with (index + 1) % count - so the closing leg
            // from the last node back to the first is walked too and has to be checked.
            if (nodes.Count == 1)
            {
                continue;
            }

            for (var i = 0; i < nodes.Count; i++)
            {
                var from = nodes[i];
                var to = nodes[(i + 1) % nodes.Count];

                ValidateLeg($"route '{name}' leg {i}", from.X, from.Y, to.X, to.Y, errors);
            }
        }
    }

    /// <summary>
    ///     Checks that every referenced route exists, and that no route is walked from two different
    ///     facets. Routes carry no map of their own - they inherit the facet of whoever references
    ///     them - so this is the only place a Trammel watchman and a Felucca courier sharing one
    ///     route can be caught.
    /// </summary>
    private void ValidateRouteConsumers(Map watchMap, List<string> errors)
    {
        var facets = new Dictionary<string, Map>(StringComparer.OrdinalIgnoreCase);

        foreach (var post in Watch.Posts)
        {
            // An omitted route is a stationary post, which is a supported configuration.
            if (string.IsNullOrEmpty(post.Route))
            {
                continue;
            }

            CheckRouteReference($"watch post ({post.X}, {post.Y})", post.Route, watchMap, facets, errors);
        }

        foreach (var entry in Townsfolk)
        {
            var who = string.IsNullOrEmpty(entry.Name) ? "townsfolk entry" : $"townsfolk '{entry.Name}'";
            var map = ValidateMap($"{who} map", entry.Map, errors);

            if (string.IsNullOrEmpty(entry.Route))
            {
                errors.Add($"{who} has no route");
            }
            else
            {
                CheckRouteReference(who, entry.Route, map, facets, errors);
            }

            if (!entry.Body.InsensitiveEquals("male") && !entry.Body.InsensitiveEquals("female") &&
                !entry.Body.InsensitiveEquals("random"))
            {
                errors.Add($"{who} has body '{entry.Body}'; expected male, female or random");
            }
        }
    }

    private void CheckRouteReference(
        string who, string route, Map map, Dictionary<string, Map> facets, List<string> errors
    )
    {
        if (GetRoute(route) == null)
        {
            errors.Add($"{who} references unknown route '{route}'");
            return;
        }

        if (map == null)
        {
            return;
        }

        if (facets.TryGetValue(route, out var existing))
        {
            if (existing != map)
            {
                errors.Add($"route '{route}' is walked on both {existing.Name} and {map.Name}");
            }

            return;
        }

        facets[route] = map;
    }

    private void ValidateShops(List<string> errors)
    {
        foreach (var name in Shops.NeverCloses)
        {
            ValidateVendorType("shops.neverCloses", name, errors);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var shop in Shops.Shops)
        {
            ValidateVendorType("shops.shops", shop.Vendor, errors);

            if (!string.IsNullOrWhiteSpace(shop.Vendor) && !seen.Add(shop.Vendor))
            {
                // Two entries for one type would fight over the same vendor: whichever ran last
                // would win, silently, and the other shop would never open.
                errors.Add($"shops.shops has more than one entry for vendor '{shop.Vendor}'");
            }

            if (shop.HomeRoute == null || shop.HomeRoute.Count == 0)
            {
                errors.Add($"shop '{shop.Vendor}' has no homeRoute");
                continue;
            }

            var who = $"shop '{shop.Vendor}'";

            ValidateLeg($"{who} leg to homeRoute[0]", shop.X, shop.Y, shop.HomeRoute[0].X, shop.HomeRoute[0].Y, errors);

            for (var i = 0; i < shop.HomeRoute.Count - 1; i++)
            {
                var from = shop.HomeRoute[i];
                var to = shop.HomeRoute[i + 1];

                ValidateLeg($"{who} homeRoute leg {i}", from.X, from.Y, to.X, to.Y, errors);
            }
        }
    }

    private static void AddIfNull(object section, string name, List<string> errors)
    {
        if (section == null)
        {
            errors.Add($"'{name}' section is missing");
        }
    }

    private static Map ValidateMap(string where, string name, List<string> errors)
    {
        if (!Map.TryParse(name, null, out var map) || map == null || map == Map.Internal)
        {
            errors.Add($"{where} '{name}' is not a valid facet");
            return null;
        }

        return map;
    }

    private static void ValidateBounds(string where, int width, int height, List<string> errors)
    {
        if (width <= 0 || height <= 0)
        {
            errors.Add($"{where} bounds must have a positive width and height (found {width}x{height})");
        }
    }

    /// <summary>
    ///     A* searches a fixed box around the midpoint of a leg, so a leg longer than
    ///     <see cref="RoutedTownsfolk.MaxLegDistance" /> cannot be pathed at all - the walker grinds
    ///     against scenery until the leg times out and is skipped.
    /// </summary>
    private static void ValidateLeg(string where, int x1, int y1, int x2, int y2, List<string> errors)
    {
        var distance = Math.Max(Math.Abs(x2 - x1), Math.Abs(y2 - y1));

        if (distance > RoutedTownsfolk.MaxLegDistance)
        {
            errors.Add(
                $"{where} is {distance} tiles, over the {RoutedTownsfolk.MaxLegDistance}-tile pathfinding limit"
            );
        }
    }

    /// <summary>
    ///     Vendors are matched at runtime by <c>GetType().Name</c>, so a typo produces no vendor and
    ///     no log line. Resolve the name once here instead.
    /// </summary>
    private static void ValidateVendorType(string where, string typeName, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            errors.Add($"{where} has an entry with no vendor type name");
            return;
        }

        var type = AssemblyHandler.FindTypeByName(typeName);

        if (type == null)
        {
            errors.Add($"{where} '{typeName}' is not a known type");
            return;
        }

        if (!type.IsSubclassOf(typeof(BaseVendor)))
        {
            errors.Add($"{where} '{typeName}' is not a BaseVendor");
        }
    }

    public class AnchorPoint
    {
        [JsonPropertyName("x")]
        public int X { get; set; }

        [JsonPropertyName("y")]
        public int Y { get; set; }

        [JsonPropertyName("z")]
        public int Z { get; set; }

        [JsonPropertyName("map")]
        public string Map { get; set; } = "Trammel";

        public Map GetMap() => Server.Map.Parse(Map);

        public Point3D ToPoint3D() => new(X, Y, Z);
    }

    public class TavernConfig
    {
        /// <summary>Rectangle patrons are placed inside, in world coordinates.</summary>
        [JsonPropertyName("x")]
        public int X { get; set; }

        [JsonPropertyName("y")]
        public int Y { get; set; }

        [JsonPropertyName("width")]
        public int Width { get; set; }

        [JsonPropertyName("height")]
        public int Height { get; set; }

        /// <summary>Authoritative: patrons spawn at exactly this Z or not at all.</summary>
        [JsonPropertyName("z")]
        public int Z { get; set; }

        [JsonPropertyName("map")]
        public string Map { get; set; } = "Trammel";

        /// <summary>How many patrons show up after dark.</summary>
        [JsonPropertyName("patronCount")]
        public int PatronCount { get; set; } = 4;

        /// <summary>Lines patrons say to themselves while drinking.</summary>
        [JsonPropertyName("chatter")]
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
        [JsonPropertyName("x")]
        public int X { get; set; }

        [JsonPropertyName("y")]
        public int Y { get; set; }

        /// <summary>
        ///     Used only for arrival detection - the pathing goal overrides it with
        ///     <c>Map.GetAverageZ</c>. A wrong Z makes a node time out and be skipped, not
        ///     mispositioned.
        /// </summary>
        [JsonPropertyName("z")]
        public int Z { get; set; }

        /// <summary>Optional line spoken on arrival.</summary>
        [JsonPropertyName("say")]
        public string Say { get; set; }

        public Point3D ToPoint3D() => new(X, Y, Z);
    }

    public class WatchConfig
    {
        [JsonPropertyName("map")]
        public string Map { get; set; } = "Trammel";

        /// <summary>Where each watchman is placed at dusk. One watchman per entry.</summary>
        [JsonPropertyName("posts")]
        public List<WatchPost> Posts { get; set; } = [];

        public Map GetMap() => Server.Map.Parse(Map);
    }

    public class WatchPost
    {
        [JsonPropertyName("x")]
        public int X { get; set; }

        [JsonPropertyName("y")]
        public int Y { get; set; }

        [JsonPropertyName("z")]
        public int Z { get; set; }

        /// <summary>Name of a route in <see cref="Routes" />; omit for a stationary post.</summary>
        [JsonPropertyName("route")]
        public string Route { get; set; }

        public Point3D ToPoint3D() => new(X, Y, Z);
    }

    public class ShopsConfig
    {
        [JsonPropertyName("map")]
        public string Map { get; set; } = "Trammel";

        /// <summary>
        ///     Vendor types that must never close, whatever else the config says. Players need these
        ///     at all hours and OSI's own towns keep them open. Listing them here rather than
        ///     hard-coding makes the rule visible when this file is copied for another town, and
        ///     guards against someone later adding one to <see cref="Shops" /> by mistake.
        /// </summary>
        [JsonPropertyName("neverCloses")]
        public List<string> NeverCloses { get; set; } = [];

        [JsonPropertyName("shops")]
        public List<ShopConfig> Shops { get; set; } = [];

        /// <summary>Area the "we're closed" reply and the greyed buy menu apply to.</summary>
        [JsonPropertyName("x")]
        public int X { get; set; }

        [JsonPropertyName("y")]
        public int Y { get; set; }

        [JsonPropertyName("width")]
        public int Width { get; set; }

        [JsonPropertyName("height")]
        public int Height { get; set; }

        public Map GetMap() => Server.Map.Parse(Map);

        public Rectangle2D ToBounds() => new(X, Y, Width, Height);

        public bool IsExcluded(string vendorType) =>
            NeverCloses?.Exists(n => n.InsensitiveEquals(vendorType)) == true;
    }

    public class ShopConfig
    {
        /// <summary>Class name of the vendor, e.g. "Baker".</summary>
        [JsonPropertyName("vendor")]
        public string Vendor { get; set; }

        /// <summary>Where the vendor stands during the day - used to find them at dusk.</summary>
        [JsonPropertyName("x")]
        public int X { get; set; }

        [JsonPropertyName("y")]
        public int Y { get; set; }

        [JsonPropertyName("z")]
        public int Z { get; set; }

        /// <summary>
        ///     Route from the shop to their lodgings. Reversed at dawn. The last node should be far
        ///     enough from the shop that the shop reads as visibly empty.
        /// </summary>
        [JsonPropertyName("homeRoute")]
        public List<RouteNode> HomeRoute { get; set; } = [];

        public Point3D ToPoint3D() => new(X, Y, Z);
    }

    public class RoutedTownsfolkConfig
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; }

        [JsonPropertyName("route")]
        public string Route { get; set; }

        [JsonPropertyName("map")]
        public string Map { get; set; } = "Trammel";

        /// <summary>"male", "female" or "random". Validated on load - a typo is an error.</summary>
        [JsonPropertyName("body")]
        public string Body { get; set; } = "random";

        public Map GetMap() => Server.Map.Parse(Map);
    }
}
