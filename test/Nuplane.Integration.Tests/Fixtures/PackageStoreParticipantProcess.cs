using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Nuplane.Integration.Tests.Fixtures;

/// <summary>Owns one test child and its deterministic ready/release gate.</summary>
internal sealed class PackageStoreParticipantProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly string _correlationId;
    private readonly string _correlationProperty;
    private readonly Task<string> _standardError;
    private readonly StringBuilder _standardOutput = new();
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    private bool _released;
    private PackageStoreParticipantExit? _exit;

    private PackageStoreParticipantProcess(Process process, string correlationId, string correlationProperty)
    {
        _process = process;
        _correlationId = correlationId;
        _correlationProperty = correlationProperty;
        ProcessId = process.Id;
        _standardError = process.StandardError.ReadToEndAsync();
    }

    public string Gate => _correlationProperty == "gate" ? _correlationId : string.Empty;
    public string OperationId => _correlationProperty == "operationId" ? _correlationId : string.Empty;
    public int ProcessId { get; }
    public bool HasExited => _exit is not null || _process.HasExited;

    public static async Task<PackageStoreParticipantProcess> StartAsync(string gate, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gate);
        return await StartCoreAsync(["--gate", gate], gate, "gate", "ready", cancellationToken).ConfigureAwait(false);
    }

    public static Task<PackageStoreParticipantProcess> StartPublisherAsync(
        string operationId, string requestPath, CancellationToken cancellationToken)
        => StartOperationAsync("--membership-publish", operationId, requestPath, cancellationToken);

    public static Task<PackageStoreParticipantProcess> StartRecoveryAsync(
        string operationId, string requestPath, CancellationToken cancellationToken)
        => StartOperationAsync("--membership-recover", operationId, requestPath, cancellationToken);

    public static Task<PackageStoreParticipantProcess> StartGroupPublisherAsync(
        string operationId, string requestPath, CancellationToken cancellationToken)
        => StartOperationAsync("--membership-group-publish", operationId, requestPath, cancellationToken);

    public static Task<PackageStoreParticipantProcess> StartMigrationAsync(
        string operationId, string requestPath, CancellationToken cancellationToken)
        => StartOperationAsync("--membership-migrate", operationId, requestPath, cancellationToken);

    public static Task<PackageStoreParticipantProcess> StartInitializationAsync(
        string operationId, string requestPath, CancellationToken cancellationToken)
        => StartOperationAsync("--membership-initialize", operationId, requestPath, cancellationToken);

    public static Task<PackageStoreParticipantProcess> StartBindingAsync(
        string operationId, string requestPath, CancellationToken cancellationToken)
        => StartOperationAsync("--membership-bind", operationId, requestPath, cancellationToken);

    public static Task<PackageStoreParticipantProcess> StartGraphUseIdleAsync(
        string operationId, string requestPath, CancellationToken cancellationToken)
        => StartOperationAsync("--graph-use-idle", operationId, requestPath, cancellationToken);

    public static Task<PackageStoreParticipantProcess> StartParticipantGraphUseIdleAsync(
        string operationId, string requestPath, CancellationToken cancellationToken)
        => StartOperationAsync("--participant-graph-use-idle", operationId, requestPath, cancellationToken);

    public static Task<PackageStoreParticipantProcess> StartParticipantGraphUseReorderedRosterAsync(
        string operationId, string requestPath, CancellationToken cancellationToken)
        => StartOperationAsync("--participant-graph-use-reordered-roster", operationId, requestPath, cancellationToken);

    public static Task<PackageStoreParticipantProcess> StartParticipantGraphUseUnscopedGateAsync(
        string operationId, string requestPath, CancellationToken cancellationToken)
        => StartOperationAsync("--participant-graph-use-unscoped-gate", operationId, requestPath, cancellationToken);

    public static Task<PackageStoreParticipantProcess> StartParticipantGraphUseScopedGateAsync(
        string operationId, string requestPath, CancellationToken cancellationToken)
        => StartOperationAsync("--participant-graph-use-scoped-gate", operationId, requestPath, cancellationToken);

    public static Task<PackageStoreParticipantProcess> StartParticipantGraphUseBeforeLoaderInputAsync(
        string operationId, string requestPath, CancellationToken cancellationToken)
        => StartOperationAsync("--participant-graph-use-before-loader-input", operationId, requestPath, cancellationToken);

    public static Task<PackageStoreParticipantProcess> StartParticipantGraphUseBeforeFirstPeReadAsync(
        string operationId, string requestPath, CancellationToken cancellationToken)
        => StartOperationAsync("--participant-graph-use-before-first-pe-read", operationId, requestPath, cancellationToken);

    public static Task<PackageStoreParticipantProcess> StartParticipantGraphUseIdentityMismatchAsync(
        string operationId, string requestPath, CancellationToken cancellationToken)
        => StartOperationAsync("--participant-graph-use-identity-mismatch", operationId, requestPath, cancellationToken);

    public static Task<PackageStoreParticipantProcess> StartExecutionAsync(
        string operationId, string requestPath, CancellationToken cancellationToken)
        => StartOperationAsync("--package-store-execute", operationId, requestPath, cancellationToken);

    private static Task<PackageStoreParticipantProcess> StartOperationAsync(
        string command, string operationId, string requestPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestPath);
        return StartCoreAsync([command, requestPath], operationId, "operationId", "started", cancellationToken);
    }

    private static async Task<PackageStoreParticipantProcess> StartCoreAsync(
        IReadOnlyList<string> arguments, string correlationId, string correlationProperty,
        string readyKind, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var host = Path.Combine(AppContext.BaseDirectory, "PackageStoreTestHost", "Nuplane.PackageStore.TestHost.dll");
        if (!File.Exists(host))
            throw new FileNotFoundException("Build the integration project to copy the package-store test host.", host);

        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(host);
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        var process = Process.Start(start) ?? throw new InvalidOperationException("The participant did not start.");
        var participant = new PackageStoreParticipantProcess(process, correlationId, correlationProperty);
        try
        {
            using var ready = await participant.ReadResponseAsync(readyKind, cancellationToken).ConfigureAwait(false);
            if (ready.RootElement.GetProperty("processId").GetInt32() != participant.ProcessId)
                throw new InvalidDataException("Participant readiness reported a different process identity.");
            return participant;
        }
        catch
        {
            await participant.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<PackageStoreParticipantExit> ReleaseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
        if (_released)
            throw new InvalidOperationException("The participant gate was already released.");
        _released = true;
        var command = JsonSerializer.Serialize(new { command = "release", gate = Gate });
        await _process.StandardInput.WriteLineAsync(command.AsMemory(), cancellationToken);
        await _process.StandardInput.FlushAsync(cancellationToken);
        using var response = await ReadResponseAsync("released", cancellationToken).ConfigureAwait(false);
        return await CaptureExitAsync(cancellationToken);
    }

    public async Task<PackageStoreParticipantExit> ReleaseOperationAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
        if (_correlationProperty != "operationId")
            throw new InvalidOperationException("Only an operation participant accepts an operation release.");
        if (_released)
            throw new InvalidOperationException("The participant gate was already released.");
        _released = true;
        var command = JsonSerializer.Serialize(new { command = "release", operationId = OperationId });
        await _process.StandardInput.WriteLineAsync(command.AsMemory(), cancellationToken);
        await _process.StandardInput.FlushAsync(cancellationToken);
        using var response = await ReadResponseAsync("exiting", cancellationToken).ConfigureAwait(false);
        return await CaptureExitAsync(cancellationToken);
    }

    public async Task<JsonDocument> RequestIdentityRepeatAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
        if (_correlationProperty != "operationId" || _released)
            throw new InvalidOperationException("Only an idle operation participant accepts an identity-repeat command.");
        var command = JsonSerializer.Serialize(new { command = "repeat", operationId = OperationId });
        await _process.StandardInput.WriteLineAsync(command.AsMemory(), cancellationToken);
        await _process.StandardInput.FlushAsync(cancellationToken);
        return await ReadResponseAsync("repeat-refused", cancellationToken).ConfigureAwait(false);
    }

    public Task<JsonDocument> ReadResponseAsync(string kind, CancellationToken cancellationToken)
        => ReadResponseCoreAsync(kind, cancellationToken);

    public Task<JsonDocument> ReadNextResponseAsync(CancellationToken cancellationToken)
        => ReadNextResponseCoreAsync(cancellationToken);

    public Task<PackageStoreParticipantExit> WaitForExitAsync(CancellationToken cancellationToken)
        => CaptureExitAsync(cancellationToken);

    public async Task<PackageStoreParticipantExit> TerminateAsync(CancellationToken cancellationToken)
    {
        if (!HasExited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) when (_process.HasExited)
            {
                // The child may finish between the identity-owned check and termination.
            }
        }
        return await CaptureExitAsync(cancellationToken);
    }

    public async Task<PackageStoreParticipantExit> KillChildAsync(CancellationToken cancellationToken)
    {
        if (!HasExited)
        {
            try
            {
                _process.Kill(entireProcessTree: false);
            }
            catch (InvalidOperationException) when (_process.HasExited)
            {
                // The owned child may exit between the identity check and termination.
            }
        }
        return await CaptureExitAsync(cancellationToken);
    }

    private async Task<JsonDocument> ReadResponseCoreAsync(string kind, CancellationToken cancellationToken)
    {
        var message = await ReadNextResponseCoreAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (message.RootElement.GetProperty("kind").GetString() == kind)
                return message;
            throw new InvalidDataException($"Unexpected participant response while awaiting {kind}.");
        }
        catch
        {
            message.Dispose();
            throw;
        }
    }

    private async Task<JsonDocument> ReadNextResponseCoreAsync(CancellationToken cancellationToken)
    {
        var line = await _process.StandardOutput.ReadLineAsync(cancellationToken);
        if (line is null)
        {
            var exit = await CaptureExitAsync(cancellationToken).ConfigureAwait(false);
            throw new EndOfStreamException(
                $"Participant exited with code {exit.ExitCode} before reporting its next response. {exit.StandardError}");
        }
        _standardOutput.AppendLine(line);
        var message = JsonDocument.Parse(line);
        try
        {
            if (message.RootElement.GetProperty(_correlationProperty).GetString() == _correlationId)
            {
                if (_correlationProperty == "operationId" &&
                    message.RootElement.GetProperty("processId").GetInt32() != ProcessId)
                {
                    throw new InvalidDataException("Participant response contained a different process identity.");
                }
                return message;
            }
            throw new InvalidDataException("Participant response contained a different correlation identity.");
        }
        catch
        {
            message.Dispose();
            throw;
        }
    }

    private async Task<PackageStoreParticipantExit> CaptureExitAsync(CancellationToken cancellationToken)
    {
        if (_exit is not null)
            return _exit;
        await _process.WaitForExitAsync(cancellationToken);
        _standardOutput.Append(await _process.StandardOutput.ReadToEndAsync(cancellationToken));
        _exit = new PackageStoreParticipantExit(_process.ExitCode, _standardOutput.ToString(), await _standardError);
        return _exit;
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await TerminateAsync(timeout.Token);
        }
        finally
        {
            _process.Dispose();
        }
    }
}

internal sealed record PackageStoreParticipantExit(int ExitCode, string StandardOutput, string StandardError);
