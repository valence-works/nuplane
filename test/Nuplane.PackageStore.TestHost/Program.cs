using System.Text.Json;

namespace Nuplane.PackageStore.TestHost;

internal static class Program
{
    private const int InvalidProtocolExitCode = 3;
    private const int PrematureEofExitCode = 2;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 2 || args[0] != "--gate" || string.IsNullOrWhiteSpace(args[1]))
        {
            await WriteDiagnosticAsync("Expected arguments: --gate <name>").ConfigureAwait(false);
            return InvalidProtocolExitCode;
        }

        var gate = args[1];
        await WriteJsonLineAsync(new ReadyMessage("ready", gate, Environment.ProcessId)).ConfigureAwait(false);

        var input = await Console.In.ReadLineAsync().ConfigureAwait(false);
        if (input is null)
            return PrematureEofExitCode;

        GateCommand? command;
        try
        {
            command = JsonSerializer.Deserialize<GateCommand>(input, JsonOptions);
        }
        catch (JsonException)
        {
            await WriteDiagnosticAsync("Expected one JSON release command.").ConfigureAwait(false);
            return InvalidProtocolExitCode;
        }

        if (command is null || command.Command != "release" || command.Gate != gate)
        {
            await WriteDiagnosticAsync($"Expected a release command for gate '{gate}'.").ConfigureAwait(false);
            return InvalidProtocolExitCode;
        }

        await WriteJsonLineAsync(new ReleasedMessage("released", gate)).ConfigureAwait(false);
        return 0;
    }

    private static async Task WriteJsonLineAsync<T>(T message)
    {
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(message, JsonOptions)).ConfigureAwait(false);
        await Console.Out.FlushAsync().ConfigureAwait(false);
    }

    private static async Task WriteDiagnosticAsync(string message)
    {
        await Console.Error.WriteLineAsync(message).ConfigureAwait(false);
        await Console.Error.FlushAsync().ConfigureAwait(false);
    }

    private sealed record ReadyMessage(string Kind, string Gate, int ProcessId);

    private sealed record GateCommand(string? Command, string? Gate);

    private sealed record ReleasedMessage(string Kind, string Gate);
}
