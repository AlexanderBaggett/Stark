namespace Stark.Compiler;

/// <summary>
/// Routes distribution-owned namespaces exclusively through the active SDK.
/// This prevents an incomplete release from silently falling back to copied
/// source trees, STARK_PATH, or an incidental project dependency.
/// </summary>
internal sealed class ReservedSdkModuleResolver :
    IModuleSourceResolver,
    IModuleDocumentResolver,
    IModuleResolutionDiagnosticProvider,
    IRootModuleDiagnosticProvider
{
    private readonly IModuleSourceResolver _sdkResolver;
    private readonly IModuleSourceResolver? _ordinaryResolver;
    private readonly string _sdkRoot;
    private readonly string _targetId;
    private readonly IReadOnlyList<string> _developmentSourceRoots;

    public ReservedSdkModuleResolver(
        IModuleSourceResolver sdkResolver,
        IModuleSourceResolver? ordinaryResolver,
        string sdkRoot,
        string targetId,
        IEnumerable<string>? developmentSourceRoots = null)
    {
        _sdkResolver = sdkResolver;
        _ordinaryResolver = ordinaryResolver;
        _sdkRoot = sdkRoot;
        _targetId = targetId;
        _developmentSourceRoots = (developmentSourceRoots ?? Array.Empty<string>())
            .Select(SdkRootResolver.CanonicalizeRootPath)
            .Distinct(GetPathComparer())
            .ToArray();
    }

    public bool TryResolveModule(string moduleName, out ResolvedModuleReference module)
    {
        if (_sdkResolver.TryResolveModule(moduleName, out module))
        {
            return true;
        }

        if (IsReservedSdkModule(moduleName) || _ordinaryResolver is null)
        {
            module = default!;
            return false;
        }

        return _ordinaryResolver.TryResolveModule(moduleName, out module);
    }

    public bool TryLoadModuleSource(
        ResolvedModuleReference module,
        out string sourceText,
        out string? filePath)
    {
        if (_sdkResolver.TryLoadModuleSource(module, out sourceText, out filePath))
        {
            return true;
        }

        if (IsReservedSdkModule(module.ModuleName) || _ordinaryResolver is null)
        {
            sourceText = string.Empty;
            filePath = module.FilePath;
            return false;
        }

        return _ordinaryResolver.TryLoadModuleSource(module, out sourceText, out filePath);
    }

    public bool TryLoadModuleDocument(
        ResolvedModuleReference module,
        LlvmTargetInfo? targetInfo,
        out LoadedModuleDocument document)
    {
        if (_sdkResolver is IModuleDocumentResolver sdkDocumentResolver
            && sdkDocumentResolver.TryLoadModuleDocument(module, targetInfo, out document))
        {
            return true;
        }

        if (!IsReservedSdkModule(module.ModuleName)
            && _ordinaryResolver is IModuleDocumentResolver ordinaryDocumentResolver)
        {
            return ordinaryDocumentResolver.TryLoadModuleDocument(module, targetInfo, out document);
        }

        document = default!;
        return false;
    }

    public bool TryGetUnresolvedModuleDiagnostic(
        string moduleName,
        out string code,
        out string message)
    {
        if (_sdkResolver is IModuleResolutionDiagnosticProvider sdkDiagnosticProvider
            && sdkDiagnosticProvider.TryGetUnresolvedModuleDiagnostic(moduleName, out code, out message))
        {
            return true;
        }

        if (!IsReservedSdkModule(moduleName))
        {
            code = string.Empty;
            message = string.Empty;
            return false;
        }

        code = "STK7495";
        message = $"Official module '{moduleName}' is not included in the active Stark SDK "
            + $"for target '{_targetId}' (SDK root '{_sdkRoot}'). Install an SDK that advertises "
            + "the package for this target; project source trees and STARK_PATH cannot shadow official modules.";
        return true;
    }

    public bool TryGetRootModuleDiagnostic(
        string moduleName,
        string? sourcePath,
        out string code,
        out string message)
    {
        if (!IsReservedSdkModule(moduleName)
            || IsManifestDeclaredDevelopmentSource(sourcePath))
        {
            code = string.Empty;
            message = string.Empty;
            return false;
        }

        code = "STK7494";
        message = $"Source root module '{moduleName}' uses the official namespace reserved by the active "
            + $"Stark SDK for target '{_targetId}' (SDK root '{_sdkRoot}'). Rename the application module; "
            + "official System and Vendor modules may only be built from source roots declared by a development SDK.";
        return true;
    }

    private bool IsManifestDeclaredDevelopmentSource(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || _developmentSourceRoots.Count == 0)
        {
            return false;
        }

        string fullSourcePath;
        try
        {
            var sourceFullPath = Path.GetFullPath(sourcePath);
            var sourceDirectory = Path.GetDirectoryName(sourceFullPath);
            fullSourcePath = string.IsNullOrWhiteSpace(sourceDirectory)
                ? sourceFullPath
                : Path.Combine(
                    SdkRootResolver.CanonicalizeRootPath(sourceDirectory),
                    Path.GetFileName(sourceFullPath));
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException
            or IOException
            or UnauthorizedAccessException)
        {
            return false;
        }

        foreach (var developmentSourceRoot in _developmentSourceRoots)
        {
            var relativePath = Path.GetRelativePath(developmentSourceRoot, fullSourcePath);
            if (!Path.IsPathRooted(relativePath)
                && !string.Equals(relativePath, "..", StringComparison.Ordinal)
                && !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsReservedSdkModule(string moduleName) =>
        string.Equals(moduleName, "System", StringComparison.Ordinal)
        || moduleName.StartsWith("System.", StringComparison.Ordinal)
        || string.Equals(moduleName, "Vendor", StringComparison.Ordinal)
        || moduleName.StartsWith("Vendor.", StringComparison.Ordinal);

    private static StringComparer GetPathComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
