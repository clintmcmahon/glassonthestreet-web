using GlassOnTheStreet.Web.Data;
using GlassOnTheStreet.Web.Models;
using GlassOnTheStreet.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GlassOnTheStreet.Tests.DataAccuracy;

/// <summary>The real site, on an in-memory database, with the background importer switched off.</summary>
public sealed class SiteFactory : WebApplicationFactory<Program>
{
    private readonly InMemoryDatabaseRoot root = new();

    private readonly string name = $"world-{Guid.NewGuid():N}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("DisableBackgroundSync", "true");
        builder.ConfigureServices(services =>
        {
            foreach (var descriptor in services.Where(d =>
                         d.ServiceType == typeof(GlassOnTheStreetContext) ||
                         (d.ServiceType.IsGenericType && d.ServiceType.GenericTypeArguments.Contains(typeof(GlassOnTheStreetContext)))).ToList())
            {
                services.Remove(descriptor);
            }

            services.AddDbContext<GlassOnTheStreetContext>(o => o.UseInMemoryDatabase(name, root));
        });
    }

    public HttpClient NewClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false
    });
}

/// <summary>
/// Everything the accuracy tests share: the fake feed, the site loaded from it through the real importer
/// (one year at a time, as production does), a few resident reports, and the oracle for the expected numbers.
/// </summary>
public sealed class World : IAsyncLifetime
{
    public SiteFactory Factory { get; } = new();

    public List<FeedRecord> Feed { get; private set; } = [];

    public Oracle Oracle { get; private set; } = null!;

    public OfficialImportResult[] YearResults { get; private set; } = [];

    public async Task InitializeAsync()
    {
        Feed = SyntheticFeed.Generate();
        Oracle = new Oracle(Feed);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GlassOnTheStreetContext>();
        var http = new HttpClient(new SyntheticFeed.FeedHandler(Feed)) { BaseAddress = new Uri("https://feed.test/") };
        var importer = new MpdIncidentImportService(http, db, NullLogger<MpdIncidentImportService>.Instance);

        var results = new List<OfficialImportResult>();
        for (var year = Oracle.FirstYear; year <= DateTime.UtcNow.Year; year++)
        {
            results.Add(await importer.ImportRangeAsync(new DateOnly(year, 1, 1), new DateOnly(year, 12, 31)));
        }

        YearResults = [.. results];
        await SeedResidentReportsAsync(db);
    }

    public Task DisposeAsync()
    {
        Factory.Dispose();
        return Task.CompletedTask;
    }

    public static readonly DateOnly NoDate = default;

    private async Task SeedResidentReportsAsync(GlassOnTheStreetContext db)
    {
        var recent = Oracle.Through.AddDays(-5);
        Report R(ReportStatus status, bool? police, IncidentType type = IncidentType.WindowSmashed, SourceType source = SourceType.UserReport, DateOnly? date = null) => new()
        {
            ReportedDate = date ?? recent,
            DisplayLat = 44.9552m, DisplayLng = -93.2776m,
            IncidentType = type, Status = status, SourceType = source, PoliceReported = police,
            Neighborhood = "Whittier"
        };

        db.Reports.AddRange(
            // Visible resident reports: 7 answered the police question, 4 of them said no.
            R(ReportStatus.Active, true), R(ReportStatus.Active, true), R(ReportStatus.Active, true),
            R(ReportStatus.Active, false), R(ReportStatus.Active, false, IncidentType.Rifled),
            R(ReportStatus.Active, false), R(ReportStatus.Active, false),
            R(ReportStatus.Active, null), R(ReportStatus.Active, null, IncidentType.Rifled),
            // Not visible, so never counted anywhere.
            R(ReportStatus.Pending, false), R(ReportStatus.Removed, false), R(ReportStatus.Flagged, false),
            // An imported MPD row is not a resident report.
            R(ReportStatus.Active, false, IncidentType.Unknown, SourceType.OfficialImport));
        await db.SaveChangesAsync();
    }
}

[CollectionDefinition("world")]
public sealed class WorldCollection : ICollectionFixture<World>;
