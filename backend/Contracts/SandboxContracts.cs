namespace OpenSandbox.Web.Contracts;

public sealed record ImageAuthDto(string Username, string Password);

public sealed record ImageRefDto(string Uri, ImageAuthDto? Auth = null);

public sealed record SandboxStatusDto(string State, string? Reason = null, string? Message = null);

public sealed record SandboxDto(
    string Id,
    ImageRefDto Image,
    SandboxStatusDto Status,
    IReadOnlyDictionary<string, string>? Metadata,
    IReadOnlyList<string> Entrypoint,
    DateTime? ExpiresAt,
    DateTime CreatedAt);

public sealed record NetworkRuleDto(string Action, string Target);

public sealed record NetworkPolicyDto(string? DefaultAction, IReadOnlyList<NetworkRuleDto>? Egress);

public sealed record VolumeDto(
    string Name,
    string MountPath,
    bool? ReadOnly = null,
    string? SubPath = null,
    string? HostPath = null,
    string? PvcClaimName = null);

public sealed record CreateSandboxRequest(
    ImageRefDto? Image,
    int? Timeout,
    Dictionary<string, string>? ResourceLimits,
    IReadOnlyList<string>? Entrypoint,
    Dictionary<string, string>? Env = null,
    Dictionary<string, string>? Metadata = null,
    NetworkPolicyDto? NetworkPolicy = null,
    IReadOnlyList<VolumeDto>? Volumes = null,
    Dictionary<string, string>? Extensions = null);

public sealed record CreateSandboxResponse(
    string Id,
    SandboxStatusDto Status,
    Dictionary<string, string>? Metadata,
    DateTime? ExpiresAt,
    DateTime CreatedAt,
    IReadOnlyList<string> Entrypoint);

public sealed record RenewExpirationRequest(string? ExpiresAt);

public sealed record RenewExpirationResponse(DateTime? ExpiresAt);

public sealed record EndpointDto(string Endpoint, IReadOnlyDictionary<string, string>? Headers);
