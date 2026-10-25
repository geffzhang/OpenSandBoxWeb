namespace OpenSandbox.Web.Host.Security;

public sealed class ConsoleAuthOptions
{
    public const string SectionName = "Auth";

    public string Authority { get; set; } = "";
    public string ClientId { get; set; } = "";
    public bool RequireHttpsMetadata { get; set; } = true;
}
