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

            if (args.Length == 2 && args[0] == "--membership-initialize")
            {
                var request = await ReadInitializationRequestAsync(args[1]).ConfigureAwait(false);
                return await RunInitializeAsync(request).ConfigureAwait(false);
            }

            if (args.Length == 2 && args[0] == "--membership-bind")
            {
                var request = await ReadBindingRequestAsync(args[1]).ConfigureAwait(false);
                return await RunBindAsync(request).ConfigureAwait(false);
            }

            await WriteDiagnosticAsync("Expected --gate <name>, --membership-publish <request.json>, --membership-recover <request.json>, --membership-initialize <request.json>, or --membership-bind <request.json>.")
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

    private static async Task<int> RunInitializeAsync(InitializationProcessRequest request)
    {
        await WriteJsonLineAsync(new StartedMessage("started", request.OperationId, Environment.ProcessId)).ConfigureAwait(false);
        var files = CreateFileSystem();
        using var root = OwnedProcessDirectory.Open(files, request.RootPath);
        var registry = new RootMembershipRegistry(files, new StoreStateSerializer());
        var expectedRoot = new PhysicalRootIdentity(files.InspectHandle(root).Identity);
        var declarations = request.DeclaredMembers
            .Select(member => new RootMemberRecord(member.MemberId, member.ConfiguredLocator,
                new RootMemberRecord.DeclaredBinding()))
            .ToArray();
        RootMembershipEnrollmentPoint? checkpoint = request.Checkpoint is null ? null : ParseRequiredEnrollmentCheckpoint(request.Checkpoint);

        var result = registry.InitializeIncomplete(
            root,
            expectedRoot,
            request.EnrollmentEpoch,
            declarations,
            request.QuiescentCutoverConfirmed,
            CancellationToken.None,
            point => PauseAtInitializationCheckpoint(request, checkpoint, point, registry, root));

        await WriteJsonLineAsync(new OperationResult("initialized", request.OperationId, Environment.ProcessId,
            result.Status.ToString(), result.LedgerDigest, result.PendingStateCommit is not null, null, null)).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> RunBindAsync(BindingProcessRequest request)
    {
        await WriteJsonLineAsync(new StartedMessage("started", request.OperationId, Environment.ProcessId)).ConfigureAwait(false);
        var files = CreateFileSystem();
        using var root = OwnedProcessDirectory.Open(files, request.RootPath);
        var parentPaths = request.MemberLocations.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ParentPath,
            StringComparer.Ordinal);
        using var parents = OpenParents(files, parentPaths);
        var registry = new RootMembershipRegistry(files, new StoreStateSerializer());
        var expectedRoot = new PhysicalRootIdentity(files.InspectHandle(root).Identity);
        var declarations = request.DeclaredMembers
            .Select(member => new RootMemberRecord(member.MemberId, member.ConfiguredLocator,
                new RootMemberRecord.DeclaredBinding()))
            .ToArray();
        var locations = request.MemberLocations.ToDictionary(
            pair => pair.Key,
            pair => (parents.Paths[pair.Key], pair.Value.RequestedBasename),
            StringComparer.Ordinal);
        RootMembershipBindingPoint? checkpoint = request.Checkpoint is null
            ? null
            : ParseRequiredBindingCheckpoint(request.Checkpoint);

        try
        {
            var result = await registry.BindDeclaredMembersAsync(
                root,
                expectedRoot,
                request.EnrollmentEpoch,
                declarations,
                locations,
                request.QuiescentCutoverConfirmed,
                CancellationToken.None,
                checkpoint is null
                    ? null
                    : point => PauseAtBindingCheckpoint(request, checkpoint.Value, point, registry, root))
                .ConfigureAwait(false);

            await WriteJsonLineAsync(new OperationResult("bound", request.OperationId, Environment.ProcessId,
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

    private static void PauseAtInitializationCheckpoint(
        InitializationProcessRequest request,
        RootMembershipEnrollmentPoint? expected,
        RootMembershipEnrollmentPoint actual,
        RootMembershipRegistry registry,
        PhysicalStoreDirectoryHandle root)
    {
        if (actual != expected)
            return;

        var observed = actual == RootMembershipEnrollmentPoint.ControlPublished
            ? registry.ReadCandidate(root)
            : null;
        WriteJsonLineAsync(new InitializationCheckpointMessage(
            "checkpoint",
            request.OperationId,
            Environment.ProcessId,
            actual.ToString(),
            observed?.Status.ToString(),
            observed?.LedgerDigest,
            observed?.Members.Select(member => member.MemberId).ToArray())).GetAwaiter().GetResult();

        WaitForContinueCommand(request.OperationId, "initialization checkpoint", "initialization checkpoint");
    }

    private static void PauseAtBindingCheckpoint(
        BindingProcessRequest request,
        RootMembershipBindingPoint expected,
        RootMembershipBindingPoint actual,
        RootMembershipRegistry registry,
        PhysicalStoreDirectoryHandle root)
    {
        if (actual != expected)
            return;

        var observed = registry.ReadCandidate(root);
        WriteJsonLineAsync(new BindingCheckpointMessage(
            "checkpoint",
            request.OperationId,
            Environment.ProcessId,
            actual.ToString(),
            observed.Status.ToString(),
            observed.LedgerDigest,
            observed.Members.Select(member => member.MemberId).ToArray())).GetAwaiter().GetResult();

        WaitForContinueCommand(request.OperationId, "binding checkpoint", "binding checkpoint");
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

        WaitForContinueCommand(request.OperationId, "checkpoint", "parent checkpoint");
    }

    private static void WaitForContinueCommand(string operationId, string checkpointDescription, string commandDescription)
    {
        var input = Console.In.ReadLine();
        if (input is null)
            throw new EndOfStreamException($"The parent ended the {checkpointDescription} protocol before releasing the child.");

        ReleaseCommand? command;
        try
        {
            command = JsonSerializer.Deserialize<ReleaseCommand>(input, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The {commandDescription} command was malformed.", exception);
        }

        if (command is null || command.Command != "continue" || command.OperationId != operationId)
            throw new InvalidDataException($"The {commandDescription} command did not match the operation.");
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

    private static RootMembershipEnrollmentPoint ParseRequiredEnrollmentCheckpoint(string? value)
    {
        if (!Enum.TryParse<RootMembershipEnrollmentPoint>(value, ignoreCase: false, out var point) ||
            !Enum.IsDefined(point))
        {
            throw new InvalidDataException("The initialization checkpoint is not supported.");
        }

        return point;
    }

    private static RootMembershipBindingPoint ParseRequiredBindingCheckpoint(string? value)
    {
        if (!Enum.TryParse<RootMembershipBindingPoint>(value, ignoreCase: false, out var point) ||
            !Enum.IsDefined(point))
        {
            throw new InvalidDataException("The binding checkpoint is not supported.");
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
        var bytes = await ReadRequestBytesAsync(path).ConfigureAwait(false);
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

    private static async Task<InitializationProcessRequest> ReadInitializationRequestAsync(string path)
    {
        var bytes = await ReadRequestBytesAsync(path).ConfigureAwait(false);
        var request = JsonSerializer.Deserialize<InitializationProcessRequest>(bytes, JsonOptions)
            ?? throw new InvalidDataException("The initialization process request was empty.");
        if (!Guid.TryParseExact(request.OperationId, "N", out _) ||
            string.IsNullOrWhiteSpace(request.RootPath) ||
            !Path.IsPathFullyQualified(request.RootPath) ||
            request.EnrollmentEpoch <= 0 ||
            request.DeclaredMembers is null || request.DeclaredMembers.Length is < 1 or > 16 ||
            request.DeclaredMembers.Any(member => member is null ||
                                                  string.IsNullOrWhiteSpace(member.MemberId) ||
                                                  string.IsNullOrWhiteSpace(member.ConfiguredLocator) ||
                                                  !Path.IsPathFullyQualified(member.ConfiguredLocator)) ||
            request.DeclaredMembers.Select(member => member.MemberId).Distinct(StringComparer.Ordinal).Count() != request.DeclaredMembers.Length)
        {
            throw new InvalidDataException("The initialization process request fields were invalid.");
        }

        if (request.Checkpoint is not null)
            _ = ParseRequiredEnrollmentCheckpoint(request.Checkpoint);
        return request;
    }

    private static async Task<BindingProcessRequest> ReadBindingRequestAsync(string path)
    {
        var bytes = await ReadRequestBytesAsync(path).ConfigureAwait(false);
        var request = JsonSerializer.Deserialize<BindingProcessRequest>(bytes, JsonOptions)
            ?? throw new InvalidDataException("The binding process request was empty.");
        if (!Guid.TryParseExact(request.OperationId, "N", out _) ||
            string.IsNullOrWhiteSpace(request.RootPath) ||
            !Path.IsPathFullyQualified(request.RootPath) ||
            request.EnrollmentEpoch <= 0 ||
            request.DeclaredMembers is null || request.DeclaredMembers.Length is < 1 or > 16 ||
            request.DeclaredMembers.Any(member => member is null ||
                                                  string.IsNullOrWhiteSpace(member.MemberId) ||
                                                  string.IsNullOrWhiteSpace(member.ConfiguredLocator) ||
                                                  !Path.IsPathFullyQualified(member.ConfiguredLocator)) ||
            request.DeclaredMembers.Select(member => member.MemberId).Distinct(StringComparer.Ordinal).Count() != request.DeclaredMembers.Length ||
            request.MemberLocations is null || request.MemberLocations.Count != request.DeclaredMembers.Length ||
            request.MemberLocations.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null ||
                                                string.IsNullOrWhiteSpace(pair.Value.ParentPath) ||
                                                !Path.IsPathFullyQualified(pair.Value.ParentPath) ||
                                                string.IsNullOrWhiteSpace(pair.Value.RequestedBasename)) ||
            !request.DeclaredMembers.Select(member => member.MemberId).ToHashSet(StringComparer.Ordinal)
                .SetEquals(request.MemberLocations.Keys))
        {
            throw new InvalidDataException("The binding process request fields were invalid.");
        }

        if (request.Checkpoint is not null)
            _ = ParseRequiredBindingCheckpoint(request.Checkpoint);
        return request;
    }

    private static async Task<byte[]> ReadRequestBytesAsync(string path)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new InvalidDataException("The process request path must be absolute.");
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is <= 0 or > 32 * 1024)
            throw new InvalidDataException("The process request must be a non-empty file no larger than 32 KiB.");
        return await File.ReadAllBytesAsync(path).ConfigureAwait(false);
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
    private sealed record InitializationMemberRequest(string MemberId, string ConfiguredLocator);
    private sealed record InitializationProcessRequest(string OperationId, string RootPath, long EnrollmentEpoch,
        bool QuiescentCutoverConfirmed, InitializationMemberRequest[] DeclaredMembers, string? Checkpoint);
    private sealed record BindingLocationRequest(string ParentPath, string RequestedBasename);
    private sealed record BindingProcessRequest(string OperationId, string RootPath, long EnrollmentEpoch,
        bool QuiescentCutoverConfirmed, InitializationMemberRequest[] DeclaredMembers,
        Dictionary<string, BindingLocationRequest> MemberLocations, string? Checkpoint);
    private sealed record CheckpointMessage(string Kind, string OperationId, int ProcessId,
        string Point, string MembershipStatus, string LedgerDigest, string? PublicationId,
        string? Resolution, string? StagedIdentity, string? BackupIdentity);
    private sealed record InitializationCheckpointMessage(string Kind, string OperationId, int ProcessId,
        string Point, string? MembershipStatus, string? LedgerDigest, string[]? DeclaredMemberIds);
    private sealed record BindingCheckpointMessage(string Kind, string OperationId, int ProcessId,
        string Point, string MembershipStatus, string LedgerDigest, string[] MemberIds);
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
