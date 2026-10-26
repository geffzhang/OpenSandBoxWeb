using System.Text.Json;

namespace OpenSandbox.Web.Host.Security;

public static class AuthConfigInjection
{
    public const string GlobalName = "__OPENSANDBOX_AUTH__";

    public static string Inject(string html, ConsoleAuthOptions options)
    {
        var payload = JsonSerializer.Serialize(new
        {
            authority = options.Authority,
            clientId = options.ClientId,
        });
        var script = $"<script>window.{GlobalName}={payload};</script>";

        var headEnd = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        if (headEnd < 0)
            throw new InvalidOperationException("index.html 缺少 </head>，无法注入登录配置");

        return html.Insert(headEnd, script);
    }
}
