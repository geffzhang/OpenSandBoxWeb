using OpenSandbox.Web.Contracts;
using OpenSandbox.Web.Infrastructure.Gateways;

namespace OpenSandbox.Web.Host.Web;

public static class SandboxEndpoints
{
    public static IEndpointRouteBuilder MapSandboxEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/sandboxes");

        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync);
        group.MapGet("/{id}", GetAsync);
        group.MapDelete("/{id}", DeleteAsync);
        group.MapPost("/{id}/pause", PauseAsync);
        group.MapPost("/{id}/resume", ResumeAsync);
        group.MapPost("/{id}/renew-expiration", RenewExpirationAsync);
        group.MapGet("/{id}/endpoints/{port:int}", GetEndpointAsync);

        return app;
    }

    private static async Task<IResult> ListAsync(
        ISandboxGateway gateway,
        string[]? state,
        string[]? metadata,
        int page = 1,
        int pageSize = 20,
        CancellationToken ct = default)
    {
        var result = await gateway.ListAsync(
            state is null || state.Length == 0 ? null : state,
            ParseMetadata(metadata),
            Math.Max(page, 1),
            Math.Clamp(pageSize, 1, 100),
            ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> CreateAsync(CreateSandboxRequest request, ISandboxGateway gateway, CancellationToken ct)
    {
        if (request is null || request.Image is null || string.IsNullOrWhiteSpace(request.Image.Uri))
            return Results.BadRequest(new ErrorResponse("invalid_argument", "image.uri is required"));
        if (request.Entrypoint is null || request.Entrypoint.Count == 0)
            return Results.BadRequest(new ErrorResponse("invalid_argument", "entrypoint is required"));

        var created = await gateway.CreateAsync(request, ct);
        return Results.Created($"/api/v1/sandboxes/{created.Id}", created);
    }

    private static async Task<IResult> GetAsync(string id, ISandboxGateway gateway, CancellationToken ct)
        => Results.Ok(await gateway.GetAsync(id, ct));

    private static async Task<IResult> DeleteAsync(string id, ISandboxGateway gateway, CancellationToken ct)
    {
        await gateway.DeleteAsync(id, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> PauseAsync(string id, ISandboxGateway gateway, CancellationToken ct)
    {
        await gateway.PauseAsync(id, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ResumeAsync(string id, ISandboxGateway gateway, CancellationToken ct)
    {
        await gateway.ResumeAsync(id, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> RenewExpirationAsync(string id, RenewExpirationRequest request, ISandboxGateway gateway, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.ExpiresAt))
            return Results.BadRequest(new ErrorResponse("invalid_argument", "expiresAt is required"));

        var renewed = await gateway.RenewExpirationAsync(id, request, ct);
        return Results.Ok(renewed);
    }

    private static async Task<IResult> GetEndpointAsync(string id, int port, ISandboxGateway gateway, CancellationToken ct)
        => Results.Ok(await gateway.GetEndpointAsync(id, port, ct));

    private static Dictionary<string, string>? ParseMetadata(string[]? metadata)
    {
        if (metadata is null || metadata.Length == 0) return null;

        var result = new Dictionary<string, string>();
        foreach (var entry in metadata)
        {
            var separator = entry.IndexOf('=');
            if (separator > 0)
                result[entry[..separator]] = entry[(separator + 1)..];
        }
        return result;
    }
}
