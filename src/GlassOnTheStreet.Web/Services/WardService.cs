using System.Text.Json;

namespace GlassOnTheStreet.Web.Services;

/// <param name="Title">"Council Member", "Council President" or "Council Vice President", as the city's page says.</param>
/// <param name="Geometry">The ward's boundary as GeoJSON (Polygon or MultiPolygon, WGS84).</param>
public record CouncilWard(
    int Ward, string Name, string Title, string Phone, string PageUrl, string ContactUrl,
    double LabelLng, double LabelLat, JsonElement Geometry)
{
    public string StatsUrl => $"/wards/{Ward}";
}

public interface IWardService
{
    IReadOnlyList<CouncilWard> All { get; }

    CouncilWard? Find(int ward);

    string Source { get; }

    /// <summary>The date the boundaries and names were last read from the city (ISO date).</summary>
    string RetrievedOn { get; }
}

/// <summary>
/// Ward boundaries and council members from Data/wards-2022.json, built by tools/build_wards.py from the
/// City of Minneapolis. Names change after elections, so the retrieval date is shown wherever they are.
/// </summary>
public class WardService : IWardService
{
    private readonly Dictionary<int, CouncilWard> byWard = new();

    public WardService()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "wards-2022.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        Source = root.GetProperty("source").GetString()!;
        RetrievedOn = root.GetProperty("retrievedOn").GetString()!;

        foreach (var w in root.GetProperty("wards").EnumerateArray())
        {
            var label = w.GetProperty("label");
            var ward = new CouncilWard(
                w.GetProperty("ward").GetInt32(),
                w.GetProperty("name").GetString()!,
                w.GetProperty("title").GetString()!,
                w.GetProperty("phone").GetString()!,
                w.GetProperty("pageUrl").GetString()!,
                w.GetProperty("contactUrl").GetString()!,
                label[0].GetDouble(), label[1].GetDouble(),
                w.GetProperty("geometry").Clone());
            byWard[ward.Ward] = ward;
        }

        All = byWard.Values.OrderBy(w => w.Ward).ToList();
    }

    public IReadOnlyList<CouncilWard> All { get; }

    public string Source { get; }

    public string RetrievedOn { get; }

    public CouncilWard? Find(int ward) => byWard.GetValueOrDefault(ward);
}
