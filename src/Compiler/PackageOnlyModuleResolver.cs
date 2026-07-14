namespace Stark.Compiler;

/// <summary>
/// Restricts an explicit search-root resolver to package images. Development
/// SDK source roots are fallback implementations of official modules; an
/// explicitly selected package must be able to replace that fallback without
/// also allowing arbitrary project source to shadow an installed SDK package.
/// </summary>
internal sealed class PackageOnlyModuleResolver : IModuleSourceResolver, IModuleDocumentResolver
{
    private readonly FileSystemModuleResolver _inner;

    public PackageOnlyModuleResolver(
        IEnumerable<string> searchDirectories,
        LlvmTargetInfo? targetInfo)
    {
        _inner = new FileSystemModuleResolver(searchDirectories, targetInfo);
    }

    public bool TryResolveModule(string moduleName, out ResolvedModuleReference module) =>
        _inner.TryResolvePackageModule(moduleName, out module);

    public bool TryLoadModuleSource(
        ResolvedModuleReference module,
        out string sourceText,
        out string? filePath) =>
        _inner.TryLoadModuleSource(module, out sourceText, out filePath);

    public bool TryLoadModuleDocument(
        ResolvedModuleReference module,
        LlvmTargetInfo? targetInfo,
        out LoadedModuleDocument document) =>
        _inner.TryLoadModuleDocument(module, targetInfo, out document);
}
