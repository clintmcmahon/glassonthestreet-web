namespace GlassOnTheStreet.Web.Services;

public record NearbyGroup(string Key, string Label, bool IsCrime, int Count, int Previous);

public record NearbyIncident(DateOnly Date, string Label, string Offense, string Block);

/// <param name="Neighborhood">The neighborhood of the closest block in the data, not necessarily of the typed address.</param>
public record NearbyResult(
    double RadiusMiles,
    int Days,
    DateOnly From,
    DateOnly Through,
    int Total,
    int PreviousTotal,
    IReadOnlyList<NearbyGroup> Groups,
    IReadOnlyList<NearbyIncident> Recent,
    string? Neighborhood,
    string? NeighborhoodUrl,
    int? Ward);

/// <summary>
/// Counts MPD offenses near a point. MPD places every record at the midpoint of its block, so
/// "within a quarter mile" means blocks whose midpoint is that close, not addresses. The point
/// is used for this one calculation and is never stored or logged.
/// </summary>
public class NearbyService(IncidentDataCache dataCache, Data.GlassOnTheStreetContext db)
{
    public static readonly double[] RadiusOptions = [0.25, 0.5, 1];
    public static readonly int[] DayOptions = [30, 90, 365];

    private const double MetersPerMile = 1609.344;
    private const int RecentShown = 25;

    public async Task<NearbyResult> GetAsync(double lat, double lng, double radiusMiles, int days, CancellationToken cancellationToken = default)
    {
        var dataset = await dataCache.GetAsync(cancellationToken);
        var through = dataset.Through;
        var from = through.AddDays(-(days - 1));
        var previousFrom = from.AddDays(-days);

        var radius = radiusMiles * MetersPerMile;
        var cosLat = Math.Cos(lat * Math.PI / 180);
        var dLat = radius / 111_320;
        var dLng = radius / (111_320 * cosLat);

        var isCrime = CrimeGroups.All.Select(g => g.IsCrime).ToArray();
        var now = new int[CrimeGroups.All.Count];
        var before = new int[CrimeGroups.All.Count];
        var recent = new List<IncidentRow>();

        var nearestMeters = double.MaxValue;
        IncidentRow? nearest = null;

        foreach (var row in dataset.Rows)
        {
            if (float.IsNaN(row.Lat) || float.IsNaN(row.Lng))
            {
                continue;
            }

            var dy = (row.Lat - lat) * 111_320;
            var dx = (row.Lng - lng) * 111_320 * cosLat;
            var meters = Math.Sqrt(dx * dx + dy * dy);

            if (meters < nearestMeters && row.Neighborhood is not null)
            {
                nearestMeters = meters;
                nearest = row;
            }

            if (Math.Abs(row.Lat - lat) > dLat || Math.Abs(row.Lng - lng) > dLng || meters > radius)
            {
                continue;
            }

            if (row.Date >= from && row.Date <= through)
            {
                now[row.Group] += row.Count;
                if (isCrime[row.Group])
                {
                    recent.Add(row);
                }
            }
            else if (row.Date >= previousFrom && row.Date < from)
            {
                before[row.Group] += row.Count;
            }
        }

        var groups = Enumerable.Range(0, CrimeGroups.All.Count)
            .Select(i => new NearbyGroup(CrimeGroups.All[i].Key, CrimeGroups.All[i].Label, CrimeGroups.All[i].IsCrime, now[i], before[i]))
            .Where(g => g.Count > 0 || g.Previous > 0)
            .OrderByDescending(g => g.IsCrime).ThenByDescending(g => g.Count)
            .ToList();

        var latest = recent.OrderByDescending(r => r.Date).ThenByDescending(r => r.Id).Take(RecentShown).ToList();
        var ids = latest.Select(r => r.Id).ToList();
        var details = ids.Count == 0
            ? new Dictionary<int, (string Offense, string? Address)>()
            : (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
                  db.MpdIncidents.Where(i => ids.Contains(i.Id)).Select(i => new { i.Id, i.Offense, i.Address }), cancellationToken))
                .ToDictionary(i => i.Id, i => (i.Offense, i.Address));

        var incidents = latest.Select(r =>
        {
            details.TryGetValue(r.Id, out var d);
            return new NearbyIncident(r.Date, CrimeGroups.All[r.Group].Label, d.Offense ?? CrimeGroups.All[r.Group].Label, MpdAddress.Pretty(d.Address));
        }).ToList();

        return new NearbyResult(
            radiusMiles, days, from, through,
            groups.Where(g => g.IsCrime).Sum(g => g.Count),
            groups.Where(g => g.IsCrime).Sum(g => g.Previous),
            groups, incidents,
            nearest?.Neighborhood,
            nearest?.Neighborhood is { } name ? $"/neighborhoods/{AreaSlug.For(name)}" : null,
            nearest is { Ward: > 0 } n ? n.Ward : null);
    }
}
