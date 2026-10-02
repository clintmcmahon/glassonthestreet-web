namespace GlassOnTheStreet.Web.Services;

public class CrimeStatsService(IncidentDataCache dataCache, IPopulationService population) : ICrimeStatsService
{
    private const int TopAreaCount = 10;

    public async Task<CrimeData> GetCrimeAsync(CrimeFilter filter, bool sortByRate = false, CancellationToken cancellationToken = default)
    {
        var dataset = await dataCache.GetAsync(cancellationToken);
        return Compute(dataset, filter, sortByRate);
    }

    public async Task<AreaCrimeSummary?> GetNeighborhoodSummaryAsync(string slug, CancellationToken cancellationToken = default)
    {
        var dataset = await dataCache.GetAsync(cancellationToken);
        if (!dataset.NeighborhoodBySlug.TryGetValue(slug.ToLowerInvariant(), out var name))
        {
            return null;
        }

        var data = Compute(dataset, new CrimeFilter(null, name, null), false);
        var all = RankAreas(dataset, null, years: data.Years, byNeighborhood: true, take: null);
        return Summarize(data, all, name);
    }

    public async Task<AreaCrimeSummary?> GetWardSummaryAsync(int ward, CancellationToken cancellationToken = default)
    {
        var dataset = await dataCache.GetAsync(cancellationToken);
        if (!dataset.Wards.Contains(ward))
        {
            return null;
        }

        var data = Compute(dataset, new CrimeFilter(null, null, ward), false);
        var all = RankAreas(dataset, null, years: data.Years, byNeighborhood: false, take: null);
        return Summarize(data, all, $"Ward {ward}");
    }

    public async Task<CompareData> GetCompareAsync(IReadOnlyList<string> areaKeys, string? group, CancellationToken cancellationToken = default)
    {
        var dataset = await dataCache.GetAsync(cancellationToken);
        var years = Enumerable.Range(IncidentDataCache.FirstYear, dataset.Through.Year - IncidentDataCache.FirstYear + 1).ToArray();
        var groupInfo = CrimeGroups.Find(group);
        var groupIndex = groupInfo is null ? -1 : CrimeGroups.IndexOf(groupInfo.Key);
        var isCrime = CrimeGroups.All.Select(g => g.IsCrime).ToArray();
        var through = dataset.Through;

        // key -> (display name, url, residents, matcher)
        var wanted = new List<(string Key, string Name, string Url, int? Residents, Func<IncidentRow, bool> Matches)>();
        foreach (var key in areaKeys.Select(k => k.Trim().ToLowerInvariant()).Where(k => k.Length > 0).Distinct().Take(4))
        {
            if (key == "minneapolis")
            {
                wanted.Add((key, "Minneapolis", "/crime", population.CityTotal, _ => true));
            }
            else if (key.StartsWith("ward-") && int.TryParse(key[5..], out var ward) && dataset.Wards.Contains(ward))
            {
                wanted.Add((key, $"Ward {ward}", $"/wards/{ward}", population.ForWard(ward), r => r.Ward == ward));
            }
            else if (dataset.NeighborhoodBySlug.TryGetValue(key, out var name))
            {
                wanted.Add((key, name, $"/neighborhoods/{key}", population.ForNeighborhood(name), r => string.Equals(r.Neighborhood, name, StringComparison.Ordinal)));
            }
        }

        var counts = wanted.Select(_ => new int[years.Length]).ToArray();
        foreach (var row in dataset.Rows)
        {
            var yearIndex = row.Date.Year - IncidentDataCache.FirstYear;
            if (yearIndex < 0 || yearIndex >= years.Length)
            {
                continue;
            }

            if (!(row.Date.Month < through.Month || (row.Date.Month == through.Month && row.Date.Day <= through.Day)))
            {
                continue;
            }

            if (!(groupIndex >= 0 ? row.Group == groupIndex : isCrime[row.Group]))
            {
                continue;
            }

            for (var i = 0; i < wanted.Count; i++)
            {
                if (wanted[i].Matches(row))
                {
                    counts[i][yearIndex] += row.Count;
                }
            }
        }

        var series = wanted.Select((w, i) => new CompareSeries(
            w.Key, w.Name, w.Url, w.Residents, counts[i],
            counts[i].Select(c => w.Key == "minneapolis" || w.Residents is { } r && r >= population.MinimumForRate || w.Key.StartsWith("ward-")
                ? Rate(c, w.Residents ?? 0, 1) : null).ToArray())).ToList();

        return new CompareData(years, through, groupInfo?.Label ?? "All crimes", series);
    }

    private AreaCrimeSummary Summarize(CrimeData data, List<AreaTrend> all, string name)
    {
        var byCount = all.OrderByDescending(a => a.Counts[^1]).ToList();
        var rated = all.Where(a => a.RatePerThousand is not null).OrderByDescending(a => a.RatePerThousand).ToList();
        var rateIndex = rated.FindIndex(a => a.Name == name);
        return new AreaCrimeSummary(
            data,
            byCount.FindIndex(a => a.Name == name) + 1,
            byCount.Count,
            rateIndex < 0 ? null : rateIndex + 1,
            rated.Count,
            Rate(data.SamePeriod[^1], population.CityTotal, minimum: 0));
    }

    private double? Rate(int count, int residents, int minimum) =>
        residents >= Math.Max(minimum, 1) ? Math.Round(count * 1000.0 / residents, 1) : null;

    private CrimeData Compute(IncidentDataset dataset, CrimeFilter filter, bool sortByRate)
    {
        var rows = dataset.Rows;
        var through = dataset.Through;
        var currentYear = through.Year;
        var years = Enumerable.Range(IncidentDataCache.FirstYear, currentYear - IncidentDataCache.FirstYear + 1).ToArray();
        var currentIndex = years.Length - 1;

        var groupKey = CrimeGroups.Find(filter.Group)?.Key;
        var groupIndex = groupKey is null ? -1 : CrimeGroups.IndexOf(groupKey);
        var isCrime = CrimeGroups.All.Select(g => g.IsCrime).ToArray();

        bool InPeriod(DateOnly d) => d.Month < through.Month || (d.Month == through.Month && d.Day <= through.Day);
        var neighborhood = string.IsNullOrWhiteSpace(filter.Neighborhood) ? null : filter.Neighborhood;
        var ward = filter.Ward;
        var hourStart = through.AddDays(-364);

        var monthly = years.Select(_ => new int?[12]).ToArray();
        for (var i = 0; i < years.Length; i++)
        {
            var lastMonth = years[i] == currentYear ? through.Month : 12;
            for (var m = 0; m < lastMonth; m++)
            {
                monthly[i][m] = 0;
            }
        }

        var samePeriod = new int[years.Length];
        var groupCounts = CrimeGroups.All.Select(_ => new int[years.Length]).ToArray();
        var hourWeekday = Enumerable.Range(0, 7).Select(_ => new int[24]).ToArray();
        var hourTotal = 0;

        foreach (var row in rows)
        {
            var yearIndex = row.Date.Year - IncidentDataCache.FirstYear;
            if (yearIndex < 0 || yearIndex >= years.Length)
            {
                continue;
            }

            if (neighborhood is not null && !string.Equals(row.Neighborhood, neighborhood, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (ward is not null && row.Ward != ward)
            {
                continue;
            }

            var inPeriod = InPeriod(row.Date);
            if (inPeriod)
            {
                groupCounts[row.Group][yearIndex] += row.Count;
            }

            var matchesGroup = groupIndex >= 0 ? row.Group == groupIndex : isCrime[row.Group];
            if (!matchesGroup)
            {
                continue;
            }

            if (monthly[yearIndex][row.Date.Month - 1] is { } existing)
            {
                monthly[yearIndex][row.Date.Month - 1] = existing + row.Count;
            }

            if (inPeriod)
            {
                samePeriod[yearIndex] += row.Count;
            }

            if (row.Date >= hourStart && row.Date <= through)
            {
                var weekday = ((int)row.Date.DayOfWeek + 6) % 7;
                hourWeekday[weekday][row.Hour] += row.Count;
                hourTotal += row.Count;
            }
        }

        int residents;
        string scopeLabel;
        if (neighborhood is not null)
        {
            residents = population.ForNeighborhood(neighborhood) ?? 0;
            scopeLabel = neighborhood;
        }
        else if (ward is not null)
        {
            residents = population.ForWard(ward.Value) ?? 0;
            scopeLabel = $"Ward {ward}";
        }
        else
        {
            residents = population.CityTotal;
            scopeLabel = "Minneapolis";
        }

        var minimum = neighborhood is not null ? population.MinimumForRate : 1;
        double? Pct(int now, int then) => then == 0 ? null : Math.Round((now - then) / (double)then * 100, 1);

        GroupRow ToRow(int index)
        {
            var group = CrimeGroups.All[index];
            var counts = groupCounts[index];
            return new GroupRow(
                group.Key, group.Label, group.Definition, group.IsCrime, counts,
                currentIndex > 0 ? Pct(counts[currentIndex], counts[currentIndex - 1]) : null,
                currentIndex > 0 ? Pct(counts[currentIndex], counts[0]) : null,
                Rate(counts[currentIndex], residents, minimum));
        }

        var groups = Enumerable.Range(0, CrimeGroups.All.Count).Where(i => CrimeGroups.All[i].IsCrime).Select(ToRow).ToList();
        var metrics = Enumerable.Range(0, CrimeGroups.All.Count).Where(i => !CrimeGroups.All[i].IsCrime).Select(ToRow).ToList();

        var neighborhoods = RankAreas(dataset, groupKey, years, byNeighborhood: true, take: TopAreaCount, sortByRate);
        var wards = RankAreas(dataset, groupKey, years, byNeighborhood: false, take: null, sortByRate);

        return new CrimeData(
            years, currentYear, through, monthly, samePeriod, neighborhoods, wards, groups, metrics,
            hourWeekday, hourTotal, residents, Rate(samePeriod[currentIndex], residents, minimum), scopeLabel,
            dataset.NeighborhoodNames, dataset.Wards);
    }

    /// <summary>Same-period counts per area (ignoring any area filter), ranked by count or by rate.</summary>
    private List<AreaTrend> RankAreas(
        IncidentDataset dataset, string? groupKey, int[] years, bool byNeighborhood, int? take, bool sortByRate = false)
    {
        var through = dataset.Through;
        var groupIndex = groupKey is null ? -1 : CrimeGroups.IndexOf(groupKey);
        var isCrime = CrimeGroups.All.Select(g => g.IsCrime).ToArray();
        var currentIndex = years.Length - 1;
        var counts = new Dictionary<string, int[]>();

        foreach (var row in dataset.Rows)
        {
            var yearIndex = row.Date.Year - IncidentDataCache.FirstYear;
            if (yearIndex < 0 || yearIndex >= years.Length)
            {
                continue;
            }

            if (!(row.Date.Month < through.Month || (row.Date.Month == through.Month && row.Date.Day <= through.Day)))
            {
                continue;
            }

            if (!(groupIndex >= 0 ? row.Group == groupIndex : isCrime[row.Group]))
            {
                continue;
            }

            string? key = byNeighborhood ? row.Neighborhood : row.Ward > 0 ? $"Ward {row.Ward}" : null;
            if (key is null)
            {
                continue;
            }

            if (!counts.TryGetValue(key, out var perYear))
            {
                counts[key] = perYear = new int[years.Length];
            }

            perYear[yearIndex] += row.Count;
        }

        var trends = counts.Select(kv =>
        {
            var residents = byNeighborhood
                ? population.ForNeighborhood(kv.Key)
                : population.ForWard(int.Parse(kv.Key.Split(' ')[1]));
            var minimum = byNeighborhood ? population.MinimumForRate : 1;
            var perYear = kv.Value;
            var prior = currentIndex > 0 ? perYear[currentIndex - 1] : 0;
            double? change = prior == 0 ? null : Math.Round((perYear[currentIndex] - prior) / (double)prior * 100, 1);
            var url = byNeighborhood ? $"/neighborhoods/{AreaSlug.For(kv.Key)}" : $"/wards/{kv.Key.Split(' ')[1]}";
            return new AreaTrend(kv.Key, perYear, change, url,
                residents is { } r ? Rate(perYear[currentIndex], r, minimum) : null, residents);
        });

        var ordered = sortByRate
            ? trends.Where(a => a.RatePerThousand is not null).OrderByDescending(a => a.RatePerThousand).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            : trends.OrderByDescending(a => a.Counts[currentIndex]).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase);

        return (take is { } n ? ordered.Take(n) : ordered).ToList();
    }
}
