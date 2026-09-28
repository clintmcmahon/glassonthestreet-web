using System.Globalization;

namespace GlassOnTheStreet.Web.Infrastructure;

// Every reader-facing count on the site should route through this, so a
// number like 117576 always renders as "117,576" -- not sometimes
// formatted and sometimes not, depending on which view happened to
// remember to do it.
public static class NumberFormatting
{
    public static string Format(this int value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
