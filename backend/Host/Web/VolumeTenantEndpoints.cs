using OpenSandbox.Web.Contracts;
using OpenSandbox.Web.Infrastructure.Volumes;

namespace OpenSandbox.Web.Host.Web;

public static class VolumeTenantEndpoints
{
    public static IEndpointRouteBuilder MapVolumeTenantEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/volume-tenants");

        group.MapGet("", async (TenantService service, CancellationToken ct) =>
        {
            var items = await service.ListAsync(ct);
            return Results.Ok(new TenantListResponse(items));
        });

        group.MapPost("", async (
            CreateTenantRequest request, TenantService service, CancellationToken ct) =>
        {
            var created = await service.CreateAsync(request, ct);
            return Results.Created($"/api/v1/volume-tenants/{created.Id}", created);
        });

        group.MapPatch("/{tenantId}", async (
            string tenantId, UpdateTenantRequest request,
            TenantService service, CancellationToken ct) =>
        {
            return Results.Ok(await service.UpdateAsync(tenantId, request, ct));
        });

        group.MapGet("/{tenantId}/keys", async (
            string tenantId, TenantService service, CancellationToken ct) =>
        {
            var items = await service.ListKeysAsync(tenantId, ct);
            return Results.Ok(new TenantKeyListResponse(items));
        });

        group.MapPost("/{tenantId}/keys", async (
            string tenantId, CreateTenantKeyRequest request,
            TenantService service, CancellationToken ct) =>
        {
            var created = await service.CreateKeyAsync(tenantId, request, ct);
            return Results.Created($"/api/v1/volume-tenants/{tenantId}/keys/{created.Id}", created);
        });

        group.MapPost("/{tenantId}/keys/{keyId:guid}/revoke", async (
            string tenantId, Guid keyId, TenantService service, CancellationToken ct) =>
        {
            await service.RevokeKeyAsync(tenantId, keyId, ct);
            return Results.NoContent();
        });

        return app;
    }
}
