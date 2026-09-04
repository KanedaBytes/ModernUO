using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Server.Items;

namespace Server.Custom.AdminApi;

/// <summary>
///     Read-only context: the stock content the shard already has, drawn underneath the things the
///     editor can change. Placing a zone or a spawner sensibly means seeing what is already there -
///     where the upstream spawns are, where a teleporter will drop someone, which town region a box
///     lands inside.
///     <para>
///         Nothing here is editable. Upstream spawn files are outside the editor's whitelist by
///         design, and regions are rebuilt from <c>regions.json</c> on every boot.
///     </para>
///     <para>
///         Built once and cached per facet. The stock spawn files and <c>regions.json</c> do not
///         change while the server runs; teleporters and moongates are world items, so the cache is
///         dropped whenever spawners are re-imported.
///     </para>
/// </summary>
internal static class AdminApiOverlays
{
    private static Dictionary<string, Facet> _cache;

    private sealed class Facet
    {
        /// <summary>[x, y, label] - a flat triple rather than an object, because there are thousands.</summary>
        public List<object[]> StockSpawners { get; } = [];

        public List<object[]> Teleporters { get; } = [];

        public List<object[]> Moongates { get; } = [];

        public List<object> Regions { get; } = [];
    }

    public static void Invalidate() => _cache = null;

    public static object For(string mapName)
    {
        _cache ??= Build();

        if (mapName == null || !_cache.TryGetValue(mapName, out var facet))
        {
            facet = new Facet();
        }

        return new
        {
            stockSpawners = facet.StockSpawners,
            teleporters = facet.Teleporters,
            moongates = facet.Moongates,
            regions = facet.Regions
        };
    }

    private static Dictionary<string, Facet> Build()
    {
        var facets = new Dictionary<string, Facet>(StringComparer.OrdinalIgnoreCase);

        foreach (var map in Map.AllMaps)
        {
            if (map != null && map != Map.Internal && map.MapID < 0x7F)
            {
                facets[map.Name] = new Facet();
            }
        }

        AddStockSpawners(facets);
        AddWorldItems(facets);
        AddRegions(facets);

        return facets;
    }

    /// <summary>
    ///     Every spawn file except <c>custom/</c>, which is the editable layer and is already served
    ///     as real shapes. Read straight from the files rather than from the world, so a spawner
    ///     shows even when its entry has not been imported yet.
    /// </summary>
    private static void AddStockSpawners(Dictionary<string, Facet> facets)
    {
        var root = Path.Combine(Core.BaseDirectory, "Data", "Spawns");

        if (!Directory.Exists(root))
        {
            return;
        }

        var custom = Path.Combine(root, "custom") + Path.DirectorySeparatorChar;

        foreach (var path in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories))
        {
            if (path.StartsWith(custom, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            JsonNode parsed;

            try
            {
                parsed = AdminApiFiles.Load(path);
            }
            catch (Exception)
            {
                // A spawn file the editor cannot parse is not the editor's problem to report; the
                // import command says so properly.
                continue;
            }

            if (parsed is not JsonArray entries)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (entry is not JsonObject spawner
                    || spawner["location"] is not JsonArray location
                    || location.Count < 2)
                {
                    continue;
                }

                var mapName = spawner["map"]?.GetValue<string>();

                if (mapName == null || !facets.TryGetValue(mapName, out var facet))
                {
                    continue;
                }

                facet.StockSpawners.Add(
                    [
                        location[0].GetValue<int>(),
                        location[1].GetValue<int>(),
                        FirstEntryName(spawner) ?? spawner["name"]?.GetValue<string>() ?? "Spawner"
                    ]
                );
            }
        }
    }

    private static string FirstEntryName(JsonObject spawner) =>
        spawner["entries"] is JsonArray entries && entries.Count > 0
            ? entries[0]?["name"]?.GetValue<string>()
            : null;

    /// <summary>
    ///     Teleporters and moongates, from the world rather than from a file - they are placed by
    ///     world generation and decoration, not described anywhere the editor could read.
    ///     <para>
    ///         Iterates World.Items, which the audit rules discourage. Justified for the same reason
    ///         as the spawner count: this runs once per server run (the result is cached), there is
    ///         no spatial query for "every teleporter anywhere", and the alternative is not offering
    ///         the overlay at all.
    ///     </para>
    /// </summary>
    private static void AddWorldItems(Dictionary<string, Facet> facets)
    {
        foreach (var item in World.Items.Values)
        {
            if (item.Deleted || item.Map == null || item.Map == Map.Internal)
            {
                continue;
            }

            if (!facets.TryGetValue(item.Map.Name, out var facet))
            {
                continue;
            }

            switch (item)
            {
                case Teleporter teleporter:
                    facet.Teleporters.Add(
                        [
                            teleporter.X,
                            teleporter.Y,
                            Destination(teleporter)
                        ]
                    );
                    break;

                // PublicMoongate is the fixed town gate; Moongate covers the rest, including the
                // ones a spell or a quest leaves behind.
                case PublicMoongate:
                case Moongate:
                    facet.Moongates.Add([item.X, item.Y, item.GetType().Name]);
                    break;
            }
        }
    }

    private static string Destination(Teleporter teleporter)
    {
        var destination = teleporter.PointDest;
        var map = teleporter.MapDest;

        if (destination == Point3D.Zero)
        {
            return "Teleporter";
        }

        return map == null || map == teleporter.Map
            ? $"to {destination.X}, {destination.Y}"
            : $"to {map.Name} {destination.X}, {destination.Y}";
    }

    /// <summary>
    ///     Stock regions, from <c>Data/regions.json</c>. Its keys are PascalCase and its rectangles
    ///     are corner pairs, unlike everything the editor writes - it is upstream's format, read
    ///     as-is.
    /// </summary>
    private static void AddRegions(Dictionary<string, Facet> facets)
    {
        var path = Path.Combine(Core.BaseDirectory, "Data", "regions.json");

        if (!File.Exists(path))
        {
            return;
        }

        JsonNode parsed;

        try
        {
            parsed = AdminApiFiles.Load(path);
        }
        catch (JsonException)
        {
            return;
        }

        if (parsed is not JsonArray regions)
        {
            return;
        }

        foreach (var entry in regions)
        {
            if (entry is not JsonObject region)
            {
                continue;
            }

            var mapName = region["Map"]?.GetValue<string>();

            if (mapName == null || !facets.TryGetValue(mapName, out var facet))
            {
                continue;
            }

            var rects = new List<int[]>();

            if (region["Area"] is JsonArray area)
            {
                foreach (var node in area)
                {
                    if (node is not JsonObject rect)
                    {
                        continue;
                    }

                    var x1 = rect["x1"]?.GetValue<int>();
                    var y1 = rect["y1"]?.GetValue<int>();
                    var x2 = rect["x2"]?.GetValue<int>();
                    var y2 = rect["y2"]?.GetValue<int>();

                    if (x1 == null || y1 == null || x2 == null || y2 == null)
                    {
                        continue;
                    }

                    // Corner pair to origin-and-size, the shape everything else in the editor uses.
                    rects.Add(
                        [
                            Math.Min(x1.Value, x2.Value),
                            Math.Min(y1.Value, y2.Value),
                            Math.Abs(x2.Value - x1.Value),
                            Math.Abs(y2.Value - y1.Value)
                        ]
                    );
                }
            }

            if (rects.Count == 0)
            {
                continue;
            }

            facet.Regions.Add(
                new
                {
                    name = region["Name"]?.GetValue<string>() ?? "(unnamed)",
                    type = region["$type"]?.GetValue<string>() ?? "Region",
                    rects
                }
            );
        }
    }
}
