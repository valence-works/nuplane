using Microsoft.Extensions.Options;

namespace Nuplane.Loading.Extensions;

internal sealed class LoadingOptionsValidation(LoadingOptionsValidator validator) : IValidateOptions<LoadingOptions>
{
    private readonly LoadingOptionsValidator _validator = validator;

    public ValidateOptionsResult Validate(string? name, LoadingOptions options)
    {
        var errors = _validator.Validate(options);
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

