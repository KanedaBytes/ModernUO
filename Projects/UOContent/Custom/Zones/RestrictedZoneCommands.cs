using Server.Commands;

namespace Server.Custom;

/// <summary>
///     Staff commands for managing restricted zones.
/// </summary>
public static class RestrictedZoneCommands
{
    public static void Configure()
    {
        CommandSystem.Register("RestrictZone", AccessLevel.GameMaster, RestrictZone_OnCommand);
        CommandSystem.Register("UnrestrictZone", AccessLevel.GameMaster, UnrestrictZone_OnCommand);
        CommandSystem.Register("ListRestrictedZones", AccessLevel.GameMaster, ListRestrictedZones_OnCommand);
    }

    [Usage("RestrictZone <name>")]
    [Description("Marks a rectangular area as a restricted zone by targeting two corners.")]
    public static void RestrictZone_OnCommand(CommandEventArgs e)
    {
        var from = e.Mobile;

        if (e.Length != 1)
        {
            from.SendMessage("Usage: [RestrictZone <name>");
            return;
        }

        var name = e.GetString(0);

        if (RestrictedZoneSystem.Find(name) != null)
        {
            from.SendMessage($"A restricted zone named '{name}' already exists.");
            return;
        }

        // Handles the two-corner prompts and rejects corners on different maps.
        BoundingBoxPicker.Begin(from, (map, start, end) => OnBoundsPicked(from, name, map, start, end));
    }

    private static void OnBoundsPicked(Mobile from, string name, Map map, Point3D start, Point3D end)
    {
        if (map == null || map == Map.Internal)
        {
            from.SendMessage("That is not a valid map for a restricted zone.");
            return;
        }

        // Re-check: the staff member may have taken a while over the two targets.
        if (RestrictedZoneSystem.Find(name) != null)
        {
            from.SendMessage($"A restricted zone named '{name}' already exists.");
            return;
        }

        var bounds = new Rectangle2D(start.X, start.Y, end.X - start.X + 1, end.Y - start.Y + 1);
        var record = new RestrictedZoneRecord(name, map, bounds);

        if (!RestrictedZoneSystem.Add(record))
        {
            from.SendMessage($"A restricted zone named '{name}' already exists.");
            return;
        }

        from.SendMessage(
            $"Restricted zone '{name}' created on {map} covering {bounds.Width}x{bounds.Height} tiles at {bounds.X}, {bounds.Y}."
        );

        CommandLogging.WriteLine(
            from,
            $"{from.AccessLevel} {CommandLogging.Format(from)} creating restricted zone '{name}' on {map} at {bounds}"
        );
    }

    [Usage("UnrestrictZone <name>")]
    [Description("Removes a restricted zone by name.")]
    public static void UnrestrictZone_OnCommand(CommandEventArgs e)
    {
        var from = e.Mobile;

        if (e.Length != 1)
        {
            from.SendMessage("Usage: [UnrestrictZone <name>");
            return;
        }

        var name = e.GetString(0);
        var record = RestrictedZoneSystem.Find(name);

        if (record == null)
        {
            from.SendMessage($"No restricted zone named '{name}' exists.");
            return;
        }

        RestrictedZoneSystem.Remove(record);

        from.SendMessage($"Restricted zone '{name}' removed.");

        CommandLogging.WriteLine(
            from,
            $"{from.AccessLevel} {CommandLogging.Format(from)} removing restricted zone '{name}'"
        );
    }

    [Usage("ListRestrictedZones")]
    [Description("Lists every restricted zone.")]
    public static void ListRestrictedZones_OnCommand(CommandEventArgs e)
    {
        var from = e.Mobile;
        var zones = RestrictedZoneSystem.Zones;

        if (zones.Count == 0)
        {
            from.SendMessage("There are no restricted zones.");
            return;
        }

        from.SendMessage($"{zones.Count} restricted zone(s):");

        for (var i = 0; i < zones.Count; i++)
        {
            var record = zones[i];
            var bounds = record.Bounds;

            from.SendMessage(
                $"{record.Name}: {record.Map}, {bounds.Width}x{bounds.Height} at {bounds.X}, {bounds.Y}"
            );
        }
    }
}
