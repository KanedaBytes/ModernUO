using ModernUO.Serialization;

namespace Server.Custom;

/// <summary>
///     The persisted description of a restricted zone.
///     <para>
///         Regions themselves are never serialized - the engine rebuilds every region from
///         <c>Data/regions.json</c> on each boot, and <see cref="Region" /> is not
///         <c>ISerializable</c>. So the shard-wide pattern (the same one <c>BaseHouse</c> and
///         <c>HouseRaffleStone</c> use) is to persist a descriptor and rebuild the region from it.
///         This class is that descriptor; <see cref="RestrictedZoneSystem" /> owns the list.
///     </para>
/// </summary>
[SerializationGenerator(0)]
public partial class RestrictedZoneRecord
{
    [SerializableField(0)]
    private string _name;

    [SerializableField(1)]
    private Map _map;

    [SerializableField(2)]
    private Rectangle2D _bounds;

    public RestrictedZoneRecord()
    {
    }

    public RestrictedZoneRecord(string name, Map map, Rectangle2D bounds)
    {
        _name = name;
        _map = map;
        _bounds = bounds;
    }
}
