namespace Stark.Compiler;

internal sealed record ActiveSdkResolution(
    SdkRootResolution? Root,
    SdkManifestLoadResult? Manifest,
    SdkPackageModuleResolver? PackageResolver,
    IReadOnlyList<SdkDiagnostic> Diagnostics,
    bool WasExplicitlyRequested)
{
    public bool IsActive => Root is not null
        && Manifest?.Succeeded == true
        && PackageResolver is not null
        && Diagnostics.Count == 0;
}

/// <summary>
/// Adapts the standalone SDK model/resolver to Stage0 CLI behavior. Keeping
/// activation and presentation here prevents SDK policy from spreading across
/// the already-large command driver and gives Stage1 a focused boundary to port.
/// </summary>
internal static class SdkCompilerIntegration
{
    public static ActiveSdkResolution Resolve(string? explicitSdkRoot)
    {
        var environmentSdkRoot = Environment.GetEnvironmentVariable(SdkRootResolver.EnvironmentVariableName);
        var wasExplicitlyRequested = !string.IsNullOrWhiteSpace(explicitSdkRoot)
            || !string.IsNullOrWhiteSpace(environmentSdkRoot);

        SdkRootResolution root;
        try
        {
            root = SdkRootResolver.Resolve(explicitSdkRoot);
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or InvalidOperationException
            or NotSupportedException
            or PathTooLongException
            or UnauthorizedAccessException)
        {
            return new ActiveSdkResolution(
                Root: null,
                Manifest: null,
                PackageResolver: null,
                [new SdkDiagnostic("STK7400", $"SDK root could not be resolved: {exception.Message}")],
                wasExplicitlyRequested);
        }

        // During the transition, unit-test hosts and a development compiler
        // invoked through `dotnet compiler.dll` may not have an SDK beside the
        // process executable. An explicit CLI/environment selection is always
        // authoritative and therefore always validated. A release apphost in
        // <sdk>/bin discovers sdk.json from its conventional parent.
        if (!File.Exists(root.ManifestPath) && !wasExplicitlyRequested)
        {
            var releaseManifestPath = Path.Combine(root.RootPath, SdkRootResolver.ReleaseManifestFileName);
            if (File.Exists(releaseManifestPath))
            {
                return new ActiveSdkResolution(
                    root,
                    Manifest: null,
                    PackageResolver: null,
                    [new SdkDiagnostic(
                        "STK7400",
                        $"Installed Stark release is incomplete: '{root.ManifestPath}' is missing beside '{releaseManifestPath}'.",
                        root.ManifestPath)],
                    wasExplicitlyRequested);
            }

            return new ActiveSdkResolution(
                root,
                Manifest: null,
                PackageResolver: null,
                Array.Empty<SdkDiagnostic>(),
                wasExplicitlyRequested);
        }

        var manifest = SdkManifestLoader.Load(root);
        if (!manifest.Succeeded)
        {
            return new ActiveSdkResolution(
                root,
                manifest,
                PackageResolver: null,
                manifest.Diagnostics,
                wasExplicitlyRequested);
        }

        // Normal compilation validates the manifest/index now, then validates
        // each package image and native payload only if one of its exact
        // modules is requested. Doctor forces the same resolver eagerly below.
        var resolver = SdkPackageModuleResolver.CreateLazy(manifest);
        return new ActiveSdkResolution(
            root,
            manifest,
            resolver.Resolver,
            resolver.Diagnostics,
            wasExplicitlyRequested);
    }

    public static async Task WriteDiagnosticsAsync(
        TextWriter stderr,
        IReadOnlyList<SdkDiagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            var path = string.IsNullOrWhiteSpace(diagnostic.Path)
                ? string.Empty
                : $" ({diagnostic.Path})";
            await stderr.WriteLineAsync($"error {diagnostic.Code}: {diagnostic.Message}{path}");
        }
    }

}
