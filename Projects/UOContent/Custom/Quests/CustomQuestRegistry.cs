using Server.Engines.MLQuests;

namespace Server.Custom;

/// <summary>
///     Wires this shard's custom ML quests to the NPCs that offer them.
///     <para>
///         Upstream quests are declared in <c>Distribution/Data/MLQuests.cfg</c>, read by
///         <see cref="MLQuestSystem" />'s static constructor. That file is upstream, so custom content
///         registers through the equivalent public seam instead.
///     </para>
///     <para>
///         This must run in <c>Configure()</c>, not <c>Initialize()</c>. The server bootstraps
///         Configure -> World.Load() -> Initialize, and a player's completed-quest records are stored as
///         type names resolved through <see cref="MLQuestSystem.Quests" /> during world load. Registering
///         any later would drop those records, resetting the once-per-character guarantee on every restart.
///     </para>
/// </summary>
public static class CustomQuestRegistry
{
    public static void Configure()
    {
        MLQuestSystem.Register(new MartasFishRequest(), typeof(OldMarta));
    }
}
