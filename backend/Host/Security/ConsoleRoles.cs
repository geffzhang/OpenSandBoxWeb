using System.Security.Claims;
using System.Text.Json;

namespace OpenSandbox.Web.Host.Security;

public static class ConsoleRoles
{
    public const string SandboxAdmin = "sandbox:admin";
    public const string SandboxViewer = "sandbox:viewer";
    public const string VolumeAdmin = "volume:admin";
    public const string VolumeViewer = "volume:viewer";

    private static readonly IReadOnlySet<string> Empty = new HashSet<string>();

    public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.Ordinal)
    {
        SandboxAdmin,
        SandboxViewer,
        VolumeAdmin,
        VolumeViewer,
    };

    public static IReadOnlySet<string> RolesOf(ClaimsPrincipal principal, string clientId)
    {
        var raw = principal.FindFirst("resource_access")?.Value;
        if (string.IsNullOrWhiteSpace(raw))
            return Empty;

        try
        {
            using var document = JsonDocument.Parse(raw);
            if (!document.RootElement.TryGetProperty(clientId, out var client) ||
                !client.TryGetProperty("roles", out var roles) ||
                roles.ValueKind != JsonValueKind.Array)
                return Empty;

            var parsed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var role in roles.EnumerateArray())
            {
                if (role.ValueKind != JsonValueKind.String)
                    continue;

                var value = role.GetString();
                if (value is not null && Known.Contains(value))
                    parsed.Add(value);
            }

            return parsed;
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    public static bool Has(IReadOnlySet<string> roles, string required) => required switch
    {
        SandboxViewer => roles.Contains(SandboxAdmin) || roles.Contains(SandboxViewer),
        VolumeViewer => roles.Contains(VolumeAdmin) || roles.Contains(VolumeViewer),
        _ => roles.Contains(required),
    };
}
