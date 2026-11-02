using System.Diagnostics;
using Stark.Compiler;

namespace compiler.IntegrationTests;

[Collection("SerialToolchain")]
public sealed class NativeMathDependencyTests
{
    [Theory]
    [InlineData(false, "implicit", false)]
    [InlineData(true, "implicit", false)]
    [InlineData(false, "library", true)]
    [InlineData(true, "library", true)]
    [InlineData(false, "argument", true)]
    [InlineData(true, "argument", true)]
    [InlineData(false, "live", true)]
    [InlineData(true, "live", true)]
    [InlineData(true, "legacy", true)]
    [InlineData(true, "consumer-library", true)]
    [InlineData(true, "consumer-argument", true)]
    [InlineData(true, "restore-as-needed", false)]
    public async Task FinalLinkHonorsMathLivenessAndExplicitIntent(bool package, string intent, bool needsMath)
    {
        if (!OperatingSystem.IsLinux() || !NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("stark-native-math-");
        try
        {
            var sourceDirectory = Directory.CreateDirectory(Path.Combine(directory.FullName, "stdlib", "src", "System")).FullName;
            var packageDirectory = Directory.CreateDirectory(Path.Combine(directory.FullName, "stdlib", "dist")).FullName;
            var mathPath = Path.Combine(sourceDirectory, "Math.stark");
            var appPath = Path.Combine(directory.FullName, "App.stark");
            var nativePath = Path.Combine(directory.FullName, "input.c");
            var outputPath = Path.Combine(directory.FullName, "app");
            await File.WriteAllTextAsync(mathPath,
                """
                module System.Math

                public finite law f64 Sin(f64 value);

                unsafe ffi(c) fn f64 MathProbeInput();

                public unsafe fn i32[min max] Check()
                {
                    stack f64 value = Sin(MathProbeInput());
                    if (value > 0.8 && value < 0.9)
                    {
                        return 0;
                    }
                    return 1;
                }
                """);
            await File.WriteAllTextAsync(nativePath, "double MathProbeInput(void) { return 1.0; }");
            Assert.True(DevelopmentSdkManifestWriter.TryWrite(directory.FullName, out _, out var developmentError), developmentError);
            await File.WriteAllTextAsync(appPath,
                $$"""
                import System.Math
                module App

                export unsafe fn i32[min max] main()
                {
                    return {{(intent == "live" ? "Check()" : "0")}};
                }
                """);

            string[] explicitArguments = intent switch
            {
                "library" => ["--native-library", "m"],
                "argument" => ["--native-link-arg=-lm"],
                _ => []
            };
            if (package)
            {
                await CompileAsync([
                    mathPath, "--emit-lib", "-o", Path.Combine(packageDirectory, "libMath.a"),
                    "--sdk-root", directory.FullName, "--package-profile", "release",
                    .. explicitArguments]);
                var imagePath = Path.Combine(packageDirectory, "libMath.starkpkg");
                Assert.True(PackageImageLoader.TryLoadManifest(imagePath, out var manifest));
                if (intent == "legacy")
                {
                    manifest = PackageImageIdentity.Apply(manifest with
                    {
                        NativeDependencies = manifest.NativeDependencies! with { ImplicitLibraries = null }
                    });
                    await File.WriteAllBytesAsync(imagePath, PackageImageBinaryFormat.Encode(manifest));
                }
                else
                {
                    Assert.Equal(intent is not ("library" or "argument"),
                        manifest.NativeDependencies?.ImplicitLibraries?.Contains("m") == true);
                    if (intent == "implicit")
                    {
                        var withoutOrigin = PackageImageIdentity.Create(manifest with
                        {
                            NativeDependencies = manifest.NativeDependencies! with { ImplicitLibraries = null }
                        });
                        Assert.Equal(manifest.Identity!.ApiHash, withoutOrigin.ApiHash);
                        Assert.NotEqual(manifest.Identity.ContentHash, withoutOrigin.ContentHash);
                    }
                }
                Directory.Delete(sourceDirectory, recursive: true);
                Assert.True(StageSdkManifestWriter.TryWrite(directory.FullName, "stage0", out _, out var sdkError), sdkError);
            }

            string[] consumerArguments = intent switch
            {
                "consumer-library" => ["--native-library", "m"],
                "consumer-argument" => ["--link-arg=-lm"],
                "restore-as-needed" => ["--native-link-arg=-Wl,--as-needed", "--link-arg=-lm"],
                _ => []
            };
            await CompileAsync([
                appPath, "--emit-exe", "-I", package ? packageDirectory : sourceDirectory,
                "--native-source", nativePath, "-o", outputPath,
                "--sdk-root", directory.FullName,
                .. consumerArguments,
                .. (package ? [] : explicitArguments)]);
            var (exitCode, _, error) = await RunAsync(outputPath);
            Assert.True(exitCode == 0, error);

            var (readExitCode, dynamicSection, readError) = await RunAsync("readelf", "-d", outputPath);
            Assert.True(readExitCode == 0, readError);
            var mathIsNeeded = dynamicSection.Split('\n').Any(line =>
                line.Contains("NEEDED", StringComparison.Ordinal)
                && line.Contains("[libm.so", StringComparison.Ordinal));
            Assert.Equal(needsMath, mathIsNeeded);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static async Task CompileAsync(string[] arguments)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = await CompilerCli.RunAsync(arguments, new StringReader(string.Empty), stdout, stderr);
        Assert.True(exitCode == 0, $"{stdout}{Environment.NewLine}{stderr}");
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(string file, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output, await error);
    }
}
