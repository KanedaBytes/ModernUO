using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Server.Engines.Spawners;
using Server.Logging;
using Server.Maps;

namespace Server.Custom.AdminApi;

/// <summary>
///     A loopback-only HTTP surface for the shard editor: serves the editor, the rendered map
///     tiles, and a small API for reading and writing the config files and reloading the systems
///     that own them.
///     <para>
///         <b>Threading.</b> One dedicated listener thread. It is a sanctioned worker under
///         <c>dev-docs/threading-model.md</c> because it touches no game state: static files and
///         tiles are disk reads, and everything else is posted to the game loop through
///         <see cref="AdminApiLoop" /> and waited for there. Blocking this thread is fine - it is
///         not the loop. See <c>Custom/MODIFICATIONS.md</c> for the entry describing it.
///     </para>
///     <para>
///         <b>Security.</b> Localhost plus a bearer token, and that is only sufficient because of
///         every one of these together: the listener binds to the loopback address rather than
///         0.0.0.0; the feature is off unless explicitly enabled; the token is compared in fixed
///         time and is read only from the <c>Authorization</c> header - never a cookie or a query
///         string, either of which a hostile page could make the browser send for the user; a
///         request carrying a foreign <c>Origin</c> is refused; and the <c>Host</c> header must
///         name loopback, which is what stops DNS rebinding from turning an attacker's domain into
///         a local one. No CORS headers are emitted at all: the editor is same-origin.
///     </para>
///     <para>
///         The editor and the map tiles are served without a token, because a browser cannot put a
///         header on an <c>img</c> tag. Neither is sensitive - the tiles are a picture of the
///         player's own client files - and the API that can actually change the shard is not
///         reachable without one.
///     </para>
///     <para>
///         <b>To reach it from the Linux host</b>, forward the port over SSH rather than exposing
///         it: <c>ssh -L 8081:127.0.0.1:8081 user@host</c>, then browse 127.0.0.1:8081 as usual.
///     </para>
/// </summary>
public static class AdminApiServer
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(AdminApiServer));

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static HttpListener _listener;
    private static Thread _thread;
    private static byte[] _token;
    private static volatile bool _running;

    public static void Configure() => AdminApiConfig.Load();

    /// <summary>
    ///     Started post-world so a request can never arrive before there is a world to answer about.
    /// </summary>
    public static void Initialize()
    {
        if (!AdminApiConfig.Enabled)
        {
            return;
        }

        if (AdminApiConfig.Address is not ("127.0.0.1" or "localhost" or "::1"))
        {
            logger.Error(
                "Admin API not started: adminapi.address is {Address}. This API is loopback-only; "
                + "use an SSH tunnel to reach it remotely",
                AdminApiConfig.Address
            );

            return;
        }

        _token = Encoding.UTF8.GetBytes(AdminApiConfig.Token);

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://{AdminApiConfig.Address}:{AdminApiConfig.Port}/");

        try
        {
            _listener.Start();
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Admin API could not bind to {Address}:{Port}", AdminApiConfig.Address, AdminApiConfig.Port);
            _listener = null;
            return;
        }

        _running = true;

        _thread = new Thread(Listen)
        {
            Name = "Admin API",

            // Background so a stuck request can never hold the process open at shutdown.
            IsBackground = true
        };

        _thread.Start();

        EventSink.Shutdown += Stop;

        logger.Information(
            "Admin API listening on http://{Address}:{Port} (editor: {WebRoot})",
            AdminApiConfig.Address,
            AdminApiConfig.Port,
            AdminApiConfig.WebRoot
        );
    }

    private static void Stop()
    {
        _running = false;

        try
        {
            _listener?.Stop();
            _listener?.Close();
        }
        catch (Exception)
        {
            // Shutting down; a listener that is already torn down is not worth reporting.
        }

        _listener = null;
    }

    private static void Listen()
    {
        while (_running)
        {
            HttpListenerContext context;

            try
            {
                context = _listener.GetContext();
            }
            catch (Exception)
            {
                // Stop() closes the listener out from under GetContext; that is the exit path.
                return;
            }

            try
            {
                Handle(context);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Admin API request failed");

                try
                {
                    Send(context, 500, new { error = "Internal error." });
                }
                catch (Exception)
                {
                    // The client hung up mid-response; nothing left to say.
                }
            }
        }
    }

    private static void Handle(HttpListenerContext context)
    {
        var request = context.Request;

        if (!IsLocal(request))
        {
            Send(context, 403, new { error = "Loopback only." });
            return;
        }

        var path = request.Url?.AbsolutePath ?? "/";

        if (!path.StartsWith("/api/", StringComparison.Ordinal))
        {
            ServeStatic(context, path);
            return;
        }

        if (!IsAuthorized(request))
        {
            Send(context, 401, new { error = "Bearer token required." });
            return;
        }

        var method = request.HttpMethod;

        // Every route that changes something refuses to run mid-save: the serialization threads are
        // already reading the world, and a config write that lands then is a write nobody asked to
        // interleave.
        if (method is not ("GET" or "HEAD") && !AdminApiLoop.CanMutate)
        {
            Send(context, 503, new { error = $"World is {World.WorldState}; try again shortly." });
            return;
        }

        switch (path)
        {
            case "/api/status" when method == "GET":
                Status(context);
                return;
            case "/api/shapes" when method == "GET":
                Shapes(context);
                return;
            case "/api/shapes" when method == "PATCH":
                PatchShapes(context);
                return;
            case "/api/shapes/create" when method == "POST":
                CreateShape(context);
                return;
            // POST rather than DELETE: http.sys is particular about bodies on DELETE, and the
            // pointer identifying the shape has to travel somewhere.
            case "/api/shapes/delete" when method == "POST":
                DeleteShape(context);
                return;
            case "/api/types" when method == "GET":
                Types(context);
                return;
            case "/api/entities" when method == "GET":
                Entities(context);
                return;
            case "/api/staff" when method == "GET":
                Staff(context);
                return;
            case "/api/reload/dailylife" when method == "POST":
                Reload(context, DailyLifeCommands.TryReload);
                return;
            case "/api/reload/zones" when method == "POST":
                Reload(context, RestrictedZoneSystem.TryReload);
                return;
            case "/api/reload/spawners" when method == "POST":
                ReloadSpawners(context);
                return;
            default:
                Send(context, 404, new { error = "No such endpoint." });
                return;
        }
    }

    // --- endpoints ---------------------------------------------------------------------------

    private static void Status(HttpListenerContext context)
    {
        if (!AdminApiLoop.TryRun(
                () => new
                {
                    worldState = World.WorldState.ToString(),
                    facets = FacetNames(),
                    zones = RestrictedZoneSystem.Zones.Count,
                    spawners = LiveSpawnerCount(),
                    dailyLifeLoaded = TownScheduleConfig.Current != null
                },
                out var status,
                out var error
            ))
        {
            Send(context, 503, new { error });
            return;
        }

        Send(context, 200, status);
    }

    private static void Shapes(HttpListenerContext context)
    {
        if (!AdminApiLoop.TryRun(AdminApiShapes.All, out var shapes, out var error))
        {
            Send(context, 503, new { error });
            return;
        }

        Send(context, 200, new { shapes });
    }

    private static void PatchShapes(HttpListenerContext context)
    {
        var body = ReadBody(context.Request);

        List<AdminApiShapes.Edit> edits;

        try
        {
            edits = JsonSerializer.Deserialize<List<AdminApiShapes.Edit>>(body, _json);
        }
        catch (JsonException ex)
        {
            Send(context, 400, new { error = $"Malformed request: {ex.Message}" });
            return;
        }

        if (edits is not { Count: > 0 })
        {
            Send(context, 400, new { error = "No edits supplied." });
            return;
        }

        // Applied on the loop even though it is file I/O: the zone layer reads the live record list
        // to resolve pointers, and a reload posted straight afterwards must see this write.
        if (!AdminApiLoop.TryRun(
                () =>
                {
                    var ok = AdminApiShapes.Apply(edits, out var applyError);
                    return (ok, applyError);
                },
                out var result,
                out var error
            ))
        {
            Send(context, 503, new { error });
            return;
        }

        if (!result.ok)
        {
            Send(context, 400, new { error = result.applyError });
            return;
        }

        Send(context, 200, new { applied = edits.Count });
    }

    private static void CreateShape(HttpListenerContext context)
    {
        if (!TryReadBody<AdminApiMutations.CreateRequest>(context, out var request))
        {
            return;
        }

        if (!AdminApiLoop.TryRun(
                () =>
                {
                    var ok = AdminApiMutations.Create(request, out var createError, out var pointer);
                    return (ok, createError, pointer);
                },
                out var result,
                out var error
            ))
        {
            Send(context, 503, new { error });
            return;
        }

        if (!result.ok)
        {
            Send(context, 400, new { error = result.createError });
            return;
        }

        Send(context, 200, new { pointer = result.pointer });
    }

    private static void DeleteShape(HttpListenerContext context)
    {
        if (!TryReadBody<AdminApiMutations.DeleteRequest>(context, out var request))
        {
            return;
        }

        if (!AdminApiLoop.TryRun(
                () =>
                {
                    var ok = AdminApiMutations.Delete(request, out var deleteError);
                    return (ok, deleteError);
                },
                out var result,
                out var error
            ))
        {
            Send(context, 503, new { error });
            return;
        }

        if (!result.ok)
        {
            Send(context, 400, new { error = result.deleteError });
            return;
        }

        Send(context, 200, new { ok = true });
    }

    private static void Types(HttpListenerContext context)
    {
        if (!AdminApiLoop.TryRun(AdminApiMutations.Types, out var types, out var error))
        {
            Send(context, 503, new { error });
            return;
        }

        Send(context, 200, types);
    }

    /// <summary>Reads and deserializes a JSON body, answering 400 itself when it cannot.</summary>
    private static bool TryReadBody<T>(HttpListenerContext context, out T value) where T : class
    {
        value = null;

        try
        {
            value = JsonSerializer.Deserialize<T>(ReadBody(context.Request), _json);
        }
        catch (JsonException ex)
        {
            Send(context, 400, new { error = $"Malformed request: {ex.Message}" });
            return false;
        }

        if (value == null)
        {
            Send(context, 400, new { error = "Empty request." });
            return false;
        }

        return true;
    }

    private static void Entities(HttpListenerContext context)
    {
        if (!AdminApiLoop.TryRun(AdminApiEntities.All, out var entities, out var error))
        {
            Send(context, 503, new { error });
            return;
        }

        Send(context, 200, new { entities });
    }

    private static void Staff(HttpListenerContext context)
    {
        if (!AdminApiLoop.TryRun(AdminApiEntities.Staff, out var staff, out var error))
        {
            Send(context, 503, new { error });
            return;
        }

        Send(context, 200, new { staff });
    }

    private delegate bool ReloadHandler(out string error);

    private static void Reload(HttpListenerContext context, ReloadHandler handler)
    {
        if (!AdminApiLoop.TryRun(
                () =>
                {
                    var ok = handler(out var reloadError);
                    return (ok, reloadError);
                },
                out var result,
                out var error
            ))
        {
            Send(context, 503, new { error });
            return;
        }

        if (!result.ok)
        {
            // The config was rejected, so the previous one is still live. That is a client error,
            // not a server one: the file the editor just wrote does not validate.
            Send(context, 422, new { error = result.reloadError });
            return;
        }

        Send(context, 200, new { ok = true });
    }

    /// <summary>
    ///     Re-imports the custom spawn files through the same path as
    ///     <c>[GenerateSpawners Data/Spawns/custom/**.json</c>.
    ///     <para>
    ///         Globs <c>custom/</c> explicitly. The admin gump's world-generation button only covers
    ///         <c>uoml</c>, <c>post-uoml</c> and <c>shared</c>, so anything relying on that would
    ///         silently do nothing here.
    ///     </para>
    /// </summary>
    private static void ReloadSpawners(HttpListenerContext context)
    {
        if (!AdminApiLoop.TryRun(
                () =>
                {
                    var existing = new Dictionary<Guid, ISpawner>();

                    foreach (var item in World.Items.Values)
                    {
                        if (item is ISpawner spawner)
                        {
                            existing[spawner.Guid] = spawner;
                        }
                    }

                    var files = 0;

                    foreach (var path in AdminApiFiles.SpawnFiles())
                    {
                        ImportSpawnersCommand.ImportFile(new FileInfo(path), existing);
                        files++;
                    }

                    return files;
                },
                out var imported,
                out var error
            ))
        {
            Send(context, 503, new { error });
            return;
        }

        Send(context, 200, new { ok = true, files = imported });
    }

    // --- static files ------------------------------------------------------------------------

    private static void ServeStatic(HttpListenerContext context, string path)
    {
        string root;
        string relative;

        if (path.StartsWith("/tiles/", StringComparison.Ordinal))
        {
            root = AdminApiConfig.TileRoot;
            relative = path[7..];
        }
        else
        {
            root = AdminApiConfig.WebRoot;
            relative = path == "/" ? "index.html" : path[1..];
        }

        var full = AdminApiFiles.ResolveUnder(root, relative);

        if (full == null || !File.Exists(full))
        {
            Send(context, 404, new { error = "Not found." });
            return;
        }

        var response = context.Response;
        response.StatusCode = 200;
        response.ContentType = ContentType(Path.GetExtension(full));

        // Tiles are immutable for a given render; the editor pans over hundreds of them.
        response.AddHeader(
            "Cache-Control",
            root == AdminApiConfig.TileRoot ? "public, max-age=86400" : "no-cache"
        );

        using var file = File.OpenRead(full);
        response.ContentLength64 = file.Length;
        file.CopyTo(response.OutputStream);
        response.OutputStream.Close();
    }

    private static string ContentType(string extension) =>
        extension.ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js"   => "text/javascript; charset=utf-8",
            ".css"  => "text/css; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".png"  => "image/png",
            ".svg"  => "image/svg+xml",
            ".ico"  => "image/x-icon",
            _       => "application/octet-stream"
        };

    // --- request plumbing --------------------------------------------------------------------

    /// <summary>
    ///     Loopback endpoint, loopback <c>Host</c>, and no foreign <c>Origin</c>.
    ///     <para>
    ///         The <c>Host</c> check is the anti-DNS-rebinding one: without it a hostile domain that
    ///         resolves to 127.0.0.1 reaches this listener as a same-origin page of its own, and the
    ///         browser would happily let its script read the responses.
    ///     </para>
    /// </summary>
    private static bool IsLocal(HttpListenerRequest request)
    {
        if (!IPAddress.IsLoopback(request.RemoteEndPoint.Address))
        {
            return false;
        }

        var host = request.UserHostName;

        if (host != null)
        {
            var colon = host.LastIndexOf(':');
            var name = colon > 0 ? host[..colon] : host;

            if (name is not ("127.0.0.1" or "localhost" or "[::1]" or "::1"))
            {
                return false;
            }
        }

        var origin = request.Headers["Origin"];

        if (origin != null
            && origin != $"http://127.0.0.1:{AdminApiConfig.Port}"
            && origin != $"http://localhost:{AdminApiConfig.Port}")
        {
            return false;
        }

        return true;
    }

    private static bool IsAuthorized(HttpListenerRequest request)
    {
        var header = request.Headers["Authorization"];

        if (header == null || !header.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return false;
        }

        var supplied = Encoding.UTF8.GetBytes(header[7..].Trim());

        return CryptographicOperations.FixedTimeEquals(supplied, _token);
    }

    private static string ReadBody(HttpListenerRequest request)
    {
        // Bounded: these are coordinate edits, not uploads.
        const int limit = 4 * 1024 * 1024;

        using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
        var buffer = new char[limit];
        var read = reader.ReadBlock(buffer, 0, limit);

        return new string(buffer, 0, read);
    }

    private static void Send(HttpListenerContext context, int status, object payload)
    {
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, _json));

        var response = context.Response;
        response.StatusCode = status;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = body.Length;
        response.OutputStream.Write(body, 0, body.Length);
        response.OutputStream.Close();
    }

    /// <summary>
    ///     Only the facets this shard actually enables. Filtering by the expansion's map selection
    ///     rather than by every entry in map-definitions.json keeps the editor's facet list in step
    ///     with what MapExport rendered - offering TerMur under ML would give a picker entry with
    ///     no tiles behind it.
    /// </summary>
    /// <summary>
    ///     How many spawners are actually live in the world, as opposed to how many are described
    ///     in the files - the two diverge until a spawner reload, and that gap is worth being able
    ///     to see.
    ///     <para>
    ///         Iterates World.Items, which the audit rules discourage. It is justified here and only
    ///         here: /api/status is requested once when the editor connects, never polled, and there
    ///         is no spatial query for "every spawner anywhere". <c>ImportSpawnersCommand</c> builds
    ///         its own index the same way.
    ///     </para>
    /// </summary>
    private static int LiveSpawnerCount()
    {
        var count = 0;

        foreach (var item in World.Items.Values)
        {
            if (item is ISpawner)
            {
                count++;
            }
        }

        return count;
    }

    private static List<object> FacetNames()
    {
        var facets = new List<object>();
        var enabled = ExpansionInfo.GetInfo(Core.Expansion).MapSelectionFlags;

        foreach (var map in Map.AllMaps)
        {
            if (map == null || map == Map.Internal || map.MapID >= 0x7F)
            {
                continue;
            }

            if ((enabled & map.ToSelectionFlag()) != 0)
            {
                facets.Add(new { name = map.Name, width = map.Width, height = map.Height });
            }
        }

        return facets;
    }
}
