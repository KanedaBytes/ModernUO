using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Server.Custom;

/// <summary>
///     Root of <c>Data/Custom/restricted-zones.json</c>.
///     <para>
///         A wrapper object rather than a bare array so an absent or renamed key is a loud error
///         rather than an empty list, and so per-file settings can be added later without a format
///         break.
///     </para>
/// </summary>
public class RestrictedZoneStore
{
    [JsonPropertyName("zones")]
    public List<RestrictedZoneRecord> Zones { get; set; }
}

/// <summary>
///     The stored description of a restricted zone.
///     <para>
///         Regions themselves are never persisted - the engine rebuilds every region from
///         <c>Data/regions.json</c> on each boot, and <see cref="Region" /> is not
///         <c>ISerializable</c>. So the shard-wide pattern is to keep a descriptor and rebuild the
///         region from it. This class is that descriptor; <see cref="RestrictedZoneSystem" /> owns
///         the list.
///     </para>
///     <para>
///         Stored as JSON alongside the daily life config rather than in the world save, so zones
///         are diffable, hand-editable and editable by the shard editor. Coordinates are flat ints
///         for the same reason daily life uses them: there is no <c>Rectangle2D</c> JSON converter
///         in the server, only <c>Point3D</c> and <c>Rectangle3D</c>.
///     </para>
/// </summary>
public class RestrictedZoneRecord
{
    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("map")]
    public string MapName { get; set; } = "Trammel";

    [JsonPropertyName("x")]
    public int X { get; set; }

    [JsonPropertyName("y")]
    public int Y { get; set; }

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    public RestrictedZoneRecord()
    {
    }

    public RestrictedZoneRecord(string name, Map map, Rectangle2D bounds)
    {
        Name = name;
        MapName = map?.Name;
        X = bounds.X;
        Y = bounds.Y;
        Width = bounds.Width;
        Height = bounds.Height;
    }

    /// <summary>
    ///     Resolved facet, or null when the name does not name one. Deliberately <c>TryParse</c>
    ///     rather than <c>Parse</c>: callers guard on null, and a throw from a property getter would
    ///     surface somewhere unrelated. Loading validates the name, so null here means a record
    ///     built in code rather than one read from the file.
    /// </summary>
    [JsonIgnore]
    public Map Map => Server.Map.TryParse(MapName, null, out var map) ? map : null;

    [JsonIgnore]
    public Rectangle2D Bounds => new(X, Y, Width, Height);
}
