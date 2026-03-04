using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Runtime.Configuration;
using Nuplane.Store.State;

namespace Nuplane.Extensions;

internal sealed class ConvergenceOptionsValidator : IValidateOptions<ConvergenceOptions>
{
    public ValidateOptionsResult Validate(string? name, ConvergenceOptions options)
    {
        var errors = new List<string>();

        if (options.PollInterval <= TimeSpan.Zero)
        {
            errors.Add("Convergence PollInterval must be greater than zero.");
        }

        if (options.Retry.MaxAttempts < 0)
        {
            errors.Add("Convergence Retry.MaxAttempts must be greater than or equal to zero.");
        }

        if (options.Retry.InitialBackoff <= TimeSpan.Zero)
        {
            errors.Add("Convergence Retry.InitialBackoff must be greater than zero.");
        }

        if (options.Retry.MaxBackoff < options.Retry.InitialBackoff)
        {
            errors.Add("Convergence Retry.MaxBackoff must be greater than or equal to Retry.InitialBackoff.");
        }

        if (options.Manifest.Enabled && string.IsNullOrWhiteSpace(options.Manifest.Path))
        {
            errors.Add("Convergence Manifest.Path must be provided when Manifest.Enabled is true.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

internal sealed class ReconciliationOptionsValidator : IValidateOptions<ReconciliationOptions>
{
    public ValidateOptionsResult Validate(string? name, ReconciliationOptions options)
    {
        var errors = new List<string>();

        if (options.PollInterval <= TimeSpan.Zero)
        {
            errors.Add("Reconciliation PollInterval must be greater than zero.");
        }

        if (options.MaxRetryAttempts < 0)
        {
            errors.Add("Reconciliation MaxRetryAttempts must be greater than or equal to zero.");
        }

        if (options.InitialRetryBackoff <= TimeSpan.Zero)
        {
            errors.Add("Reconciliation InitialRetryBackoff must be greater than zero.");
        }

        if (options.MaxRetryBackoff < options.InitialRetryBackoff)
        {
            errors.Add("Reconciliation MaxRetryBackoff must be greater than or equal to InitialRetryBackoff.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

internal sealed class FeedResolutionOptionsValidator : IValidateOptions<FeedResolutionOptions>
{
    public ValidateOptionsResult Validate(string? name, FeedResolutionOptions options)
    {
        if (options.Feeds.Count > 0 && options.ValidateDeterministicOrdering && !options.DeterministicFeedOrder)
        {
            return ValidateOptionsResult.Fail("Deterministic feed ordering validation is enabled, but DeterministicFeedOrder is false.");
        }

        return ValidateOptionsResult.Success;
    }
}

internal sealed class FeedTrustPolicyOptionsValidator : IValidateOptions<FeedTrustPolicyOptions>
{
    public ValidateOptionsResult Validate(string? name, FeedTrustPolicyOptions options)
    {
        if (!options.RequireOverrideReason)
        {
            return ValidateOptionsResult.Success;
        }

        var errors = options.Overrides
            .Where(x => x.Scope != Nuplane.Abstractions.FeedOverrideScope.None && string.IsNullOrWhiteSpace(x.Reason))
            .Select(x => $"Override reason is required for override target '{x.Target}'.")
            .ToArray();

        return errors.Length == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

internal sealed class LockFileOptionsValidator : IValidateOptions<LockFileOptions>
{
    public ValidateOptionsResult Validate(string? name, LockFileOptions options)
    {
        return string.IsNullOrWhiteSpace(options.Path)
            ? ValidateOptionsResult.Fail("Lock file path must be provided.")
            : ValidateOptionsResult.Success;
    }
}

internal sealed class CleanupPolicyOptionsValidator : IValidateOptions<CleanupPolicyOptions>
{
    public ValidateOptionsResult Validate(string? name, CleanupPolicyOptions options)
    {
        var errors = new List<string>();

        if (options.RetainLastNVersions.HasValue && options.RetainLastNVersions.Value < 0)
        {
            errors.Add("Cleanup RetainLastNVersions must be greater than or equal to zero.");
        }

        if (options.RetainYoungerThanDays.HasValue && options.RetainYoungerThanDays.Value < 0)
        {
            errors.Add("Cleanup RetainYoungerThanDays must be greater than or equal to zero.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

internal sealed class FeedCredentialCompositeValidator(
    FeedCredentialOptionsValidator credentialValidator,
    IOptions<FeedTrustPolicyOptions> trustPolicyOptions,
    IOptions<SourceTrustOptions> sourceTrustOptions)
    : IValidateOptions<FeedResolutionOptions>
{
    private readonly FeedCredentialOptionsValidator credentialValidator = credentialValidator;
    private readonly IOptions<FeedTrustPolicyOptions> trustPolicyOptions = trustPolicyOptions;
    private readonly IOptions<SourceTrustOptions> sourceTrustOptions = sourceTrustOptions;

    public ValidateOptionsResult Validate(string? name, FeedResolutionOptions options)
    {
        var errors = credentialValidator.Validate(options, trustPolicyOptions.Value, sourceTrustOptions.Value);
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

internal sealed class TrustedSourcePolicyOptionsValidator : IValidateOptions<TrustedSourcePolicyOptions>
{
    public ValidateOptionsResult Validate(string? name, TrustedSourcePolicyOptions options)
    {
        var errors = new List<string>();

        if (options.Enabled && options.TrustedSourceNames.Count == 0)
        {
            errors.Add("TrustedSourcePolicy is enabled but no trusted source names are configured. All sources will be rejected.");
        }

        if (options.AllowSecretReferences && options.RejectInlineCredentials == false)
        {
            // Not an error, but both allowing secrets and inline credentials is a weak security posture.
            // We still allow it — validation only enforces hard invariants.
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

