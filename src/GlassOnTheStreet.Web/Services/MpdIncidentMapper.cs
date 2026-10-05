using System.Text.Json;
using GlassOnTheStreet.Web.Infrastructure;
using GlassOnTheStreet.Web.Models;

namespace GlassOnTheStreet.Web.Services;

/// <summary>Turns one feature of MPD's crime feed (its attributes object) into an <see cref="MpdIncident"/>.</summary>
public static class MpdIncidentMapper
{
    private const double MercatorRadius = 20037508.34;

    /// <summary>Null when the row has no case number, offense or occurred date, so it can't be placed.</summary>
    public static MpdIncident? FromAttributes(JsonElement attrs)
    {
        var caseNumber = Text(attrs, "Case_Number");
        var offense = Text(attrs, "Offense");
        var occurredMs = Long(attrs, "Occurred_Date");
        if (caseNumber is null || offense is null || occurredMs is null)
        {
            return null;
        }

        var local = TimeZoneInfo.ConvertTimeFromUtc(
            DateTimeOffset.FromUnixTimeMilliseconds(occurredMs.Value).UtcDateTime, CentralTime.Zone);

        var category = Text(attrs, "Offense_Category");
        var group = CrimeGroups.Classify(category, offense, Text(attrs, "Type"));
        var nibrsCode = Text(attrs, "NIBRS_Code") ?? "";

        decimal? lat = null, lng = null;
        // MPD sends 0,0 for a record it could not place. That means "no location", not a point off the coast of Africa.
        if (Double(attrs, "wgsXAnon") is { } x && Double(attrs, "wgsYAnon") is { } y && !(x == 0 && y == 0))
        {
            // Despite the "wgs" name these are Web Mercator (EPSG:3857) meters.
            lng = Math.Round((decimal)(x / MercatorRadius * 180.0), 6);
            lat = Math.Round((decimal)(180.0 / Math.PI * (2 * Math.Atan(Math.Exp(y / MercatorRadius * Math.PI)) - Math.PI / 2)), 6);
        }

        return new MpdIncident
        {
            ExternalKey = Truncate($"{caseNumber}|{nibrsCode}|{offense}", 160),
            CaseNumber = Truncate(caseNumber, 20),
            OccurredDate = DateOnly.FromDateTime(local),
            OccurredHour = (byte)local.Hour,
            GroupKey = group.Key,
            Offense = Truncate(offense, 100),
            OffenseCategory = Text(attrs, "Offense_Category") is { } c ? Truncate(c, 80) : null,
            CrimeAgainst = Text(attrs, "NIBRS_Crime_Against") is { } a ? Truncate(a, 30) : null,
            IsCrime = group.IsCrime,
            CrimeCount = (short)Math.Clamp(Long(attrs, "Crime_Count") ?? 1, 1, short.MaxValue),
            Neighborhood = Text(attrs, "Neighborhood") is { } n ? Truncate(n, 120) : null,
            Ward = Byte(attrs, "Ward"),
            Precinct = Byte(attrs, "Precinct"),
            Lat = lat,
            Lng = lng,
            Address = Text(attrs, "Address") is { } addr && !addr.Equals("No Address", StringComparison.OrdinalIgnoreCase) ? Truncate(addr, 100) : null
        };
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private static string? Text(JsonElement attrs, string name)
    {
        if (!attrs.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString()?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static long? Long(JsonElement attrs, string name) =>
        attrs.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var l) ? l : null;

    private static double? Double(JsonElement attrs, string name) =>
        attrs.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private static byte? Byte(JsonElement attrs, string name) =>
        Long(attrs, name) is { } l && l is >= 0 and <= byte.MaxValue ? (byte)l : null;
}
