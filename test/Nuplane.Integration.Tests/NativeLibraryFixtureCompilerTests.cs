using System.Text;

namespace Nuplane.Integration.Tests;

public sealed class NativeLibraryFixtureCompilerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParseMsvcEnvironmentOutput_DecodesUtf16AndPreservesUnicodeValues(bool includeByteOrderMark)
    {
        const string path = "C:\\Program Files\\Visual Studio\\bin;D:\\café\\tools";
        const string include = "C:\\Program Files\\Visual Studio\\include";
        const string library = "C:\\Program Files\\Visual Studio\\lib";
        var output = Encoding.Unicode.GetBytes(
            $"=C:=C:\\work\r\nPATH={path}\r\nINCLUDE={include}\r\nLIB={library}\r\nCUSTOM=first=second\r\n");
        if (includeByteOrderMark)
            output = [0xFF, 0xFE, .. output];

        var environment = NativeLibraryFixtureCompiler.ParseMsvcEnvironmentOutput(output);

        Assert.Equal(path, environment["PATH"]);
        Assert.Equal(include, environment["INCLUDE"]);
        Assert.Equal(library, environment["LIB"]);
        Assert.Equal("first=second", environment["CUSTOM"]);
        Assert.DoesNotContain("=C:", environment.Keys);
    }

    [Fact]
    public void ParseMsvcEnvironmentOutput_RejectsMalformedEncoding()
    {
        var oddLength = Assert.Throws<Xunit.Sdk.XunitException>(() =>
            NativeLibraryFixtureCompiler.ParseMsvcEnvironmentOutput([0x41]));
        Assert.Contains("UTF-16", oddLength.Message, StringComparison.Ordinal);

        var invalidSurrogate = Assert.Throws<Xunit.Sdk.XunitException>(() =>
            NativeLibraryFixtureCompiler.ParseMsvcEnvironmentOutput([0x00, 0xD8]));
        Assert.Contains("invalid UTF-16", invalidSurrogate.Message, StringComparison.Ordinal);

        var bigEndian = Assert.Throws<Xunit.Sdk.XunitException>(() =>
            NativeLibraryFixtureCompiler.ParseMsvcEnvironmentOutput([0xFE, 0xFF, 0x00, 0x41]));
        Assert.Contains("little-endian", bigEndian.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseMsvcEnvironmentOutput_RejectsMissingRequiredVariablesAndDuplicateNames()
    {
        var missingInclude = Assert.Throws<Xunit.Sdk.XunitException>(() =>
            NativeLibraryFixtureCompiler.ParseMsvcEnvironmentOutput(
                Encoding.Unicode.GetBytes("PATH=C:\\tools\\bin\r\n")));
        Assert.Contains("INCLUDE", missingInclude.Message, StringComparison.Ordinal);

        var duplicatePath = Assert.Throws<Xunit.Sdk.XunitException>(() =>
            NativeLibraryFixtureCompiler.ParseMsvcEnvironmentOutput(Encoding.Unicode.GetBytes(
                "PATH=C:\\first\r\npath=C:\\second\r\nINCLUDE=C:\\include\r\nLIB=C:\\lib\r\n")));
        Assert.Contains("duplicate variable 'path'", duplicatePath.Message, StringComparison.Ordinal);
    }
}
