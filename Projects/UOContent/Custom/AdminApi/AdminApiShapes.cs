using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Server.Custom.AdminApi;

/// <summary>
///     Projects the shard's config files into one flat vocabulary of rectangles, polylines and
///     points, and applies edits back.
///     <para>
///         The editor never speaks any of the underlying schemas. That matters because they do not
///         agree with each other: the tavern rectangle carries a Z and the shop district does not,
///         a route is a named entry in a dictionary but a shop's walk home is an inline array, and
///         the facet is stored in five different places. Teaching a canvas about all of that would
///         put shard-specific knowledge in the browser, where it cannot be tested. Here it is one
///         adapter per layer, and the wire format stays the same shape for every layer.
///     </para>
///     <para>
///         Each shape carries a JSON Pointer identifying where it came from, so applying an edit
///         needs no server-side index and no stable ids in the files themselves.
///     </para>
/// </summary>
internal static class AdminApiShapes
{
    public const string ZonesLayer = "zones";
    public const string DailyLifeLayer = "dailylife";
    public const string SpawnersLayer = "spawners";

    /// <summary>One editable thing on the map.</summary>
    internal sealed class Shape
    {
        public string Layer { get; init; }

        public string Id { get; init; }

        /// <summary>"rect", "polyline" or "point".</summary>
        public string Kind { get; init; }

        public string Map { get; init; }

        public string Label { get; init; }

        /// <summary>File this shape lives in, relative to the Distribution directory.</summary>
        public string File { get; init; }

        /// <summary>JSON Pointer to the node an edit should be applied to.</summary>
        public string Pointer { get; init; }

        /// <summary>[x, y, width, height] when <see cref="Kind" /> is "rect".</summary>
        public int[] Rect { get; init; }

        /// <summary>[[x, y, z], ...] when "polyline" or "point".</summary>
        public int[][] Points { get; init; }

        /// <summary>Everything the property panel shows; free-form per layer.</summary>
        public Dictionary<string, object> Props { get; init; }

        /// <summary>Set for shapes derived from data the editor cannot write back.</summary>
        public bool ReadOnly { get; init; }
    }

    /// <summary>An edit from the editor. Exactly one of the geometry fields is normally set.</summary>
    internal sealed class Edit
    {
        public string Layer { get; set; }

        public string File { get; set; }

        public string Pointer { get; set; }

        public int[] Rect { get; set; }

        public int[][] Points { get; set; }

        public Dictionary<string, JsonNode> Props { get; set; }
    }

    public static List<Shape> All()
    {
        var shapes = new List<Shape>();

        AddZones(shapes);
        AddDailyLife(shapes);
        AddSpawners(shapes);

        return shapes;
    }

    // --- zones -------------------------------------------------------------------------------

    private static void AddZones(List<Shape> shapes)
    {
        var zones = RestrictedZoneSystem.Zones;

        for (var i = 0; i < zones.Count; i++)
        {
            var zone = zones[i];

            shapes.Add(
                new Shape
                {
                    Layer = ZonesLayer,
                    Id = $"zone:{zone.Name}",
                    Kind = "rect",
                    Map = zone.MapName,
                    Label = zone.Name,
                    File = RestrictedZoneSystem.ConfigPath,
                    Pointer = $"/zones/{i}",
                    Rect = [zone.X, zone.Y, zone.Width, zone.Height],
                    Props = new Dictionary<string, object>
                    {
                        ["name"] = zone.Name,
                        ["map"] = zone.MapName
                    }
                }
            );
        }
    }

    // --- daily life --------------------------------------------------------------------------

    private static void AddDailyLife(List<Shape> shapes)
    {
        var config = TownScheduleConfig.Current;

        if (config == null)
        {
            return;
        }

        var file = TownScheduleConfig.ConfigPath;

        if (config.Anchor != null)
        {
            shapes.Add(
                new Shape
                {
                    Layer = DailyLifeLayer,
                    Id = "anchor",
                    Kind = "point",
                    Map = config.Anchor.Map,
                    Label = "Clock anchor",
                    File = file,
                    Pointer = "/anchor",
                    Points = [[config.Anchor.X, config.Anchor.Y, config.Anchor.Z]],
                    Props = new Dictionary<string, object> { ["map"] = config.Anchor.Map }
                }
            );
        }

        if (config.Tavern != null)
        {
            shapes.Add(
                new Shape
                {
                    Layer = DailyLifeLayer,
                    Id = "tavern",
                    Kind = "rect",
                    Map = config.Tavern.Map,
                    Label = "Tavern",
                    File = file,
                    Pointer = "/tavern",
                    Rect = [config.Tavern.X, config.Tavern.Y, config.Tavern.Width, config.Tavern.Height],
                    Props = new Dictionary<string, object>
                    {
                        ["map"] = config.Tavern.Map,

                        // Authoritative, unlike a route node's Z: patrons spawn at exactly this
                        // height or not at all.
                        ["z"] = config.Tavern.Z,
                        ["patronCount"] = config.Tavern.PatronCount
                    }
                }
            );
        }

        if (config.Shops != null)
        {
            shapes.Add(
                new Shape
                {
                    Layer = DailyLifeLayer,
                    Id = "shop-district",
                    Kind = "rect",
                    Map = config.Shops.Map,
                    Label = "Shop district",
                    File = file,
                    Pointer = "/shops",
                    Rect = [config.Shops.X, config.Shops.Y, config.Shops.Width, config.Shops.Height],
                    Props = new Dictionary<string, object> { ["map"] = config.Shops.Map }
                }
            );

            for (var i = 0; i < config.Shops.Shops.Count; i++)
            {
                var shop = config.Shops.Shops[i];

                shapes.Add(
                    new Shape
                    {
                        Layer = DailyLifeLayer,
                        Id = $"shop:{shop.Vendor}",
                        Kind = "point",
                        Map = config.Shops.Map,
                        Label = shop.Vendor,
                        File = file,
                        Pointer = $"/shops/shops/{i}",
                        Points = [[shop.X, shop.Y, shop.Z]],
                        Props = new Dictionary<string, object> { ["vendor"] = shop.Vendor }
                    }
                );

                shapes.Add(
                    new Shape
                    {
                        Layer = DailyLifeLayer,
                        Id = $"shop-home:{shop.Vendor}",
                        Kind = "polyline",
                        Map = config.Shops.Map,
                        Label = $"{shop.Vendor} walk home",
                        File = file,
                        Pointer = $"/shops/shops/{i}/homeRoute",
                        Points = ToPoints(shop.HomeRoute),
                        Props = new Dictionary<string, object> { ["vendor"] = shop.Vendor }
                    }
                );
            }
        }

        if (config.Watch != null)
        {
            for (var i = 0; i < config.Watch.Posts.Count; i++)
            {
                var post = config.Watch.Posts[i];

                shapes.Add(
                    new Shape
                    {
                        Layer = DailyLifeLayer,
                        Id = $"watch-post:{i}",
                        Kind = "point",
                        Map = config.Watch.Map,
                        Label = $"Watch post {i + 1}",
                        File = file,
                        Pointer = $"/watch/posts/{i}",
                        Points = [[post.X, post.Y, post.Z]],
                        Props = new Dictionary<string, object> { ["route"] = post.Route }
                    }
                );
            }
        }

        if (config.Routes == null)
        {
            return;
        }

        foreach (var (name, nodes) in config.Routes)
        {
            shapes.Add(
                new Shape
                {
                    Layer = DailyLifeLayer,
                    Id = $"route:{name}",
                    Kind = "polyline",
                    // Routes carry no facet of their own; they inherit it from whoever walks them,
                    // which config validation already proves is a single facet.
                    Map = RouteFacet(config, name),
                    Label = name,
                    File = file,
                    Pointer = $"/routes/{Escape(name)}",
                    Points = ToPoints(nodes),
                    Props = new Dictionary<string, object>
                    {
                        ["closed"] = true,
                        ["says"] = Says(nodes)
                    }
                }
            );
        }
    }

    private static string RouteFacet(TownScheduleConfig config, string route)
    {
        foreach (var entry in config.Townsfolk)
        {
            if (route.InsensitiveEquals(entry.Route))
            {
                return entry.Map;
            }
        }

        foreach (var post in config.Watch.Posts)
        {
            if (route.InsensitiveEquals(post.Route))
            {
                return config.Watch.Map;
            }
        }

        return config.Anchor?.Map;
    }

    // --- spawners ----------------------------------------------------------------------------

    private static void AddSpawners(List<Shape> shapes)
    {
        foreach (var path in AdminApiFiles.SpawnFiles())
        {
            var relative = AdminApiFiles.RelativePath(path);

            JsonNode root;

            try
            {
                root = AdminApiFiles.Load(path);
            }
            catch (Exception)
            {
                // A spawn file the editor cannot parse is skipped rather than failing the whole
                // layer; the import command reports the real error.
                continue;
            }

            if (root is not JsonArray entries)
            {
                continue;
            }

            for (var i = 0; i < entries.Count; i++)
            {
                if (entries[i] is not JsonObject spawner)
                {
                    continue;
                }

                var location = spawner["location"] as JsonArray;

                if (location is not { Count: >= 2 })
                {
                    continue;
                }

                var name = spawner["name"]?.GetValue<string>();

                shapes.Add(
                    new Shape
                    {
                        Layer = SpawnersLayer,
                        Id = $"spawner:{relative}:{i}",
                        Kind = "point",
                        Map = spawner["map"]?.GetValue<string>(),
                        Label = string.IsNullOrEmpty(name) ? $"Spawner {i}" : name,
                        File = relative,
                        Pointer = $"/{i}/location",
                        Points =
                        [
                            [
                                location[0].GetValue<int>(),
                                location[1].GetValue<int>(),
                                location.Count > 2 ? location[2].GetValue<int>() : 0
                            ]
                        ],
                        Props = new Dictionary<string, object>
                        {
                            ["name"] = name,
                            ["count"] = spawner["count"]?.GetValue<int>(),
                            ["entries"] = EntryNames(spawner["entries"] as JsonArray)
                        }
                    }
                );
            }
        }
    }

    private static List<string> EntryNames(JsonArray entries)
    {
        var names = new List<string>();

        if (entries == null)
        {
            return names;
        }

        foreach (var entry in entries)
        {
            var name = entry?["name"]?.GetValue<string>();

            if (name != null)
            {
                names.Add(name);
            }
        }

        return names;
    }

    // --- applying edits ------------------------------------------------------------------------

    /// <summary>
    ///     Applies a batch of edits, grouped by file so each file is read, patched and written once.
    ///     All-or-nothing per file: a batch that fails validation leaves the file untouched.
    /// </summary>
    public static bool Apply(List<Edit> edits, out string error)
    {
        var byFile = new Dictionary<string, List<Edit>>(StringComparer.OrdinalIgnoreCase);

        foreach (var edit in edits)
        {
            if (AdminApiFiles.Resolve(edit.File) == null)
            {
                error = $"'{edit.File}' is not an editable file.";
                return false;
            }

            if (!byFile.TryGetValue(edit.File, out var list))
            {
                byFile[edit.File] = list = [];
            }

            list.Add(edit);
        }

        foreach (var (file, fileEdits) in byFile)
        {
            var full = AdminApiFiles.Resolve(file);
            var root = AdminApiFiles.Load(full);

            foreach (var edit in fileEdits)
            {
                if (!ApplyOne(root, edit, out error))
                {
                    return false;
                }
            }

            AdminApiFiles.Save(full, root);
        }

        error = null;
        return true;
    }

    private static bool ApplyOne(JsonNode root, Edit edit, out string error)
    {
        var target = AdminApiFiles.Follow(root, edit.Pointer);

        if (target == null)
        {
            error = $"'{edit.Pointer}' does not exist in {edit.File}.";
            return false;
        }

        if (edit.Rect != null)
        {
            if (edit.Rect.Length != 4 || edit.Rect[2] <= 0 || edit.Rect[3] <= 0)
            {
                error = "A rectangle needs [x, y, width, height] with a positive size.";
                return false;
            }

            if (target is not JsonObject rect)
            {
                error = $"'{edit.Pointer}' is not an object.";
                return false;
            }

            rect["x"] = edit.Rect[0];
            rect["y"] = edit.Rect[1];
            rect["width"] = edit.Rect[2];
            rect["height"] = edit.Rect[3];
        }

        if (edit.Points is { Length: > 0 })
        {
            if (!ApplyPoints(root, target, edit, out error))
            {
                return false;
            }
        }

        if (edit.Props != null)
        {
            if (target is not JsonObject props)
            {
                error = $"'{edit.Pointer}' is not an object.";
                return false;
            }

            foreach (var (key, value) in edit.Props)
            {
                props[key] = value?.DeepClone();
            }
        }

        error = null;
        return true;
    }

    /// <summary>
    ///     Moves a point, or rewrites a polyline in place.
    ///     <para>
    ///         Existing nodes are edited rather than replaced so that fields the editor does not
    ///         model - a route node's <c>say</c> above all - survive a drag. Only trailing nodes are
    ///         added or removed, so inserting in the middle shifts the lines that follow; the
    ///         property panel is where that gets fixed.
    ///     </para>
    /// </summary>
    private static bool ApplyPoints(JsonNode root, JsonNode target, Edit edit, out string error)
    {
        // A spawner's location is an [x, y, z] array rather than an object.
        if (target is JsonArray array && edit.Points.Length == 1 && array.Count >= 2
            && array[0] is JsonValue)
        {
            var moved = edit.Points[0];

            array[0] = moved[0];
            array[1] = moved[1];

            if (array.Count > 2 && moved.Length > 2)
            {
                array[2] = moved[2];
            }

            error = null;
            return true;
        }

        if (target is JsonObject point && edit.Points.Length == 1)
        {
            point["x"] = edit.Points[0][0];
            point["y"] = edit.Points[0][1];

            if (edit.Points[0].Length > 2)
            {
                point["z"] = edit.Points[0][2];
            }

            error = null;
            return true;
        }

        if (target is not JsonArray nodes)
        {
            error = $"'{edit.Pointer}' is not a list of points.";
            return false;
        }

        while (nodes.Count > edit.Points.Length)
        {
            nodes.RemoveAt(nodes.Count - 1);
        }

        for (var i = 0; i < edit.Points.Length; i++)
        {
            var moved = edit.Points[i];

            if (i < nodes.Count && nodes[i] is JsonObject existing)
            {
                existing["x"] = moved[0];
                existing["y"] = moved[1];
                existing["z"] = moved.Length > 2 ? moved[2] : 0;
                continue;
            }

            nodes.Add(
                new JsonObject
                {
                    ["x"] = moved[0],
                    ["y"] = moved[1],
                    ["z"] = moved.Length > 2 ? moved[2] : 0
                }
            );
        }

        _ = root;
        error = null;
        return true;
    }

    // --- helpers -----------------------------------------------------------------------------

    private static int[][] ToPoints(List<TownScheduleConfig.RouteNode> nodes)
    {
        if (nodes == null)
        {
            return [];
        }

        var points = new int[nodes.Count][];

        for (var i = 0; i < nodes.Count; i++)
        {
            points[i] = [nodes[i].X, nodes[i].Y, nodes[i].Z];
        }

        return points;
    }

    private static List<string> Says(List<TownScheduleConfig.RouteNode> nodes)
    {
        var says = new List<string>(nodes.Count);

        foreach (var node in nodes)
        {
            says.Add(node.Say);
        }

        return says;
    }

    private static string Escape(string token) => token.Replace("~", "~0").Replace("/", "~1");
}
