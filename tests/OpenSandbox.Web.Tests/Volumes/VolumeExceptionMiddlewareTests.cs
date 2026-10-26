using System.Data.Common;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenSandbox.Web.Contracts;
using OpenSandbox.Web.Host.Web;
using OpenSandbox.Web.Infrastructure.Volumes;
using Xunit;

namespace OpenSandbox.Web.Tests.Volumes;

public sealed class VolumeExceptionMiddlewareTests
{
    [Fact]
    public async Task DbUpdateException_MapsTo503()
    {
        var (context, error) = await InvokeAsync(new DbUpdateException("写入失败"));

        Assert.Equal(503, context.Response.StatusCode);
        Assert.Equal("dependency_unavailable", error.Code);
        Assert.Equal("数据库暂不可用", error.Message);
    }

    [Fact]
    public async Task DbException_MapsTo503()
    {
        var (context, error) = await InvokeAsync(new FakeDbException());

        Assert.Equal(503, context.Response.StatusCode);
        Assert.Equal("dependency_unavailable", error.Code);
        Assert.Equal("数据库暂不可用", error.Message);
    }

    [Fact]
    public async Task VolumeValidationException_MapsTo400()
    {
        var (context, error) = await InvokeAsync(new VolumeValidationException("参数错误"));

        Assert.Equal(400, context.Response.StatusCode);
        Assert.Equal("invalid_argument", error.Code);
        Assert.Equal("参数错误", error.Message);
    }

    [Fact]
    public async Task UnrelatedException_Propagates()
    {
        var context = RequestContext();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Middleware(_ => throw new InvalidOperationException("boom")).InvokeAsync(context));
    }

    private static async Task<(HttpContext Context, ErrorResponse Error)> InvokeAsync(
        Exception exception)
    {
        var context = RequestContext();
        await Middleware(_ => throw exception).InvokeAsync(context);

        context.Response.Body.Position = 0;
        var error = await JsonSerializer.DeserializeAsync<ErrorResponse>(
            context.Response.Body, JsonSerializerOptions.Web);
        return (context, error!);
    }

    private static DefaultHttpContext RequestContext()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static VolumeExceptionMiddleware Middleware(RequestDelegate next) =>
        new(next, NullLogger<VolumeExceptionMiddleware>.Instance);

    private sealed class FakeDbException() : DbException("数据库连接失败");
}
