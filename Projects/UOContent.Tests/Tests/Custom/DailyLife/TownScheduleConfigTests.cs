using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Server;
using Server.Custom;
using Server.Json;
using Xunit;

namespace UOContent.Tests;

/// <summary>
///     Guards the two ways the daily life config can silently stop working.
///     <para>
///         The first is binding. <c>JsonConfig</c>'s options are case-SENSITIVE and set no naming
///         policy, so a PascalCase property binds nothing against a camelCase key -
///         deserialization "succeeds" and every section comes back null. That is exactly what
///         happened: the whole feature was inert and the only symptom was an empty Britain.
///     </para>
///     <para>
///         The second is drift between the schema and the shipped file. A config that no longer
///         validates should fail here, in CI, rather than in Britain.
///     </para>
/// </summary>
[Collection("Sequential UOContent Tests")]
public class TownScheduleConfigTests
{
    /// <summary>
    ///     Structural guard: a member added later without an attribute is caught even if no test
    ///     ever reads it. Checks the whole nested config tree, not just the root.
    /// </summary>
    [Fact]
    public void EveryConfigPropertyDeclaresItsCamelCaseJsonName()
    {
        var missing = new List<string>();

        foreach (var type in ConfigTypes())
        {
            foreach (var property in type.GetProperties())
            {
                // Statics (Current) and read-only members are not part of the wire format.
                if (property.GetSetMethod() == null)
                {
                    continue;
                }

                var attribute = property.GetCustomAttributes(typeof(JsonPropertyNameAttribute), false);

                if (attribute.Length == 0)
                {
                    missing.Add($"{type.Name}.{property.Name} has no [JsonPropertyName]");
                    continue;
                }

                var expected = char.ToLowerInvariant(property.Name[0]) + property.Name[1..];
                var actual = ((JsonPropertyNameAttribute)attribute[0]).Name;

                if (actual != expected)
                {
                    missing.Add($"{type.Name}.{property.Name} binds '{actual}', expected '{expected}'");
                }
            }
        }

        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
    }

    /// <summary>
    ///     Behavioural guard on the same thing: round-trips a payload touching every member through
    ///     the exact options the loader uses, and asserts each one arrived.
    /// </summary>
    [Fact]
    public void EveryMemberBindsThroughJsonConfigOptions()
    {
        const string json = """
            {
              "anchor": { "x": 1, "y": 2, "z": 3, "map": "Trammel" },
              "tavern": {
                "x": 10, "y": 11, "width": 12, "height": 13, "z": 14,
                "map": "Trammel", "patronCount": 15, "chatter": [ "hic" ]
              },
              "routes": {
                "loop": [ { "x": 20, "y": 21, "z": 22, "say": "hello" } ]
              },
              "watch": {
                "map": "Trammel",
                "posts": [ { "x": 30, "y": 31, "z": 32, "route": "loop" } ]
              },
              "townsfolk": [
                { "name": "Perrin", "title": "the courier", "route": "loop", "map": "Trammel", "body": "male" }
              ],
              "shops": {
                "map": "Trammel",
                "neverCloses": [ "Banker" ],
                "x": 40, "y": 41, "width": 42, "height": 43,
                "shops": [
                  {
                    "vendor": "Baker", "x": 50, "y": 51, "z": 52,
                    "homeRoute": [ { "x": 60, "y": 61, "z": 62, "say": "night" } ]
                  }
                ]
              }
            }
            """;

        var config = JsonSerializer.Deserialize<TownScheduleConfig>(json, JsonConfig.DefaultOptions);

        Assert.NotNull(config);

        Assert.Equal(1, config.Anchor.X);
        Assert.Equal(2, config.Anchor.Y);
        Assert.Equal(3, config.Anchor.Z);
        Assert.Equal("Trammel", config.Anchor.Map);

        Assert.Equal(10, config.Tavern.X);
        Assert.Equal(11, config.Tavern.Y);
        Assert.Equal(12, config.Tavern.Width);
        Assert.Equal(13, config.Tavern.Height);
        Assert.Equal(14, config.Tavern.Z);
        Assert.Equal("Trammel", config.Tavern.Map);
        Assert.Equal(15, config.Tavern.PatronCount);
        Assert.Equal("hic", Assert.Single(config.Tavern.Chatter));

        var node = Assert.Single(Assert.Single(config.Routes).Value);
        Assert.Equal(20, node.X);
        Assert.Equal(21, node.Y);
        Assert.Equal(22, node.Z);
        Assert.Equal("hello", node.Say);

        Assert.Equal("Trammel", config.Watch.Map);
        var post = Assert.Single(config.Watch.Posts);
        Assert.Equal(30, post.X);
        Assert.Equal(31, post.Y);
        Assert.Equal(32, post.Z);
        Assert.Equal("loop", post.Route);

        var walker = Assert.Single(config.Townsfolk);
        Assert.Equal("Perrin", walker.Name);
        Assert.Equal("the courier", walker.Title);
        Assert.Equal("loop", walker.Route);
        Assert.Equal("Trammel", walker.Map);
        Assert.Equal("male", walker.Body);

        Assert.Equal("Trammel", config.Shops.Map);
        Assert.Equal("Banker", Assert.Single(config.Shops.NeverCloses));
        Assert.Equal(40, config.Shops.X);
        Assert.Equal(41, config.Shops.Y);
        Assert.Equal(42, config.Shops.Width);
        Assert.Equal(43, config.Shops.Height);

        var shop = Assert.Single(config.Shops.Shops);
        Assert.Equal("Baker", shop.Vendor);
        Assert.Equal(50, shop.X);
        Assert.Equal(51, shop.Y);
        Assert.Equal(52, shop.Z);

        var home = Assert.Single(shop.HomeRoute);
        Assert.Equal(60, home.X);
        Assert.Equal(61, home.Y);
        Assert.Equal(62, home.Z);
        Assert.Equal("night", home.Say);
    }

    /// <summary>
    ///     Loads the file the shard actually ships. UOContent.Tests copies
    ///     <c>Distribution/Data/**</c> to its output (see the CopyData target in the csproj), so
    ///     this reads the real config through the real loader.
    /// </summary>
    [Fact]
    public void ShippedConfigLoadsAndValidates()
    {
        var path = Path.Combine(Core.BaseDirectory, TownScheduleConfig.ConfigPath);

        Assert.True(File.Exists(path), $"Expected the shipped config at {path}.");

        Assert.True(TownScheduleConfig.TryLoad(out var error), error);

        // Binding, not just validation: if a section came back null the validator would have
        // rejected it, but assert the shape anyway so this test fails loudly rather than subtly.
        var config = TownScheduleConfig.Current;

        Assert.NotNull(config.Anchor);
        Assert.NotNull(config.Tavern);
        Assert.NotEmpty(config.Routes);
        Assert.NotNull(config.Watch);
        Assert.NotEmpty(config.Townsfolk);
        Assert.NotNull(config.Shops);
        Assert.NotEmpty(config.Shops.Shops);
    }

    [Fact]
    public void MissingSectionIsAnError()
    {
        var config = Valid();
        config.Tavern = null;

        Assert.False(config.Validate(out var error));
        Assert.Contains("'tavern' section is missing", error);
    }

    [Fact]
    public void UnknownFacetIsAnError()
    {
        var config = Valid();
        config.Anchor.Map = "Trammell";

        Assert.False(config.Validate(out var error));
        Assert.Contains("anchor.map", error);
    }

    [Fact]
    public void UnknownVendorTypeIsAnError()
    {
        var config = Valid();
        config.Shops.Shops[0].Vendor = "Bakerr";

        Assert.False(config.Validate(out var error));
        Assert.Contains("Bakerr", error);
    }

    [Fact]
    public void NonVendorTypeIsAnError()
    {
        // Resolves as a type, but is not something the shop schedule could ever drive.
        var config = Valid();
        config.Shops.Shops[0].Vendor = "Dragon";

        Assert.False(config.Validate(out var error));
        Assert.Contains("not a BaseVendor", error);
    }

    [Theory]
    [InlineData("Male ")]
    [InlineData("man")]
    public void UnknownBodyIsAnError(string body)
    {
        var config = Valid();
        config.Townsfolk[0].Body = body;

        Assert.False(config.Validate(out var error));
        Assert.Contains("expected male, female or random", error);
    }

    [Fact]
    public void UnknownRouteReferenceIsAnError()
    {
        var config = Valid();
        config.Townsfolk[0].Route = "nowhere";

        Assert.False(config.Validate(out var error));
        Assert.Contains("unknown route 'nowhere'", error);
    }

    /// <summary>
    ///     A route has no map of its own, so two consumers on different facets is the one shape that
    ///     is valid per-entry and wrong overall.
    /// </summary>
    [Fact]
    public void RouteWalkedFromTwoFacetsIsAnError()
    {
        var config = Valid();
        config.Townsfolk[0].Map = "Felucca";

        Assert.False(config.Validate(out var error));
        Assert.Contains("is walked on both", error);
    }

    [Fact]
    public void RouteLegOverThePathfindingLimitIsAnError()
    {
        var config = Valid();
        config.Routes["loop"][1].X += 40;

        Assert.False(config.Validate(out var error));
        Assert.Contains("pathfinding limit", error);
    }

    [Fact]
    public void ZeroSizeBoundsAreAnError()
    {
        var config = Valid();
        config.Shops.Width = 0;

        Assert.False(config.Validate(out var error));
        Assert.Contains("positive width and height", error);
    }

    [Fact]
    public void DuplicateVendorEntryIsAnError()
    {
        var config = Valid();
        config.Shops.Shops.Add(
            new TownScheduleConfig.ShopConfig
            {
                Vendor = "Baker",
                X = 100,
                Y = 100,
                Z = 0,
                HomeRoute = [new TownScheduleConfig.RouteNode { X = 105, Y = 105 }]
            }
        );

        Assert.False(config.Validate(out var error));
        Assert.Contains("more than one entry for vendor", error);
    }

    private static IEnumerable<Type> ConfigTypes()
    {
        yield return typeof(TownScheduleConfig);

        foreach (var nested in typeof(TownScheduleConfig).GetNestedTypes().Where(t => t.IsClass))
        {
            yield return nested;
        }
    }

    /// <summary>A minimal config that passes validation, for the negative cases to break one way.</summary>
    private static TownScheduleConfig Valid() =>
        new()
        {
            Anchor = new TownScheduleConfig.AnchorPoint { X = 1000, Y = 1000, Z = 0, Map = "Trammel" },
            Tavern = new TownScheduleConfig.TavernConfig
            {
                X = 1000, Y = 1000, Width = 10, Height = 10, Z = 0, Map = "Trammel", PatronCount = 3
            },
            Routes = new Dictionary<string, List<TownScheduleConfig.RouteNode>>
            {
                ["loop"] =
                [
                    new TownScheduleConfig.RouteNode { X = 1000, Y = 1000 },
                    new TownScheduleConfig.RouteNode { X = 1010, Y = 1000 }
                ]
            },
            Watch = new TownScheduleConfig.WatchConfig
            {
                Map = "Trammel",
                Posts = [new TownScheduleConfig.WatchPost { X = 1000, Y = 1000, Route = "loop" }]
            },
            Townsfolk =
            [
                new TownScheduleConfig.RoutedTownsfolkConfig
                {
                    Name = "Perrin", Route = "loop", Map = "Trammel", Body = "male"
                }
            ],
            Shops = new TownScheduleConfig.ShopsConfig
            {
                Map = "Trammel",
                X = 990,
                Y = 990,
                Width = 50,
                Height = 50,
                NeverCloses = ["Banker"],
                Shops =
                [
                    new TownScheduleConfig.ShopConfig
                    {
                        Vendor = "Baker",
                        X = 1000,
                        Y = 1000,
                        Z = 0,
                        HomeRoute = [new TownScheduleConfig.RouteNode { X = 1005, Y = 1005 }]
                    }
                ]
            }
        };
}
