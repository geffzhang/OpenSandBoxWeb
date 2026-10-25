using System.Globalization;
using Microsoft.Extensions.Options;
using OpenSandbox.Config;
using OpenSandbox.Core;
using OpenSandbox.Web.Contracts;
using Models = OpenSandbox.Models;

namespace OpenSandbox.Web.Infrastructure.Gateways;

// The SDK builds its low-level ISandboxes/IExecdMetrics adapters through internal
// constructors; Sandbox and SandboxManager are its supported public entry points.
public sealed class SandboxGateway : ISandboxGateway, IAsyncDisposable
{
    private readonly ConnectionConfig _connectionConfig;
    private readonly SdkDiagnosticsOptions _diagnostics;
    private readonly Lazy<SandboxManager> _manager;

    public SandboxGateway(IOptions<OpenSandboxOptions> options, ILoggerFactory loggerFactory)
    {
        _connectionConfig = new ConnectionConfig(options.Value.ToConnectionConfigOptions());
        _diagnostics = new SdkDiagnosticsOptions { LoggerFactory = loggerFactory };
        _manager = new Lazy<SandboxManager>(() => SandboxManager.Create(new SandboxManagerOptions
        {
            ConnectionConfig = _connectionConfig,
            Diagnostics = _diagnostics,
        }));
    }

    private SandboxManager Manager => _manager.Value;

    public async Task<CreateSandboxResponse> CreateAsync(CreateSandboxRequest request, CancellationToken ct = default)
    {
        var sdkRequest = SandboxMapper.ToSdk(request);
        await using var sandbox = await Sandbox.CreateAsync(new SandboxCreateOptions
        {
            ConnectionConfig = _connectionConfig,
            Diagnostics = _diagnostics,
            Image = sdkRequest.Image!.Uri,
            ImageAuth = sdkRequest.Image.Auth,
            Entrypoint = sdkRequest.Entrypoint,
            TimeoutSeconds = sdkRequest.Timeout,
            Resource = sdkRequest.ResourceLimits,
            Env = sdkRequest.Env,
            Metadata = sdkRequest.Metadata,
            NetworkPolicy = sdkRequest.NetworkPolicy,
            Volumes = sdkRequest.Volumes,
            Extensions = request.Extensions,
            SkipHealthCheck = true,
        }, ct);
        var info = await sandbox.GetInfoAsync(ct);
        return SandboxMapper.ToCreateResponse(new Models.CreateSandboxResponse
        {
            Id = info.Id,
            Status = info.Status,
            Metadata = info.Metadata,
            ExpiresAt = info.ExpiresAt,
            CreatedAt = info.CreatedAt,
            Entrypoint = info.Entrypoint,
        });
    }

    public async Task<ListSandboxesResponse> ListAsync(
        IReadOnlyList<string>? states,
        IReadOnlyDictionary<string, string>? metadata,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        var result = await Manager.ListSandboxInfosAsync(new SandboxFilter
        {
            States = states,
            Metadata = metadata,
            Page = page,
            PageSize = pageSize,
        }, ct);
        var pagination = result.Pagination;
        return new ListSandboxesResponse(
            result.Items.Select(SandboxMapper.ToDto).ToList(),
            new PaginationInfoDto(
                pagination?.Page ?? page,
                pagination?.PageSize ?? pageSize,
                pagination?.TotalItems ?? result.Items.Count,
                pagination?.TotalPages ?? 1,
                pagination?.HasNextPage ?? false));
    }

    public async Task<SandboxDto> GetAsync(string id, CancellationToken ct = default)
        => SandboxMapper.ToDto(await Manager.GetSandboxInfoAsync(id, ct));

    public Task DeleteAsync(string id, CancellationToken ct = default)
        => Manager.KillSandboxAsync(id, ct);

    public Task PauseAsync(string id, CancellationToken ct = default)
        => Manager.PauseSandboxAsync(id, ct);

    public Task ResumeAsync(string id, CancellationToken ct = default)
        => Manager.ResumeSandboxAsync(id, ct);

    public async Task<RenewExpirationResponse> RenewExpirationAsync(string id, RenewExpirationRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.ExpiresAt))
            throw new InvalidArgumentException("expiresAt is required", null);
        if (!DateTimeOffset.TryParse(request.ExpiresAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expiresAt))
            throw new InvalidArgumentException("expiresAt must be an ISO 8601 timestamp", null);

        // The public renew API accepts a timeout in seconds, so the absolute expiry
        // from the request is converted and echoed back in the response.
        var remainingSeconds = Math.Clamp(Math.Ceiling((expiresAt - DateTimeOffset.UtcNow).TotalSeconds), 0, int.MaxValue);
        await Manager.RenewSandboxAsync(id, (int)remainingSeconds, ct);
        return new RenewExpirationResponse(expiresAt.UtcDateTime);
    }

    public async Task<EndpointDto> GetEndpointAsync(string id, int port, CancellationToken ct = default)
    {
        await using var sandbox = await ConnectAsync(id, ct);
        var endpoint = await sandbox.GetEndpointAsync(port, ct);
        return new EndpointDto(endpoint.EndpointAddress, endpoint.Headers);
    }

    public async Task<MetricsDto> GetMetricsAsync(string id, CancellationToken ct = default)
    {
        await using var sandbox = await ConnectAsync(id, ct);
        return SandboxMapper.ToMetricsDto(await sandbox.GetMetricsAsync(ct));
    }

    public async ValueTask DisposeAsync()
    {
        if (_manager.IsValueCreated)
            await _manager.Value.DisposeAsync();
    }

    private Task<Sandbox> ConnectAsync(string id, CancellationToken ct) =>
        Sandbox.ConnectAsync(new SandboxConnectOptions
        {
            SandboxId = id,
            ConnectionConfig = _connectionConfig,
            Diagnostics = _diagnostics,
            SkipHealthCheck = true,
        }, ct);
}
