using Stark.Compiler;

namespace compiler.IntegrationTests;

[Collection("SerialToolchain")]
public sealed class PackageImageCliToolingTests
{
    [Fact]
    public async Task EmitPackageModeWritesPackageImageWithRequestedLibraryFileName()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-emit-pkg-");
        var sourcePath = Path.Combine(tempDirectory.FullName, "Demo.stark");
        var packagePath = Path.Combine(tempDirectory.FullName, "Demo.starkpkg.json");
        await File.WriteAllTextAsync(sourcePath, DemoSource);

        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [sourcePath, "--emit-pkg", "--package-library-file", "libDemoCustom.a", "-o", packagePath],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.Contains("Emitted package image:", stdout.ToString(), StringComparison.Ordinal);
            Assert.Contains("Package library file: libDemoCustom.a", stdout.ToString(), StringComparison.Ordinal);
            Assert.True(File.Exists(packagePath));

            var manifest = StarkPackageManifest.FromJson(await File.ReadAllTextAsync(packagePath));
            Assert.NotNull(manifest);
            Assert.Equal("Demo", manifest!.RootModule);
            Assert.Equal("libDemoCustom.a", manifest.LibraryFileName);
            Assert.Single(manifest.Modules);
        }
        finally
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
    }

    [Fact]
    public async Task InspectPackageModePrintsReadableSummaryForValidPackageImage()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-inspect-pkg-");
        var sourcePath = Path.Combine(tempDirectory.FullName, "Demo.stark");
        var packagePath = Path.Combine(tempDirectory.FullName, "Demo.starkpkg.json");
        await File.WriteAllTextAsync(sourcePath, DemoSource);

        try
        {
            var emitStdout = new StringWriter();
            var emitStderr = new StringWriter();
            var emitExitCode = await CompilerCli.RunAsync(
                [sourcePath, "--emit-pkg", "-o", packagePath],
                new StringReader(string.Empty),
                emitStdout,
                emitStderr);

            Assert.Equal(0, emitExitCode);
            Assert.Equal(string.Empty, emitStderr.ToString());
            Assert.True(File.Exists(packagePath));

            var inspectStdout = new StringWriter();
            var inspectStderr = new StringWriter();
            var inspectExitCode = await CompilerCli.RunAsync(
                [packagePath, "--inspect-pkg"],
                new StringReader(string.Empty),
                inspectStdout,
                inspectStderr);

            Assert.Equal(0, inspectExitCode);
            Assert.Equal(string.Empty, inspectStderr.ToString());

            var inspection = inspectStdout.ToString();
            Assert.Contains("package image:", inspection, StringComparison.Ordinal);
            Assert.Contains("root module: Demo", inspection, StringComparison.Ordinal);
            Assert.Contains("module count: 1", inspection, StringComparison.Ordinal);
            Assert.Contains("module Demo:", inspection, StringComparison.Ordinal);
            Assert.Contains("source-surface", inspection, StringComparison.Ordinal);
            Assert.Contains("typed-interface", inspection, StringComparison.Ordinal);
            Assert.Contains("compiler-facts", inspection, StringComparison.Ordinal);
        }
        finally
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
    }

    [Fact]
    public async Task InspectPackageModeReportsValidationDiagnosticsForMalformedContent()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-inspect-pkg-invalid-");
        var packagePath = Path.Combine(tempDirectory.FullName, "Broken.starkpkg.json");
        await File.WriteAllTextAsync(
            packagePath,
            """
            {
              "RootModule": "Demo",
              "LibraryFileName": "libDemo.a",
              "Modules": [
                {
                  "ModuleName": "Demo",
                  "ReExports": [],
                  "Functions": [],
                  "Types": [],
                  "Globals": []
                },
                {
                  "ModuleName": "Demo",
                  "ReExports": [],
                  "Functions": [],
                  "Types": [],
                  "Globals": []
                }
              ]
            }
            """);

        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [packagePath, "--inspect-pkg"],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, stdout.ToString());
            var diagnostics = stderr.ToString();
            Assert.Contains("STK7106", diagnostics, StringComparison.Ordinal);
            Assert.Contains("package-image", diagnostics, StringComparison.Ordinal);
            Assert.Contains("Failure summary:", diagnostics, StringComparison.Ordinal);
        }
        finally
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
    }

    private const string DemoSource =
        """
        module Demo

        public fn i32[-2147483648 2147483647] Run() {
            return 7;
        }
        """;
}
