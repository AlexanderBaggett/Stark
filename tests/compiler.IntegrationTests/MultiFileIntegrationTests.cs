using System.Text.Json;
using Stark.Compiler;

namespace compiler.IntegrationTests;

public sealed class MultiFileIntegrationTests
{
    [Fact]
    public async Task SiblingModulesResolveThroughTheSourceSearchPath()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-multifile-source-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        var appDirectory = Path.Combine(tempDirectory.FullName, "app");
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(appDirectory);

        var mathPath = Path.Combine(packageDirectory, "Math.stark");
        var appPath = Path.Combine(appDirectory, "App.stark");

        try
        {
            await File.WriteAllTextAsync(
                mathPath,
                """
                module Math

                public finite law i32[-2147483648 2147483647] Add(i32[-2147483648 2147483647] left, i32[-2147483648 2147483647] right) {
                    return left + right;
                }
                """);

            await File.WriteAllTextAsync(
                appPath,
                """
                import Math
                module App

                fn i32[-2147483648 2147483647] Run() {
                    return Math.Add(3, 4);
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--check", "-I", packageDirectory],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Check succeeded.", stdout.ToString());
            AssertCompilerLogsEmitted(stderr.ToString());
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task ExportedReExportsMakeTransitiveModulesAvailableToConsumingApps()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-multifile-reexport-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        var appDirectory = Path.Combine(tempDirectory.FullName, "app");
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(appDirectory);

        var mathPath = Path.Combine(packageDirectory, "Math.stark");
        var facadePath = Path.Combine(packageDirectory, "Facade.stark");
        var appPath = Path.Combine(appDirectory, "App.stark");

        try
        {
            await File.WriteAllTextAsync(
                mathPath,
                """
                module Math

                public finite law i32[-2147483648 2147483647] Add(i32[-2147483648 2147483647] left, i32[-2147483648 2147483647] right) {
                    return left + right;
                }
                """);

            await File.WriteAllTextAsync(
                facadePath,
                """
                export import Math
                module Facade

                public fn i32[-2147483648 2147483647] Double(i32[-2147483648 2147483647] value) {
                    return Math.Add(value, value);
                }
                """);

            await File.WriteAllTextAsync(
                appPath,
                """
                import Facade
                module App

                fn i32[-2147483648 2147483647] Run() {
                    return Math.Add(Facade.Double(2), 3);
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--check", "-I", packageDirectory],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Check succeeded.", stdout.ToString());
            AssertCompilerLogsEmitted(stderr.ToString());
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task ModuleQualifiedEnumCasesResolveThroughImportedEnumTypes()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-multifile-enum-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        var textDirectory = Path.Combine(packageDirectory, "System");
        var appDirectory = Path.Combine(tempDirectory.FullName, "app");
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(textDirectory);
        Directory.CreateDirectory(appDirectory);

        var systemPath = Path.Combine(packageDirectory, "System.stark");
        var textPath = Path.Combine(textDirectory, "Text.stark");
        var appPath = Path.Combine(appDirectory, "App.stark");

        try
        {
            await File.WriteAllTextAsync(
                systemPath,
                """
                export import System.Text
                module System
                """);

            await File.WriteAllTextAsync(
                textPath,
                """
                module System.Text

                public enum Encoding {
                    Binary,
                    UTF8,
                    UTF16,
                    UTF32,
                }
                """);

            await File.WriteAllTextAsync(
                appPath,
                """
                import System
                module App

                fn i32[-2147483648 2147483647] Run() {
                    stack System.Text.Encoding encoding = System.Text.Encoding.UTF8;
                    return 0;
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--check", "-I", packageDirectory],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Check succeeded.", stdout.ToString());
            AssertCompilerLogsEmitted(stderr.ToString());
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task ModulePrivateDeclarationsStayHiddenAcrossModuleBoundaries()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-multifile-visibility-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        var appDirectory = Path.Combine(tempDirectory.FullName, "app");
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(appDirectory);

        var mathPath = Path.Combine(packageDirectory, "Math.stark");
        var facadePath = Path.Combine(packageDirectory, "Facade.stark");
        var appPath = Path.Combine(appDirectory, "App.stark");

        try
        {
            await File.WriteAllTextAsync(
                mathPath,
                """
                module Math

                fn i32[-2147483648 2147483647] HiddenAdd(i32[-2147483648 2147483647] left, i32[-2147483648 2147483647] right) {
                    return left + right;
                }

                public fn i32[-2147483648 2147483647] Add(i32[-2147483648 2147483647] left, i32[-2147483648 2147483647] right) {
                    return HiddenAdd(left, right);
                }
                """);

            await File.WriteAllTextAsync(
                facadePath,
                """
                export import Math
                module Facade

                public fn i32[-2147483648 2147483647] Double(i32[-2147483648 2147483647] value) {
                    return Math.Add(value, value);
                }
                """);

            await File.WriteAllTextAsync(
                appPath,
                """
                import Facade
                module App

                fn i32[-2147483648 2147483647] Run() {
                    return Math.HiddenAdd(Facade.Double(2), 3);
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--check", "-I", packageDirectory],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.NotEqual(0, exitCode);
            Assert.Contains("HiddenAdd", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task ManifestBackedLibrariesCanBeConsumedWithoutSourceFiles()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-multifile-manifest-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        var appDirectory = Path.Combine(tempDirectory.FullName, "app");
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(appDirectory);

        var mathPath = Path.Combine(packageDirectory, "Math.stark");
        var facadePath = Path.Combine(packageDirectory, "Facade.stark");
        var libraryPath = Path.Combine(packageDirectory, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");
        var manifestPath = Path.Combine(packageDirectory, "libFacade.starkpkg.json");
        var appPath = Path.Combine(appDirectory, "App.stark");
        var outputPath = Path.Combine(appDirectory, OperatingSystem.IsWindows() ? "app.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(
                mathPath,
                """
                module Math

                public finite law i32[-2147483648 2147483647] Add(i32[-2147483648 2147483647] left, i32[-2147483648 2147483647] right) {
                    return left + right;
                }
                """);

            await File.WriteAllTextAsync(
                facadePath,
                """
                export import Math
                module Facade

                public finite law i32[-2147483648 2147483647] Double(i32[-2147483648 2147483647] value) {
                    return Math.Add(value, value);
                }
                """);

            var buildStdout = new StringWriter();
            var buildStderr = new StringWriter();
            var buildExitCode = await CompilerCli.RunAsync(
                [facadePath, "--emit-lib", "-o", libraryPath],
                new StringReader(string.Empty),
                buildStdout,
                buildStderr);

            Assert.Equal(0, buildExitCode);
            Assert.Contains("Emitted static library:", buildStdout.ToString());
            Assert.Contains("Emitted package image:", buildStdout.ToString());
            AssertCompilerLogsEmitted(buildStderr.ToString());
            Assert.True(File.Exists(libraryPath));
            Assert.True(File.Exists(manifestPath));

            using (var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath)))
            {
                Assert.Equal("Facade", manifest.RootElement.GetProperty("RootModule").GetString());
                Assert.Contains(
                    manifest.RootElement.GetProperty("Modules").EnumerateArray(),
                    module =>
                    {
                        if (module.GetProperty("ModuleName").GetString() != "Facade")
                        {
                            return false;
                        }

                        return module.GetProperty("SourceSurface")
                            .GetProperty("ReExports")
                            .EnumerateArray()
                            .Any(reExport => reExport.GetProperty("ModuleName").GetString() == "Math");
                    });
            }

            File.Delete(mathPath);
            File.Delete(facadePath);

            await File.WriteAllTextAsync(
                appPath,
                """
                import Facade
                module App

                export unsafe ffi fn i32[-2147483648 2147483647] main() {
                    return Math.Add(3, 4);
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", packageDirectory, "-o", outputPath],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Emitted executable:", stdout.ToString());
            AssertCompilerLogsEmitted(stderr.ToString());
            Assert.True(File.Exists(outputPath));

            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = outputPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            Assert.NotNull(process);
            var processStdout = await process!.StandardOutput.ReadToEndAsync();
            var processStderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.Equal(7, process.ExitCode);
            Assert.Equal(string.Empty, processStdout);
            Assert.Equal(string.Empty, processStderr);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task ManifestBackedPublicGlobalsLinkAcrossPackageBoundaries()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-multifile-global-manifest-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        var appDirectory = Path.Combine(tempDirectory.FullName, "app");
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(appDirectory);

        var globalsPath = Path.Combine(packageDirectory, "Globals.stark");
        var libraryPath = Path.Combine(packageDirectory, OperatingSystem.IsWindows() ? "Globals.lib" : "libGlobals.a");
        var manifestPath = Path.Combine(packageDirectory, "libGlobals.starkpkg.json");
        var appPath = Path.Combine(appDirectory, "App.stark");
        var outputPath = Path.Combine(appDirectory, OperatingSystem.IsWindows() ? "app.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(
                globalsPath,
                """
                module Globals

                public const Answer = 7;
                """);

            var buildStdout = new StringWriter();
            var buildStderr = new StringWriter();
            var buildExitCode = await CompilerCli.RunAsync(
                [globalsPath, "--emit-lib", "-o", libraryPath],
                new StringReader(string.Empty),
                buildStdout,
                buildStderr);

            Assert.Equal(0, buildExitCode);
            Assert.Contains("Emitted static library:", buildStdout.ToString());
            Assert.Contains("Emitted package image:", buildStdout.ToString());
            AssertCompilerLogsEmitted(buildStderr.ToString());
            Assert.True(File.Exists(libraryPath));
            Assert.True(File.Exists(manifestPath));

            File.Delete(globalsPath);

            await File.WriteAllTextAsync(
                appPath,
                """
                import Globals
                module App

                export unsafe ffi fn i32[-2147483648 2147483647] main() {
                    return Globals.Answer;
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", packageDirectory, "-o", outputPath],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Emitted executable:", stdout.ToString());
            AssertCompilerLogsEmitted(stderr.ToString());
            Assert.True(File.Exists(outputPath));

            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = outputPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            Assert.NotNull(process);
            var processStdout = await process!.StandardOutput.ReadToEndAsync();
            var processStderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.Equal(7, process.ExitCode);
            Assert.Equal(string.Empty, processStdout);
            Assert.Equal(string.Empty, processStderr);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task SystemTextSourceModuleSupportsRuntimeAsciiUnicodeConversionHelpers()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var sourceTextPath = Path.Combine(repositoryRoot, "stdlib", "src", "System", "Text.stark");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-multifile-system-text-runtime-");
        var appPath = Path.Combine(tempDirectory.FullName, "TextRuntime.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");

        try
        {
            var sourceText = await File.ReadAllTextAsync(sourceTextPath);

            await File.WriteAllTextAsync(
                appPath,
                sourceText
                + """

                export unsafe ffi fn i32[-2147483648 2147483647] main() {
                    stack mut i32[-2147483648 2147483647][8] unicodeBuffer = { 0, 0, 0, 0, 0, 0, 0, 0 };
                    stack mut Unicode unicodeText = new Unicode() {
                        Data = &unicodeBuffer[0],
                        Length = 0,
                        Capacity = 8
                    };

                    if (!TryConvertAsciiToUnicode(&unicodeText, "caf\u00E9")) {
                        return 1;
                    }

                    if (unicodeText.Length != 4) {
                        return 2;
                    }

                    if (unicodeText.Data == null || *unicodeText.Data != 99) {
                        return 3;
                    }

                    if (*(&unicodeText.Data[1]) != 97 || *(&unicodeText.Data[2]) != 102 || *(&unicodeText.Data[3]) != 233) {
                        return 4;
                    }

                    stack mut i8[-128 127][16] asciiBuffer = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
                    stack mut Ascii asciiText = new Ascii() {
                        Data = &asciiBuffer[0],
                        Length = 0,
                        Capacity = 16
                    };

                    if (!TryConvertUnicodeToAscii(&asciiText, (unicode)"\u03B1!")) {
                        return 5;
                    }

                    if (asciiText.Length != 3) {
                        return 6;
                    }

                    stack mut i32[-2147483648 2147483647][8] roundTripBuffer = { 0, 0, 0, 0, 0, 0, 0, 0 };
                    stack mut Unicode roundTrip = new Unicode() {
                        Data = &roundTripBuffer[0],
                        Length = 0,
                        Capacity = 8
                    };

                    if (!TryConvertAsciiToUnicode(&roundTrip, AsciiView(asciiText))) {
                        return 7;
                    }

                    if (roundTrip.Length != 2) {
                        return 8;
                    }

                    if (roundTrip.Data == null || *roundTrip.Data != 945 || *(&roundTrip.Data[1]) != 33) {
                        return 9;
                    }

                    stack mut i8[-128 127][2] smallAsciiBuffer = { 0, 0 };
                    stack mut Ascii tooSmall = new Ascii() {
                        Data = &smallAsciiBuffer[0],
                        Length = 0,
                        Capacity = 2
                    };

                    if (TryConvertUnicodeToAscii(&tooSmall, (unicode)"\u03B1!")) {
                        return 10;
                    }

                    if (tooSmall.Length != 0) {
                        return 11;
                    }

                    return 0;
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", Path.Combine(repositoryRoot, "stdlib", "src"), "-o", outputPath],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Emitted executable:", stdout.ToString());
            AssertCompilerLogsEmitted(stderr.ToString());
            Assert.True(File.Exists(outputPath));

            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = outputPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            Assert.NotNull(process);
            var processStdout = await process!.StandardOutput.ReadToEndAsync();
            var processStderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.Equal(0, process.ExitCode);
            Assert.Equal(string.Empty, processStdout);
            Assert.Equal(string.Empty, processStderr);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task SystemTextSourceModuleSupportsRuntimeUtf16ConversionHelpers()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var sourceTextPath = Path.Combine(repositoryRoot, "stdlib", "src", "System", "Text.stark");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-multifile-system-text-utf16-runtime-");
        var appPath = Path.Combine(tempDirectory.FullName, "TextUtf16Runtime.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");

        try
        {
            var sourceText = await File.ReadAllTextAsync(sourceTextPath);

            await File.WriteAllTextAsync(
                appPath,
                sourceText
                + """

                export unsafe ffi fn i32[-2147483648 2147483647] main() {
                    stack mut i16[-32768 32767][8] utf16Buffer;
                    stack mut i64[-9223372036854775808 9223372036854775807] utf16Length = 0;
                    if (!TryConvertAsciiToUtf16(&utf16Buffer[0], 8, "A𐍈", &utf16Length)) {
                        return 1;
                    }

                    if (utf16Length != 3) {
                        return 2;
                    }

                    stack mut i32[-2147483648 2147483647][4] unicodeBuffer = { 0, 0, 0, 0 };
                    stack mut Unicode unicodeText = new Unicode() {
                        Data = &unicodeBuffer[0],
                        Length = 0,
                        Capacity = 4
                    };

                    if (!TryConvertUtf16ToUnicode(&unicodeText, &utf16Buffer[0], utf16Length)) {
                        return 3;
                    }

                    if (unicodeText.Length != 2) {
                        return 4;
                    }

                    if (unicodeText.Data == null || *unicodeText.Data != 65 || *(&unicodeText.Data[1]) != 66376) {
                        return 5;
                    }

                    stack mut i16[-32768 32767][8] secondUtf16Buffer;
                    stack mut i64[-9223372036854775808 9223372036854775807] secondUtf16Length = 0;
                    if (!TryConvertUnicodeToUtf16(&secondUtf16Buffer[0], 8, UnicodeView(unicodeText), &secondUtf16Length)) {
                        return 6;
                    }

                    if (secondUtf16Length != 3) {
                        return 7;
                    }

                    stack mut i8[-128 127][8] asciiBuffer = { 0, 0, 0, 0, 0, 0, 0, 0 };
                    stack mut Ascii asciiText = new Ascii() {
                        Data = &asciiBuffer[0],
                        Length = 0,
                        Capacity = 8
                    };

                    if (!TryConvertUtf16ToAscii(&asciiText, &secondUtf16Buffer[0], secondUtf16Length)) {
                        return 8;
                    }

                    if (asciiText.Length != 5) {
                        return 9;
                    }

                    stack mut i32[-2147483648 2147483647][4] roundTripBuffer = { 0, 0, 0, 0 };
                    stack mut Unicode roundTrip = new Unicode() {
                        Data = &roundTripBuffer[0],
                        Length = 0,
                        Capacity = 4
                    };

                    if (!TryConvertAsciiToUnicode(&roundTrip, AsciiView(asciiText))) {
                        return 10;
                    }

                    if (roundTrip.Length != 2) {
                        return 11;
                    }

                    if (roundTrip.Data == null || *roundTrip.Data != 65 || *(&roundTrip.Data[1]) != 66376) {
                        return 12;
                    }

                    stack mut i32[-2147483648 2147483647][1] gothicBuffer = { 66376 };
                    stack mut Unicode gothic = new Unicode() {
                        Data = &gothicBuffer[0],
                        Length = 1,
                        Capacity = 1
                    };
                    stack mut i16[-32768 32767][1] tooSmallUtf16;
                    stack mut i64[-9223372036854775808 9223372036854775807] tooSmallLength = 17;
                    if (TryConvertUnicodeToUtf16(&tooSmallUtf16[0], 1, UnicodeView(gothic), &tooSmallLength)) {
                        return 13;
                    }

                    if (tooSmallLength != 0) {
                        return 14;
                    }

                    return 0;
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", Path.Combine(repositoryRoot, "stdlib", "src"), "-o", outputPath],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Emitted executable:", stdout.ToString());
            AssertCompilerLogsEmitted(stderr.ToString());
            Assert.True(File.Exists(outputPath));

            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = outputPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            Assert.NotNull(process);
            var processStdout = await process!.StandardOutput.ReadToEndAsync();
            var processStderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.Equal(0, process.ExitCode);
            Assert.Equal(string.Empty, processStdout);
            Assert.Equal(string.Empty, processStderr);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    private static void Cleanup(DirectoryInfo tempDirectory)
    {
        try
        {
            tempDirectory.Delete(recursive: true);
        }
        catch
        {
            // Best effort cleanup only.
        }
    }

    private static void AssertCompilerLogsEmitted(string text)
    {
        Assert.Equal(string.Empty, text);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Stark.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Unable to locate the Stark repository root for multi-file integration tests.");
    }
}
