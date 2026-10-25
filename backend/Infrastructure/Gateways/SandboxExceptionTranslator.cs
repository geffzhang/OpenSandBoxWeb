using Microsoft.AspNetCore.Http;
using OpenSandbox.Core;
using OpenSandbox.Web.Contracts;

namespace OpenSandbox.Web.Infrastructure.Gateways;

public static class SandboxExceptionTranslator
{
    public static (int StatusCode, ErrorResponse Body) Translate(Exception exception) => exception switch
    {
        InvalidArgumentException => (StatusCodes.Status400BadRequest, new ErrorResponse("invalid_argument", exception.Message)),
        SandboxReadyTimeoutException => (StatusCodes.Status504GatewayTimeout, new ErrorResponse("sandbox_not_ready", exception.Message)),
        SandboxUnhealthyException => (StatusCodes.Status503ServiceUnavailable, new ErrorResponse("sandbox_unhealthy", exception.Message)),
        SandboxInternalException => (StatusCodes.Status500InternalServerError, new ErrorResponse("sandbox_internal", exception.Message)),
        SandboxApiException apiException => TranslateApiException(apiException),
        SandboxException => (StatusCodes.Status502BadGateway, new ErrorResponse("sandbox_error", exception.Message)),
        _ => (StatusCodes.Status500InternalServerError, new ErrorResponse("internal_error", "An unexpected error occurred.")),
    };

    private static (int StatusCode, ErrorResponse Body) TranslateApiException(SandboxApiException exception)
    {
        var statusCode = exception.StatusCode is >= 400 and <= 599
            ? exception.StatusCode.Value
            : StatusCodes.Status502BadGateway;
        var code = exception.Error?.Code ?? "sandbox_api_error";
        var message = exception.Error?.Message ?? exception.Message;
        return (statusCode, new ErrorResponse(code, message));
    }
}
