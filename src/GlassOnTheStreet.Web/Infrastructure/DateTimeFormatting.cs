namespace GlassOnTheStreet.Web.Infrastructure;

public static class DateTimeFormatting
{
    // Every stored timestamp on the site is UTC (DateTime.UtcNow), but the
    // audience is Minneapolis residents and city officials, not a UTC-native
    // one -- nothing gets displayed as UTC. SpecifyKind is needed because
    // MySQL/Pomelo hands values back as DateTimeKind.Unspecified, not Utc,
    // even though they really are UTC on the wire.
    public static DateTime ToCentralTime(this DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), CentralTime.Zone);

    // Every "last updated"-style timestamp on the site should route through
    // this, for the same reason NumberFormatting exists: one place decides
    // the format, not one decision per view.
    public static string FormatSyncTimestamp(this DateTime utc) =>
        $"{utc.ToCentralTime():MMM d, yyyy 'at' h:mm tt} CT";

    public static string FormatDateTime(this DateTime utc) =>
        utc.ToCentralTime().ToString("yyyy-MM-dd HH:mm");
}
