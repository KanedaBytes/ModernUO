using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace Server.Tools.MapExport;

/// <summary>
///     Renders the enabled facets to a PNG tile pyramid for the shard editor.
///     <para>
///         Console output rather than <c>LogFactory</c> is deliberate and follows
///         <c>Projects/BuildTool</c>: this is a standalone CLI whose output is its entire
///         interface, and Serilog's async console sink exists to keep the *server's* logging off
///         the game loop. The audit rule against <c>Console.WriteLine</c> is about the server.
///     </para>
/// </summary>
internal static class Program
{
    private const int DefaultTileSize = 256;

    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        if (Array.IndexOf(args, "--help") >= 0 || Array.IndexOf(args, "-h") >= 0)
        {
            Usage();
            return 0;
        }

        var options = Options.Parse(args);
        var root = options.Distribution ?? FindDistribution();

        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine($"Error: no Distribution directory at {root}. Pass --dist.");
            return 1;
        }

        var clientDirectories = ResolveClientDirectories(options.Client, root);

        if (clientDirectories.Count == 0)
        {
            Console.Error.WriteLine(
                "Error: no client directory. Pass --client, set MODERNUO_CLIENT_PATH, or configure "
                + "dataDirectories in Distribution/Configuration/modernuo.json."
            );

            return 1;
        }

        // The minimal server bootstrap, per Projects/Server.Tests/Fixtures/TestServerInitializer.
        // TileMatrix needs Core.FindDataFile and nothing else - no assemblies, no timers, no world.
        // mocked: true also makes ServerConfiguration.Save() a no-op, so this cannot write over the
        // shard's own configuration.
        Core.ApplicationAssembly = Assembly.GetExecutingAssembly();
        ServerConfiguration.Load(true);

        foreach (var directory in clientDirectories)
        {
            ServerConfiguration.DataDirectories.Add(directory);
        }

        var radarPath = Core.FindDataFile("radarcol.mul", false);

        if (radarPath == null)
        {
            Console.Error.WriteLine(
                $"Error: radarcol.mul not found in {string.Join(", ", clientDirectories)}."
            );

            return 1;
        }

        var radar = RadarColors.Load(radarPath);
        Console.WriteLine($"radarcol.mul: {radar.Count} entries from {radarPath}");

        var definitions = ReadMapDefinitions(root);
        var selected = SelectFacets(definitions, options.Facets, root);

        if (selected.Count == 0)
        {
            Console.Error.WriteLine("Error: no facets selected.");
            return 1;
        }

        var output = options.Output ?? Path.Combine(root, "web", "tiles");
        Directory.CreateDirectory(output);

        Console.WriteLine($"Rendering {selected.Count} facet(s) to {output}");

        foreach (var definition in selected)
        {
            // Built standalone rather than registered in Map.Maps: nothing here needs the global
            // registry, and keeping each facet local means its TileMatrix - which caches every
            // block it reads - can be collected before the next one starts.
            var map = new Map(
                definition.Id,
                definition.Index,
                definition.FileIndex,
                definition.Width,
                definition.Height,
                definition.Season,
                definition.Name,
                definition.Rules
            );

            TilePyramid.Render(map, radar, output, options.TileSize);
        }

        Console.WriteLine("Done.");
        return 0;
    }

    private static void Usage()
    {
        Console.WriteLine(
            """
            Renders each enabled facet to a PNG tile pyramid for the shard editor.

              --dist <path>       Distribution directory (default: found from the repo root)
              --client <path>     UO client files (default: modernuo.json dataDirectories,
                                  or the MODERNUO_CLIENT_PATH environment variable)
              --out <path>        Output root (default: <dist>/web/tiles)
              --facets <list>     Comma-separated facet names (default: those enabled in
                                  Configuration/expansion.json)
              --tile-size <n>     Tile edge in pixels (default: 256)

            Output layout: <out>/<facet>/<z>/<x>/<y>.png, z ascending with detail, one pixel per
            game tile at the deepest level.
            """
        );
    }

    /// <summary>
    ///     Walks up from the running assembly for <c>ModernUO.slnx</c>, the same anchor
    ///     <c>Projects/BuildTool</c> uses to find the repository.
    /// </summary>
    private static string FindDistribution()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ModernUO.slnx")))
            {
                return Path.Combine(directory.FullName, "Distribution");
            }

            directory = directory.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "Distribution");
    }

    private static List<string> ResolveClientDirectories(string explicitPath, string root)
    {
        if (explicitPath != null)
        {
            return [explicitPath];
        }

        var fromEnvironment = Environment.GetEnvironmentVariable("MODERNUO_CLIENT_PATH");

        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return [fromEnvironment];
        }

        // Read the shard's own configuration rather than booting far enough for
        // ServerConfiguration to find it: Core.BaseDirectory here is this tool's bin directory,
        // not Distribution.
        var path = Path.Combine(root, "Configuration", "modernuo.json");
        var directories = new List<string>();

        if (!File.Exists(path))
        {
            return directories;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));

        if (document.RootElement.TryGetProperty("dataDirectories", out var element))
        {
            foreach (var directory in element.EnumerateArray())
            {
                var value = directory.GetString();

                if (!string.IsNullOrWhiteSpace(value))
                {
                    directories.Add(value);
                }
            }
        }

        return directories;
    }

    private static List<MapDefinition> ReadMapDefinitions(string root)
    {
        var path = Path.Combine(root, "Data", "map-definitions.json");

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Map definitions not found at {path}.");
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));

        var definitions = new List<MapDefinition>();

        foreach (var element in document.RootElement.EnumerateArray())
        {
            var index = element.GetProperty("index").GetInt32();

            // 127 is the internal map and 255 is reserved; neither has tile data.
            if (index is 127 or 255)
            {
                continue;
            }

            var rules = MapRules.None;

            if (element.TryGetProperty("rules", out var rulesElement) &&
                Enum.TryParse(rulesElement.GetString(), out MapRules parsed))
            {
                rules = parsed;
            }

            definitions.Add(
                new MapDefinition
                {
                    Index = index,
                    Id = element.GetProperty("id").GetInt32(),
                    FileIndex = element.GetProperty("fileIndex").GetInt32(),
                    Name = element.GetProperty("name").GetString(),
                    Width = element.GetProperty("width").GetInt32(),
                    Height = element.GetProperty("height").GetInt32(),
                    Season = element.GetProperty("season").GetInt32(),
                    Rules = rules
                }
            );
        }

        return definitions;
    }

    /// <summary>
    ///     Honours <c>--facets</c> when given, otherwise the shard's own expansion selection - so
    ///     the tool renders exactly the facets the server serves, and TerMur is skipped under ML.
    /// </summary>
    private static List<MapDefinition> SelectFacets(
        List<MapDefinition> definitions, string[] requested, string root
    )
    {
        var selected = new List<MapDefinition>();

        if (requested is { Length: > 0 })
        {
            foreach (var name in requested)
            {
                var match = definitions.Find(d => d.Name.InsensitiveEquals(name));

                if (match == null)
                {
                    throw new ArgumentException($"Unknown facet '{name}'.");
                }

                selected.Add(match);
            }

            return selected;
        }

        var enabled = ReadEnabledFacets(root);

        foreach (var definition in definitions)
        {
            if (enabled == null || enabled.Contains(definition.Name))
            {
                selected.Add(definition);
            }
        }

        return selected;
    }

    private static HashSet<string> ReadEnabledFacets(string root)
    {
        var path = Path.Combine(root, "Configuration", "expansion.json");

        if (!File.Exists(path))
        {
            return null;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));

        if (!document.RootElement.TryGetProperty("MapSelectionFlags", out var flags))
        {
            return null;
        }

        var enabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var flag in flags.EnumerateObject())
        {
            if (flag.Value.ValueKind == JsonValueKind.True)
            {
                enabled.Add(flag.Name);
            }
        }

        return enabled;
    }

    private sealed class MapDefinition
    {
        public int Index { get; init; }

        public int Id { get; init; }

        public int FileIndex { get; init; }

        public string Name { get; init; }

        public int Width { get; init; }

        public int Height { get; init; }

        public int Season { get; init; }

        public MapRules Rules { get; init; }
    }

    private sealed class Options
    {
        public string Distribution { get; private set; }

        public string Client { get; private set; }

        public string Output { get; private set; }

        public string[] Facets { get; private set; }

        public int TileSize { get; private set; } = DefaultTileSize;

        public static Options Parse(string[] args)
        {
            var options = new Options();

            for (var i = 0; i < args.Length; i++)
            {
                var value = i + 1 < args.Length ? args[i + 1] : null;

                switch (args[i])
                {
                    case "--dist":
                        options.Distribution = Required(value, "--dist");
                        i++;
                        break;
                    case "--client":
                        options.Client = Required(value, "--client");
                        i++;
                        break;
                    case "--out":
                        options.Output = Required(value, "--out");
                        i++;
                        break;
                    case "--facets":
                        options.Facets = Required(value, "--facets")
                            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        i++;
                        break;
                    case "--tile-size":
                        options.TileSize = int.Parse(Required(value, "--tile-size"));
                        i++;
                        break;
                    default:
                        throw new ArgumentException($"Unknown argument '{args[i]}'. Try --help.");
                }
            }

            if (options.TileSize is < 32 or > 2048)
            {
                throw new ArgumentException("--tile-size must be between 32 and 2048.");
            }

            return options;
        }

        private static string Required(string value, string name) =>
            value ?? throw new ArgumentException($"{name} needs a value.");
    }
}
