using OpenSandbox.Core;
using OpenSandbox.Web.Contracts;
using OpenSandbox.Web.Infrastructure.Gateways;
using Xunit;

namespace OpenSandbox.Web.Tests.Gateways;

public sealed class SandboxExceptionTranslatorTests
{
    [Theory]
    [InlineData(typeof(InvalidArgumentException), 400, "invalid_argument")]
    [InlineData(typeof(SandboxUnhealthyException), 503, "sandbox_unhealthy")]
    [InlineData(typeof(SandboxReadyTimeoutException), 504, "sandbox_not_ready")]
    [InlineData(typeof(SandboxInternalException), 500, "sandbox_internal")]
    public void KnownExceptionTypes_MapToExpectedStatusAndCode(Type exceptionType, int expectedStatus, string expectedCode)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType, "boom", null)!;

        var (status, body) = SandboxExceptionTranslator.Translate(exception);

        Assert.Equal(expectedStatus, status);
        Assert.Equal(expectedCode, body.Code);
    }

    [Fact]
    public void ApiException_WithStatusCode_UsesItsCodeAndMessage()
    {
        var exception = new SandboxApiException("conflict", 409, null, null, null,
            new SandboxError("sandbox_conflict", "already exists"));

        var (status, body) = SandboxExceptionTranslator.Translate(exception);

        Assert.Equal(409, status);
        Assert.Equal("sandbox_conflict", body.Code);
        Assert.Equal("already exists", body.Message);
    }

    [Fact]
    public void UnknownException_MapsToInternalError()
    {
        var (status, body) = SandboxExceptionTranslator.Translate(new InvalidOperationException("boom"));

        Assert.Equal(500, status);
        Assert.Equal("internal_error", body.Code);
    }
}
