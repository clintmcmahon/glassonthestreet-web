using System.Text;
using Microsoft.AspNetCore.Mvc.Filters;

namespace GlassOnTheStreet.Web.Infrastructure;

/// <summary>
/// Minimal HTTP Basic Auth gate for the admin area. Credentials come from
/// configuration (Admin:Username / Admin:Password) -- no user/role system
/// needed since reports are anonymous and there's a single admin login.
/// </summary>
public class AdminBasicAuthAttribute : ActionFilterAttribute
{
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var config = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        var expectedUser = config["Admin:Username"];
        var expectedPassword = config["Admin:Password"];

        if (string.IsNullOrEmpty(expectedUser) || string.IsNullOrEmpty(expectedPassword))
        {
            context.Result = new Microsoft.AspNetCore.Mvc.StatusCodeResult(StatusCodes.Status503ServiceUnavailable);
            return;
        }

        var header = context.HttpContext.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..]));
                var parts = decoded.Split(':', 2);
                if (parts.Length == 2 &&
                    parts[0] == expectedUser &&
                    parts[1] == expectedPassword)
                {
                    return;
                }
            }
            catch (FormatException)
            {
                // fall through to challenge
            }
        }

        context.HttpContext.Response.Headers.WWWAuthenticate = "Basic realm=\"Glass on the Street Admin\"";
        context.Result = new Microsoft.AspNetCore.Mvc.UnauthorizedResult();
    }
}
