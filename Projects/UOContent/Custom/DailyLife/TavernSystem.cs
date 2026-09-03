using System.Collections.Generic;
using ModernUO.CodeGeneratedEvents;
using Server.Logging;

namespace Server.Custom;

/// <summary>
///     Fills the tavern after dark and empties it at dawn.
///     <para>
///         Patrons are tracked in a plain in-memory list rather than persisted. That is deliberate:
///         a restart deletes any saved patrons (see <see cref="TavernPatron" />) and this list comes
///         back empty, so <see cref="ApplyPhase" /> simply rebuilds the correct crowd for whatever
///         phase it is - no orphans, no reconciliation.
///     </para>
/// </summary>
public static class TavernSystem
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(TavernSystem));

    private static readonly List<TavernPatron> _patrons = [];

    public static int PatronCount => _patrons.Count;

    public static void Initialize()
    {
        // Apply the current phase directly rather than waiting for a transition, so a restart at
        // midnight has a full tavern immediately.
        ApplyPhase(DayCycleSystem.Current);
    }

    [OnEvent(nameof(DayCycleSystem.DayPhaseChangedEvent))]
    public static void OnDayPhaseChanged(DayPhase oldPhase, DayPhase newPhase) => ApplyPhase(newPhase);

    /// <summary>
    ///     Rebuilds the crowd from the current config.
    ///     <para>
    ///         Empties first rather than just re-applying: <see cref="FillTavern" /> only tops up to
    ///         the wanted count, so lowering <c>patronCount</c> or moving the tavern bounds would
    ///         otherwise have no visible effect.
    ///     </para>
    /// </summary>
    public static void Reload()
    {
        EmptyTavern();
        ApplyPhase(DayCycleSystem.Current);
    }

    public static void ApplyPhase(DayPhase phase)
    {
        if (phase.IsAfterDark())
        {
            FillTavern();
        }
        else
        {
            EmptyTavern();
        }
    }

    private static void FillTavern()
    {
        var tavern = TownScheduleConfig.Current?.Tavern;

        if (tavern == null)
        {
            return;
        }

        var map = tavern.GetMap();

        if (map == null || map == Map.Internal)
        {
            return;
        }

        PruneDeleted();

        var wanted = tavern.PatronCount;

        for (var i = _patrons.Count; i < wanted; i++)
        {
            if (!TryFindSpot(tavern, map, out var location))
            {
                logger.Warning("Could not place a tavern patron after several attempts; check the tavern bounds");
                break;
            }

            var patron = new TavernPatron
            {
                Home = location,
                HomeMap = map,
                // A small wander radius keeps them milling about inside rather than pinned to a tile
                // or drifting out of the door.
                RangeHome = 3
            };

            patron.MoveToWorld(location, map);
            _patrons.Add(patron);
        }

        logger.Information("Tavern filled with {Count} patron(s)", _patrons.Count);
    }

    private static void EmptyTavern()
    {
        if (_patrons.Count == 0)
        {
            return;
        }

        for (var i = _patrons.Count - 1; i >= 0; i--)
        {
            _patrons[i]?.Delete();
        }

        _patrons.Clear();

        logger.Information("Tavern emptied");
    }

    private static void PruneDeleted()
    {
        for (var i = _patrons.Count - 1; i >= 0; i--)
        {
            if (_patrons[i]?.Deleted != false)
            {
                _patrons.RemoveAt(i);
            }
        }
    }

    private static bool TryFindSpot(TownScheduleConfig.TavernConfig tavern, Map map, out Point3D location)
    {
        var bounds = tavern.ToBounds();

        // Bounded retries: an ill-fitting rectangle should warn, not spin.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var x = Utility.RandomMinMax(bounds.X, bounds.X + bounds.Width - 1);
            var y = Utility.RandomMinMax(bounds.Y, bounds.Y + bounds.Height - 1);

            if (map.CanSpawnMobile(x, y, tavern.Z))
            {
                location = new Point3D(x, y, tavern.Z);
                return true;
            }
        }

        location = Point3D.Zero;
        return false;
    }
}
