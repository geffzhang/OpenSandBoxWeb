using OpenSandbox.Core;
using OpenSandbox.Web.Contracts;
using Models = OpenSandbox.Models;

namespace OpenSandbox.Web.Infrastructure.Gateways;

public static class SandboxMapper
{
    public static Models.CreateSandboxRequest ToSdk(CreateSandboxRequest request)
    {
        if (request.Image is null || string.IsNullOrWhiteSpace(request.Image.Uri))
            throw new InvalidArgumentException("image.uri is required", null);
        if (request.Entrypoint is null || request.Entrypoint.Count == 0)
            throw new InvalidArgumentException("entrypoint is required", null);

        return new Models.CreateSandboxRequest
        {
            Image = new Models.ImageSpec
            {
                Uri = request.Image.Uri,
                Auth = request.Image.Auth is null ? null : new Models.ImageAuth
                {
                    Username = request.Image.Auth.Username,
                    Password = request.Image.Auth.Password,
                },
            },
            Entrypoint = request.Entrypoint,
            Timeout = request.Timeout,
            ResourceLimits = request.ResourceLimits,
            Env = request.Env,
            Metadata = request.Metadata,
            NetworkPolicy = request.NetworkPolicy is null ? null : new Models.NetworkPolicy
            {
                DefaultAction = string.IsNullOrEmpty(request.NetworkPolicy.DefaultAction)
                    ? null
                    : Enum.Parse<Models.NetworkRuleAction>(request.NetworkPolicy.DefaultAction, ignoreCase: true),
                Egress = request.NetworkPolicy.Egress?.Select(rule => new Models.NetworkRule
                {
                    Action = Enum.Parse<Models.NetworkRuleAction>(rule.Action, ignoreCase: true),
                    Target = rule.Target,
                }).ToList(),
            },
            Volumes = request.Volumes?.Select(volume => new Models.Volume
            {
                Name = volume.Name,
                MountPath = volume.MountPath,
                ReadOnly = volume.ReadOnly,
                SubPath = volume.SubPath,
                Host = volume.HostPath is null ? null : new Models.Host { Path = volume.HostPath },
                Pvc = volume.PvcClaimName is null ? null : new Models.PVC { ClaimName = volume.PvcClaimName },
            }).ToList(),
            Extensions = request.Extensions?.ToDictionary(pair => pair.Key, pair => (object)pair.Value),
        };
    }

    public static SandboxDto ToDto(Models.SandboxInfo info) => new(
        info.Id,
        info.Image is null
            ? new ImageRefDto(string.Empty)
            : new ImageRefDto(
                info.Image.Uri,
                info.Image.Auth is null
                    ? null
                    : new ImageAuthDto(info.Image.Auth.Username ?? string.Empty, info.Image.Auth.Password ?? string.Empty)),
        new SandboxStatusDto(info.Status.State, info.Status.Reason, info.Status.Message),
        info.Metadata,
        info.Entrypoint,
        info.ExpiresAt,
        info.CreatedAt);

    public static CreateSandboxResponse ToCreateResponse(Models.CreateSandboxResponse response) => new(
        response.Id,
        new SandboxStatusDto(response.Status.State, response.Status.Reason, response.Status.Message),
        response.Metadata is null ? null : new Dictionary<string, string>(response.Metadata),
        response.ExpiresAt,
        response.CreatedAt,
        response.Entrypoint);

    public static MetricsDto ToMetricsDto(Models.SandboxMetrics metrics) => new(
        metrics.CpuCount,
        metrics.CpuUsedPercentage,
        metrics.MemoryTotalMiB,
        metrics.MemoryUsedMiB,
        metrics.Timestamp);
}
