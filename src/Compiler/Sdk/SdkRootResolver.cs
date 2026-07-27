namespace Stark.Compiler;

internal enum SdkRootOrigin
{
    Explicit,
    Environment,
    Executable
}

internal sealed record SdkRootResolution(
    string RootPath,
    string ManifestPath,
    SdkRootOrigin Origin,
    string? ExecutablePath);

internal static class SdkRootResolver
{
    public const string EnvironmentVariableName = "STARK_SDK_ROOT";
    public const string ManifestFileName = "sdk.json";
    public const string ReleaseManifestFileName = "release.json";
    private const string ExecutableDirectoryName = "bin";

    public static SdkRootResolution Resolve(
        string? explicitRoot = null,
        string? executablePath = null,
        Func<string, string?>? environmentVariableReader = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            return CreateRootResolution(explicitRoot, SdkRootOrigin.Explicit, executablePath: null);
        }

        environmentVariableReader ??= Environment.GetEnvironmentVariable;
        var environmentRoot = environmentVariableReader(EnvironmentVariableName);
        if (!string.IsNullOrWhiteSpace(environmentRoot))
        {
            return CreateRootResolution(environmentRoot, SdkRootOrigin.Environment, executablePath: null);
        }

        executablePath = string.IsNullOrWhiteSpace(executablePath)
            ? Environment.ProcessPath
            : executablePath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException(
                "The Stark SDK root could not be discovered because the active compiler executable path is unavailable.");
        }

        var canonicalExecutablePath = CanonicalizeFilePath(executablePath);
        var executableDirectory = Path.GetDirectoryName(canonicalExecutablePath);
        if (string.IsNullOrWhiteSpace(executableDirectory))
        {
            throw new InvalidOperationException(
                $"The Stark SDK root could not be discovered from compiler executable '{executablePath}'.");
        }

        var executableRelativeRoot = executableDirectory;
        var adjacentManifestPath = Path.Combine(executableDirectory, ManifestFileName);
        if (!File.Exists(adjacentManifestPath)
            && string.Equals(
                Path.GetFileName(executableDirectory),
                ExecutableDirectoryName,
                StringComparison.Ordinal))
        {
            // Installed SDKs use <sdk-root>/bin/stark. Only inspect this one
            // conventional parent; an unrestricted ancestor walk could bind a
            // compiler to an unrelated SDK manifest elsewhere in the tree.
            // release.json is also a root marker so an archive with a missing
            // sdk.json still reaches the precise incomplete-release diagnostic.
            var sdkRoot = Directory.GetParent(executableDirectory)?.FullName;
            if (!string.IsNullOrWhiteSpace(sdkRoot)
                && (File.Exists(Path.Combine(sdkRoot, ManifestFileName))
                    || File.Exists(Path.Combine(sdkRoot, ReleaseManifestFileName))))
            {
                executableRelativeRoot = sdkRoot;
            }
        }

        return CreateRootResolution(
            executableRelativeRoot,
            SdkRootOrigin.Executable,
            canonicalExecutablePath);
    }

    internal static string CanonicalizeRootPath(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        return Path.TrimEndingDirectorySeparator(CanonicalizeDirectoryPath(new DirectoryInfo(fullPath)));
    }

    private static SdkRootResolution CreateRootResolution(
        string rootPath,
        SdkRootOrigin origin,
        string? executablePath)
    {
        var canonicalRoot = CanonicalizeRootPath(rootPath.Trim());
        return new SdkRootResolution(
            canonicalRoot,
            Path.Combine(canonicalRoot, ManifestFileName),
            origin,
            executablePath);
    }

    private static string CanonicalizeFilePath(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath.Trim());
        var directoryPath = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            fullPath = Path.Combine(
                CanonicalizeDirectoryPath(new DirectoryInfo(directoryPath)),
                Path.GetFileName(fullPath));
        }

        var file = new FileInfo(fullPath);
        if (!file.Exists)
        {
            return fullPath;
        }

        var target = file.ResolveLinkTarget(returnFinalTarget: true);
        var resolvedPath = Path.GetFullPath(target?.FullName ?? file.FullName);
        var resolvedDirectory = Path.GetDirectoryName(resolvedPath);
        return string.IsNullOrWhiteSpace(resolvedDirectory)
            ? resolvedPath
            : Path.Combine(
                CanonicalizeDirectoryPath(new DirectoryInfo(resolvedDirectory)),
                Path.GetFileName(resolvedPath));
    }

    private static string CanonicalizeDirectoryPath(DirectoryInfo directory)
    {
        if (directory.Parent is null)
        {
            return Path.GetFullPath(directory.FullName);
        }

        // Resolve each existing path component, not just the leaf. A common
        // installation shape is /usr/local/bin -> another prefix with the SDK
        // below it; resolving only the final executable or directory leaves
        // that intermediate alias in cache keys and containment checks.
        var canonicalParent = CanonicalizeDirectoryPath(directory.Parent);
        var candidate = new DirectoryInfo(Path.Combine(canonicalParent, directory.Name));
        if (!candidate.Exists)
        {
            return Path.GetFullPath(candidate.FullName);
        }

        var target = candidate.ResolveLinkTarget(returnFinalTarget: true);
        return target is null
            ? Path.GetFullPath(candidate.FullName)
            : CanonicalizeDirectoryPath(new DirectoryInfo(Path.GetFullPath(target.FullName)));
    }
}
