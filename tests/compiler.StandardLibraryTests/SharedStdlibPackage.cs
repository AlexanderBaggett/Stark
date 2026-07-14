using Stark.Compiler;

namespace compiler.StandardLibraryTests;

/// <summary>
/// Builds the repository standard library package once per test process. Tests that only
/// consume System APIs compile against this package directory instead of rebuilding the
/// standard library from stdlib/src on every test. Tests that assert on the standard
/// library source-compilation pipeline itself should keep using stdlib/src directly.
/// </summary>
public static class SharedStdlibPackage
{
    private static readonly Lazy<Task<SharedStdlibPackageArtifact>> Package = new(
        BuildAsync,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static Task<SharedStdlibPackageArtifact> GetAsync() => Package.Value;

    public static async Task<string> GetDirectoryAsync() =>
        (await GetAsync().ConfigureAwait(false)).DirectoryPath;

    private static async Task<SharedStdlibPackageArtifact> BuildAsync()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            throw new InvalidOperationException(
                "Unable to detect the active target for the shared standard-library package.");
        }

        var repositoryRoot = FindRepositoryRoot();
        var systemPath = Path.Combine(repositoryRoot, "stdlib", "src", "System.stark");
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"stark-shared-stdlib-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        };

        var libraryFileName = OperatingSystem.IsWindows() ? "System.lib" : "libSystem.a";
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = await CompilerCli.RunAsync(
            [
                systemPath,
                "--emit-lib",
                "--target", targetInfo.Triple,
                "-o", Path.Combine(directory, libraryFileName)
            ],
            new StringReader(string.Empty),
            stdout,
            stderr);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"Shared stdlib package build failed with exit code {exitCode}."
                + $"{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
        }

        return new SharedStdlibPackageArtifact(directory, targetInfo);
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

        throw new InvalidOperationException(
            "Unable to locate the Stark repository root for the shared stdlib package build.");
    }
}

public sealed record SharedStdlibPackageArtifact(
    string DirectoryPath,
    LlvmTargetInfo TargetInfo);
