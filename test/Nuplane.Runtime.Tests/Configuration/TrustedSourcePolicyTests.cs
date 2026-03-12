using Nuplane.Abstractions;
using Nuplane.Trust.Source;
using Nuplane.Trust.Validation;

namespace Nuplane.Runtime.Tests.Configuration;

public sealed class TrustedSourcePolicyTests
{
    private readonly TrustedSourcePolicyEvaluator _evaluator = new();
    private readonly TrustedSourcePolicyOptionsValidator _validator = new();

    // ── Evaluator: Source trust ───────────────────────────────────────

    [Fact]
    public void Evaluate_PolicyDisabled_AllowsAnySource()
    {
        var opts = new TrustedSourcePolicyOptions { Enabled = false };

        var result = _evaluator.Evaluate("untrusted-source", opts);

        Assert.True(result.IsAllowed);
        Assert.Equal("policy.disabled", result.ReasonCode);
    }

    [Fact]
    public void Evaluate_PolicyEnabled_TrustedSource_Allowed()
    {
        var opts = new TrustedSourcePolicyOptions { Enabled = true };
        opts.TrustedSourceNames.Add("src-a");
        opts.TrustedSourceNames.Add("src-b");

        var result = _evaluator.Evaluate("src-a", opts);

        Assert.True(result.IsAllowed);
        Assert.Equal("policy.trusted", result.ReasonCode);
    }

    [Fact]
    public void Evaluate_PolicyEnabled_UntrustedSource_Rejected()
    {
        var opts = new TrustedSourcePolicyOptions { Enabled = true };
        opts.TrustedSourceNames.Add("src-a");

        var result = _evaluator.Evaluate("src-b", opts);

        Assert.False(result.IsAllowed);
        Assert.Equal("policy.untrusted", result.ReasonCode);
    }

    [Fact]
    public void Evaluate_PolicyEnabled_NoTrustedNames_RejectsAll()
    {
        var opts = new TrustedSourcePolicyOptions { Enabled = true };

        var result = _evaluator.Evaluate("any-source", opts);

        Assert.False(result.IsAllowed);
        Assert.Equal("policy.no_trusted_sources", result.ReasonCode);
    }

    [Fact]
    public void Evaluate_NullSourceName_Throws()
    {
        var opts = new TrustedSourcePolicyOptions { Enabled = true };

        Assert.ThrowsAny<ArgumentException>(() => _evaluator.Evaluate(null!, opts));
    }

    [Fact]
    public void Evaluate_EmptySourceName_Throws()
    {
        var opts = new TrustedSourcePolicyOptions { Enabled = true };

        Assert.Throws<ArgumentException>(() => _evaluator.Evaluate("", opts));
    }

    // ── Evaluator: Credential policy ─────────────────────────────────

    [Fact]
    public void EvaluateCredential_NullCredential_Allowed()
    {
        var opts = new TrustedSourcePolicyOptions();

        var result = _evaluator.EvaluateCredential(null, opts);

        Assert.True(result.IsAllowed);
        Assert.Equal("credential.none", result.ReasonCode);
    }

    [Fact]
    public void EvaluateCredential_EmptyCredential_Allowed()
    {
        var opts = new TrustedSourcePolicyOptions();

        var result = _evaluator.EvaluateCredential("", opts);

        Assert.True(result.IsAllowed);
        Assert.Equal("credential.none", result.ReasonCode);
    }

    [Fact]
    public void EvaluateCredential_SecretReference_AllowedWhenEnabled()
    {
        var opts = new TrustedSourcePolicyOptions { AllowSecretReferences = true };

        var result = _evaluator.EvaluateCredential("secrets://vault/key", opts);

        Assert.True(result.IsAllowed);
        Assert.Equal("credential.secret_reference_allowed", result.ReasonCode);
    }

    [Fact]
    public void EvaluateCredential_SecretReference_RejectedWhenDisabled()
    {
        var opts = new TrustedSourcePolicyOptions { AllowSecretReferences = false };

        var result = _evaluator.EvaluateCredential("secrets://vault/key", opts);

        Assert.False(result.IsAllowed);
        Assert.Equal("credential.secret_reference_rejected", result.ReasonCode);
    }

    [Fact]
    public void EvaluateCredential_InlineCredential_RejectedWhenConfigured()
    {
        var opts = new TrustedSourcePolicyOptions { RejectInlineCredentials = true };

        var result = _evaluator.EvaluateCredential("plain-password", opts);

        Assert.False(result.IsAllowed);
        Assert.Equal("credential.inline_rejected", result.ReasonCode);
    }

    [Fact]
    public void EvaluateCredential_InlineCredential_AllowedWhenNotRejected()
    {
        var opts = new TrustedSourcePolicyOptions { RejectInlineCredentials = false };

        var result = _evaluator.EvaluateCredential("plain-password", opts);

        Assert.True(result.IsAllowed);
        Assert.Equal("credential.inline_allowed", result.ReasonCode);
    }

    // ── Validator ────────────────────────────────────────────────────

    [Fact]
    public void Validate_Disabled_Succeeds()
    {
        var opts = new TrustedSourcePolicyOptions { Enabled = false };

        var result = _validator.Validate(null, opts);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_EnabledWithNames_Succeeds()
    {
        var opts = new TrustedSourcePolicyOptions { Enabled = true };
        opts.TrustedSourceNames.Add("src-a");

        var result = _validator.Validate(null, opts);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_EnabledWithoutNames_Fails()
    {
        var opts = new TrustedSourcePolicyOptions { Enabled = true };

        var result = _validator.Validate(null, opts);

        Assert.True(result.Failed);
        Assert.Contains("no trusted source names", result.FailureMessage);
    }
}
