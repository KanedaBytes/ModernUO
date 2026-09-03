using Server.Gumps;
using Server.Network;

namespace Server.Custom;

/// <summary>
///     Small top-centre display counting down the seconds before a trespasser is jailed.
///     <para>
///         <see cref="StaticGump{TSelf}" /> compiles and caches its layout once per gump type; only
///         the placeholder text is rebuilt per send. That is what makes re-sending this once a
///         second cheap - <c>BuildLayout</c> never runs again after the first send.
///     </para>
/// </summary>
public class RestrictedZoneCountdownGump : StaticGump<RestrictedZoneCountdownGump>
{
    private const int GumpWidth = 220;
    private const int GumpHeight = 60;

    private readonly string _zoneName;
    private readonly int _secondsLeft;

    /// <summary>
    ///     Mandatory. Without it every re-send stacks another copy instead of replacing the last,
    ///     and at one send per second a player would hit the 512-gump cap - and be disconnected -
    ///     in under nine minutes.
    /// </summary>
    public override bool Singleton => true;

    // 640x480 is the reference viewport this codebase assumes throughout.
    private RestrictedZoneCountdownGump(string zoneName, int secondsLeft)
        : base((640 - GumpWidth) / 2, 20)
    {
        _zoneName = zoneName;
        _secondsLeft = secondsLeft;
    }

    /// <summary>
    ///     Static entry point so validation happens before the gump is constructed. A gump that
    ///     short-circuits after construction is sent empty, and an empty gump carrying the no-close
    ///     flags below would be permanently stuck on the client.
    /// </summary>
    public static void DisplayTo(Mobile from, string zoneName, int secondsLeft)
    {
        if (from?.NetState == null || zoneName == null)
        {
            return;
        }

        from.SendGump(new RestrictedZoneCountdownGump(zoneName, secondsLeft));
    }

    protected override void BuildLayout(ref StaticGumpBuilder builder)
    {
        // SetNoClose only blocks right-click; ESC still dismisses without SetNoDispose. SetNoMove
        // both prevents dragging and avoids the gump snapping back on every refresh, since X/Y are
        // re-sent in each packet.
        builder.SetNoClose();
        builder.SetNoDispose();
        builder.SetNoMove();
        builder.SetNoResize();

        builder.AddPage();

        // A background is required, not decoration: without visual elements HasVisualElements is
        // false and the gump becomes an invisible, undismissable leak.
        builder.AddBackground(0, 0, GumpWidth, GumpHeight, 9200);
        builder.AddImageTiled(10, 10, GumpWidth - 20, GumpHeight - 20, 2624);
        builder.AddAlphaRegion(10, 10, GumpWidth - 20, GumpHeight - 20);

        builder.AddHtmlPlaceholder(15, 12, GumpWidth - 30, 20, "zoneName");
        builder.AddHtmlPlaceholder(15, 32, GumpWidth - 30, 20, "secondsLeft");
    }

    /// <summary>
    ///     Slot order is positional on cached sends - the Nth SetHtmlText call fills whichever slot
    ///     was Nth on the first compile. Every slot must be set unconditionally, in a fixed order.
    /// </summary>
    protected override void BuildStrings(ref GumpStringsBuilder builder)
    {
        builder.SetHtmlText("zoneName", $"RESTRICTED: {_zoneName}", "#FF6600", 4);
        builder.SetHtmlText(
            "secondsLeft",
            _secondsLeft == 1 ? "Leave now - 1 second" : $"Leave now - {_secondsLeft} seconds",
            "#FFFFFF",
            4
        );
    }

    /// <summary>
    ///     The no-close flags are client-side hints and are not server-enforced, so a modified
    ///     client can still dismiss this. Re-display immediately if that happens - by the time
    ///     OnResponse runs the gump has already been removed from server tracking, so a plain send
    ///     is correct here.
    /// </summary>
    public override void OnResponse(NetState sender, in RelayInfo info)
    {
        if (info.ButtonID == 0 && RestrictedZoneSystem.HasCountdown(sender?.Mobile))
        {
            DisplayTo(sender.Mobile, _zoneName, _secondsLeft);
        }
    }
}
