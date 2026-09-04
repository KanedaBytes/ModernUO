using System.IO;
using System.Text.Json.Nodes;
using Server;
using Server.Custom.AdminApi;
using Xunit;

namespace UOContent.Tests;

/// <summary>
///     The admin API's path containment and JSON Pointer handling.
///     <para>
///         The traversal cases matter more than they look. On Windows they never reach managed
///         code - http.sys rejects a URL containing <c>..</c> itself - so a manual check against a
///         running server on this machine proves nothing about the shard's actual deployment
///         target. On Linux <c>HttpListener</c> is fully managed and hands the path straight
///         through, making <see cref="AdminApiFiles.ResolveUnder" /> the only thing between a
///         crafted URL and <c>Configuration/modernuo.json</c>. These run the same on both.
///     </para>
/// </summary>
[Collection("Sequential UOContent Tests")]
public class AdminApiFilesTests
{
    private static string Root => Path.Combine(Core.BaseDirectory, "Data");

    [Theory]
    [InlineData("Custom/britain-daily-life.json")]
    [InlineData("Spawns/custom/trammel/Britain.json")]
    [InlineData("a/b/c.png")]
    public void ResolvesPathsInsideTheRoot(string relative) =>
        Assert.NotNull(AdminApiFiles.ResolveUnder(Root, relative));

    [Theory]
    [InlineData("../Configuration/modernuo.json")]
    [InlineData("Custom/../../Configuration/modernuo.json")]
    [InlineData("Custom/../../../../../../etc/passwd")]
    [InlineData("./../../Configuration/modernuo.json")]
    public void RefusesToEscapeTheRoot(string relative) =>
        Assert.Null(AdminApiFiles.ResolveUnder(Root, relative));

    /// <summary>
    ///     A rooted path makes <c>Path.Combine</c> discard the root entirely and return the
    ///     absolute path unchanged - the containment check would then be comparing the attacker's
    ///     path against itself.
    /// </summary>
    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/win.ini")]
    public void RefusesRootedPaths(string relative) =>
        Assert.Null(AdminApiFiles.ResolveUnder(Root, relative));

    /// <summary>A NUL truncates the path below managed code, so a safe-looking name opens another file.</summary>
    [Fact]
    public void RefusesEmbeddedNul() =>
        Assert.Null(AdminApiFiles.ResolveUnder(Root, "Custom/ok.json" + (char)0 + ".png"));

    /// <summary>
    ///     Percent-encoding is decoded before the check, so an encoded traversal is caught by the
    ///     same path as a plain one rather than sneaking past it.
    /// </summary>
    [Theory]
    [InlineData("%2e%2e/Configuration/modernuo.json")]
    [InlineData("Custom/%2e%2e/%2e%2e/Configuration/modernuo.json")]
    public void RefusesEncodedTraversal(string relative) =>
        Assert.Null(AdminApiFiles.ResolveUnder(Root, relative));

    /// <summary>
    ///     Without a trailing separator on the canonical root, a sibling whose name merely starts
    ///     with the root's name passes a prefix test.
    /// </summary>
    [Fact]
    public void RefusesASiblingSharingTheRootsPrefix() =>
        Assert.Null(AdminApiFiles.ResolveUnder(Root, "../DataEvil/secret.json"));

    [Fact]
    public void WhitelistAcceptsOnlyTheEditableTrees()
    {
        Assert.NotNull(AdminApiFiles.Resolve("Data/Custom/britain-daily-life.json"));
        Assert.NotNull(AdminApiFiles.Resolve("Data/Spawns/custom/trammel/Britain.json"));

        Assert.Null(AdminApiFiles.Resolve("Configuration/modernuo.json"));
        Assert.Null(AdminApiFiles.Resolve("Data/Spawns/shared/trammel/Outdoors.json"));
        Assert.Null(AdminApiFiles.Resolve("Data/Custom/../../Configuration/modernuo.json"));
    }

    [Fact]
    public void FollowsJsonPointers()
    {
        var root = JsonNode.Parse("""
            { "shops": { "shops": [ { "homeRoute": [ { "x": 1 }, { "x": 2 } ] } ] },
              "routes": { "a/b": [ { "x": 9 } ] } }
            """);

        Assert.Equal(2, AdminApiFiles.Follow(root, "/shops/shops/0/homeRoute/1")["x"].GetValue<int>());

        // RFC 6901: ~1 is an escaped '/', so a route whose name contains a slash is addressable.
        Assert.Equal(9, AdminApiFiles.Follow(root, "/routes/a~1b/0")["x"].GetValue<int>());

        Assert.Null(AdminApiFiles.Follow(root, "/shops/shops/7"));
        Assert.Null(AdminApiFiles.Follow(root, "/nope"));
        Assert.Null(AdminApiFiles.Follow(root, "no-leading-slash"));
    }
}
