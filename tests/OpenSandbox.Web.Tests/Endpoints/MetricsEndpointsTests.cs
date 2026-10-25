using System.Net;
using System.Net.Http.Json;
using OpenSandbox.Web.Contracts;
using Xunit;

namespace OpenSandbox.Web.Tests.Endpoints;

public sealed class MetricsEndpointsTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public MetricsEndpointsTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Snapshot_ReturnsMetrics()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/sandboxes/sb-1/metrics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<MetricsDto>();
        Assert.Equal(4, body!.CpuCount);
        Assert.Equal(12.5, body.CpuUsedPct);
    }

    [Fact]
    public async Task Watch_StreamsConnectedEventAsSse()
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/sandboxes/sb-1/metrics/watch");

        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var line = await reader.ReadLineAsync(cts.Token);

        Assert.StartsWith("data: ", line);
        Assert.Contains("\"type\":\"connected\"", line);
    }
}
