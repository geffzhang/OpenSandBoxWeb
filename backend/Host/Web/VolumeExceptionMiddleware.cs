using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using OpenSandbox.Web.Contracts;
using OpenSandbox.Web.Infrastructure.Volumes;

namespace OpenSandbox.Web.Host.Web;

public sealed class VolumeExceptionMiddleware(
    RequestDelegate next,
    ILogger<VolumeExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (VolumeDependencyException exception)
        {
            await WriteAsync(context, 503, "dependency_unavailable", exception.Message);
        }
        catch (DbUpdateException)
        {
            await WriteAsync(context, 503, "dependency_unavailable", "数据库暂不可用");
        }
        catch (DbException)
        {
            await WriteAsync(context, 503, "dependency_unavailable", "数据库暂不可用");
        }
        catch (VolumeUnauthorizedException exception)
        {
            await WriteAsync(context, 401, "unauthorized", exception.Message);
        }
        catch (VolumeValidationException exception)
        {
            await WriteAsync(context, 400, "invalid_argument", exception.Message);
        }
        catch (VolumeNotFoundException exception)
        {
            await WriteAsync(context, 404, "not_found", exception.Message);
        }
        catch (VolumeConflictException exception)
        {
            await WriteAsync(context, 409, "conflict", exception.Message);
        }
        catch (ScanInProgressException exception)
        {
            await WriteAsync(context, 409, "scan_in_progress", exception.Message);
        }
    }

    private async Task WriteAsync(
        HttpContext context, int statusCode, string code, string message)
    {
        if (context.Response.HasStarted)
        {
            logger.LogWarning("响应已开始，无法写入卷治理错误 {Code}", code);
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        await context.Response.WriteAsJsonAsync(new ErrorResponse(code, message));
    }
}
