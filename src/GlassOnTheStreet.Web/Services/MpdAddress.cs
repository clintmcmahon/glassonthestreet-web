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

    private static readonly HashSet<string> KeepUpper = new(StringComparer.OrdinalIgnoreCase)
    {
        "N", "S", "E", "W", "NE", "NW", "SE", "SW", "MLK"
    };

    /// <summary>"0048XX 13TH AVE S" becomes "4800 block of 13th Ave S"; "4TH ST N / 1ST AVE N" becomes "4th St N &amp; 1st Ave N".</summary>
    public static string Pretty(string? mpdAddress)
    {
        if (string.IsNullOrWhiteSpace(mpdAddress))
        {
            return "";
        }

        var address = mpdAddress.Trim();
        var match = Regex.Match(address, @"^(\d{4})XX\s+(.+)$");
        if (match.Success)
        {
            return $"{int.Parse(match.Groups[1].Value) * 100} block of {TitleStreet(match.Groups[2].Value)}";
        }

        return string.Join(" & ", address.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(TitleStreet));
    }

    private static string TitleStreet(string street) =>
        string.Join(' ', street.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(token =>
        {
            if (KeepUpper.Contains(token))
            {
                return token.ToUpperInvariant();
            }

            var ordinal = Regex.Match(token, @"^(\d+)(ST|ND|RD|TH)$", RegexOptions.IgnoreCase);
            if (ordinal.Success)
            {
                return ordinal.Groups[1].Value + ordinal.Groups[2].Value.ToLowerInvariant();
            }

            return char.ToUpperInvariant(token[0]) + token[1..].ToLowerInvariant();
        }));

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
