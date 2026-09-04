using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Server;
using Server.Custom;
using Server.Json;
using Xunit;

namespace UOContent.Tests;

/// <summary>
///     Zones moved out of the world save and into <c>Data/Custom/restricted-zones.json</c>. That
///     buys diffability and hot reload, and costs the world save's guarantee that a record either
///     round-trips or fails hard - so the same binding and validation guards the daily life config
///     has apply here.
/// </summary>
[Collection("Sequential UOContent Tests")]
public class RestrictedZoneStoreTests
{
    [Fact]
    public void ZoneBindsThroughJsonConfigOptions()
    {
        const string json = """
            {
              "zones": [
                { "name": "Test Vault", "map": "Trammel", "x": 100, "y": 200, "width": 10, "height": 20 }
              ]
            }
            """;

        var store = JsonSerializer.Deserialize<RestrictedZoneStore>(json, JsonConfig.DefaultOptions);

        Assert.NotNull(store);

        var zone = Assert.Single(store.Zones);

        Assert.Equal("Test Vault", zone.Name);
        Assert.Equal("Trammel", zone.MapName);
        Assert.Equal(100, zone.X);
        Assert.Equal(200, zone.Y);
        Assert.Equal(10, zone.Width);
        Assert.Equal(20, zone.Height);

        Assert.Same(Map.Trammel, zone.Map);
        Assert.Equal(new Rectangle2D(100, 200, 10, 20), zone.Bounds);
    }

    /// <summary>The round trip a save from the editor performs: write, read back, same zone.</summary>
    [Fact]
    public void ZoneSurvivesARoundTrip()
    {
        var original = new RestrictedZoneRecord("Vault", Map.Trammel, new Rectangle2D(10, 20, 30, 40));

        var store = new RestrictedZoneStore { Zones = [original] };
        var json = JsonConfig.Serialize(store);
        var restored = Assert.Single(JsonSerializer.Deserialize<RestrictedZoneStore>(json, JsonConfig.DefaultOptions).Zones);

        Assert.Equal(original.Name, restored.Name);
        Assert.Equal(original.MapName, restored.MapName);
        Assert.Equal(original.Bounds, restored.Bounds);
        Assert.Same(Map.Trammel, restored.Map);

        // The resolved views are conveniences, not fields - they must not reach the file.
        Assert.DoesNotContain("\"Bounds\"", json);
        Assert.DoesNotContain("\"bounds\"", json);
    }

    [Fact]
    public void ShippedZoneFileLoads()
    {
        var path = Path.Combine(Core.BaseDirectory, RestrictedZoneSystem.ConfigPath);

        Assert.True(File.Exists(path), $"Expected the zone file at {path}.");
        Assert.True(RestrictedZoneSystem.TryLoad(out var error), error);
    }

    [Fact]
    public void UnknownFacetIsAnError()
    {
        var records = new List<RestrictedZoneRecord>
        {
            new() { Name = "Vault", MapName = "Trammell", Width = 5, Height = 5 }
        };

        Assert.False(RestrictedZoneSystem.Validate(records, out var error));
        Assert.Contains("not a valid facet", error);
    }

    [Fact]
    public void ZeroSizeBoundsAreAnError()
    {
        var records = new List<RestrictedZoneRecord>
        {
            new() { Name = "Vault", MapName = "Trammel", Width = 0, Height = 5 }
        };

        Assert.False(RestrictedZoneSystem.Validate(records, out var error));
        Assert.Contains("positive width and height", error);
    }

    [Fact]
    public void MissingNameIsAnError()
    {
        var records = new List<RestrictedZoneRecord>
        {
            new() { MapName = "Trammel", Width = 5, Height = 5 }
        };

        Assert.False(RestrictedZoneSystem.Validate(records, out var error));
        Assert.Contains("has no name", error);
    }

    /// <summary>Find() is case-insensitive, so a case-only duplicate makes one zone unreachable.</summary>
    [Fact]
    public void CaseInsensitiveDuplicateNameIsAnError()
    {
        var records = new List<RestrictedZoneRecord>
        {
            new() { Name = "Vault", MapName = "Trammel", Width = 5, Height = 5 },
            new() { Name = "vault", MapName = "Trammel", Width = 5, Height = 5 }
        };

        Assert.False(RestrictedZoneSystem.Validate(records, out var error));
        Assert.Contains("duplicates the name", error);
    }
}
