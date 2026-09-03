using System;
using Server.Gumps;
using Server.Mobiles;
using Server.Network;

namespace Server.Custom;

/// <summary>
///     Small top-centre display showing how long is left on a jail sentence.
///     <para>
///         Unlike the restricted-zone countdown this one is deliberately closable - a twelve-hour
///         sentence is a long time to stare at an undismissable window. The once-a-minute refresh
///         brings it back, so it can be dismissed for a minute of quiet without losing track.
///     </para>
/// </summary>
public class JailStatusGump : StaticGump<JailStatusGump>
{
    private const int GumpWidth = 220;
    private const int GumpHeight = 60;

    private readonly string _remaining;

    /// <summary>
    ///     Mandatory: without it each refresh stacks another copy rather than replacing the last,
    ///     and a long sentence would walk a player into the 512-gump cap and a disconnect.
    /// </summary>
    public override bool Singleton => true;

    private JailStatusGump(string remaining) : base((640 - GumpWidth) / 2, 20) => _remaining = remaining;

    /// <summary>
    ///     Static entry point so the "is this player actually jailed" check happens before the gump
    ///     is constructed - a gump that short-circuits post-construction is sent empty, and an empty
    ///     gump cannot be closed by the client.
    /// </summary>
    public static void DisplayTo(PlayerMobile pm)
    {
        if (pm?.NetState == null || !JailStatusSystem.IsJailed(pm))
        {
            return;
        }

        pm.SendGump(new JailStatusGump(FormatRemaining(JailStatusSystem.GetRemaining(pm))));
    }

    /// <summary>
    ///     Coarse by design: the gump only refreshes once a minute, so second-level precision would
    ///     be a lie for up to 59 seconds at a time.
    /// </summary>
    public static string FormatRemaining(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero)
        {
            return "Release is imminent";
        }

        var hours = (int)remaining.TotalHours;
        var minutes = remaining.Minutes;

        if (hours > 0)
        {
            return $"{hours}h {minutes}m remaining";
        }

        return minutes > 0 ? $"{minutes}m remaining" : "Less than a minute remaining";
    }

    protected override void BuildLayout(ref StaticGumpBuilder builder)
    {
        // Closable (no SetNoClose/SetNoDispose), but pinned so it cannot be dragged - X/Y are
        // re-sent on every refresh, so a dragged gump would snap back each minute anyway.
        builder.SetNoMove();
        builder.SetNoResize();

        builder.AddPage();

        // Required: without visual elements the gump is invisible and leaks a slot.
        builder.AddBackground(0, 0, GumpWidth, GumpHeight, 9200);
        builder.AddImageTiled(10, 10, GumpWidth - 20, GumpHeight - 20, 2624);
        builder.AddAlphaRegion(10, 10, GumpWidth - 20, GumpHeight - 20);

        builder.AddHtmlPlaceholder(15, 12, GumpWidth - 30, 20, "header");
        builder.AddHtmlPlaceholder(15, 32, GumpWidth - 30, 20, "remaining");
    }

    /// <summary>
    ///     Slot mapping is positional on cached sends, so both slots are set unconditionally and in
    ///     a fixed order - a conditional slot would silently shift the other one.
    /// </summary>
    protected override void BuildStrings(ref GumpStringsBuilder builder)
    {
        builder.SetHtmlText("header", "JAILED", "#FF6600", 4);
        builder.SetHtmlText("remaining", _remaining, "#FFFFFF", 4);
    }

    // Deliberately no OnResponse override: dismissing this gump is allowed and should stick until
    // the next sweep, which is the whole point of it being closable. The minute tick puts it back.
}
