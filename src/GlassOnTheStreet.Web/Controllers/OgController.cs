using System.Globalization;
using GlassOnTheStreet.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace GlassOnTheStreet.Web.Controllers;

/// <summary>
/// Share images (Open Graph). The card text comes from the same services as the pages, never
/// from the request, so the endpoint can't be used to put arbitrary words on an image.
/// A failure to draw falls back to the static image rather than breaking a shared link.
/// </summary>
public class OgController(
    OgImageService images, ICrimeStatsService crimeStats, ITrendsService trends, MonthlyReportService monthly,
    IMemoryCache cache, ILogger<OgController> logger) : Controller
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(1);

    [HttpGet("og/crime.png")]
    public Task<IActionResult> Crime(CancellationToken ct) => Serve("og-crime", async () =>
    {
        var d = await crimeStats.GetCrimeAsync(new CrimeFilter(null, null, null), false, ct);
        return new OgCard(
            "Minneapolis crime statistics", Fmt(d.SamePeriod[^1]), $"offenses so far in {d.CurrentYear}",
            Compare(d.SamePeriod[^1], d.SamePeriod[^2], d.CurrentYear - 1, d.SamePeriod[0], d.Years[0]), Footer(d.Through));
    });

    [HttpGet("og/trends.png")]
    public Task<IActionResult> Trends(CancellationToken ct) => Serve("og-trends", async () =>
    {
        var d = await trends.GetTrendsAsync(new TrendFilter(Models.IncidentType.Unknown, null, null), ct);
        return new OgCard(
            "Minneapolis car break-ins", Fmt(d.SamePeriod[^1]), $"theft-from-vehicle reports so far in {d.CurrentYear}",
            Compare(d.SamePeriod[^1], d.SamePeriod[^2], d.CurrentYear - 1, d.SamePeriod[0], d.Years[0]), Footer(d.Through));
    });

    [HttpGet("og/neighborhood/{slug}.png")]
    public Task<IActionResult> Neighborhood(string slug, CancellationToken ct) => Serve("og-n-" + slug, async () =>
    {
        var s = await crimeStats.GetNeighborhoodSummaryAsync(slug, ct);
        return s is null ? null : AreaCard(s, "neighborhood");
    });

    [HttpGet("og/ward/{ward:int}.png")]
    public Task<IActionResult> Ward(int ward, CancellationToken ct) => Serve("og-w-" + ward, async () =>
    {
        var s = await crimeStats.GetWardSummaryAsync(ward, ct);
        return s is null ? null : AreaCard(s, "ward");
    });

    [HttpGet("og/monthly/{year:int}-{month:int}.png")]
    public Task<IActionResult> Monthly(int year, int month, CancellationToken ct) => Serve($"og-m-{year}-{month}", async () =>
    {
        var r = await monthly.GetAsync(year, month, ct);
        if (r is null)
        {
            return null;
        }

        string Move(int now, int then) => then == 0 ? "" : $"{(now >= then ? "up" : "down")} {Math.Abs(Math.Round((now - then) / (double)then * 100)).ToString("0", CultureInfo.InvariantCulture)}%";
        var parts = new[]
        {
            Move(r.Total, r.PriorMonthTotal) is { Length: > 0 } a ? $"{a} from {MonthlyReportService.Label(month == 1 ? year - 1 : year, month == 1 ? 12 : month - 1)}" : null,
            Move(r.Total, r.LastYearTotal) is { Length: > 0 } b ? $"{b} from {MonthlyReportService.Label(year - 1, month)}" : null
        }.Where(p => p is not null);
        return new OgCard($"Minneapolis crime in {r.Label}", Fmt(r.Total), "offenses recorded", string.Join(", ", parts), Footer(r.Through));
    });

    private static OgCard AreaCard(AreaCrimeSummary s, string kind)
    {
        var d = s.Data;
        var sub = new List<string>();
        if (d.RatePerThousand is { } rate && d.Population > 0)
        {
            sub.Add($"{rate.ToString("N1", CultureInfo.InvariantCulture)} per 1,000 residents");
        }

        if (d.SamePeriod[^2] > 0)
        {
            var change = (d.SamePeriod[^1] - d.SamePeriod[^2]) / (double)d.SamePeriod[^2] * 100;
            sub.Add($"{(change >= 0 ? "up" : "down")} {Math.Abs(Math.Round(change)).ToString("0", CultureInfo.InvariantCulture)}% from {d.CurrentYear - 1}");
        }

        var name = d.ScopeLabel;
        return new OgCard(
            kind == "ward" ? $"Crime in {name}, Minneapolis" : $"Crime in {name}, Minneapolis",
            Fmt(d.SamePeriod[^1]), $"offenses so far in {d.CurrentYear}", string.Join(", ", sub), Footer(d.Through));
    }

    private async Task<IActionResult> Serve(string key, Func<Task<OgCard?>> build)
    {
        try
        {
            if (!cache.TryGetValue(key, out byte[]? png) || png is null)
            {
                var card = await build();
                if (card is null)
                {
                    return NotFound();
                }

                png = images.Render(card);
                cache.Set(key, png, CacheFor);
            }

            Response.Headers.CacheControl = "public, max-age=3600";
            return File(png, "image/png");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Includes a native Skia library that fails to load on the host.
            logger.LogError(ex, "Could not draw share image {Key}; using the static image", key);
            return Redirect("/og-image.png");
        }
    }

    private static string Fmt(int n) => n.ToString("N0", CultureInfo.InvariantCulture);

    private static string Footer(DateOnly through) =>
        $"glassonthestreet.com  |  MPD data through {through.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)}";

    private static string Compare(int now, int prior, int priorYear, int baseline, int baseYear)
    {
        string Move(int a, int b) => b == 0 ? "" : $"{(a >= b ? "up" : "down")} {Math.Abs(Math.Round((a - b) / (double)b * 100)).ToString("0", CultureInfo.InvariantCulture)}%";
        return string.Join(", ", new[] { Move(now, prior) is { Length: > 0 } a ? $"{a} from {priorYear}" : null,
            Move(now, baseline) is { Length: > 0 } b ? $"{b} from {baseYear}" : null }.Where(x => x is not null));
    }
}
