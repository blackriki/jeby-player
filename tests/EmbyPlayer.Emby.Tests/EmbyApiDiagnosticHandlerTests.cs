using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.Emby.Tests;

[TestClass]
public sealed class EmbyApiDiagnosticHandlerTests
{
    [TestMethod]
    public async Task SendAsync_Success_DoesNotWriteDiagnostic()
    {
        var diagnostics = new TestApplicationDiagnostics();
        using var client = CreateClient(HttpStatusCode.OK, diagnostics);

        using var response = await client.GetAsync("https://media.local/System/Info/Public");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(0, diagnostics.Events.Count);
    }

    [DataTestMethod]
    [DataRow(400)]
    [DataRow(503)]
    public async Task SendAsync_NonSuccess_WritesSafeStatusAndCategoryWithoutRequestSecrets(int statusCode)
    {
        var diagnostics = new TestApplicationDiagnostics();
        using var client = CreateClient((HttpStatusCode)statusCode, diagnostics);
        var querySecret = string.Concat("query", "-secret-value");
        var headerSecret = string.Concat("header", "-secret-value");
        var requestUri = "https://[::1]:8096/Users/user-secret/Items/media-secret?api_"
            + "key=" + querySecret + "&mode=private";
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.TryAddWithoutValidation("X-Emby-" + "Token", headerSecret);

        using var response = await client.SendAsync(request);

        Assert.AreEqual(statusCode, (int)response.StatusCode);
        Assert.AreEqual(1, diagnostics.Events.Count);
        var diagnostic = diagnostics.Events[0];
        Assert.AreEqual("api", diagnostic.Category);
        Assert.AreEqual("non-success", diagnostic.EventName);
        Assert.AreEqual($"method=GET status={statusCode} requestCategory=items", diagnostic.Details);
        foreach (var sensitiveValue in new[]
                 {
                     querySecret,
                     headerSecret,
                     "user-secret",
                     "media-secret",
                     "mode=private"
                 })
        {
            Assert.IsFalse(diagnostic.Details!.Contains(sensitiveValue, StringComparison.Ordinal));
        }
    }

    private static HttpClient CreateClient(
        HttpStatusCode responseStatus,
        TestApplicationDiagnostics diagnostics)
    {
        return new HttpClient(new EmbyApiDiagnosticHandler(
            new StubHttpMessageHandler(responseStatus),
            diagnostics));
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode responseStatus;

        public StubHttpMessageHandler(HttpStatusCode responseStatus)
        {
            this.responseStatus = responseStatus;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(responseStatus));
        }
    }
}
