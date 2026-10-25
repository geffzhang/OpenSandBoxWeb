using OpenSandbox.Config;

namespace OpenSandbox.Web.Infrastructure.Gateways;

public sealed class OpenSandboxOptions
{
    public const string SectionName = "OpenSandbox";

    public string Domain { get; init; } = "";
    public string? ApiKey { get; init; }
    public string Protocol { get; init; } = "https";
    public int RequestTimeoutSeconds { get; init; } = 30;
    public bool DisableMetrics { get; init; }

    public ConnectionConfigOptions ToConnectionConfigOptions() => new()
    {
        Domain = Domain,
        ApiKey = ApiKey,
        Protocol = Enum.TryParse<ConnectionProtocol>(Protocol, ignoreCase: true, out var protocol)
            ? protocol
            : ConnectionProtocol.Https,
        RequestTimeoutSeconds = RequestTimeoutSeconds,
        DisableMetrics = DisableMetrics,
    };
}
