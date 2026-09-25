namespace GlassOnTheStreet.Web.Services;

public interface ICaptchaService
{
    Task<bool> VerifyAsync(string? token, string? remoteIp, CancellationToken cancellationToken = default);
}
