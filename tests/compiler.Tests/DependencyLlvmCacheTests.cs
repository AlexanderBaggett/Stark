using Stark.Compiler;

namespace compiler.Tests;

public sealed class DependencyLlvmCacheTests
{
    private const string DependencySource =
        """
        module Dep

        public fn i64[min max] Three()
        {
            return 3;
        }
        """;

    private const string AppSource =
        """
        import Dep
        module App

        export fn i64[min max] main()
        {
            return Dep.Three();
        }
        """;

    [Fact]
    public async Task DependencyEmissionIsCachedByteIdenticallyAndInvalidatedByEdits()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-dep-cache-");
        var cacheDirectory = Path.Combine(tempDirectory.FullName, "cache");
        var sourceDirectory = Path.Combine(tempDirectory.FullName, "src");
        Directory.CreateDirectory(sourceDirectory);
        var dependencyPath = Path.Combine(sourceDirectory, "Dep.stark");
        var appPath = Path.Combine(sourceDirectory, "App.stark");
        await File.WriteAllTextAsync(dependencyPath, DependencySource);
        await File.WriteAllTextAsync(appPath, AppSource);

        var previousCache = Environment.GetEnvironmentVariable("STARK_DEP_CACHE");
        var previousVerify = Environment.GetEnvironmentVariable("STARK_DEP_CACHE_VERIFY");
        Environment.SetEnvironmentVariable("STARK_DEP_CACHE", cacheDirectory);

        try
        {
            // Cold: populates the cache.
            await CompileAppAsync(appPath, Path.Combine(tempDirectory.FullName, "app1"));
            var cachedEntries = Directory.GetFiles(cacheDirectory, "*.ll", SearchOption.AllDirectories);
            Assert.Single(cachedEntries);

            // Warm + verify: the hit recompiles and must be byte-identical, or
            // CompileDependencyLlvm throws the key-incompleteness error.
            Environment.SetEnvironmentVariable("STARK_DEP_CACHE_VERIFY", "1");
            await CompileAppAsync(appPath, Path.Combine(tempDirectory.FullName, "app2"));

            // Edit the dependency: the key must change (no stale reuse) and the
            // build must still succeed under verify.
            await File.WriteAllTextAsync(dependencyPath, DependencySource.Replace("return 3;", "return 4;", StringComparison.Ordinal));
            await CompileAppAsync(appPath, Path.Combine(tempDirectory.FullName, "app3"));
            Assert.Equal(2, Directory.GetFiles(cacheDirectory, "*.ll", SearchOption.AllDirectories).Length);
        }
        finally
        {
            Environment.SetEnvironmentVariable("STARK_DEP_CACHE", previousCache);
            Environment.SetEnvironmentVariable("STARK_DEP_CACHE_VERIFY", previousVerify);
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

    private static async Task CompileAppAsync(string appPath, string outputPath)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = await CompilerCli.RunAsync(
            [appPath, "--emit-exe", "--no-stark-path", "-o", outputPath],
            new StringReader(string.Empty),
            stdout,
            stderr);
        Assert.True(exitCode == 0, $"compile failed:\n{stderr}");
    }
}
