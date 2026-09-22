using System.Net;
using System.Text;

namespace Nuplane.Runtime.Tests.TestSupport;

/// <summary>
/// The basic-auth credentials a <see cref="TestNuGetFeedServer"/> demands. The server compares the
/// whole <c>Authorization</c> header against what these produce and never exposes what it received,
/// so a test can prove authentication happened without holding the secret itself.
/// </summary>
internal sealed record TestFeedBasicAuth(string UserName, string Password);

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
    private readonly string? _expectedAuthorization;

    public int PackageDownloads { get; private set; }
    public int ServiceIndexRequests { get; private set; }

    /// <summary>Every request the server accepted, whatever it was for.</summary>
    public int Requests { get; private set; }

    /// <summary>Requests that arrived carrying the expected basic-auth header.</summary>
    public int AuthorizedRequests { get; private set; }

    /// <summary>Requests answered with 401 because the header was missing or wrong.</summary>
    public int UnauthorizedRequests { get; private set; }

    public Uri ServiceIndexUri => new(new(_baseAddress), "v3/index.json");

    public TestNuGetFeedServer(
        string packageId,
        string version,
        byte[] packageBytes,
        bool omitPackageBaseTrailingSlash = false,
        TestFeedBasicAuth? requiredAuth = null)
    {
        _packageId = packageId ?? throw new ArgumentNullException(nameof(packageId));
        _version = version ?? throw new ArgumentNullException(nameof(version));
        _packageBytes = packageBytes ?? throw new ArgumentNullException(nameof(packageBytes));
        _omitPackageBaseTrailingSlash = omitPackageBaseTrailingSlash;
        _expectedAuthorization = requiredAuth is null
            ? null
            : "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{requiredAuth.UserName}:{requiredAuth.Password}"));

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
        Requests++;

        if (!IsAuthorized(context.Request))
        {
            UnauthorizedRequests++;
            context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            // The challenge NuGet's client needs to retry with the credentials configured on its
            // PackageSource; the plain HttpClient path sends its header without being asked.
            context.Response.AddHeader("WWW-Authenticate", "Basic realm=\"nuplane-test\"");
            context.Response.Close();
            return;
        }

        var requestPath = context.Request.Url?.AbsolutePath ?? string.Empty;
        var lowerPackageId = _packageId.ToLowerInvariant();
        var lowerVersion = _version.ToLowerInvariant();
        var lowerNupkgName = $"{lowerPackageId}.{lowerVersion}.nupkg";
        var packageBaseAddress = _omitPackageBaseTrailingSlash
            ? $"{_baseAddress}flatcontainer"
            : $"{_baseAddress}flatcontainer/";

        if (requestPath.Equals("/v3/index.json", StringComparison.OrdinalIgnoreCase))
        {
            ServiceIndexRequests++;
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

    private bool IsAuthorized(HttpListenerRequest request)
    {
        if (_expectedAuthorization is null)
        {
            return true;
        }

        if (!string.Equals(request.Headers["Authorization"], _expectedAuthorization, StringComparison.Ordinal))
        {
            return false;
        }

        AuthorizedRequests++;
        return true;
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
