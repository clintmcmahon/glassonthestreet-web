namespace GlassOnTheStreet.Web.Infrastructure;

public static class DateTimeFormatting
{
    // Every "last updated"-style timestamp on the site should route through
    // this, for the same reason NumberFormatting exists: one place decides
    // the format, not one decision per view. UTC, labeled explicitly --
    // this is a technical status timestamp (was the sync healthy?), not
    // reader-facing content, so it doesn't need the site's usual
    // Central-Time framing.
    public static string FormatSyncTimestamp(this DateTime utc) => $"{utc:MMM d, yyyy 'at' h:mm tt} UTC";
}
