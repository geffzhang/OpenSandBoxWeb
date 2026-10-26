using System.Text.RegularExpressions;
using OpenSandbox.Web.Infrastructure.Volumes;
using Xunit;

namespace OpenSandbox.Web.Tests.Volumes;

public sealed class ApiKeysTests
{
    [Fact]
    public void Create_Returns43UrlSafeCharacters()
    {
        for (var i = 0; i < 50; i++)
        {
            var key = ApiKeys.Create();
            Assert.Equal(43, key.Length);
            Assert.Matches(new Regex("^[A-Za-z0-9_-]+$"), key);
        }
    }

    [Fact]
    public void Create_IsRandom()
    {
        var keys = Enumerable.Range(0, 100).Select(_ => ApiKeys.Create()).ToHashSet();
        Assert.Equal(100, keys.Count);
    }

    [Fact]
    public void Hash_IsSha256AndDeterministic()
    {
        var key = ApiKeys.Create();
        Assert.Equal(32, ApiKeys.Hash(key).Length);
        Assert.Equal(ApiKeys.Hash(key), ApiKeys.Hash(key));
        Assert.NotEqual(ApiKeys.Hash(key), ApiKeys.Hash(ApiKeys.Create()));
    }

    [Fact]
    public void Prefix_IsFirstEightCharacters()
        => Assert.Equal("abcdefgh", ApiKeys.Prefix("abcdefghijklmnop"));
}
