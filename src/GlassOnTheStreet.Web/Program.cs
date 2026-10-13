using System.Threading.RateLimiting;
using GlassOnTheStreet.Web.Data;
using GlassOnTheStreet.Web.Services;
using Microsoft.AspNetCore.HttpOverrides;
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

// The map ships every block as one JSON response (about 2 MB for the all-time view); compressed it is a fraction of that.
builder.Services.AddResponseCompression(options => options.EnableForHttps = true);
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
builder.Services.AddSingleton<IWardService, WardService>();
builder.Services.AddScoped<IncidentDataCache>();
builder.Services.AddScoped<ICrimeStatsService, CrimeStatsService>();
builder.Services.AddScoped<MonthlyReportService>();
builder.Services.AddScoped<WeeklyReportService>();
builder.Services.AddScoped<NearbyService>();
builder.Services.AddScoped<MapDataService>();
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

// Tests host the app against an in-memory database and must not call the city's feed.
if (!builder.Configuration.GetValue<bool>("DisableBackgroundSync"))
{
    builder.Services.AddHostedService<OfficialDataSyncBackgroundService>();
}

// Behind nginx every connection arrives from the proxy, so without this the rate limiters below would
// share one bucket for the whole site and Turnstile would see the proxy's address. Only the loopback
// proxy is trusted by default; list other proxies under ForwardedHeaders:KnownProxies.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    foreach (var proxy in builder.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [])
    {
        options.KnownProxies.Add(System.Net.IPAddress.Parse(proxy));
    }
});

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
    options.AddPolicy("flag-report", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
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

app.UseForwardedHeaders();

app.UseResponseCompression();

// Apply pending EF Core migrations automatically on startup, so a deploy
// that ships new columns/tables (e.g. Ward/Precinct) doesn't require a
// manual SQL step against the production server -- the app brings its own
// schema up to date every time it starts.
using (var migrationScope = app.Services.CreateScope())
{
    var db = migrationScope.ServiceProvider.GetRequiredService<GlassOnTheStreetContext>();
    if (db.Database.IsRelational())
    {
        db.Database.Migrate();
    }
    else
    {
        db.Database.EnsureCreated();
    }
}

// The admin login lives in the database as a password hash. On the first start with none, the one in
// configuration (Admin:Username / Admin:Password) is hashed and saved; after that the config values are
// ignored and can be removed. The password is changed from the admin page.
using (var seedScope = app.Services.CreateScope())
{
    var db = seedScope.ServiceProvider.GetRequiredService<GlassOnTheStreetContext>();
    var seedUser = builder.Configuration["Admin:Username"];
    var seedPassword = builder.Configuration["Admin:Password"];
    if (!db.AdminUsers.Any() && !string.IsNullOrEmpty(seedUser) && !string.IsNullOrEmpty(seedPassword))
    {
        db.AdminUsers.Add(new GlassOnTheStreet.Web.Models.AdminUser { Username = seedUser, PasswordHash = AdminPasswordHasher.Hash(seedPassword) });
        db.SaveChanges();
        app.Logger.LogInformation("Admin login created from configuration; Admin:Password can now be removed from the config.");
    }
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

// Lets the test project host the app (WebApplicationFactory<Program>).
public partial class Program;
