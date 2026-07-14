using Stark.Compiler;

namespace compiler.Tests;

public sealed class ModuleResolutionProvenanceTests
{
    [Fact]
    public void ResolverPrefersExplicitSourceOverImplicitPackage()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-provenance-resolver-");

        try
        {
            var rootDirectory = Path.Combine(tempDirectory.FullName, "root");
            var sourceDirectory = Path.Combine(tempDirectory.FullName, "src");
            WriteSingleModulePackageImage(rootDirectory, "Dep");
            Directory.CreateDirectory(sourceDirectory);
            var shadowedSourcePath = Path.Combine(sourceDirectory, "Dep.stark");
            File.WriteAllText(shadowedSourcePath, DependencyModuleSource);

            var resolver = new FileSystemModuleResolver(
                [rootDirectory, sourceDirectory],
                targetInfo: null,
                implicitSearchDirectories: [rootDirectory]);

            Assert.True(resolver.TryResolveModule("Dep", out var module));
            Assert.Null(module.ManifestPath);
            Assert.Equal(Path.GetFullPath(shadowedSourcePath), Path.GetFullPath(module.FilePath!));

            var note = Assert.Single(resolver.SnapshotResolutionNotes());
            Assert.Equal("Dep", note.ModuleName);
            Assert.Null(note.ManifestPath);
            Assert.Equal(Path.GetFullPath(shadowedSourcePath), Path.GetFullPath(note.SourceFilePath!));
            Assert.Null(note.ShadowedSourcePath);
        }
        finally
        {
            CleanUp(tempDirectory);
        }
    }

    [Fact]
    public void ResolverPrefersSourceOverExplicitPackageCandidate()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-provenance-explicit-");

        try
        {
            var packageDirectory = Path.Combine(tempDirectory.FullName, "dist");
            var sourceDirectory = Path.Combine(tempDirectory.FullName, "src");
            WriteSingleModulePackageImage(packageDirectory, "Dep");
            Directory.CreateDirectory(sourceDirectory);
            File.WriteAllText(Path.Combine(sourceDirectory, "Dep.stark"), DependencyModuleSource);

            var resolver = new FileSystemModuleResolver(
                [packageDirectory, sourceDirectory],
                targetInfo: null,
                implicitSearchDirectories: null);

            Assert.True(resolver.TryResolveModule("Dep", out var module));
            Assert.Null(module.ManifestPath);

            var note = Assert.Single(resolver.SnapshotResolutionNotes());
            Assert.NotNull(note.SourceFilePath);
            Assert.Null(note.ManifestPath);
            Assert.Null(note.ShadowedSourcePath);
        }
        finally
        {
            CleanUp(tempDirectory);
        }
    }

    [Fact]
    public void ResolverPrefersExplicitPackageOverImplicitSourceFallback()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-provenance-package-over-fallback-");

        try
        {
            var packageDirectory = Path.Combine(tempDirectory.FullName, "dist");
            var sourceDirectory = Path.Combine(tempDirectory.FullName, "ambient-src");
            WriteSingleModulePackageImage(packageDirectory, "Dep");
            Directory.CreateDirectory(sourceDirectory);
            File.WriteAllText(Path.Combine(sourceDirectory, "Dep.stark"), DependencyModuleSource);

            var resolver = new FileSystemModuleResolver(
                [packageDirectory, sourceDirectory],
                targetInfo: null,
                implicitSearchDirectories: [sourceDirectory]);

            Assert.True(resolver.TryResolveModule("Dep", out var module));
            Assert.NotNull(module.ManifestPath);
            Assert.Equal(module.ManifestPath, module.FilePath);

            var note = Assert.Single(resolver.SnapshotResolutionNotes());
            Assert.NotNull(note.ManifestPath);
            Assert.Null(note.SourceFilePath);
        }
        finally
        {
            CleanUp(tempDirectory);
        }
    }

    [Fact]
    public async Task CliPrefersExplicitSourceOverRootDirectoryPackage()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-provenance-cli-shadow-");

        try
        {
            var rootDirectory = Path.Combine(tempDirectory.FullName, "root");
            var sourceDirectory = Path.Combine(tempDirectory.FullName, "src");
            WriteSingleModulePackageImage(rootDirectory, "Dep");
            Directory.CreateDirectory(sourceDirectory);
            File.WriteAllText(Path.Combine(sourceDirectory, "Dep.stark"), DependencyModuleSource);
            var probePath = Path.Combine(rootDirectory, "Probe.stark");
            File.WriteAllText(probePath, ProbeModuleSource);

            var (exitCode, _, stderrText) = await RunCliAsync(
                [probePath, "--check", "-I", sourceDirectory, "--no-stark-path", "--explain-modules"]);

            Assert.Equal(0, exitCode);
            Assert.Contains("stark: module Dep <- source '", stderrText, StringComparison.Ordinal);
            Assert.DoesNotContain("shadows source module 'Dep'", stderrText, StringComparison.Ordinal);
            Assert.DoesNotContain("the package wins", stderrText, StringComparison.Ordinal);
        }
        finally
        {
            CleanUp(tempDirectory);
        }
    }

    [Fact]
    public async Task CliKeepsStderrCleanForPackageResolutionsWithoutShadowing()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-provenance-cli-package-");

        try
        {
            var rootDirectory = Path.Combine(tempDirectory.FullName, "root");
            WriteSingleModulePackageImage(rootDirectory, "Dep");
            var probePath = Path.Combine(rootDirectory, "Probe.stark");
            File.WriteAllText(probePath, ProbeModuleSource);

            var (quietExitCode, _, quietStderr) = await RunCliAsync(
                [probePath, "--check", "--no-stark-path"]);

            Assert.Equal(0, quietExitCode);
            Assert.Equal(string.Empty, quietStderr);

            var (explainExitCode, _, explainStderr) = await RunCliAsync(
                [probePath, "--check", "--no-stark-path", "--explain-modules"]);

            Assert.Equal(0, explainExitCode);
            Assert.Contains("stark: module Dep <- package '", explainStderr, StringComparison.Ordinal);
            Assert.Contains("sha256:", explainStderr, StringComparison.Ordinal);
            Assert.DoesNotContain("shadows source module", explainStderr, StringComparison.Ordinal);
        }
        finally
        {
            CleanUp(tempDirectory);
        }
    }

    [Fact]
    public async Task CliExplainModulesListsSourceResolutionsAndStaysQuietByDefault()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-provenance-cli-explain-");

        try
        {
            var rootDirectory = Path.Combine(tempDirectory.FullName, "root");
            var sourceDirectory = Path.Combine(tempDirectory.FullName, "src");
            Directory.CreateDirectory(rootDirectory);
            Directory.CreateDirectory(sourceDirectory);
            File.WriteAllText(Path.Combine(sourceDirectory, "Dep.stark"), DependencyModuleSource);
            var probePath = Path.Combine(rootDirectory, "Probe.stark");
            File.WriteAllText(probePath, ProbeModuleSource);

            var (quietExitCode, _, quietStderr) = await RunCliAsync(
                [probePath, "--check", "-I", sourceDirectory, "--no-stark-path"]);
            Assert.Equal(0, quietExitCode);
            Assert.DoesNotContain("stark: module", quietStderr, StringComparison.Ordinal);

            var (explainExitCode, _, explainStderr) = await RunCliAsync(
                [probePath, "--check", "-I", sourceDirectory, "--no-stark-path", "--explain-modules"]);
            Assert.Equal(0, explainExitCode);
            Assert.Contains("stark: module Dep <- source '", explainStderr, StringComparison.Ordinal);
        }
        finally
        {
            CleanUp(tempDirectory);
        }
    }

    private const string DependencyModuleSource =
        """
        module Dep

        public fn i32[min max] One()
        {
            return 1;
        }
        """;

    private const string ProbeModuleSource =
        """
        import Dep
        module Probe

        fn i32[min max] main()
        {
            return Dep.One();
        }
        """;

    private static async Task<(int ExitCode, string StdoutText, string StderrText)> RunCliAsync(string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exitCode = await CompilerCli.RunAsync(args, TextReader.Null, stdout, stderr);
        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    private static string WriteSingleModulePackageImage(string directory, string moduleName)
    {
        Directory.CreateDirectory(directory);
        var sourcePath = Path.Combine(directory, $"{moduleName}Package.stark");
        var libraryPath = Path.Combine(directory, $"lib{moduleName}.a");
        var manifestPath = Path.Combine(directory, $"lib{moduleName}.starkpkg");

        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(DependencyModuleSource, sourcePath),
            new CompilerOptions(StopAfterPassId: "lower-abi"));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

        File.WriteAllBytes(libraryPath, Array.Empty<byte>());
        var manifest = PackageImageBuilder.Create(result, libraryPath);
        File.WriteAllBytes(manifestPath, PackageImageBinaryFormat.Encode(manifest));
        return manifestPath;
    }

    private static void CleanUp(DirectoryInfo tempDirectory)
    {
        try
        {
            tempDirectory.Delete(recursive: true);
        }
        catch
        {
            // Best effort cleanup.
        }
    }
}
