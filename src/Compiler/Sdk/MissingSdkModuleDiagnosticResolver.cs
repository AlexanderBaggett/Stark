namespace Stark.Compiler;

/// <summary>
/// Preserves ordinary source/package resolution when no SDK is active, but
/// turns a failed official import into an installation diagnostic instead of
/// the generic missing-module error. It deliberately does not reserve source
/// roots: stdlib/vendor source builds remain possible through an explicitly
/// selected development SDK or the existing bootstrap paths.
/// </summary>
internal sealed class MissingSdkModuleDiagnosticResolver :
    IModuleSourceResolver,
    IModuleDocumentResolver,
    IModuleResolutionDiagnosticProvider,
    IRootModuleDiagnosticProvider
{
    private readonly IModuleSourceResolver _inner;

    public MissingSdkModuleDiagnosticResolver(IModuleSourceResolver inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public bool TryResolveModule(string moduleName, out ResolvedModuleReference module) =>
        _inner.TryResolveModule(moduleName, out module);

    public bool TryLoadModuleSource(
        ResolvedModuleReference module,
        out string sourceText,
        out string? filePath) =>
        _inner.TryLoadModuleSource(module, out sourceText, out filePath);

    public bool TryLoadModuleDocument(
        ResolvedModuleReference module,
        LlvmTargetInfo? targetInfo,
        out LoadedModuleDocument document)
    {
        if (_inner is IModuleDocumentResolver documentResolver)
        {
            return documentResolver.TryLoadModuleDocument(module, targetInfo, out document);
        }

        document = default!;
        return false;
    }

    public bool TryGetUnresolvedModuleDiagnostic(
        string moduleName,
        out string code,
        out string message)
    {
        if (_inner is IModuleResolutionDiagnosticProvider diagnosticProvider
            && diagnosticProvider.TryGetUnresolvedModuleDiagnostic(moduleName, out code, out message))
        {
            return true;
        }

        if (!ReservedSdkModuleResolver.IsReservedSdkModule(moduleName))
        {
            code = string.Empty;
            message = string.Empty;
            return false;
        }

        code = "STK7496";
        message = $"Official module '{moduleName}' could not be resolved because no active Stark SDK manifest "
            + "is available. Run 'stark doctor', install a complete Stark SDK archive, or select a development "
            + "SDK with --sdk-root/STARK_SDK_ROOT; STARK_PATH is not an SDK installation mechanism.";
        return true;
    }

    public bool TryGetRootModuleDiagnostic(
        string moduleName,
        string? sourcePath,
        out string code,
        out string message)
    {
        if (_inner is IRootModuleDiagnosticProvider rootProvider)
        {
            return rootProvider.TryGetRootModuleDiagnostic(moduleName, sourcePath, out code, out message);
        }

        code = string.Empty;
        message = string.Empty;
        return false;
    }
}
