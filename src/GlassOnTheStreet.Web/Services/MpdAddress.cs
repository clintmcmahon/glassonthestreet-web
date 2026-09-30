using System.Text.RegularExpressions;

namespace GlassOnTheStreet.Web.Services;

/// <summary>
/// Builds MPD-style block labels ("0048XX 13TH AVE S") from a house number
/// and an OpenStreetMap street name, so a resident report can be matched to
/// the block anchor MPD already uses for that block. MPD's abbreviations
/// were read off its own feed (AVE, ST, PKWY, BLVD, RD, DR, PL, TERR, ...).
/// </summary>
public static class MpdAddress
{
    private static readonly Dictionary<string, string> StreetTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Avenue"] = "AVE",
        ["Street"] = "ST",
        ["Road"] = "RD",
        ["Boulevard"] = "BLVD",
        ["Parkway"] = "PKWY",
        ["Drive"] = "DR",
        ["Lane"] = "LN",
        ["Court"] = "CT",
        ["Place"] = "PL",
        ["Terrace"] = "TERR",
        ["Circle"] = "CIR",
    };

    private static readonly Dictionary<string, string> Directions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["North"] = "N",
        ["South"] = "S",
        ["East"] = "E",
        ["West"] = "W",
        ["Northeast"] = "NE",
        ["Northwest"] = "NW",
        ["Southeast"] = "SE",
        ["Southwest"] = "SW",
    };

    /// <summary>"13th Avenue South" becomes "13TH AVE S".</summary>
    public static string NormalizeStreet(string osmStreetName)
    {
        var tokens = osmStreetName.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        // OSM writes numbered streets as "East 49th Street"; MPD writes
        // "49TH ST E". Move the leading direction to the end.
        if (tokens.Count >= 2 && Directions.ContainsKey(tokens[0]) && Regex.IsMatch(tokens[1], @"^\d+(st|nd|rd|th)$", RegexOptions.IgnoreCase))
        {
            tokens.Add(tokens[0]);
            tokens.RemoveAt(0);
        }

        for (var i = 1; i < tokens.Count; i++)
        {
            // A leading "East"/"West" (East Minnehaha Parkway) is part of
            // MPD's name; only a trailing direction is abbreviated.
            if (i == tokens.Count - 1 && Directions.TryGetValue(tokens[i], out var direction))
            {
                tokens[i] = direction;
            }
            else if (StreetTypes.TryGetValue(tokens[i], out var type))
            {
                tokens[i] = type;
            }
        }

        return string.Join(' ', tokens).ToUpperInvariant();
    }

    /// <summary>The 4836 block of "13TH AVE S" is "0048XX 13TH AVE S".</summary>
    public static string BlockLabel(int houseNumber, string normalizedStreet) =>
        $"{houseNumber / 100:D4}XX {normalizedStreet}";

    /// <summary>Leading digits of an OSM house number ("4836", "4836A", "4836-4838"), or null.</summary>
    public static int? ParseHouseNumber(string? houseNumber)
    {
        if (houseNumber is null)
        {
            return null;
        }

        var match = Regex.Match(houseNumber, @"^\s*(\d{1,6})");
        return match.Success ? int.Parse(match.Groups[1].Value) : null;
    }
}
