using System.Net;
using GlassOnTheStreet.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GlassOnTheStreet.Tests;

/// <summary>
/// The city's feed answers 504 often. An importer that treats one failed page as "no more data"
/// reports success with the oldest years missing, which is how production lost 2019.
/// </summary>
public class ArcGisPageFetcherTests
{
    private sealed class ScriptedHandler(params (HttpStatusCode Status, string Body)[] responses) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, body) = responses[Math.Min(Calls, responses.Length - 1)];
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private static Task<System.Text.Json.JsonElement?> Fetch(ScriptedHandler handler, int attempts = 4) =>
        ArcGisPageFetcher.GetPageAsync(
            new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") }, "query?x=1",
            NullLogger.Instance, CancellationToken.None, attempts, TimeSpan.Zero);

    private const string Page = """{"features":[{"attributes":{"Case_Number":"26-1"}}]}""";

    [Fact]
    public async Task ReturnsThePageOnFirstSuccess()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, Page));

        var doc = await Fetch(handler);

        Assert.NotNull(doc);
        Assert.Equal(1, doc!.Value.GetProperty("features").GetArrayLength());
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task RetriesGatewayErrorsUntilThePageComesBack()
    {
        var handler = new ScriptedHandler(
            (HttpStatusCode.GatewayTimeout, "<html>504</html>"),
            (HttpStatusCode.GatewayTimeout, "<html>504</html>"),
            (HttpStatusCode.OK, Page));

        var doc = await Fetch(handler);

        Assert.NotNull(doc);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task ReturnsNullWhenEveryAttemptFails_SoCallersCanFailInsteadOfStoppingQuietly()
    {
        var handler = new ScriptedHandler((HttpStatusCode.GatewayTimeout, "<html>504</html>"));

        var doc = await Fetch(handler, attempts: 4);

        Assert.Null(doc);
        Assert.Equal(4, handler.Calls);
    }

    [Fact]
    public async Task TreatsAnEmbeddedErrorBodyAsAFailure()
    {
        var handler = new ScriptedHandler(
            (HttpStatusCode.OK, """{"error":{"code":500,"message":"Unable to complete operation."}}"""),
            (HttpStatusCode.OK, Page));

        var doc = await Fetch(handler);

        Assert.NotNull(doc);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task TreatsABodyWithoutAFeaturesArrayAsAFailure()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, """{"unexpected":true}"""));

        Assert.Null(await Fetch(handler, attempts: 2));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task AnEmptyFeaturesArrayIsARealEndOfData()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, """{"features":[]}"""));

        var doc = await Fetch(handler);

        Assert.NotNull(doc);
        Assert.Equal(0, doc!.Value.GetProperty("features").GetArrayLength());
    }
}
