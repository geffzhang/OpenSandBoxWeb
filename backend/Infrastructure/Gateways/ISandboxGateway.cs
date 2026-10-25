using OpenSandbox.Web.Contracts;

namespace OpenSandbox.Web.Infrastructure.Gateways;

public interface ISandboxGateway
{
    Task<CreateSandboxResponse> CreateAsync(CreateSandboxRequest request, CancellationToken ct = default);
    Task<ListSandboxesResponse> ListAsync(
        IReadOnlyList<string>? states,
        IReadOnlyDictionary<string, string>? metadata,
        int page,
        int pageSize,
        CancellationToken ct = default);
    Task<SandboxDto> GetAsync(string id, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
    Task PauseAsync(string id, CancellationToken ct = default);
    Task ResumeAsync(string id, CancellationToken ct = default);
    Task<RenewExpirationResponse> RenewExpirationAsync(string id, RenewExpirationRequest request, CancellationToken ct = default);
    Task<EndpointDto> GetEndpointAsync(string id, int port, CancellationToken ct = default);
    Task<MetricsDto> GetMetricsAsync(string id, CancellationToken ct = default);
}
