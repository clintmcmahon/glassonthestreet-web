using System.Globalization;

namespace GlassOnTheStreet.Web.Services;

/// <param name="Group">null = all crimes, "car" = the four car-related groups, otherwise a CrimeGroups key.</param>
public record MapFilter(string? Group, DateOnly From, DateOnly To);

/// <param name="Top">The block's most common offense types as "key:count|key:count", most common first.</param>
public record MapBlock(double Lat, double Lng, string Address, string? Neighborhood, int Ward, int Count, string Top);

public record MapBlocksResult(
    DateOnly From, DateOnly To, DateOnly Through, string GroupLabel, int Total, IReadOnlyList<MapBlock> Blocks,
    IReadOnlyDictionary<string, string> GroupLabels, int Unlocated = 0);

public record MapCount(string Name, int Count, string? Url);

public record MapSummary(
    DateOnly From,
    DateOnly To,
    DateOnly Through,
    string GroupLabel,
    int Total,
    int PriorTotal,
    double? ChangeVsPrior,
    IReadOnlyList<MapCount> Neighborhoods,
    IReadOnlyList<MapCount> Wards,
    IReadOnlyList<MapCount> TimeOfDay,
    IReadOnlyList<MapCount> Types);

/// <summary>
/// Everything the map needs from the full MPD feed. MPD places every record at the midpoint of its
/// block, so 400k offenses sit on about 12k distinct points: the map gets one feature per block
/// with its count and top offense types, which is small enough to ship whole and cluster in the browser.
/// </summary>
public class MapDataService(IncidentDataCache dataCache)
{
    public const string CarGroup = "car";
    public const string CarLabel = "Car break-ins and car crime";

    private const int TopTypesPerBlock = 6;
    private const int TopAreas = 6;
    private static readonly string[] CarGroupKeys = ["theft-from-vehicle", "vehicle-theft", "parts-theft", "vandalism"];

    /// <summary>
    /// Turns the request's range options into dates. Presets count back from the last date MPD has
    /// published (not from today), so "last 30 days" is always 30 days of data.
    /// </summary>
    public static (DateOnly From, DateOnly To) ResolveRange(string? range, DateOnly? from, DateOnly? to, DateOnly through)
    {
        var first = new DateOnly(IncidentDataCache.FirstYear, 1, 1);

        if (from is not null && to is not null)
        {
            var start = from.Value < first ? first : from.Value;
            var end = to.Value > through ? through : to.Value;
            return start <= end ? (start, end) : (end, start);
        }

        return (range ?? "30").ToLowerInvariant() switch
        {
            "all" => (first, through),
            "ytd" => (new DateOnly(through.Year, 1, 1), through),
            var days when int.TryParse(days, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is > 0 and <= 4000
                => (through.AddDays(-(n - 1)) < first ? first : through.AddDays(-(n - 1)), through),
            _ => (through.AddDays(-29), through)
        };
    }

    public static string NormalizeGroup(string? group) =>
        string.IsNullOrWhiteSpace(group) || group == "all" ? "" :
        group == CarGroup ? CarGroup :
        CrimeGroups.Find(group)?.Key ?? "";

    private static (bool[] Matches, string Label) Resolve(string? group)
    {
        var matches = new bool[CrimeGroups.All.Count];
        var key = NormalizeGroup(group);
        if (key == "")
        {
            for (var i = 0; i < matches.Length; i++)
            {
                matches[i] = CrimeGroups.All[i].IsCrime;
            }

            return (matches, "All crimes");
        }

        if (key == CarGroup)
        {
            foreach (var carKey in CarGroupKeys)
            {
                matches[CrimeGroups.IndexOf(carKey)] = true;
            }

            return (matches, CarLabel);
        }

        matches[CrimeGroups.IndexOf(key)] = true;
        return (matches, CrimeGroups.Find(key)!.Label);
    }

    public async Task<MapBlocksResult> GetBlocksAsync(MapFilter filter, CancellationToken cancellationToken = default)
    {
        var dataset = await dataCache.GetAsync(cancellationToken);
        var (matches, label) = Resolve(filter.Group);

        var groupCount = CrimeGroups.All.Count;
        var blocks = new Dictionary<long, Accumulator>();
        var total = 0;
        var unlocated = 0;

        foreach (var row in dataset.Rows)
        {
            if (row.Date < filter.From || row.Date > filter.To || !matches[row.Group])
            {
                continue;
            }

            // MPD gave these no location. They are in the summary's totals but can't be drawn.
            if (float.IsNaN(row.Lat) || float.IsNaN(row.Lng))
            {
                unlocated += row.Count;
                continue;
            }

            var key = ((long)BitConverter.SingleToInt32Bits(row.Lat) << 32) | (uint)BitConverter.SingleToInt32Bits(row.Lng);
            if (!blocks.TryGetValue(key, out var block))
            {
                blocks[key] = block = new Accumulator(row.Lat, row.Lng, row.Neighborhood, row.Ward, row.AddressIndex, groupCount);
            }

            block.Add(row);
            total += row.Count;
        }

        var result = blocks.Values
            .Select(b => new MapBlock(
                Math.Round(b.Lat, 5), Math.Round(b.Lng, 5),
                MpdAddress.Pretty(dataset.AddressAt(b.AddressIndex)), b.Neighborhood, b.Ward, b.Count, b.TopTypes(TopTypesPerBlock)))
            .OrderByDescending(b => b.Count)
            .ToList();

        return new MapBlocksResult(
            filter.From, filter.To, dataset.Through, label, total, result,
            CrimeGroups.All.ToDictionary(g => g.Key, g => g.Label), unlocated);
    }

    public async Task<MapSummary> GetSummaryAsync(MapFilter filter, CancellationToken cancellationToken = default)
    {
        var dataset = await dataCache.GetAsync(cancellationToken);
        var (matches, label) = Resolve(filter.Group);

        // The period just before this one, of the same length, for the "vs. the prior period" figure.
        var length = filter.To.DayNumber - filter.From.DayNumber + 1;
        var priorTo = filter.From.AddDays(-1);
        var priorFrom = priorTo.AddDays(-(length - 1));
        var hasPrior = priorFrom >= new DateOnly(IncidentDataCache.FirstYear, 1, 1);

        var total = 0;
        var prior = 0;
        var hoods = new Dictionary<string, int>();
        var wards = new Dictionary<int, int>();
        var buckets = new int[4];
        var types = new int[CrimeGroups.All.Count];

        foreach (var row in dataset.Rows)
        {
            if (!matches[row.Group])
            {
                continue;
            }

            if (row.Date >= filter.From && row.Date <= filter.To)
            {
                total += row.Count;
                types[row.Group] += row.Count;
                if (row.Neighborhood is not null)
                {
                    hoods[row.Neighborhood] = hoods.GetValueOrDefault(row.Neighborhood) + row.Count;
                }

                if (row.Ward > 0)
                {
                    wards[row.Ward] = wards.GetValueOrDefault(row.Ward) + row.Count;
                }

                // The same four buckets the break-in pages use.
                buckets[row.Hour switch { < 6 => 0, < 12 => 1, < 17 => 2, < 22 => 3, _ => 0 }] += row.Count;
            }
            else if (hasPrior && row.Date >= priorFrom && row.Date <= priorTo)
            {
                prior += row.Count;
            }
        }

        double? change = hasPrior && prior > 0 ? Math.Round((total - prior) / (double)prior * 100, 1) : null;

        return new MapSummary(
            filter.From, filter.To, dataset.Through, label, total, hasPrior ? prior : 0, change,
            hoods.OrderByDescending(h => h.Value).ThenBy(h => h.Key, StringComparer.OrdinalIgnoreCase).Take(TopAreas)
                .Select(h => new MapCount(h.Key, h.Value, $"/neighborhoods/{AreaSlug.For(h.Key)}")).ToList(),
            wards.OrderByDescending(w => w.Value).ThenBy(w => w.Key).Take(TopAreas)
                .Select(w => new MapCount($"Ward {w.Key}", w.Value, $"/wards/{w.Key}")).ToList(),
            new[] { "Overnight", "Morning", "Afternoon", "Evening" }.Select((name, i) => new MapCount(name, buckets[i], null)).ToList(),
            Enumerable.Range(0, types.Length).Where(i => types[i] > 0).OrderByDescending(i => types[i]).Take(8)
                .Select(i => new MapCount(CrimeGroups.All[i].Label, types[i], null)).ToList());
    }

    private sealed class Accumulator(float lat, float lng, string? neighborhood, int ward, int addressIndex, int groupCount)
    {
        private readonly int[] perGroup = new int[groupCount];

        public float Lat { get; } = lat;

        public float Lng { get; } = lng;

        public string? Neighborhood { get; } = neighborhood;

        public int Ward { get; } = ward;

        public int AddressIndex { get; } = addressIndex;

        public int Count { get; private set; }

        public void Add(IncidentRow row)
        {
            perGroup[row.Group] += row.Count;
            Count += row.Count;
        }

        public string TopTypes(int take) =>
            string.Join("|", Enumerable.Range(0, perGroup.Length).Where(i => perGroup[i] > 0)
                .OrderByDescending(i => perGroup[i]).Take(take)
                .Select(i => $"{CrimeGroups.All[i].Key}:{perGroup[i]}"));
    }
}
