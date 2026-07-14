namespace Stark.Compiler;

internal static class CompilerModuleResolverFactory
{
    public static IModuleResolver? Create(
        string? inputPath,
        IReadOnlyList<string> searchDirectories,
        LlvmTargetInfo? targetInfo,
        bool useStarkPathEnvironment,
        ActiveSdkResolution activeSdk,
        out FileSystemModuleResolver? fileSystemResolver)
    {
        var resolvedDirectories = new List<string>();
        // Directories the user never named on the command line: the root file's
        // own directory and STARK_PATH. The resolver receives them separately so
        // this call shape stays compatible with provenance-aware resolution; the
        // resolver itself prefers source files before package images to avoid
        // stale package artifacts shadowing explicit `-I` source roots.
        var implicitDirectories = new List<string>();

        if (inputPath is not null)
        {
            var fullInputPath = Path.GetFullPath(inputPath);
            var directory = Path.GetDirectoryName(fullInputPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                resolvedDirectories.Add(directory);
                implicitDirectories.Add(directory);
            }
        }

        resolvedDirectories.AddRange(searchDirectories.Where(static path => !string.IsNullOrWhiteSpace(path)));

        var environmentSearchPath = useStarkPathEnvironment
            ? Environment.GetEnvironmentVariable("STARK_PATH")
            : null;
        if (!string.IsNullOrWhiteSpace(environmentSearchPath))
        {
            foreach (var path in environmentSearchPath.Split(
                         Path.PathSeparator,
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                resolvedDirectories.Add(path);
                implicitDirectories.Add(path);
            }
        }

        // Platform dispatch templates are a source-build concern. Once an SDK
        // is active, release/stage packages already contain the selected
        // System.Runtime.Platform module and ordinary -I/STARK_PATH roots must
        // not be allowed to replace it. A development SDK may opt back into
        // templates only through its manifest-declared source roots below.
        var targetAwareSearchDirectories = activeSdk.IsActive
            ? new List<string>()
            : new List<string>(resolvedDirectories);

        IModuleSourceResolver? ordinaryResolver = null;
        if (resolvedDirectories.Count != 0)
        {
            fileSystemResolver = new FileSystemModuleResolver(resolvedDirectories, targetInfo, implicitDirectories);
            ordinaryResolver = fileSystemResolver;
        }
        else
        {
            fileSystemResolver = null;
        }

        var sdkResolvers = new List<IModuleSourceResolver>();
        IReadOnlyList<string> developmentSourceRoots = Array.Empty<string>();
        if (activeSdk.PackageResolver is not null)
        {
            sdkResolvers.Add(activeSdk.PackageResolver);
        }

        // Explicit package roots sit between installed SDK packages and the
        // development SDK's source fallbacks. This lets `-I vendor/dist` use
        // the package (and its native metadata) while a release SDK's indexed
        // package remains authoritative for its reserved module names.
        var explicitSearchDirectories = searchDirectories
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .ToArray();
        if (activeSdk.IsActive && explicitSearchDirectories.Length != 0)
        {
            sdkResolvers.Add(new PackageOnlyModuleResolver(explicitSearchDirectories, targetInfo));
        }

        if (activeSdk.Manifest?.Manifest is { Kind: SdkDistributionKind.Development } developmentManifest
            && activeSdk.Manifest.PackageIndex is not null
            && developmentManifest.DevelopmentSourceRoots.Count != 0)
        {
            developmentSourceRoots = developmentManifest.DevelopmentSourceRoots
                .Select(activeSdk.Manifest.PackageIndex.ResolvePath)
                .ToArray();
            sdkResolvers.Add(new FileSystemModuleResolver(developmentSourceRoots, targetInfo));
            targetAwareSearchDirectories.AddRange(developmentSourceRoots);
        }

        IModuleSourceResolver? resolver = ordinaryResolver;
        if (sdkResolvers.Count != 0)
        {
            var sdkResolver = sdkResolvers.Count == 1
                ? sdkResolvers[0]
                : new CompositeModuleResolver(sdkResolvers);
            resolver = new ReservedSdkModuleResolver(
                sdkResolver,
                ordinaryResolver,
                activeSdk.Root!.RootPath,
                activeSdk.Manifest!.Manifest!.Target.Id,
                developmentSourceRoots);
        }
        else if (resolver is not null)
        {
            resolver = new MissingSdkModuleDiagnosticResolver(resolver);
        }

        if (resolver is null)
        {
            return null;
        }

        if (targetInfo is not null && targetAwareSearchDirectories.Count != 0)
        {
            resolver = new TargetAwareStdLibModuleResolver(resolver, targetAwareSearchDirectories, targetInfo);
        }

        return resolver;
    }
}
