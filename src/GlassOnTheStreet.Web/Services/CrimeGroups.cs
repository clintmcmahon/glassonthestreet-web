namespace GlassOnTheStreet.Web.Services;

/// <param name="IsCrime">False for metrics that aren't counted as crimes (see MpdIncident.IsCrime).</param>
public record CrimeGroup(string Key, string Label, bool IsCrime, string Definition);

/// <summary>
/// Plain-language groups over MPD's offense names, so the site can say
/// "aggravated assault" or "theft from motor vehicle" instead of NIBRS
/// jargon. Built from the feed's own Offense_Category, Offense and Type
/// fields; nothing is guessed beyond that mapping.
/// </summary>
public static class CrimeGroups
{
    public static readonly IReadOnlyList<CrimeGroup> Crimes =
    [
        new("homicide", "Homicide", true, "MPD's \"Homicide Offenses\": murder and non-negligent manslaughter. Counted per victim."),
        new("agg-assault", "Aggravated assault", true, "MPD's \"Aggravated Assault\": an attack with a weapon or causing serious injury."),
        new("simple-assault", "Simple assault", true, "MPD's \"Simple Assault\": an attack without a weapon or serious injury."),
        new("intimidation", "Intimidation", true, "MPD's \"Intimidation\": threatening someone without a physical attack."),
        new("robbery", "Robbery", true, "MPD's \"Robbery\": taking property from a person by force or threat. Includes carjacking."),
        new("sex-offenses", "Sex offenses", true, "MPD's \"Sex Offenses\", all NIBRS sex offense types."),
        new("burglary", "Burglary", true, "MPD's \"Burglary/Breaking & Entering\": unlawful entry into a building."),
        new("theft-from-vehicle", "Theft from motor vehicle", true, "MPD's \"Theft From Motor Vehicle\": items taken from a car. MPD doesn't record whether a window was smashed or a door was unlocked."),
        new("vehicle-theft", "Motor vehicle theft", true, "MPD's \"Motor Vehicle Theft\": the whole vehicle taken."),
        new("parts-theft", "Vehicle parts theft", true, "MPD's \"Theft of Motor Vehicle Parts or Accessories\": catalytic converters, wheels, stereos and similar."),
        new("shoplifting", "Shoplifting", true, "MPD's \"Shoplifting\"."),
        new("other-larceny", "Other theft", true, "The rest of MPD's \"Larceny/Theft Offenses\", such as \"All Other Larceny\" and theft from a building."),
        new("vandalism", "Property damage / vandalism", true, "MPD's \"Destruction/Damage/Vandalism of Property\": graffiti, broken windows and other damage. Not specific to vehicles."),
        new("arson", "Arson", true, "MPD's \"Arson\"."),
        new("fraud", "Fraud", true, "MPD's \"Fraud Offenses\": credit card fraud, identity theft, swindles."),
        new("drugs", "Drug offenses", true, "MPD's \"Drug/Narcotic Offenses\"."),
        new("weapons", "Weapon law violations", true, "MPD's \"Weapon Law Violations\"."),
        new("other", "Other offenses", true, "Every other NIBRS offense in the feed, including stolen property, counterfeiting, kidnapping and trafficking."),
    ];

    /// <summary>Feed rows that describe activity but aren't crimes, or repeat a crime counted elsewhere.</summary>
    public static readonly IReadOnlyList<CrimeGroup> Metrics =
    [
        new("shots-fired", "Shots-fired calls", false, "ShotSpotter detections and 911 calls about gunfire. Counts of calls, not confirmed crimes."),
        new("gunshot-victims", "Gunshot wound victims", false, "MPD's \"Gunshot Wound Victims\" count. A separate tally from the offenses above."),
        new("domestic-agg-assault", "Domestic aggravated assault", false, "A subset of aggravated assault already counted above. Shown separately, never added in."),
        new("carjacking", "Carjacking", false, "A subset of robbery already counted above. Shown separately, never added in."),
    ];

    public static readonly IReadOnlyList<CrimeGroup> All = [.. Crimes, .. Metrics];

    private static readonly Dictionary<string, CrimeGroup> ByKey = All.ToDictionary(g => g.Key);

    private static readonly Dictionary<string, int> IndexByKey =
        All.Select((g, i) => (g.Key, i)).ToDictionary(x => x.Key, x => x.i);

    /// <summary>Position in <see cref="All"/>; the "other" group for anything unrecognized.</summary>
    public static int IndexOf(string key) => IndexByKey.TryGetValue(key, out var i) ? i : IndexByKey["other"];

    public static CrimeGroup? Find(string? key) =>
        key is not null && ByKey.TryGetValue(key, out var group) ? group : null;

    /// <summary>Maps one feed row to a group from its own category, offense and type fields.</summary>
    public static CrimeGroup Classify(string? offenseCategory, string? offense, string? type)
    {
        var category = offenseCategory?.Trim() ?? "";
        var name = offense?.Trim() ?? "";
        var rowType = type?.Trim() ?? "";

        if (rowType == "Shots Fired Calls" || category == "Shots Fired Calls")
        {
            return ByKey["shots-fired"];
        }

        if (rowType == "Gunshot Wound Victims" || category == "Gunshot Wound Victims")
        {
            return ByKey["gunshot-victims"];
        }

        if (category == "Subset of NIBRS Assault Offenses")
        {
            return ByKey["domestic-agg-assault"];
        }

        if (category == "Subset of NIBRS Robbery")
        {
            return ByKey["carjacking"];
        }

        return category switch
        {
            "Homicide Offenses" => ByKey["homicide"],
            "Assault Offenses" => name switch
            {
                "Aggravated Assault" => ByKey["agg-assault"],
                "Simple Assault" => ByKey["simple-assault"],
                "Intimidation" => ByKey["intimidation"],
                _ => ByKey["other"]
            },
            "Robbery" => ByKey["robbery"],
            "Sex Offenses" => ByKey["sex-offenses"],
            "Burglary/Breaking & Entering" => ByKey["burglary"],
            "Motor Vehicle Theft" => ByKey["vehicle-theft"],
            "Larceny/Theft Offenses" => name switch
            {
                "Theft From Motor Vehicle" => ByKey["theft-from-vehicle"],
                "Theft of Motor Vehicle Parts or Accessories" => ByKey["parts-theft"],
                "Shoplifting" => ByKey["shoplifting"],
                _ => ByKey["other-larceny"]
            },
            "Destruction/Damage/Vandalism of Property" => ByKey["vandalism"],
            "Arson" => ByKey["arson"],
            "Fraud Offenses" => ByKey["fraud"],
            "Drug/Narcotic Offenses" => ByKey["drugs"],
            "Weapon Law Violations" => ByKey["weapons"],
            _ => ByKey["other"]
        };
    }
}
