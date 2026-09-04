using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using Server.Engines.Spawners;
using Server.Logging;
using Server.Mobiles;

namespace Server.Custom.AdminApi;

/// <summary>
///     Creating and deleting shapes. Moving one is a <c>PATCH</c> through
///     <see cref="AdminApiShapes" />; adding or removing one changes the shape of the file and is
///     handled here.
///     <para>
///         Names are resolved and checked here rather than left to the reload. A vendor or creature
///         type that does not exist is refused with a reason the editor can show, because the
///         alternatives are both silent: a mistyped vendor makes <c>ShopScheduleSystem</c> skip the
///         shop with no log line at all, and a mistyped creature makes the spawner log to
///         <c>spawner-errors.log</c> and carry on.
///     </para>
/// </summary>
internal static class AdminApiMutations
{
    /// <summary>Matches the defaults <c>[ExportSpawners</c> writes, so hand- and editor-made files agree.</summary>
    private const string DefaultMinDelay = "00:05:00";
    private const string DefaultMaxDelay = "00:10:00";

    internal sealed class CreateRequest
    {
        /// <summary>zone, shop, watchpost, townsfolk, route or spawner.</summary>
        public string Kind { get; set; }

        public string File { get; set; }

        public string Name { get; set; }

        public string Map { get; set; }

        /// <summary>[x, y, width, height] for a zone.</summary>
        public int[] Rect { get; set; }

        /// <summary>[[x, y, z], ...] - one entry for a point, several for a route.</summary>
        public int[][] Points { get; set; }

        public Dictionary<string, JsonNode> Props { get; set; }

        public string Prop(string key) =>
            Props != null && Props.TryGetValue(key, out var value) ? value?.GetValue<string>() : null;

        public int PropInt(string key, int fallback)
        {
            if (Props == null || !Props.TryGetValue(key, out var value) || value == null)
            {
                return fallback;
            }

            return int.TryParse(value.ToString(), out var parsed) ? parsed : fallback;
        }
    }

    internal sealed class DeleteRequest
    {
        public string Layer { get; set; }

        public string File { get; set; }

        public string Pointer { get; set; }
    }

    public static bool Create(CreateRequest request, out string error, out string pointer)
    {
        pointer = null;

        switch (request.Kind)
        {
            case "zone": return CreateZone(request, out error, out pointer);
            case "shop": return CreateShop(request, out error, out pointer);
            case "watchpost": return CreateWatchPost(request, out error, out pointer);
            case "townsfolk": return CreateTownsfolk(request, out error, out pointer);
            case "route": return CreateRoute(request, out error, out pointer);
            case "spawner": return CreateSpawner(request, out error, out pointer);
            default:
                error = $"Unknown shape kind '{request.Kind}'.";
                return false;
        }
    }

    // --- zones -------------------------------------------------------------------------------

    private static bool CreateZone(CreateRequest request, out string error, out string pointer)
    {
        pointer = null;

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            error = "A zone needs a name.";
            return false;
        }

        if (!ValidateMap(request.Map, out error) || !ValidateRect(request.Rect, out error))
        {
            return false;
        }

        return Edit(
            RestrictedZoneSystem.ConfigPath,
            root =>
            {
                // Against the file, not RestrictedZoneSystem.Find: between a create and its reload
                // the file holds zones the live list has never seen, and checking memory would let
                // a duplicate straight through.
                if (AdminApiFiles.Follow(root, "/zones") is JsonArray zones)
                {
                    foreach (var zone in zones)
                    {
                        if (request.Name.InsensitiveEquals(zone?["name"]?.GetValue<string>()))
                        {
                            return (null, $"A restricted zone named '{request.Name}' already exists.");
                        }
                    }
                }

                return (
                    AdminApiFiles.Append(
                        root,
                        "/zones",
                        new JsonObject
                        {
                            ["name"] = request.Name,
                            ["map"] = request.Map,
                            ["x"] = request.Rect[0],
                            ["y"] = request.Rect[1],
                            ["width"] = request.Rect[2],
                            ["height"] = request.Rect[3]
                        }
                    ),
                    null
                );
            },
            out error,
            out pointer
        );
    }

    // --- daily life --------------------------------------------------------------------------

    private static bool CreateShop(CreateRequest request, out string error, out string pointer)
    {
        pointer = null;

        var vendor = request.Prop("vendor");

        if (!ValidateVendor(vendor, out error) || !ValidatePoint(request.Points, out error))
        {
            return false;
        }

        // The walk home is what makes the shop read as empty after dark; without it
        // ShopScheduleSystem skips the entry and config validation rejects the file.
        var home = request.Props != null && request.Props.TryGetValue("homeRoute", out var node)
            ? node as JsonArray
            : null;

        if (home is not { Count: > 0 })
        {
            error = $"Shop '{vendor}' needs a home route with at least one node.";
            return false;
        }

        var shop = new JsonObject
        {
            ["vendor"] = vendor,
            ["x"] = request.Points[0][0],
            ["y"] = request.Points[0][1],
            ["z"] = request.Points[0].Length > 2 ? request.Points[0][2] : 0,
            ["homeRoute"] = home.DeepClone()
        };

        return Edit(
            TownScheduleConfig.ConfigPath,
            root => (AdminApiFiles.Append(root, "/shops/shops", shop), null),
            out error,
            out pointer
        );
    }

    private static bool CreateWatchPost(CreateRequest request, out string error, out string pointer)
    {
        pointer = null;

        if (!ValidatePoint(request.Points, out error))
        {
            return false;
        }

        var post = new JsonObject
        {
            ["x"] = request.Points[0][0],
            ["y"] = request.Points[0][1],
            ["z"] = request.Points[0].Length > 2 ? request.Points[0][2] : 0
        };

        // An omitted route is a stationary post, which is a supported configuration.
        var route = request.Prop("route");

        if (!string.IsNullOrWhiteSpace(route))
        {
            post["route"] = route;
        }

        return Edit(
            TownScheduleConfig.ConfigPath,
            root => (AdminApiFiles.Append(root, "/watch/posts", post), null),
            out error,
            out pointer
        );
    }

    private static bool CreateTownsfolk(CreateRequest request, out string error, out string pointer)
    {
        pointer = null;

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            error = "A townsperson needs a name.";
            return false;
        }

        var route = request.Prop("route");

        if (string.IsNullOrWhiteSpace(route))
        {
            error = "A townsperson needs a route to walk.";
            return false;
        }

        if (!ValidateMap(request.Map, out error))
        {
            return false;
        }

        var body = request.Prop("body") ?? "random";

        if (!body.InsensitiveEquals("male") && !body.InsensitiveEquals("female")
            && !body.InsensitiveEquals("random"))
        {
            error = $"Body '{body}' is not male, female or random.";
            return false;
        }

        var walker = new JsonObject
        {
            ["name"] = request.Name,
            ["title"] = request.Prop("title") ?? "",
            ["route"] = route,
            ["map"] = request.Map,
            ["body"] = body
        };

        return Edit(
            TownScheduleConfig.ConfigPath,
            root => (AdminApiFiles.Append(root, "/townsfolk", walker), null),
            out error,
            out pointer
        );
    }

    private static bool CreateRoute(CreateRequest request, out string error, out string pointer)
    {
        pointer = null;

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            error = "A route needs a name.";
            return false;
        }

        if (request.Points is not { Length: >= 2 })
        {
            error = "A route needs at least two nodes.";
            return false;
        }

        var nodes = new JsonArray();

        foreach (var point in request.Points)
        {
            nodes.Add(
                new JsonObject
                {
                    ["x"] = point[0],
                    ["y"] = point[1],
                    ["z"] = point.Length > 2 ? point[2] : 0
                }
            );
        }

        var key = $"/routes/{AdminApiFiles.Escape(request.Name)}";

        return Edit(
            TownScheduleConfig.ConfigPath,
            root =>
            {
                if (AdminApiFiles.Follow(root, "/routes") is not JsonObject routes)
                {
                    return (null, "The config has no 'routes' section.");
                }

                // Same reasoning as zones: the file is the authority, not TownScheduleConfig.Current.
                foreach (var existing in routes)
                {
                    if (request.Name.InsensitiveEquals(existing.Key))
                    {
                        return (null, $"A route named '{request.Name}' already exists.");
                    }
                }

                routes[request.Name] = nodes;

                return (key, null);
            },
            out error,
            out pointer
        );
    }

    // --- spawners ----------------------------------------------------------------------------

    private static bool CreateSpawner(CreateRequest request, out string error, out string pointer)
    {
        pointer = null;

        if (!ValidateMap(request.Map, out error) || !ValidatePoint(request.Points, out error))
        {
            return false;
        }

        var creature = request.Prop("creature");

        if (!ValidateSpawnType(creature, out error))
        {
            return false;
        }

        var count = Math.Max(1, request.PropInt("count", 1));
        var full = AdminApiFiles.Resolve(request.File);

        if (full == null)
        {
            error = $"'{request.File}' is not an editable spawn file.";
            return false;
        }

        // A new file is a legitimate way to start a town; seed it as an empty array so the append
        // below has something to append to.
        if (!File.Exists(full))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            AdminApiFiles.Save(full, new JsonArray());
        }

        var spawner = new JsonObject
        {
            ["$type"] = "Spawner",

            // Generated here, not by the client: the guid is what makes a re-import replace this
            // spawner rather than stack a second one on top of it.
            ["guid"] = Guid.NewGuid().ToString(),
            ["name"] = string.IsNullOrWhiteSpace(request.Name) ? creature : request.Name,
            ["location"] = new JsonArray(
                request.Points[0][0],
                request.Points[0][1],
                request.Points[0].Length > 2 ? request.Points[0][2] : 0
            ),
            ["map"] = request.Map,
            ["count"] = count,
            ["minDelay"] = DefaultMinDelay,
            ["maxDelay"] = DefaultMaxDelay,
            ["homeRange"] = Math.Max(0, request.PropInt("homeRange", 2)),
            ["entries"] = new JsonArray(
                new JsonObject
                {
                    ["name"] = creature,
                    ["maxCount"] = count,
                    ["probability"] = 100
                }
            )
        };

        return Edit(request.File, root => (AdminApiFiles.Append(root, "", spawner), null), out error, out pointer);
    }

    // --- deletion ----------------------------------------------------------------------------

    /// <summary>
    ///     Removes a shape from its file.
    ///     <para>
    ///         A spawner also has a live counterpart in the world, which removing the entry would
    ///         otherwise leave running forever - re-importing only replaces spawners it finds in a
    ///         file. Delete it by guid here.
    ///     </para>
    ///     <para>
    ///         A shop's vendor is *not* deleted: it belongs to an upstream spawner, not to us. The
    ///         daily-life reload that follows returns it to its shop, because
    ///         <c>ShopScheduleSystem</c> tracks the vendors it drives and releases any that fall out
    ///         of the config. Without that reload the vendor stays wherever the schedule last left
    ///         it, with a wander radius of zero.
    ///     </para>
    /// </summary>
    public static bool Delete(DeleteRequest request, out string error)
    {
        var full = AdminApiFiles.Resolve(request.File);

        if (full == null)
        {
            error = $"'{request.File}' is not an editable file.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.Pointer))
        {
            error = "No pointer supplied.";
            return false;
        }

        var root = AdminApiFiles.Load(full);

        // A shape's pointer addresses what a drag moves, which is not always what a delete removes.
        // A spawner shape points at its `location` array; removing that would leave a spawner with
        // no position rather than no spawner. Step up to the object that owns it.
        var pointer = request.Pointer;

        if (request.Layer == AdminApiShapes.SpawnersLayer
            && pointer.EndsWith("/location", StringComparison.Ordinal))
        {
            pointer = pointer[..^"/location".Length];
        }

        var target = AdminApiFiles.Follow(root, pointer);

        if (target == null)
        {
            error = $"'{pointer}' does not exist in {request.File}.";
            return false;
        }

        if (request.Layer == AdminApiShapes.SpawnersLayer)
        {
            DeleteLiveSpawner(target);
        }

        if (!AdminApiFiles.Remove(root, pointer))
        {
            error = $"Could not remove '{pointer}'.";
            return false;
        }

        AdminApiFiles.Save(full, root);

        error = null;
        return true;
    }

    private static void DeleteLiveSpawner(JsonNode target)
    {
        var raw = (target as JsonObject)?["guid"]?.GetValue<string>();

        if (!Guid.TryParse(raw, out var guid))
        {
            return;
        }

        foreach (var item in World.Items.Values)
        {
            if (item is ISpawner live && live.Guid == guid)
            {
                item.Delete();

                LogFactory.GetLogger(typeof(AdminApiMutations))
                    .Information("Deleted live spawner {Guid} removed from its file by the editor", guid);

                return;
            }
        }
    }

    // --- type lists --------------------------------------------------------------------------

    /// <summary>
    ///     The vendor and creature names the editor offers in its pickers. Built from the loaded
    ///     assemblies so the list is whatever this shard actually has, including anything added
    ///     under <c>Custom/</c>.
    /// </summary>
    public static object Types()
    {
        var vendors = new List<string>();
        var creatures = new List<string>();

        foreach (var assembly in AssemblyHandler.Assemblies)
        {
            Collect(AssemblyHandler.GetTypeCache(assembly).Types, vendors, creatures);
        }

        Collect(AssemblyHandler.GetTypeCache(Core.Assembly).Types, vendors, creatures);

        vendors.Sort(StringComparer.OrdinalIgnoreCase);
        creatures.Sort(StringComparer.OrdinalIgnoreCase);

        return new { vendors, creatures };
    }

    private static void Collect(Type[] types, List<string> vendors, List<string> creatures)
    {
        for (var i = 0; i < types.Length; i++)
        {
            var type = types[i];

            if (type.IsAbstract || !type.IsClass)
            {
                continue;
            }

            if (type.IsSubclassOf(typeof(BaseVendor)))
            {
                vendors.Add(type.Name);
            }

            if (type.IsSubclassOf(typeof(BaseCreature)))
            {
                creatures.Add(type.Name);
            }
        }
    }

    // --- shared ------------------------------------------------------------------------------

    /// <summary>Loads a file, lets <paramref name="mutate" /> change it, and writes it back.</summary>
    private static bool Edit(
        string file, Func<JsonNode, (string Pointer, string Error)> mutate, out string error, out string pointer
    )
    {
        pointer = null;

        var full = AdminApiFiles.Resolve(file);

        if (full == null)
        {
            error = $"'{file}' is not an editable file.";
            return false;
        }

        var root = AdminApiFiles.Load(full);

        var (created, failure) = mutate(root);

        pointer = created;

        if (pointer == null)
        {
            error = failure ?? $"Could not add to {file}; the expected section is missing.";
            return false;
        }

        AdminApiFiles.Save(full, root);

        error = null;
        return true;
    }

    private static bool ValidateMap(string name, out string error)
    {
        if (!Map.TryParse(name, null, out var map) || map == null || map == Map.Internal)
        {
            error = $"'{name}' is not a valid facet.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool ValidateRect(int[] rect, out string error)
    {
        if (rect is not { Length: 4 } || rect[2] <= 0 || rect[3] <= 0)
        {
            error = "A rectangle needs [x, y, width, height] with a positive size.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool ValidatePoint(int[][] points, out string error)
    {
        if (points is not { Length: > 0 } || points[0].Length < 2)
        {
            error = "A location is required.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool ValidateVendor(string name, out string error)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "A shop needs a vendor type.";
            return false;
        }

        var type = AssemblyHandler.FindTypeByName(name);

        if (type == null || !type.IsSubclassOf(typeof(BaseVendor)))
        {
            error = $"'{name}' is not a BaseVendor type.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    ///     Spawners take anything a spawner can build - a creature or an item - resolved the same
    ///     way <c>BaseSpawner</c> resolves it, so a name that passes here is a name that will spawn.
    /// </summary>
    private static bool ValidateSpawnType(string name, out string error)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "A spawner needs something to spawn.";
            return false;
        }

        var type = AssemblyHandler.FindTypeByName(name);

        if (type == null)
        {
            error = $"'{name}' is not a known type.";
            return false;
        }

        if (!typeof(Mobile).IsAssignableFrom(type) && !typeof(Item).IsAssignableFrom(type))
        {
            error = $"'{name}' is neither a mobile nor an item, so a spawner cannot create it.";
            return false;
        }

        error = null;
        return true;
    }
}
