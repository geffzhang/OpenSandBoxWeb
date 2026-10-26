using OpenSandbox.Web.Contracts;
using OpenSandbox.Web.Infrastructure.Volumes;

namespace OpenSandbox.Web.Host.Web;

public static class VolumeAdminEndpoints
{
    public static IEndpointRouteBuilder MapVolumeAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/volumes");

        group.MapGet("", async (
            [AsParameters] VolumeListQuery query,
            VolumeQueries queries,
            CancellationToken ct) =>
            Results.Ok(await queries.ListAsync(query, null, ct)));

        group.MapGet("/report", async (
            VolumeQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.ReportAsync(ct)));

        group.MapPost("/scan", async (
            ScanCoordinator coordinator, CancellationToken ct) =>
            Results.Ok(ToResponse(await coordinator.ScanAsync("manual", ct))));

        group.MapPost("/import", async (
            VolumeImportRequest request, ImportService service, CancellationToken ct) =>
            Results.Ok(await service.ImportAsync(request, ct)));

        group.MapGet("/{namespaceName}/{claimName}", async (
            string namespaceName,
            string claimName,
            VolumeQueries queries,
            CancellationToken ct) =>
        {
            var detail = await queries.GetAsync(namespaceName, claimName, null, ct);
            return detail is null
                ? Results.NotFound(new ErrorResponse("not_found", "卷不存在"))
                : Results.Ok(detail);
        });

        group.MapPost("/{namespaceName}/{claimName}/lifecycle", async (
            string namespaceName,
            string claimName,
            VolumeLifecycleRequest request,
            LifecycleService service,
            CancellationToken ct) =>
            Results.Ok(await service.ChangeAsync(
                null, namespaceName, claimName, request, true, "admin", ct)));

        return app;
    }

    private static VolumeScanResponse ToResponse(ScanSummary summary) => new(
        summary.ScanId,
        summary.Trigger,
        summary.StartedAt,
        summary.FinishedAt,
        summary.NamespacesScanned,
        summary.NamespacesSucceeded,
        summary.VolumeCount,
        summary.Errors.Select(error => new ScanErrorDto(error.Namespace, error.Message)).ToArray());
}
