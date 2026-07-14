namespace Stark.Compiler;

/// <summary>
/// Resolves modules from a small, ordered set of resolvers while preserving
/// the resolver that produced each reference for subsequent source/document
/// loading. The owner cache is safe for the compiler's parallel dependency
/// pipeline and avoids probing unrelated package images after resolution.
/// </summary>
internal sealed class CompositeModuleResolver :
    IModuleSourceResolver,
    IModuleDocumentResolver,
    IModuleResolutionDiagnosticProvider,
    IRootModuleDiagnosticProvider
{
    private readonly IReadOnlyList<IModuleSourceResolver> _resolvers;
    private readonly Dictionary<ModuleReferenceIdentity, IModuleSourceResolver> _owners = [];
    private readonly object _ownersLock = new();
    private readonly Dictionary<string, (string Code, string Message)> _resolutionDiagnostics =
        new(StringComparer.Ordinal);

    public CompositeModuleResolver(IEnumerable<IModuleSourceResolver> resolvers)
    {
        _resolvers = resolvers.Where(static resolver => resolver is not null).ToArray();
        if (_resolvers.Count == 0)
        {
            throw new ArgumentException("At least one module resolver is required.", nameof(resolvers));
        }
    }

    public bool TryResolveModule(string moduleName, out ResolvedModuleReference module)
    {
        foreach (var resolver in _resolvers)
        {
            if (!resolver.TryResolveModule(moduleName, out module))
            {
                if (resolver is IModuleResolutionDiagnosticProvider diagnosticProvider
                    && diagnosticProvider.TryGetUnresolvedModuleDiagnostic(
                        moduleName,
                        out var diagnosticCode,
                        out var diagnosticMessage))
                {
                    lock (_ownersLock)
                    {
                        _resolutionDiagnostics[moduleName] = (diagnosticCode, diagnosticMessage);
                    }

                    module = default!;
                    return false;
                }

                continue;
            }

            lock (_ownersLock)
            {
                _resolutionDiagnostics.Remove(moduleName);
                _owners[ModuleReferenceIdentity.Create(module)] = resolver;
            }

            return true;
        }

        module = default!;
        return false;
    }

    public bool TryGetUnresolvedModuleDiagnostic(
        string moduleName,
        out string code,
        out string message)
    {
        lock (_ownersLock)
        {
            if (_resolutionDiagnostics.TryGetValue(moduleName, out var diagnostic))
            {
                code = diagnostic.Code;
                message = diagnostic.Message;
                return true;
            }
        }

        foreach (var resolver in _resolvers)
        {
            if (resolver is IModuleResolutionDiagnosticProvider diagnosticProvider
                && diagnosticProvider.TryGetUnresolvedModuleDiagnostic(moduleName, out code, out message))
            {
                return true;
            }
        }

        code = string.Empty;
        message = string.Empty;
        return false;
    }

    public bool TryGetRootModuleDiagnostic(
        string moduleName,
        string? sourcePath,
        out string code,
        out string message)
    {
        foreach (var resolver in _resolvers)
        {
            if (resolver is IRootModuleDiagnosticProvider provider
                && provider.TryGetRootModuleDiagnostic(moduleName, sourcePath, out code, out message))
            {
                return true;
            }
        }

        code = string.Empty;
        message = string.Empty;
        return false;
    }

    public bool TryLoadModuleSource(ResolvedModuleReference module, out string sourceText, out string? filePath)
    {
        if (TryGetOwner(module, out var owner)
            && owner is not null
            && owner.TryLoadModuleSource(module, out sourceText, out filePath))
        {
            return true;
        }

        // Keep hand-constructed references and callers that resolve/load on
        // different composite instances useful without weakening precedence.
        foreach (var resolver in _resolvers)
        {
            if (ReferenceEquals(resolver, owner))
            {
                continue;
            }

            if (resolver.TryLoadModuleSource(module, out sourceText, out filePath))
            {
                return true;
            }
        }

        sourceText = string.Empty;
        filePath = module.FilePath;
        return false;
    }

    public bool TryLoadModuleDocument(
        ResolvedModuleReference module,
        LlvmTargetInfo? targetInfo,
        out LoadedModuleDocument document)
    {
        if (TryGetOwner(module, out var owner)
            && owner is not null
            && owner is IModuleDocumentResolver ownerDocumentResolver
            && ownerDocumentResolver.TryLoadModuleDocument(module, targetInfo, out document))
        {
            return true;
        }

        foreach (var resolver in _resolvers)
        {
            if (ReferenceEquals(resolver, owner)
                || resolver is not IModuleDocumentResolver documentResolver)
            {
                continue;
            }

            if (documentResolver.TryLoadModuleDocument(module, targetInfo, out document))
            {
                return true;
            }
        }

        document = default!;
        return false;
    }

    private bool TryGetOwner(ResolvedModuleReference module, out IModuleSourceResolver? owner)
    {
        lock (_ownersLock)
        {
            return _owners.TryGetValue(ModuleReferenceIdentity.Create(module), out owner);
        }
    }

    private sealed record ModuleReferenceIdentity(
        string ModuleName,
        string? FilePath,
        string? ManifestPath,
        string? LibraryPath)
    {
        public static ModuleReferenceIdentity Create(ResolvedModuleReference module) => new(
            module.ModuleName,
            module.FilePath,
            module.ManifestPath,
            module.LibraryPath);
    }
}
