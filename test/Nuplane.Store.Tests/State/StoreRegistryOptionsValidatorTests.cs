using Nuplane.Store.State;

namespace Nuplane.Store.Tests.State;

public sealed class StoreRegistryOptionsValidatorTests
{
    private readonly StoreRegistryOptionsValidator _sut = new();

    [Fact]
    public void Validate_DefaultOptions_Succeeds()
    {
        var result = _sut.Validate(null, new StoreRegistryOptions());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_ValidStateFilePath_Succeeds()
    {
        var result = _sut.Validate(null, new StoreRegistryOptions { StateFilePath = "./state.json" });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_BlankStateFilePath_Fails()
    {
        var result = _sut.Validate(null, new StoreRegistryOptions { StateFilePath = "  " });

        Assert.True(result.Failed);
        Assert.Contains("StateFilePath cannot be blank", result.FailureMessage);
    }

    [Fact]
    public void Validate_EmptyStringStateFilePath_Fails()
    {
        var result = _sut.Validate(null, new StoreRegistryOptions { StateFilePath = "" });

        Assert.True(result.Failed);
        Assert.Contains("StateFilePath cannot be blank", result.FailureMessage);
    }

    [Fact]
    public void Validate_UseInMemoryStoreOnly_Succeeds()
    {
        var result = _sut.Validate(null, new StoreRegistryOptions { UseInMemoryStore = true });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_UseInMemoryStoreWithStateFilePath_Fails()
    {
        var result = _sut.Validate(null, new StoreRegistryOptions
        {
            UseInMemoryStore = true,
            StateFilePath = "./state.json"
        });

        Assert.True(result.Failed);
        Assert.Contains("UseInMemoryStore cannot be combined", result.FailureMessage);
    }

    [Fact]
    public void Validate_UseInMemoryStoreWithNullPath_Succeeds()
    {
        var result = _sut.Validate(null, new StoreRegistryOptions
        {
            UseInMemoryStore = true,
            StateFilePath = null
        });

        Assert.True(result.Succeeded);
    }
}
