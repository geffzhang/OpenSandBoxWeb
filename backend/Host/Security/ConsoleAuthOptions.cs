namespace OpenSandbox.Web.Host.Security;

public sealed class ConsoleAuthOptions
{
    public const string SectionName = "Auth";

    public string Authority { get; set; } = "";
    public string ClientId { get; set; } = "";
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>
    /// 可选。容器内部可达的 OIDC discovery 地址；留空时由 Authority 推导。
    /// Authority 始终是浏览器可见的 issuer，用于 iss 校验。
    /// </summary>
    public string? MetadataAddress { get; set; }
}
