using System.Text.Json;

namespace GlassOnTheStreet.Web.Services;

public interface IPopulationService
{
    int CityTotal { get; }

    /// <summary>Residents (2020 Census) in a neighborhood, matched to MPD's neighborhood name; null if unknown.</summary>
    int? ForNeighborhood(string name);

    int? ForWard(int ward);

    /// <summary>
    /// Below this many residents a per-resident rate is meaningless (an
    /// industrial area with 40 residents and a few hundred reports), so rates
    /// are withheld.
    /// </summary>
    int MinimumForRate { get; }

    string Source { get; }
}

/// <summary>
/// Resident population from Data/population-2020.json, built by
/// tools/build_population.py from 2020 Census block counts summed into the
/// city's neighborhood and ward boundaries.
/// </summary>
public class PopulationService : IPopulationService
{
    private readonly Dictionary<string, int> neighborhoods = new();
    private readonly Dictionary<int, int> wards = new();

    public PopulationService()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "population-2020.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        CityTotal = root.GetProperty("cityTotal").GetInt32();
        Source = root.GetProperty("source").GetString() ?? "";
        foreach (var item in root.GetProperty("neighborhoods").EnumerateObject())
        {
            neighborhoods[Normalize(item.Name)] = item.Value.GetInt32();
        }

        foreach (var item in root.GetProperty("wards").EnumerateObject())
        {
            wards[int.Parse(item.Name)] = item.Value.GetInt32();
        }
    }

    public int CityTotal { get; }

    public int MinimumForRate => 1000;

    public string Source { get; }

    public int? ForNeighborhood(string name) =>
        neighborhoods.TryGetValue(Normalize(name), out var population) ? population : null;

    public int? ForWard(int ward) => wards.TryGetValue(ward, out var population) ? population : null;

    // The city's boundary layer and MPD spell a few names differently ("Steven's Square").
    private static string Normalize(string name) =>
        new(name.Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
