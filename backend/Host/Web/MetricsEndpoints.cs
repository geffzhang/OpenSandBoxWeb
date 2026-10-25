using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenSandbox.Web.Contracts;
using OpenSandbox.Web.Infrastructure.Gateways;

namespace OpenSandbox.Web.Host.Web;

public static class MetricsEndpoints
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapMetricsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/sandboxes/{id}/metrics", GetSnapshotAsync);
        app.MapGet("/api/v1/sandboxes/{id}/metrics/watch", WatchAsync);
        return app;
    }

    private static async Task<IResult> GetSnapshotAsync(string id, ISandboxGateway gateway, CancellationToken ct)
        => Results.Ok(await gateway.GetMetricsAsync(id, ct));

    private static IResult WatchAsync(string id, ISandboxGateway gateway, CancellationToken ct)
        => TypedResults.ServerSentEvents(StreamMetrics(id, gateway, ct));

    private static async IAsyncEnumerable<SseItem<string>> StreamMetrics(
        string id,
        ISandboxGateway gateway,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new SseItem<string>(JsonSerializer.Serialize(new { type = "connected" }, WebJson));

        while (!ct.IsCancellationRequested)
        {
            MetricsDto? metrics = null;
            string? error = null;
            try
            {
                metrics = await gateway.GetMetricsAsync(id, ct);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
            catch (Exception exception)
            {
                // C# forbids yielding from a catch block, so surface the error after it.
                error = exception.Message;
            }

            if (error is not null)
            {
                yield return new SseItem<string>(JsonSerializer.Serialize(new { type = "error", error }, WebJson));
                yield break;
            }

            yield return new SseItem<string>(JsonSerializer.Serialize(new { type = "metrics", data = metrics }, WebJson));

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
        }
    }
}
