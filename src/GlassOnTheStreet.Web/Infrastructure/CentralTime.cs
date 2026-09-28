namespace GlassOnTheStreet.Web.Infrastructure;

// Shared Central Time zone lookup -- "America/Chicago" on Linux (the
// production server and most dev machines), falling back to the Windows
// id for local dev on Windows. Every UTC timestamp this site displays
// (sync status, submission times) should convert through this rather than
// showing UTC, since the audience is Minneapolis residents and city
// officials, not a UTC-native one.
public static class CentralTime
{
    public static readonly TimeZoneInfo Zone = Resolve();

    private static TimeZoneInfo Resolve()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");
        }
    }
}
