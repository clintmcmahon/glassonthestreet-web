using System.Text.Json;
using System.Text.RegularExpressions;
using GlassOnTheStreet.Tests.DataAccuracy;
using GlassOnTheStreet.Web.Data;
using GlassOnTheStreet.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace GlassOnTheStreet.Tests.Live;

/// <summary>
/// The live audit: checks the real database, the city's real feed and a running copy of the site.
/// Off unless asked for, so an ordinary test run stays fast and offline.
///
///   GOTS_AUDIT_DB      MySQL connection string, or "local" to use appsettings.Local.json
///   GOTS_AUDIT_SITE    base URL of a running site, e.g. https://glassonthestreet.com or http://localhost:5000
///   GOTS_AUDIT_SOURCE  "1" to reconcile the database against the city's ArcGIS feed (needs the network)
///
///   dotnet test --filter Category=Live
/// </summary>
public static class LiveWorld
{
    public static string? DbConnection => Environment.GetEnvironmentVariable("GOTS_AUDIT_DB") switch
    {
        null or "" => null,
        "local" => LocalConnection(),
        var other => other
    };

    public static string? SiteUrl => Environment.GetEnvironmentVariable("GOTS_AUDIT_SITE")?.TrimEnd('/');

    public static bool SourceEnabled => Environment.GetEnvironmentVariable("GOTS_AUDIT_SOURCE") == "1";

    private static string? LocalConnection()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var file = Path.Combine(dir.FullName, "GlassOnTheStreet.Web", "appsettings.Local.json");
            if (File.Exists(file))
            {
                return JsonDocument.Parse(File.ReadAllText(file)).RootElement.GetProperty("ConnectionStrings").GetProperty("Default").GetString();
            }
        }

        return null;
    }

    public static GlassOnTheStreetContext NewContext() => new(
        new DbContextOptionsBuilder<GlassOnTheStreetContext>()
            .UseMySql(DbConnection!, new MySqlServerVersion(new Version(8, 0, 39)))
            .Options);

    private static readonly Lazy<List<MpdIncident>> IncidentsLazy = new(() =>
    {
        using var db = NewContext();
        return db.MpdIncidents.AsNoTracking().ToList();
    });

    /// <summary>Every stored MPD row, loaded once for the whole run.</summary>
    public static List<MpdIncident> Incidents => IncidentsLazy.Value;

    private static readonly Lazy<Oracle> OracleLazy = new(() => new Oracle(Incidents
        .Where(i => i.OccurredDate.Year >= Oracle.FirstYear)
        .Select(i => new FeedRecord(
            // The row's own unique key stands in for case|code|offense so the oracle never merges two stored rows.
            i.ExternalKey, i.Address ?? "", 0, "", i.OffenseCategory ?? "", "", "", i.CrimeAgainst ?? "", i.CrimeCount,
            i.Neighborhood, i.Ward is { } w ? w : null, i.Precinct,
            // Degrees stand in for the feed's meters: all the oracle asks is whether the row has a real point.
            i.Lng is null || IncidentDataCacheUnplaced(i) ? null : (double)i.Lng, i.Lat is null || IncidentDataCacheUnplaced(i) ? null : (double)i.Lat,
            i.GroupKey, i.IsCrime, i.OccurredDate, i.OccurredHour))));

    /// <summary>Expected numbers worked out from the stored rows, using the same independent rules as the synthetic tests.</summary>
    public static Oracle Oracle => OracleLazy.Value;

    private static bool IncidentDataCacheUnplaced(MpdIncident i) => i.Lat == 0 && i.Lng == 0;

    public static HttpClient NewSiteClient() => new(new HttpClientHandler { AllowAutoRedirect = true }) { BaseAddress = new Uri(SiteUrl + "/"), Timeout = TimeSpan.FromSeconds(90) };

    public static readonly Regex NonDigits = new(@"[^\d]");
}

/// <summary>Skipped unless the environment names what the test needs.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute(bool needsSite = false, bool needsSource = false)
    {
        if (LiveWorld.DbConnection is null)
        {
            Skip = "Live audit off: set GOTS_AUDIT_DB (a connection string, or \"local\").";
        }
        else if (needsSite && LiveWorld.SiteUrl is null)
        {
            Skip = "Needs GOTS_AUDIT_SITE (the site's base URL).";
        }
        else if (needsSource && !LiveWorld.SourceEnabled)
        {
            Skip = "Needs GOTS_AUDIT_SOURCE=1 (calls the city's ArcGIS service).";
        }
    }
}
