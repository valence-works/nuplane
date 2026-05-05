using System.Net;
using System.Text;

namespace Nuplane.Runtime.Tests.TestSupport;

internal sealed class TestNuGetFeedServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _serveLoop;
    private readonly byte[] _packageBytes;
    private readonly string _packageId;
    private readonly string _version;
    private readonly string _baseAddress;
    private readonly bool _omitPackageBaseTrailingSlash;

    public int PackageDownloads { get; private set; }
    public Uri ServiceIndexUri => new(new(_baseAddress), "v3/index.json");

    public TestNuGetFeedServer(string packageId, string version, byte[] packageBytes, bool omitPackageBaseTrailingSlash = false)
    {
        _packageId = packageId ?? throw new ArgumentNullException(nameof(packageId));
        _version = version ?? throw new ArgumentNullException(nameof(version));
        _packageBytes = packageBytes ?? throw new ArgumentNullException(nameof(packageBytes));
        _omitPackageBaseTrailingSlash = omitPackageBaseTrailingSlash;

        var prefix = $"http://127.0.0.1:{GetFreePort()}/";
        _baseAddress = prefix;
        _listener.Prefixes.Add(prefix);
        _listener.Start();
        _serveLoop = Task.Run(ServeAsync);
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _listener.Stop();
        try
        {
            await _serveLoop;
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        finally
        {
            _listener.Close();
            _shutdown.Dispose();
        }
    }

    private async Task ServeAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (HttpListenerException) when (_shutdown.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested)
            {
                break;
            }

            await HandleAsync(context);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var requestPath = context.Request.Url?.AbsolutePath ?? string.Empty;
        var lowerPackageId = _packageId.ToLowerInvariant();
        var lowerVersion = _version.ToLowerInvariant();
        var lowerNupkgName = $"{lowerPackageId}.{lowerVersion}.nupkg";
        var packageBaseAddress = _omitPackageBaseTrailingSlash
            ? $"{_baseAddress}flatcontainer"
            : $"{_baseAddress}flatcontainer/";

        if (requestPath.Equals("/v3/index.json", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonAsync(context.Response, $$"""
                {
                  "version": "3.0.0",
                  "resources": [
                    {
                      "@id": "{{packageBaseAddress}}",
                      "@type": "PackageBaseAddress/3.0.0"
                    }
                  ]
                }
                """);
            return;
        }

        if (requestPath.Equals($"/flatcontainer/{lowerPackageId}/index.json", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonAsync(context.Response, $$"""
                {
                  "versions": ["{{lowerVersion}}"]
                }
                """);
            return;
        }

        if (requestPath.Equals($"/flatcontainer/{lowerPackageId}/{lowerVersion}/{lowerNupkgName}", StringComparison.OrdinalIgnoreCase))
        {
            PackageDownloads++;
            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Response.ContentType = "application/octet-stream";
            context.Response.ContentLength64 = _packageBytes.Length;
            await context.Response.OutputStream.WriteAsync(_packageBytes);
            context.Response.Close();
            return;
        }

        context.Response.StatusCode = (int)HttpStatusCode.NotFound;
        context.Response.Close();
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = "application/json";
        response.ContentLength64 = payload.Length;
        await response.OutputStream.WriteAsync(payload);
        response.Close();
    }

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
