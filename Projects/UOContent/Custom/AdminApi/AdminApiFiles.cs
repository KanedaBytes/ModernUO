using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Server.Engines.Spawners;

namespace Server.Custom.AdminApi;

/// <summary>
///     Reading and writing the config files the editor is allowed to touch.
///     <para>
///         Edits are applied to a <see cref="JsonNode" /> of the file and written back, rather than
///         round-tripping through the typed config classes. That is what makes "preserve unrelated
///         entries" true by construction: a typed round-trip would silently drop any key the class
///         does not model and reorder the rest, so one nudged rectangle would rewrite the file.
///     </para>
/// </summary>
internal static class AdminApiFiles
{
    /// <summary>Directories the editor may read and write, relative to <c>Core.BaseDirectory</c>.</summary>
    private static readonly string[] _allowedRoots =
    [
        "Data/Custom",
        "Data/Spawns/custom"
    ];

    /// <summary>
    ///     Resolves a repo-relative path inside the whitelist, or returns null.
    ///     <para>
    ///         Canonicalises before comparing: <c>Data/Custom/../../../secrets.json</c> is a valid
    ///         relative path that leaves the allowed tree, and only comparing full paths catches it.
    ///         The trailing separator on the root matters too, or <c>Data/CustomEvil</c> would pass
    ///         a prefix test against <c>Data/Custom</c>.
    ///     </para>
    /// </summary>
    public static string Resolve(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            return null;
        }

        string full;

        try
        {
            full = Path.GetFullPath(Path.Combine(Core.BaseDirectory, relativePath));
        }
        catch (Exception)
        {
            return null;
        }

        foreach (var root in _allowedRoots)
        {
            var allowed = Path.GetFullPath(Path.Combine(Core.BaseDirectory, root))
                          + Path.DirectorySeparatorChar;

            if (full.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
            {
                return full;
            }
        }

        return null;
    }

    /// <summary>
    ///     Resolves a request path under a root without letting it escape, or returns null.
    ///     <para>
    ///         On Windows this is belt and braces - http.sys rejects a path containing <c>..</c>
    ///         before the listener ever sees it. On Linux <c>HttpListener</c> is fully managed and
    ///         does no such thing, so on the shard's actual deployment target this method is the
    ///         only thing standing between a crafted URL and the configuration directory. It is
    ///         covered by tests for that reason.
    ///     </para>
    ///     <para>
    ///         The trailing separator on the root is load-bearing: without it, a sibling directory
    ///         named <c>ShardEditorEvil</c> passes a prefix test against <c>ShardEditor</c>.
    ///     </para>
    /// </summary>
    public static string ResolveUnder(string root, string relative)
    {
        if (relative == null || relative.Contains((char)0))
        {
            return null;
        }

        string full;
        string canonicalRoot;

        try
        {
            canonicalRoot = Path.GetFullPath(root);

            if (!canonicalRoot.EndsWith(Path.DirectorySeparatorChar))
            {
                canonicalRoot += Path.DirectorySeparatorChar;
            }

            var decoded = Uri.UnescapeDataString(relative);

            // A rooted or drive-qualified segment makes Path.Combine discard the root entirely,
            // so reject it rather than combining it.
            if (Path.IsPathRooted(decoded))
            {
                return null;
            }

            full = Path.GetFullPath(Path.Combine(canonicalRoot, decoded));
        }
        catch (Exception)
        {
            return null;
        }

        return full.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    public static IEnumerable<string> SpawnFiles()
    {
        var root = Path.Combine(Core.BaseDirectory, "Data", "Spawns", "custom");

        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories);
    }

    public static string RelativePath(string fullPath) =>
        Path.GetRelativePath(Core.BaseDirectory, fullPath).Replace('\\', '/');

    public static JsonNode Load(string fullPath)
    {
        var options = new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        };

        return JsonNode.Parse(File.ReadAllText(fullPath), documentOptions: options);
    }

    /// <summary>
    ///     Writes a node back through the spawner serializer's compact writer - the same one
    ///     <c>[ExportSpawners</c> uses: a container stays on one line when all its values are
    ///     scalars and the line fits.
    ///     <para>
    ///         Used for every file the editor writes, not just spawn files. Plain
    ///         <c>WriteIndented</c> JSON explodes each <c>{ "x": .., "y": .., "z": .. }</c> route
    ///         node across four lines, so nudging one coordinate in
    ///         <c>britain-daily-life.json</c> produced a 229-line diff - which buries the actual
    ///         change and makes the file unpleasant to hand-edit afterwards. The compact layout is
    ///         also how both files were authored in the first place, so the editor and a human
    ///         writing JSON by hand now produce the same thing.
    ///     </para>
    /// </summary>
    public static void Save(string fullPath, JsonNode node) =>
        File.WriteAllText(fullPath, SpawnerJsonSerializer.SerializeCompact(node));

    /// <summary>
    ///     Walks a JSON Pointer (RFC 6901) such as <c>/shops/shops/0/homeRoute</c>. The pointer is
    ///     the editor's identity for a shape: it survives an edit to a neighbouring section and does
    ///     not depend on the server keeping any in-memory index.
    /// </summary>
    public static JsonNode Follow(JsonNode root, string pointer)
    {
        if (string.IsNullOrEmpty(pointer) || pointer == "/")
        {
            return root;
        }

        if (pointer[0] != '/')
        {
            return null;
        }

        var current = root;

        foreach (var raw in pointer[1..].Split('/'))
        {
            if (current == null)
            {
                return null;
            }

            var token = Unescape(raw);

            switch (current)
            {
                case JsonArray array when int.TryParse(token, out var index)
                                          && index >= 0 && index < array.Count:
                    current = array[index];
                    break;
                case JsonObject obj when obj.TryGetPropertyValue(token, out var child):
                    current = child;
                    break;
                default:
                    return null;
            }
        }

        return current;
    }

    /// <summary>
    ///     Removes the node at <paramref name="pointer" /> from its parent - an element from an
    ///     array, or a key from an object (which is how a named route is deleted).
    /// </summary>
    public static bool Remove(JsonNode root, string pointer)
    {
        var separator = pointer.LastIndexOf('/');

        if (separator < 0)
        {
            return false;
        }

        var parent = Follow(root, pointer[..separator]);
        var token = Unescape(pointer[(separator + 1)..]);

        switch (parent)
        {
            case JsonArray array when int.TryParse(token, out var index)
                                      && index >= 0 && index < array.Count:
                array.RemoveAt(index);
                return true;
            case JsonObject obj:
                return obj.Remove(token);
            default:
                return false;
        }
    }

    /// <summary>
    ///     Appends to the array at <paramref name="pointer" />, creating it when absent, and returns
    ///     the pointer to the new element so the caller can hand the editor something to select.
    /// </summary>
    public static string Append(JsonNode root, string pointer, JsonNode value)
    {
        if (Follow(root, pointer) is not JsonArray array)
        {
            array = [];

            if (!Replace(root, pointer, array))
            {
                return null;
            }
        }

        array.Add(value);

        return $"{pointer}/{array.Count - 1}";
    }

    /// <summary>RFC 6901 escaping: ~1 is '/', ~0 is '~', and in that order.</summary>
    public static string Escape(string token) => token.Replace("~", "~0").Replace("/", "~1");

    private static string Unescape(string token) => token.Replace("~1", "/").Replace("~0", "~");

    /// <summary>Replaces the node at <paramref name="pointer" /> within its parent.</summary>
    public static bool Replace(JsonNode root, string pointer, JsonNode value)
    {
        var separator = pointer.LastIndexOf('/');

        if (separator < 0)
        {
            return false;
        }

        var parent = Follow(root, pointer[..separator]);
        var token = Unescape(pointer[(separator + 1)..]);

        switch (parent)
        {
            case JsonArray array when int.TryParse(token, out var index)
                                      && index >= 0 && index < array.Count:
                array[index] = value;
                return true;
            case JsonObject obj:
                obj[token] = value;
                return true;
            default:
                return false;
        }
    }
}
