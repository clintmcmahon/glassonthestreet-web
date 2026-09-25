using System.Threading.RateLimiting;
using GlassOnTheStreet.Web.Data;
using GlassOnTheStreet.Web.Services;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();

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
builder.Services.AddScoped<ICaptchaService, TurnstileCaptchaService>();

builder.Services.AddHttpClient<IGeocodingService, GeocodingService>(client =>
{
    client.BaseAddress = new Uri("https://nominatim.openstreetmap.org/");
    client.DefaultRequestHeaders.UserAgent.ParseAdd("GlassOnTheStreet/1.0 (Minneapolis break-in map)");
});

builder.Services.AddHttpClient<ICaptchaService, TurnstileCaptchaService>();

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
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

var app = builder.Build();

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
