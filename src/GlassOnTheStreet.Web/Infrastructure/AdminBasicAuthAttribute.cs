using System.Text;
using GlassOnTheStreet.Web.Data;
using GlassOnTheStreet.Web.Models;
using GlassOnTheStreet.Web.Services;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace GlassOnTheStreet.Web.Infrastructure;

/// <summary>
/// HTTP Basic Auth gate for the admin area. The login is an <see cref="AdminUser"/> row holding a salted
/// password hash (see <see cref="AdminPasswordHasher"/>). An unknown username costs the same hashing work
/// as a wrong password, and an address that fails <see cref="MaxFailures"/> times within
/// <see cref="LockoutWindow"/> is locked out (429) until the window passes, so the password can't be
/// guessed at speed. Admin responses are never cached. The signed-in user's name is left in
/// <c>HttpContext.Items["AdminUser"]</c>.
/// </summary>
public class AdminBasicAuthAttribute : ActionFilterAttribute
{
    public const int MaxFailures = 10;
    public static readonly TimeSpan LockoutWindow = TimeSpan.FromMinutes(15);
    public const string UserItem = "AdminUser";

    public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var http = context.HttpContext;
        var attempts = http.RequestServices.GetRequiredService<IMemoryCache>();
        var db = http.RequestServices.GetRequiredService<GlassOnTheStreetContext>();

        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";

        // No admin exists yet (nothing configured to create one): closed, not open.
        if (!await db.AdminUsers.AnyAsync(http.RequestAborted))
        {
            context.Result = new Microsoft.AspNetCore.Mvc.StatusCodeResult(StatusCodes.Status503ServiceUnavailable);
            return;
        }

        var key = "admin-failures:" + (http.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        if (attempts.TryGetValue(key, out Failures? failures) && failures!.Count >= MaxFailures)
        {
            http.Response.Headers.RetryAfter = ((int)LockoutWindow.TotalSeconds).ToString();
            context.Result = new Microsoft.AspNetCore.Mvc.StatusCodeResult(StatusCodes.Status429TooManyRequests);
            return;
        }

        var header = http.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..]));
                var parts = decoded.Split(':', 2);
                if (parts.Length == 2)
                {
                    var user = await db.AdminUsers.FirstOrDefaultAsync(u => u.Username == parts[0], http.RequestAborted);
                    var ok = AdminPasswordHasher.Verify(parts[1], user?.PasswordHash ?? AdminPasswordHasher.Decoy);
                    if (ok && user is not null && user.Username == parts[0])
                    {
                        attempts.Remove(key);
                        http.Items[UserItem] = user.Username;
                        await next();
                        return;
                    }
                }
            }
            catch (FormatException)
            {
                // fall through to challenge
            }
        }

        // A request with no credentials is the browser's first, unauthenticated visit, not a guess.
        if (header.Length > 0)
        {
            RecordFailure(attempts, key);
        }

        http.Response.Headers.WWWAuthenticate = "Basic realm=\"Glass on the Street Admin\"";
        context.Result = new Microsoft.AspNetCore.Mvc.UnauthorizedResult();
    }

    // The lockout window starts at the first failure and later failures don't extend it.
    private sealed record Failures(int Count, DateTimeOffset Until);

    private static void RecordFailure(IMemoryCache attempts, string key)
    {
        var until = attempts.TryGetValue(key, out Failures? current) ? current!.Until : DateTimeOffset.UtcNow.Add(LockoutWindow);
        attempts.Set(key, new Failures((current?.Count ?? 0) + 1, until), until);
    }
}
