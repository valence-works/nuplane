using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Nuplane.Integration.Tests.Support;

internal static class EndpointRouteTestHarness
{
    public static WebApplication CreateApp(Action<IServiceCollection> configureServices, Action<WebApplication> mapEndpoints)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
            ApplicationName = typeof(EndpointRouteTestHarness).Assembly.FullName
        });

        configureServices(builder.Services);

        var app = builder.Build();
        mapEndpoints(app);
        return app;
    }

    public static bool HasRoute(WebApplication app, string pattern, string method) =>
        ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()
            .Any(endpoint =>
                string.Equals(endpoint.RoutePattern.RawText, pattern, StringComparison.Ordinal)
                && endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(method, StringComparer.OrdinalIgnoreCase) == true);

    public static RouteEndpoint GetRoute(WebApplication app, string pattern, string method) =>
        ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(endpoint =>
                string.Equals(endpoint.RoutePattern.RawText, pattern, StringComparison.Ordinal)
                && endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(method, StringComparer.OrdinalIgnoreCase) == true);

    public static async Task<EndpointInvocationResult> InvokeAsync(WebApplication app, string pattern, string method)
    {
        var endpoint = GetRoute(app, pattern, method);
        var httpContext = new DefaultHttpContext
        {
            RequestServices = app.Services
        };
        httpContext.Request.Method = method;
        httpContext.Request.Path = pattern;
        httpContext.Response.Body = new MemoryStream();

        await endpoint.RequestDelegate!(httpContext);

        httpContext.Response.Body.Position = 0;
        using var reader = new StreamReader(httpContext.Response.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        return new EndpointInvocationResult(httpContext.Response.StatusCode, body);
    }
}

internal sealed record EndpointInvocationResult(int StatusCode, string Body);

