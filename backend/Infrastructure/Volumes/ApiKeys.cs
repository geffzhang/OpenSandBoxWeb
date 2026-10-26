using System.Security.Cryptography;
using System.Text;

namespace OpenSandbox.Web.Infrastructure.Volumes;

public static class ApiKeys
{
    public static string Create() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static byte[] Hash(string key) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(key));

    public static string Prefix(string key) => key[..Math.Min(8, key.Length)];

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
