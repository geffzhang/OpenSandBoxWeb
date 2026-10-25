using OpenSandbox.Core;
using OpenSandbox.Web.Contracts;
using OpenSandbox.Web.Infrastructure.Gateways;

namespace OpenSandbox.Web.Tests.Endpoints;

public sealed class FakeSandboxGateway : ISandboxGateway
{
    public List<SandboxDto> Sandboxes { get; } =
    [
        new SandboxDto(
            "sb-1",
            new ImageRefDto("ubuntu:24.04"),
            new SandboxStatusDto("Running"),
            new Dictionary<string, string> { ["name"] = "demo" },
            ["/bin/sh"],
            null,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
    ];

    public Exception? NextException { get; set; }

    private Exception TakeException()
    {
        var exception = NextException!;
        NextException = null;
        return exception;
    }

    public Task<CreateSandboxResponse> CreateAsync(CreateSandboxRequest request, CancellationToken ct = default)
    {
        if (NextException is not null) throw TakeException();
        return Task.FromResult(new CreateSandboxResponse(
            "sb-new",
            new SandboxStatusDto("Pending"),
            request.Metadata,
            null,
            DateTime.UtcNow,
            request.Entrypoint ?? []));
    }

    public Task<ListSandboxesResponse> ListAsync(
        IReadOnlyList<string>? states,
        IReadOnlyDictionary<string, string>? metadata,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        if (NextException is not null) throw TakeException();
        var filtered = Sandboxes
            .Where(s => states is null || states.Count == 0 || states.Contains(s.Status.State))
            .ToList();
        var total = filtered.Count;
        var items = filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return Task.FromResult(new ListSandboxesResponse(
            items,
            new PaginationInfoDto(page, pageSize, total, (int)Math.Ceiling(total / (double)pageSize), page * pageSize < total)));
    }

    public Task<SandboxDto> GetAsync(string id, CancellationToken ct = default)
    {
        if (NextException is not null) throw TakeException();
        var sandbox = Sandboxes.FirstOrDefault(s => s.Id == id);
        if (sandbox is null)
            throw new SandboxApiException("not found", 404, null, null, null, new SandboxError("not_found", $"sandbox {id} not found"));
        return Task.FromResult(sandbox);
    }

    public Task DeleteAsync(string id, CancellationToken ct = default)
    {
        if (NextException is not null) throw TakeException();
        return Task.CompletedTask;
    }

    public Task PauseAsync(string id, CancellationToken ct = default)
    {
        if (NextException is not null) throw TakeException();
        return Task.CompletedTask;
    }

    public Task ResumeAsync(string id, CancellationToken ct = default)
    {
        if (NextException is not null) throw TakeException();
        return Task.CompletedTask;
    }

    public Task<RenewExpirationResponse> RenewExpirationAsync(string id, RenewExpirationRequest request, CancellationToken ct = default)
    {
        if (NextException is not null) throw TakeException();
        return Task.FromResult(new RenewExpirationResponse(DateTime.Parse(request.ExpiresAt!)));
    }

    public Task<EndpointDto> GetEndpointAsync(string id, int port, CancellationToken ct = default)
    {
        if (NextException is not null) throw TakeException();
        return Task.FromResult(new EndpointDto($"sandbox-{id}:{port}", null));
    }

    public Task<MetricsDto> GetMetricsAsync(string id, CancellationToken ct = default)
    {
        if (NextException is not null) throw TakeException();
        return Task.FromResult(new MetricsDto(4, 12.5, 4096, 1024, 1700000000000));
    }
}
