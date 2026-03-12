using Microsoft.Extensions.Options;

namespace Nuplane.Loading.Extensions;

/// <summary>
/// Adapts <see cref="LoadingOptionsValidator"/> to the <see cref="IValidateOptions{TOptions}"/>
/// interface required by the .NET Options validation infrastructure.
/// </summary>
/// <param name="validator">The loading options validator to delegate to.</param>
public sealed class LoadingOptionsValidation(LoadingOptionsValidator validator) : IValidateOptions<LoadingOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, LoadingOptions options)
    {
        var errors = validator.Validate(options);
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
