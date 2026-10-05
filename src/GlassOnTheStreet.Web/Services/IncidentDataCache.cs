using GlassOnTheStreet.Web.Data;
using GlassOnTheStreet.Web.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace GlassOnTheStreet.Web.Services;

/// <summary>One MPD offense row, trimmed to what the statistics need.</summary>
/// <param name="Group">Index into <see cref="CrimeGroups.All"/>.</param>
/// <param name="AddressIndex">Position in <see cref="IncidentDataset.AddressTable"/> (0 when unknown).</param>
public readonly record struct IncidentRow(
    int Id, DateOnly Date, byte Hour, byte Group, short Count, string? Neighborhood, byte Ward, float Lat, float Lng,
    int AddressIndex = 0);

public sealed record IncidentDataset(
    IncidentRow[] Rows,
    DateOnly Through,
    IReadOnlyDictionary<string, string> NeighborhoodBySlug,
    IReadOnlyList<string> NeighborhoodNames,
    IReadOnlyList<int> Wards,
    IReadOnlyList<string>? AddressTable = null,
    DateOnly? LatestRecord = null)
{
    /// <summary>MPD's block-range address for a row, e.g. "0048XX 13TH AVE S"; empty when unknown.</summary>
    public string AddressAt(int index) =>
        AddressTable is { } table && index > 0 && index < table.Count ? table[index] : "";
}

/// <summary>
/// The whole MPD crime feed (about 400k small rows) held in memory so every
/// filter, ranking and nearby lookup is computed without going back to SQL.
/// The feed syncs daily, so a half hour of staleness is invisible.
/// </summary>
public class IncidentDataCache(GlassOnTheStreetContext db, IMemoryCache cache)
{
    public const int FirstYear = 2019;

    /// <summary>
    /// MPD posts records days after the fact: compared with the same weekday a year earlier, records
    /// under about ten days old are only partly there (roughly three quarters at a week, half at three
    /// days). Including them makes the latest period look like a drop. So the newest days are held back:
    /// the dataset, and every figure built on it, runs through the latest record minus this many days.
    /// </summary>
    public const int LagDays = 10;

    /// <summary>The last date whose records are essentially complete.</summary>
    public static DateOnly CompleteThrough(DateOnly latestRecord, DateOnly today) =>
        (latestRecord > today ? today : latestRecord).AddDays(-LagDays);

    /// <summary>Rows MPD couldn't place arrive as 0,0 (older imports stored that as is). They count in every total but have no point.</summary>
    public static bool IsUnplaced(decimal? lat, decimal? lng) => lat is null || lng is null || (lat == 0 && lng == 0);

    private const string CacheKey = "incident-dataset";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);

    public virtual async Task<IncidentDataset> GetAsync(CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(CacheKey, out IncidentDataset? cached) && cached is not null)
        {
            return cached;
        }

        var loaded = await db.MpdIncidents
            .Where(i => i.OccurredDate >= new DateOnly(FirstYear, 1, 1))
            .Select(i => new { i.Id, i.OccurredDate, i.OccurredHour, i.GroupKey, i.CrimeCount, i.Neighborhood, i.Ward, i.Lat, i.Lng, i.Address })
            .ToListAsync(cancellationToken);

        // About 11k distinct block addresses across ~400k rows: keep each once, point rows at it.
        var addressTable = new List<string> { "" };
        var addressIndex = new Dictionary<string, int>();

        var rows = new IncidentRow[loaded.Count];
        for (var i = 0; i < loaded.Count; i++)
        {
            var r = loaded[i];
            var address = 0;
            if (!string.IsNullOrEmpty(r.Address) && !addressIndex.TryGetValue(r.Address, out address))
            {
                address = addressTable.Count;
                addressTable.Add(r.Address);
                addressIndex[r.Address] = address;
            }

            rows[i] = new IncidentRow(
                r.Id, r.OccurredDate, r.OccurredHour, (byte)CrimeGroups.IndexOf(r.GroupKey), r.CrimeCount,
                r.Neighborhood is null ? null : string.Intern(r.Neighborhood), r.Ward ?? 0,
                IsUnplaced(r.Lat, r.Lng) ? float.NaN : (float)r.Lat!.Value, IsUnplaced(r.Lat, r.Lng) ? float.NaN : (float)r.Lng!.Value,
                address);
        }

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, CentralTime.Zone));
        var latest = rows.Length == 0 ? today : rows.Max(r => r.Date);
        var through = CompleteThrough(latest, today);

        // The newest days are still being posted; keep them out of every statistic (they stay in the database).
        rows = rows.Where(r => r.Date <= through).ToArray();

        var names = rows.Where(r => r.Neighborhood is not null).Select(r => r.Neighborhood!).Distinct()
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        var bySlug = new Dictionary<string, string>();
        foreach (var name in names)
        {
            bySlug.TryAdd(AreaSlug.For(name), name);
        }

        var wards = rows.Where(r => r.Ward > 0).Select(r => (int)r.Ward).Distinct().OrderBy(w => w).ToList();

        var dataset = new IncidentDataset(rows, through, bySlug, names, wards, addressTable, latest);
        cache.Set(CacheKey, dataset, CacheDuration);
        return dataset;
    }
}
