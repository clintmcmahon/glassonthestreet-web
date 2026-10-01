using System.Text.Json;

namespace GlassOnTheStreet.Web.Infrastructure;

/// <summary>
/// schema.org JSON-LD built with a serializer instead of hand-written
/// strings, so a neighborhood name with an apostrophe or ampersand can't
/// break the markup. The default encoder also escapes "&lt;", so the output
/// is safe inside a script tag.
/// </summary>
public static class StructuredData
{
    // MPD's feed that this site imports (documented in MinneapolisOpenDataImportService).
    private const string SourceUrl = "https://services.arcgis.com/afSMGVsC7QlRK1kZ/arcgis/rest/services/Crime_Data/FeatureServer/0";

    /// <summary>The full script tag, written literally so the "+" in the type isn't entity-encoded.</summary>
    public static string Tag(string json) => $"<script type=\"application/ld+json\">{json}</script>";

    public static string Dataset(
        string baseUrl, string path, string name, string description, string placeName,
        DateOnly through, string distributionPath) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["@context"] = "https://schema.org",
            ["@type"] = "Dataset",
            ["name"] = name,
            ["description"] = description,
            ["url"] = baseUrl + path,
            ["keywords"] = new[] { "Minneapolis", "car break-ins", "theft from motor vehicle", "auto theft", "vehicle parts theft", "vandalism", "crime statistics" },
            ["isAccessibleForFree"] = true,
            ["temporalCoverage"] = $"2019-01-01/{through:yyyy-MM-dd}",
            ["spatialCoverage"] = new Dictionary<string, object> { ["@type"] = "Place", ["name"] = placeName },
            ["creator"] = new Dictionary<string, object> { ["@type"] = "Organization", ["name"] = "Glass on the Street", ["url"] = baseUrl + "/" },
            ["isBasedOn"] = new Dictionary<string, object>
            {
                ["@type"] = "Dataset",
                ["name"] = "Minneapolis Police Department Crime Data (City of Minneapolis open data)",
                ["url"] = SourceUrl
            },
            ["variableMeasured"] = new[]
            {
                "Reports per month",
                "Reports per year, same calendar period",
                "Reports by neighborhood and ward",
                "Reports by offense category and time of day"
            },
            ["distribution"] = new Dictionary<string, object>
            {
                ["@type"] = "DataDownload",
                ["encodingFormat"] = "application/json",
                ["contentUrl"] = baseUrl + distributionPath
            }
        });

    public static string Breadcrumbs(string baseUrl, params (string Name, string Path)[] items) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["@context"] = "https://schema.org",
            ["@type"] = "BreadcrumbList",
            ["itemListElement"] = items.Select((item, i) => new Dictionary<string, object>
            {
                ["@type"] = "ListItem",
                ["position"] = i + 1,
                ["name"] = item.Name,
                ["item"] = baseUrl + item.Path
            }).ToArray()
        });
}
