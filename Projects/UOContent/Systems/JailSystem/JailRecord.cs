using System;
using ModernUO.Serialization;

namespace Server.Systems.JailSystem;

[SerializationGenerator(1)]
public partial class JailRecord
{
    [SerializableField(0)]
    private int _jailCount;

    [SerializableField(1)]
    private DateTime _lastJailed;

    [SerializableField(2)]
    private DateTime _jailEndTime;

    [SerializableField(3)]
    private string _lastJailReason;

    [SerializableField(4)]
    private Mobile _jailedBy;

    /// <summary>
    ///     The facet the player was jailed from, so they can be released back to it instead of
    ///     always landing on Felucca. Null for records written before v1.
    /// </summary>
    [SerializableField(5)]
    private Map _originMap;

    public bool IsCurrentlyJailed => JailEndTime > Core.Now;

    private void MigrateFrom(V0Content content)
    {
        _jailCount = content.JailCount;
        _lastJailed = content.LastJailed;
        _jailEndTime = content.JailEndTime;
        _lastJailReason = content.LastJailReason;
        _jailedBy = content.JailedBy;

        // _originMap stays null: pre-v1 records predate the facet being recorded, so release falls
        // back to the default map.
    }
}
