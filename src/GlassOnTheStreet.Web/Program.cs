using System.Threading.RateLimiting;
using GlassOnTheStreet.Web.Data;
using GlassOnTheStreet.Web.Services;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Personal/machine-specific overrides (real connection string, admin
// credentials, etc.) that should never be committed. Dev-only: this loads
// last, so if it were also loaded in Production it would silently outrank
// appsettings.Production.json for every key they share -- exactly the bug
// where Production.json "appears to be skipped" because a stray/stale
// Local.json on the server wins every time regardless of environment.
if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
}

// Add services to the container.
builder.Services.AddControllersWithViews();
builder.Services.AddMemoryCache();

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? "Server=localhost;Database=glass_on_the_street;User=root;Password=;";

// A fixed server version (rather than ServerVersion.AutoDetect) avoids
// needing a live database connection at design time (e.g. `dotnet ef
// migrations add`) and at app startup before the DB is reachable.
var mySqlServerVersion = new MySqlServerVersion(new Version(8, 0, 39));

builder.Services.AddDbContext<GlassOnTheStreetContext>(options =>
    options.UseMySql(connectionString, mySqlServerVersion));

builder.Services.AddScoped<ILocationPrivacyService, LocationPrivacyService>();
builder.Services.AddScoped<IGeofenceService, GeofenceService>();
builder.Services.AddScoped<IBlockAnchorService, BlockAnchorService>();
builder.Services.AddScoped<ICaptchaService, TurnstileCaptchaService>();
builder.Services.AddScoped<IReportStatsService, ReportStatsService>();
builder.Services.AddScoped<ITrendsService, TrendsService>();
builder.Services.AddSingleton<IPopulationService, PopulationService>();
builder.Services.AddScoped<IncidentDataCache>();
builder.Services.AddScoped<ICrimeStatsService, CrimeStatsService>();
builder.Services.AddScoped<MonthlyReportService>();
builder.Services.AddScoped<NearbyService>();
builder.Services.AddSingleton<OgImageService>();
builder.Services.AddScoped<CsvExportService>();
builder.Services.AddScoped<ISyncStatusService, SyncStatusService>();

builder.Services.AddHttpClient<IGeocodingService, GeocodingService>(client =>
{
    client.BaseAddress = new Uri("https://nominatim.openstreetmap.org/");
    client.DefaultRequestHeaders.UserAgent.ParseAdd("GlassOnTheStreet/1.0 (Minneapolis break-in map)");
});

builder.Services.AddHttpClient<IIntersectionFinder, OverpassIntersectionFinder>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(12);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("GlassOnTheStreet/1.0 (Minneapolis break-in map)");
});

builder.Services.AddHttpClient<ICaptchaService, TurnstileCaptchaService>();

builder.Services.AddScoped<IOfficialDataImportService, MinneapolisOpenDataImportService>();
builder.Services.AddHttpClient<IOfficialDataImportService, MinneapolisOpenDataImportService>(client =>
{
    // City of Minneapolis open data portal -- "Crime_Data" feature service,
    // verified 2026-09-25. See MinneapolisOpenDataImportService for field notes.
    client.BaseAddress = new Uri(
        "https://services.arcgis.com/afSMGVsC7QlRK1kZ/arcgis/rest/services/Crime_Data/FeatureServer/0/");
});

builder.Services.AddHttpClient<IMpdIncidentImportService, MpdIncidentImportService>(client =>
{
    // Same City of Minneapolis "Crime_Data" feature service; this importer reads every offense.
    client.BaseAddress = new Uri(
        "https://services.arcgis.com/afSMGVsC7QlRK1kZ/arcgis/rest/services/Crime_Data/FeatureServer/0/");
    client.Timeout = TimeSpan.FromMinutes(3);
});

builder.Services.AddHostedService<OfficialDataSyncBackgroundService>();

// Anonymous submissions get rate limited per IP so one person can't flood
// the map with reports.
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("report-submission", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(10),
                QueueLimit = 0
            }));
    options.AddPolicy("geocode-search", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
    options.AddPolicy("near-lookup", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

var app = builder.Build();

// Apply pending EF Core migrations automatically on startup, so a deploy
// that ships new columns/tables (e.g. Ward/Precinct) doesn't require a
// manual SQL step against the production server -- the app brings its own
// schema up to date every time it starts.
using (var migrationScope = app.Services.CreateScope())
{
    var db = migrationScope.ServiceProvider.GetRequiredService<GlassOnTheStreetContext>();
    db.Database.Migrate();
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();

app.UseRateLimiter();
app.UseAuthorization();

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.Run();
