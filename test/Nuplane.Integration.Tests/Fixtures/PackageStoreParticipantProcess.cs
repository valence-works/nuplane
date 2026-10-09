using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Nuplane.Integration.Tests.Fixtures;

/// <summary>Owns one test child and its deterministic ready/release gate.</summary>
internal sealed class PackageStoreParticipantProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task<string> _standardError;
    private readonly StringBuilder _standardOutput = new();
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    private bool _released;
    private PackageStoreParticipantExit? _exit;

    private PackageStoreParticipantProcess(Process process, string gate)
    {
        _process = process;
        Gate = gate;
        ProcessId = process.Id;
        _standardError = process.StandardError.ReadToEndAsync();
    }

    public string Gate { get; }
    public int ProcessId { get; }
    public bool HasExited => _exit is not null || _process.HasExited;

    public static async Task<PackageStoreParticipantProcess> StartAsync(string gate, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gate);
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
        start.ArgumentList.Add("--gate");
        start.ArgumentList.Add(gate);
        var process = Process.Start(start) ?? throw new InvalidOperationException("The participant did not start.");
        var participant = new PackageStoreParticipantProcess(process, gate);
        try
        {
            using var ready = await participant.ReadResponseAsync("ready", cancellationToken);
            if (ready.RootElement.GetProperty("processId").GetInt32() != participant.ProcessId)
                throw new InvalidDataException("Participant readiness reported a different process identity.");
            return participant;
        }
        catch
        {
            await participant.DisposeAsync();
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
        using var response = await ReadResponseAsync("released", cancellationToken);
        return await CaptureExitAsync(cancellationToken);
    }

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

    private async Task<JsonDocument> ReadResponseAsync(string kind, CancellationToken cancellationToken)
    {
        var line = await _process.StandardOutput.ReadLineAsync(cancellationToken)
            ?? throw new EndOfStreamException($"Participant exited before reporting {kind}.");
        _standardOutput.AppendLine(line);
        var message = JsonDocument.Parse(line);
        try
        {
            if (message.RootElement.GetProperty("kind").GetString() == kind &&
                message.RootElement.GetProperty("gate").GetString() == Gate)
                return message;
            throw new InvalidDataException($"Unexpected participant response while awaiting {kind}.");
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
