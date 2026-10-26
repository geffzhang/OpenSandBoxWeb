using System.Security.Claims;
using Microsoft.Extensions.Options;
using OpenSandbox.Web.Host.Security;

namespace OpenSandbox.Web.Host.Web;

public static class ConsoleEndpoints
{
    public sealed record ConsoleMeResponse(
        string Subject,
        string Username,
        IReadOnlyList<string> Roles);

    public static IEndpointRouteBuilder MapConsoleEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/console").WithTags("Console");

        group.MapGet("/me", (ClaimsPrincipal user, IOptions<ConsoleAuthOptions> options) =>
        {
            var subject = user.FindFirst("sub")?.Value ?? "";
            var preferredUsername = user.FindFirst("preferred_username")?.Value;
            var username = string.IsNullOrWhiteSpace(preferredUsername)
                ? (subject.Length > 8 ? subject[..8] : subject)
                : preferredUsername;
            var roles = ConsoleRoles.RolesOf(user, options.Value.ClientId)
                .OrderBy(role => role, StringComparer.Ordinal)
                .ToArray();

            return Results.Ok(new ConsoleMeResponse(subject, username, roles));
        });

        return app;
    }
}
