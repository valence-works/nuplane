using System.Text.Json;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.State;
using Nuplane.Tests.Shared;

namespace Nuplane.PackageStore.TestHost;

internal static class Program
{
    private const int InvalidProtocolExitCode = 3;
    private const int PrematureEofExitCode = 2;
    private const int UnexpectedOperationExitCode = 1;
    private const string ZeroDigest = "0000000000000000000000000000000000000000000000000000000000000000";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0] == "--gate" && !string.IsNullOrWhiteSpace(args[1]))
                return await RunGateAsync(args[1]).ConfigureAwait(false);

            if (args.Length == 2 && args[0] is "--membership-publish" or "--membership-recover")
            {
                var request = await ReadRequestAsync(args[1]).ConfigureAwait(false);
                return args[0] == "--membership-publish"
                    ? await RunPublishAsync(request).ConfigureAwait(false)
                    : await RunRecoverAsync(request).ConfigureAwait(false);
            }

            await WriteDiagnosticAsync("Expected --gate <name>, --membership-publish <request.json>, or --membership-recover <request.json>.")
                .ConfigureAwait(false);
            return InvalidProtocolExitCode;
        }
        catch (Exception exception)
        {
            await WriteDiagnosticAsync(exception.ToString()).ConfigureAwait(false);
            return UnexpectedOperationExitCode;
        }
    }

    private static async Task<int> RunGateAsync(string gate)
    {
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

    private static async Task<int> RunPublishAsync(MembershipProcessRequest request)
    {
        await WriteJsonLineAsync(new StartedMessage("started", request.OperationId, Environment.ProcessId)).ConfigureAwait(false);
        var files = CreateFileSystem();
        using var root = OwnedProcessDirectory.Open(files, request.RootPath);
        using var parents = OpenParents(files, request.MemberParentPaths);
        var serializer = new StoreStateSerializer();
        var registry = new RootMembershipRegistry(files, serializer);
        var ledger = registry.ReadCandidate(root);
        var member = ledger.Members.Single(candidate => candidate.MemberId == request.TargetMemberId);
        var revision = member.Binding switch
        {
            RootMemberRecord.ProspectiveBinding => 1,
            RootMemberRecord.ExistingUnprotectedBinding => 1,
            RootMemberRecord.AcknowledgedBinding acknowledged => checked(acknowledged.ProtectionRecord.Revision + 1),
            _ => throw new InvalidDataException("The target member binding is not publishable.")
        };
        var body = StoreStateRecord.Empty() with { UpdatedAt = DateTimeOffset.UnixEpoch.AddDays(request.NextStateDay) };
        var next = Protect(body, ledger.RootIdentity, ledger.EnrollmentEpoch, member.MemberId, revision);
        var checkpoint = ParseRequiredCheckpoint(request.Checkpoint);

        var result = await registry.PublishStateAsync(root, parents.Paths, member.MemberId, next, CancellationToken.None,
            point => PauseAtRequestedCheckpoint(request, checkpoint, point, registry, root))
            .ConfigureAwait(false);

        await WriteJsonLineAsync(new OperationResult("published", request.OperationId, Environment.ProcessId,
            result.Status.ToString(), result.LedgerDigest, result.PendingStateCommit is not null, null, null))
            .ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> RunRecoverAsync(MembershipProcessRequest request)
    {
        await WriteJsonLineAsync(new StartedMessage("started", request.OperationId, Environment.ProcessId)).ConfigureAwait(false);
        var files = CreateFileSystem();
        using var root = OwnedProcessDirectory.Open(files, request.RootPath);
        using var parents = OpenParents(files, request.MemberParentPaths);
        var registry = new RootMembershipRegistry(files, new StoreStateSerializer());
        RootMembershipPublicationPoint? checkpoint = request.Checkpoint is null ? null : ParseRequiredCheckpoint(request.Checkpoint);

        try
        {
            var result = await registry.RecoverAsync(root, parents.Paths, CancellationToken.None,
                checkpoint is null ? null : point => PauseAtRequestedCheckpoint(request, checkpoint.Value, point, registry, root))
                .ConfigureAwait(false);
            await WriteJsonLineAsync(new OperationResult("recovered", request.OperationId, Environment.ProcessId,
                result.Status.ToString(), result.LedgerDigest, result.PendingStateCommit is not null, null, null))
                .ConfigureAwait(false);
            return 0;
        }
        catch (PackageStoreAdmissionException exception)
        {
            await WriteJsonLineAsync(new OperationResult("refused", request.OperationId, Environment.ProcessId,
                null, null, null, exception.Reason.ToString(), exception.Message)).ConfigureAwait(false);
            return 0;
        }
    }

    private static void PauseAtRequestedCheckpoint(MembershipProcessRequest request,
        RootMembershipPublicationPoint expected, RootMembershipPublicationPoint actual,
        RootMembershipRegistry registry, PhysicalStoreDirectoryHandle root)
    {
        if (actual != expected)
            return;

        var ledger = registry.ReadCandidate(root);
        var pending = ledger.PendingStateCommit;
        WriteJsonLineAsync(new CheckpointMessage("checkpoint", request.OperationId, Environment.ProcessId,
            actual.ToString(), ledger.Status.ToString(), ledger.LedgerDigest, pending?.PublicationId.ToString("N"),
            pending?.Resolution.ToString(), pending?.StagedStateFileIdentity?.FileId,
            pending?.BackupStateFileIdentity?.FileId)).GetAwaiter().GetResult();

        var input = Console.In.ReadLine();
        if (input is null)
            throw new EndOfStreamException("The parent ended the checkpoint protocol before releasing the child.");

        ReleaseCommand? command;
        try
        {
            command = JsonSerializer.Deserialize<ReleaseCommand>(input, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The parent checkpoint command was malformed.", exception);
        }

        if (command is null || command.Command != "continue" || command.OperationId != request.OperationId)
            throw new InvalidDataException("The parent checkpoint command did not match the operation.");
    }

    private static RootMembershipPublicationPoint ParseRequiredCheckpoint(string? value)
    {
        if (!Enum.TryParse<RootMembershipPublicationPoint>(value, ignoreCase: false, out var point) ||
            !Enum.IsDefined(point))
        {
            throw new InvalidDataException("The operation checkpoint is not supported.");
        }

        return point;
    }

    private static StoreStateRecord Protect(StoreStateRecord body, PhysicalRootIdentity root,
        long epoch, string memberId, long revision)
    {
        var knownEmpty = new PackageProtectionClosure(PackageProtectionClosureKnowledge.Known, null, []);
        var candidate = new PackageProtectionRecord(PackageProtectionRecord.CurrentSchemaVersion, root, epoch,
            memberId, revision, ProtectionDigest.StateBody(body), ZeroDigest, knownEmpty, knownEmpty, [], false);
        var protection = new PackageProtectionRecord(PackageProtectionRecord.CurrentSchemaVersion, root, epoch,
            memberId, revision, candidate.StateBodyDigest, ProtectionDigest.Protection(candidate), knownEmpty, knownEmpty, [], false);
        return body with { ProtectionRecord = protection };
    }

    private static HeldParents OpenParents(
        IPhysicalStoreFileSystem files, IReadOnlyDictionary<string, string> paths)
    {
        var parents = new Dictionary<string, PhysicalStoreDirectoryHandle>(StringComparer.Ordinal);
        try
        {
            foreach (var pair in paths)
                parents.Add(pair.Key, OwnedProcessDirectory.Open(files, pair.Value));
            return new HeldParents(parents);
        }
        catch
        {
            foreach (var parent in parents.Values.Reverse())
                parent.Dispose();
            throw;
        }
    }

    private static IPhysicalStoreFileSystem CreateFileSystem()
        => OperatingSystem.IsWindows() ? new WindowsPhysicalStoreFileSystem() : new UnixPhysicalStoreFileSystem();

    private static async Task<MembershipProcessRequest> ReadRequestAsync(string path)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new InvalidDataException("The process request path must be absolute.");
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is <= 0 or > 32 * 1024)
            throw new InvalidDataException("The process request must be a non-empty file no larger than 32 KiB.");
        var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        var request = JsonSerializer.Deserialize<MembershipProcessRequest>(bytes, JsonOptions)
            ?? throw new InvalidDataException("The process request was empty.");
        if (!Guid.TryParseExact(request.OperationId, "N", out _) ||
            string.IsNullOrWhiteSpace(request.TargetMemberId) ||
            string.IsNullOrWhiteSpace(request.RootPath) ||
            !Path.IsPathFullyQualified(request.RootPath) ||
            request.MemberParentPaths is null || request.MemberParentPaths.Count is < 1 or > 16 ||
            !request.MemberParentPaths.ContainsKey(request.TargetMemberId) ||
            request.MemberParentPaths.Any(pair => string.IsNullOrWhiteSpace(pair.Key) ||
                                                  string.IsNullOrWhiteSpace(pair.Value) ||
                                                  !Path.IsPathFullyQualified(pair.Value)) ||
            request.NextStateDay is < 1 or > 1000)
        {
            throw new InvalidDataException("The process request fields were invalid.");
        }

        if (request.Checkpoint is not null)
            _ = ParseRequiredCheckpoint(request.Checkpoint);
        return request;
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
    private sealed record ReleaseCommand(string? Command, string? OperationId);
    private sealed record StartedMessage(string Kind, string OperationId, int ProcessId);
    private sealed record MembershipProcessRequest(string OperationId, string RootPath,
        string TargetMemberId, Dictionary<string, string> MemberParentPaths,
        string? Checkpoint, int NextStateDay);
    private sealed record CheckpointMessage(string Kind, string OperationId, int ProcessId,
        string Point, string MembershipStatus, string LedgerDigest, string? PublicationId,
        string? Resolution, string? StagedIdentity, string? BackupIdentity);
    private sealed record OperationResult(string Kind, string OperationId, int ProcessId,
        string? MembershipStatus, string? LedgerDigest, bool? HasPending, string? RefusalReason, string? Message);

    private sealed class HeldParents(Dictionary<string, PhysicalStoreDirectoryHandle> paths) : IDisposable
    {
        internal IReadOnlyDictionary<string, PhysicalStoreDirectoryHandle> Paths { get; } = paths;

        public void Dispose()
        {
            foreach (var parent in Paths.Values.Reverse())
                parent.Dispose();
        }
    }
}
