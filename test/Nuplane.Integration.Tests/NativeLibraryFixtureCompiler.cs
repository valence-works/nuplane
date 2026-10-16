using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Nuplane.Integration.Tests;

/// <summary>Compiles the tiny package-owned native export used by the deferred-load lifetime proof.</summary>
internal static class NativeLibraryFixtureCompiler
{
    internal const string LibraryName = "NuplaneDeferredNativeProbe";
    internal const string ExportName = "NuplaneDeferredNativeProbe";
    internal const string MethodName = "InvokeDeferredNativeProbe";
    internal const int InputValue = 137;
    internal const int ExpectedValue = (InputValue * 37) + 0x51A3;

    private static readonly TimeSpan CompilerTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ProcessExitGracePeriod = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromSeconds(5);
    private const string Source = """
        #if defined(_WIN32)
        #define NUPLANE_EXPORT __declspec(dllexport)
        #define NUPLANE_CALL __cdecl
        #else
        #define NUPLANE_EXPORT __attribute__((visibility("default")))
        #define NUPLANE_CALL
        #endif

        NUPLANE_EXPORT int NUPLANE_CALL NuplaneDeferredNativeProbe(int value)
        {
            return (value * 37) + 0x51A3;
        }
        """;

    internal static string CompileInto(string nativeDirectory)
    {
        Directory.CreateDirectory(nativeDirectory);
        var output = Path.Combine(nativeDirectory, OutputFileName());
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "nuplane-t086-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var sourcePath = Path.Combine(temporaryDirectory, "nuplane_deferred_native_probe.c");
            File.WriteAllText(sourcePath, Source, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (OperatingSystem.IsWindows())
                CompileWithMsvc(sourcePath, output, temporaryDirectory);
            else if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
                CompileWithPosixCompiler(sourcePath, output, temporaryDirectory);
            else
                throw new PlatformNotSupportedException(
                    $"The T086 native-library lifetime fixture has no compiler setup for {RuntimeInformation.OSDescription}.");

            if (!File.Exists(output) || new FileInfo(output).Length == 0)
                throw new Xunit.Sdk.XunitException($"Native compiler returned success without creating '{output}'.");
            return output;
        }
        finally
        {
            try { Directory.Delete(temporaryDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void CompileWithPosixCompiler(string sourcePath, string output, string workingDirectory)
    {
        var compiler = FindCompiler();
        var machine = Run(compiler, ["-dumpmachine"], workingDirectory);
        EnsureCompilerArchitecture(compiler, machine.StandardOutput + machine.StandardError);

        var arguments = new List<string> { "-O2", "-fPIC" };
        if (OperatingSystem.IsMacOS())
        {
            arguments.Add("-dynamiclib");
            arguments.Add("-arch");
            arguments.Add(RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "arm64",
                Architecture.X64 => "x86_64",
                var architecture => throw new PlatformNotSupportedException(
                    $"Native fixture compilation is unsupported for macOS process architecture {architecture}.")
            });
        }
        else
        {
            arguments.Add("-shared");
        }

        arguments.Add(sourcePath);
        arguments.Add("-o");
        arguments.Add(output);
        Run(compiler, arguments, workingDirectory);
    }

    private static string FindCompiler()
    {
        var candidates = new[] { Environment.GetEnvironmentVariable("CC"), "clang", "cc", "gcc" }
            .Where(static candidate => !string.IsNullOrWhiteSpace(candidate))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (var candidate in candidates)
        {
            if (Path.IsPathRooted(candidate!) && File.Exists(candidate))
                return candidate!;
            if (FindOnPath(candidate!) is { } found)
                return found;
        }

        throw new Xunit.Sdk.XunitException(
            $"Could not find a POSIX C compiler for the T086 native-library lifetime proof. Checked: {string.Join(", ", candidates)}.");
    }

    private static void EnsureCompilerArchitecture(string compiler, string machineOutput)
    {
        var machine = machineOutput.Trim();
        var expected = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => new[] { "aarch64", "arm64" },
            Architecture.X64 => new[] { "x86_64", "amd64" },
            Architecture.X86 => new[] { "i386", "i486", "i586", "i686" },
            _ => throw new PlatformNotSupportedException(
                $"Native fixture compilation is unsupported for process architecture {RuntimeInformation.ProcessArchitecture}.")
        };
        if (!expected.Any(token => machine.Contains(token, StringComparison.OrdinalIgnoreCase)))
        {
            throw new Xunit.Sdk.XunitException(
                $"C compiler '{compiler}' reports target '{machine}', which does not match executing process architecture " +
                $"'{RuntimeInformation.ProcessArchitecture}'. Refusing to compile a mismatched native fixture.");
        }
    }

    private static void CompileWithMsvc(string sourcePath, string output, string workingDirectory)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException(
                $"The Windows native fixture is configured for the current x64 runner; process architecture is {RuntimeInformation.ProcessArchitecture}.");

        var vswhere = LocateVsWhere();
        var installation = Run(vswhere,
            ["-latest", "-products", "*", "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64", "-property", "installationPath"],
            workingDirectory).StandardOutput.Trim();
        if (string.IsNullOrWhiteSpace(installation) || !Directory.Exists(installation))
            throw new Xunit.Sdk.XunitException(
                $"vswhere '{vswhere}' did not find a Visual Studio installation with the x64 C++ tools required by the T086 native proof.");

        var vsDevCmd = Path.Combine(installation, "Common7", "Tools", "VsDevCmd.bat");
        if (!File.Exists(vsDevCmd))
            throw new Xunit.Sdk.XunitException($"Visual Studio developer environment script was not found: '{vsDevCmd}'.");

        var commandInterpreter = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        const string environmentFileName = "msvc-environment.txt";
        const string setupLogFileName = "msvc-environment-setup.log";
        File.WriteAllText(Path.Combine(workingDirectory, "initialize-vs-environment.cmd"),
            "@echo off\r\nchcp 65001 >nul\r\n" +
            $"call \"%NUPLANE_VSDEVCMD_PATH%\" -arch=x64 -host_arch=x64 > \"{setupLogFileName}\" 2>&1\r\n" +
            "set \"NUPLANE_VSDEVCMD_EXIT=%ERRORLEVEL%\"\r\n" +
            "chcp 65001 >nul\r\n" +
            "if not \"%NUPLANE_VSDEVCMD_EXIT%\"==\"0\" exit /b %NUPLANE_VSDEVCMD_EXIT%\r\n" +
            "set \"NUPLANE_VSDEVCMD_EXIT=\"\r\n" +
            "set \"NUPLANE_VSDEVCMD_PATH=\"\r\n" +
            $"set > \"{environmentFileName}\"\r\n",
            Encoding.ASCII);
        var environmentScript = Path.Combine(workingDirectory, "initialize-vs-environment.cmd");
        var setupLog = Path.Combine(workingDirectory, setupLogFileName);
        var exitCode = RunWithoutCapturedOutput(commandInterpreter,
            ["/u", "/d", "/e:on", "/v:off", "/c", Path.GetFileName(environmentScript)], workingDirectory, setupLog,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["NUPLANE_VSDEVCMD_PATH"] = vsDevCmd
            });
        if (exitCode != 0)
        {
            var diagnostic = ReadMsvcDiagnostic(setupLog);
            throw new Xunit.Sdk.XunitException(
                $"VsDevCmd environment setup exited with code {exitCode}. {diagnostic}");
        }

        var environmentPath = Path.Combine(workingDirectory, environmentFileName);
        if (!File.Exists(environmentPath))
            throw new Xunit.Sdk.XunitException(
                $"VsDevCmd exited successfully without writing its environment file '{environmentPath}'.");
        var environment = ParseMsvcEnvironmentOutput(File.ReadAllBytes(environmentPath));
        var compiler = FindOnPath("cl.exe", environment["PATH"]);
        if (compiler is null)
            throw new Xunit.Sdk.XunitException("VsDevCmd's initialized PATH does not contain cl.exe.");
        var arguments = new[] { "/nologo", "/LD", "/O2", "/TC", sourcePath, "/link", $"/OUT:{output}" };
        Run(compiler, arguments, workingDirectory, environment);
    }

    internal static Dictionary<string, string> ParseMsvcEnvironmentOutput(byte[] output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (output.Length == 0 || output.Length % 2 != 0)
            throw new Xunit.Sdk.XunitException("VsDevCmd environment output must be nonempty UTF-16 little-endian text.");
        if (output.Length >= 2 && output[0] == 0xFE && output[1] == 0xFF)
            throw new Xunit.Sdk.XunitException("VsDevCmd environment output must use UTF-16 little-endian encoding.");

        var offset = output.Length >= 2 && output[0] == 0xFF && output[1] == 0xFE ? 2 : 0;
        string text;
        try
        {
            text = new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true)
                .GetString(output, offset, output.Length - offset);
        }
        catch (DecoderFallbackException exception)
        {
            throw new Xunit.Sdk.XunitException($"VsDevCmd environment output is invalid UTF-16: {exception.Message}");
        }

        if (text.Contains('\0'))
            throw new Xunit.Sdk.XunitException("VsDevCmd environment output contains an unexpected NUL character.");

        return ParseEnvironment(text);
    }

    internal static Dictionary<string, string> ParseEnvironment(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf('=');
            if (line[0] == '=')
                continue; // cmd.exe's drive-current-directory pseudo variables are not process environment entries.
            if (separator <= 0 || string.IsNullOrWhiteSpace(line[..separator]))
                throw new Xunit.Sdk.XunitException("VsDevCmd environment output contains a malformed variable line.");
            if (!environment.TryAdd(line[..separator], line[(separator + 1)..]))
                throw new Xunit.Sdk.XunitException(
                    $"VsDevCmd environment output contains duplicate variable '{line[..separator]}'.");
        }

        foreach (var requiredName in new[] { "PATH", "INCLUDE", "LIB" })
        {
            if (!environment.TryGetValue(requiredName, out var value) || string.IsNullOrWhiteSpace(value))
                throw new Xunit.Sdk.XunitException(
                    $"VsDevCmd environment output is missing required nonempty variable '{requiredName}'.");
        }

        return environment;
    }

    private static string ReadMsvcDiagnostic(string path)
    {
        if (!File.Exists(path))
            return $"No setup log was written at '{path}'.";

        var bytes = File.ReadAllBytes(path);
        if (bytes.Length == 0)
            return $"The setup log '{path}' was empty.";

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2).Trim();
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2).Trim();
        if (LooksLikeUtf16LittleEndian(bytes))
            return Encoding.Unicode.GetString(bytes).Trim();

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes).Trim();
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes).Trim();
        }
    }

    private static bool LooksLikeUtf16LittleEndian(byte[] bytes)
    {
        var pairs = Math.Min(bytes.Length / 2, 64);
        if (pairs < 2)
            return false;
        var zeroHighBytes = 0;
        for (var index = 0; index < pairs; index++)
        {
            if (bytes[(index * 2) + 1] == 0)
                zeroHighBytes++;
        }

        return zeroHighBytes * 2 >= pairs;
    }

    private static string LocateVsWhere()
    {
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var candidate = Path.Combine(programFilesX86, "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (File.Exists(candidate))
            return candidate;

        throw new Xunit.Sdk.XunitException(
            $"Could not locate vswhere.exe at the standard Visual Studio Installer path '{candidate}'.");
    }

    private static string OutputFileName() => OperatingSystem.IsWindows()
        ? LibraryName + ".dll"
        : OperatingSystem.IsMacOS()
            ? "lib" + LibraryName + ".dylib"
            : "lib" + LibraryName + ".so";

    private static string? FindOnPath(string executable, string? path = null)
    {
        path ??= Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return null;

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, executable);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static ProcessResult Run(
        string executable,
        IEnumerable<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var argumentList = arguments.ToArray();
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in argumentList)
            startInfo.ArgumentList.Add(argument);
        if (environment is not null)
        {
            startInfo.Environment.Clear();
            foreach (var item in environment)
                startInfo.Environment[item.Key] = item.Value;
        }

        using var process = new Process { StartInfo = startInfo };
        var display = FormatCommand(executable, argumentList);
        try
        {
            if (!process.Start())
                throw new Xunit.Sdk.XunitException($"Could not start native compiler command: {display}");
        }
        catch (Exception exception) when (exception is not Xunit.Sdk.XunitException)
        {
            throw new Xunit.Sdk.XunitException($"Could not start native compiler command '{display}': {exception.Message}");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var timedOut = WaitForExitOrTerminate(process, display);
        var (standardOutput, standardError) = ReadOutput(stdoutTask, stderrTask, OutputDrainTimeout, display);
        if (timedOut)
            throw new Xunit.Sdk.XunitException(
                $"Native compiler exceeded {CompilerTimeout.TotalSeconds:0}-second limit: {display}\n{standardOutput}\n{standardError}");
        if (process.ExitCode != 0)
        {
            throw new Xunit.Sdk.XunitException(
                $"Native compiler exited with code {process.ExitCode}: {display}\n{standardOutput}\n{standardError}");
        }

        return new ProcessResult(standardOutput, standardError);
    }

    private static int RunWithoutCapturedOutput(
        string executable,
        IEnumerable<string> arguments,
        string workingDirectory,
        string? diagnosticPath = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var argumentList = arguments.ToArray();
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            CreateNoWindow = true
        };
        foreach (var argument in argumentList)
            startInfo.ArgumentList.Add(argument);
        if (environment is not null)
        {
            foreach (var item in environment)
                startInfo.Environment[item.Key] = item.Value;
        }

        using var process = new Process { StartInfo = startInfo };
        var display = FormatCommand(executable, argumentList);
        try
        {
            if (!process.Start())
                throw new Xunit.Sdk.XunitException($"Could not start environment setup command: {display}");
        }
        catch (Exception exception) when (exception is not Xunit.Sdk.XunitException)
        {
            throw new Xunit.Sdk.XunitException($"Could not start environment setup command '{display}': {exception.Message}");
        }

        if (WaitForExitOrTerminate(process, display))
        {
            var diagnostic = diagnosticPath is null ? string.Empty : $" {ReadMsvcDiagnostic(diagnosticPath)}";
            throw new Xunit.Sdk.XunitException(
                $"Environment setup command exceeded {CompilerTimeout.TotalSeconds:0}-second limit: {display}.{diagnostic}");
        }
        return process.ExitCode;
    }

    private static bool WaitForExitOrTerminate(Process process, string display)
    {
        try
        {
            process.WaitForExitAsync().WaitAsync(CompilerTimeout).GetAwaiter().GetResult();
            return false;
        }
        catch (TimeoutException)
        {
            string? terminationFailure = null;
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (Win32Exception exception) { terminationFailure = exception.Message; }

            try
            {
                process.WaitForExitAsync().WaitAsync(ProcessExitGracePeriod).GetAwaiter().GetResult();
            }
            catch (TimeoutException)
            {
                var terminationDetail = terminationFailure is null
                    ? string.Empty
                    : $" Termination request also failed: {terminationFailure}.";
                throw new Xunit.Sdk.XunitException(
                    $"Command exceeded {CompilerTimeout.TotalSeconds:0}-second limit and did not exit within " +
                    $"{ProcessExitGracePeriod.TotalSeconds:0} seconds after termination: {display}.{terminationDetail}");
            }

            return true;
        }
    }

    private static (string StandardOutput, string StandardError) ReadOutput(
        Task<string> stdoutTask,
        Task<string> stderrTask,
        TimeSpan timeout,
        string display)
    {
        try
        {
            Task.WhenAll(stdoutTask, stderrTask).WaitAsync(timeout).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            throw new Xunit.Sdk.XunitException(
                $"Timed out draining native compiler output within {timeout.TotalSeconds:0} seconds: {display}");
        }

        return (stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult());
    }

    private static string FormatCommand(string executable, IReadOnlyList<string> arguments)
        => string.Join(' ', new[] { executable }.Concat(arguments).Select(Quote));

    private static string Quote(string value)
        => value.Any(char.IsWhiteSpace) ? "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : value;

    private sealed record ProcessResult(string StandardOutput, string StandardError);
}
