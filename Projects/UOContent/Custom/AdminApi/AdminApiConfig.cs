using System;
using System.IO;
using System.Security.Cryptography;

namespace Server.Custom.AdminApi;

/// <summary>
///     Settings for the local admin API, all read from the gitignored
///     <c>Distribution/Configuration/modernuo.json</c>.
///     <para>
///         Off by default. This API can write spawner JSON, and importing a spawner constructs
///         arbitrary types by name - so the token is an administrator credential, not a
///         convenience, and the surface should not exist unless someone asked for it.
///     </para>
/// </summary>
internal static class AdminApiConfig
{
    public static bool Enabled { get; private set; }

    /// <summary>Always loopback. See <see cref="AdminApiServer" /> for why this is not negotiable.</summary>
    public static string Address { get; private set; }

    public static int Port { get; private set; }

    public static string Token { get; private set; }

    /// <summary>Directory the editor's HTML/JS is served from.</summary>
    public static string WebRoot { get; private set; }

    /// <summary>Directory the rendered map tiles are served from.</summary>
    public static string TileRoot { get; private set; }

    public static void Load()
    {
        Enabled = ServerConfiguration.GetOrUpdateSetting("adminapi.enabled", false);
        Address = ServerConfiguration.GetOrUpdateSetting("adminapi.address", "127.0.0.1");
        Port = ServerConfiguration.GetOrUpdateSetting("adminapi.port", 8081);

        // Generated once and kept in the (gitignored) config, so there is never a default token and
        // never a token in source control.
        var token = ServerConfiguration.GetSetting("adminapi.token", (string)null);

        if (string.IsNullOrWhiteSpace(token))
        {
            token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');

            ServerConfiguration.SetSetting("adminapi.token", token);
        }

        Token = token;

        WebRoot = ResolveWebRoot();
        TileRoot = Resolve(ServerConfiguration.GetOrUpdateSetting("adminapi.tileRoot", "web/tiles"));
    }

    /// <summary>
    ///     On a dev box the editor is served straight from the tracked source at
    ///     <c>&lt;repo&gt;/ShardEditor</c>, so editing a .js file and refreshing is the whole loop -
    ///     no copy step. On a deployed host only <c>Distribution/</c> exists, so it falls back to
    ///     <c>web/editor</c>. An explicit setting overrides both.
    /// </summary>
    private static string ResolveWebRoot()
    {
        var configured = ServerConfiguration.GetSetting("adminapi.webRoot", (string)null);

        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Resolve(configured);
        }

        var source = Resolve(Path.Combine("..", "ShardEditor"));

        return Directory.Exists(source) ? source : Resolve("web/editor");
    }

    private static string Resolve(string path) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Core.BaseDirectory, path));
}
