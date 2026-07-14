using System.Security.Cryptography;
using System.Text;

namespace Stark.Compiler;

/// <summary>
/// Content-keyed cache for dependency-module LLVM emissions. A dependency
/// module's compile is a pure function of its own source, the sources (or
/// package images) of its transitive import closure, the codegen-relevant
/// options, the inline-clone seed set, and the compiler binary itself — so
/// the emitted module text can be reused across invocations, which removes
/// the dominant cost of from-source probe compiles (one full pipeline run
/// per dependency module).
///
/// Environment knobs: STARK_DEP_CACHE=0 disables, STARK_DEP_CACHE=&lt;dir&gt;
/// relocates (default ~/.stark/dep-llvm-cache), STARK_DEP_CACHE_VERIFY=1
/// recompiles on every hit and throws if the cached text differs (the
/// byte-identical gate), STARK_DEP_CACHE_LOG=1 prints per-invocation
/// hit/miss counts to stderr.
/// </summary>
public static class DependencyLlvmCache
{
    private static readonly Dictionary<string, string> ManifestHashCache = new(StringComparer.Ordinal);
    private static readonly object ManifestHashLock = new();
    private static string? _compilerIdentity;

    private static long _hits;
    private static long _misses;

    public static bool VerifyMode =>
        Environment.GetEnvironmentVariable("STARK_DEP_CACHE_VERIFY") == "1";

    public static bool LogMode =>
        Environment.GetEnvironmentVariable("STARK_DEP_CACHE_LOG") == "1";

    public static string? ResolveCacheDirectory()
    {
        var setting = Environment.GetEnvironmentVariable("STARK_DEP_CACHE");
        if (setting == "0")
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(setting))
        {
            return Path.GetFullPath(setting);
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home)
            ? null
            : Path.Combine(home, ".stark", "dep-llvm-cache");
    }

    /// <summary>
    /// Key over everything the dependency emission depends on. Returns null
    /// when the transitive closure cannot be resolved from the loaded graph
    /// (unknown import), in which case the caller compiles uncached.
    /// </summary>
    public static string? ComputeKey(
        string moduleName,
        string moduleSourceText,
        IReadOnlyDictionary<string, LoadedModuleDocument> graphModules,
        CompilerOptions rootOptions,
        IReadOnlySet<string>? importedInlineCloneSeedFunctions)
    {
        var builder = new StringBuilder();
        builder.Append("v1\n");
        builder.Append(CompilerIdentity()).Append('\n');

        var target = rootOptions.TargetInfo;
        builder.Append("target:").Append(target?.Triple).Append('|').Append(target?.DataLayout).Append('|')
            .Append(target?.Cpu).Append('|').Append(target?.Features is { Count: > 0 } features ? string.Join(',', features) : string.Empty).Append('|')
            .Append(target?.RelocationModel.ToString()).Append('|').Append(target?.CodeModel?.ToString()).Append('\n');
        builder.Append("options:")
            .Append(rootOptions.InternalizeModulePrivate ? '1' : '0')
            .Append(rootOptions.EnforceIntegerRangeStorageRules ? '1' : '0')
            .Append('\n');
        if (!string.IsNullOrWhiteSpace(rootOptions.SdkManifestIdentity))
        {
            builder.Append("sdk:").Append(rootOptions.SdkManifestIdentity).Append('\n');
        }

        if (importedInlineCloneSeedFunctions is not null)
        {
            builder.Append("seeds:");
            foreach (var seed in importedInlineCloneSeedFunctions.OrderBy(static name => name, StringComparer.Ordinal))
            {
                builder.Append(seed).Append(';');
            }

            builder.Append('\n');
        }

        // Transitive import closure in deterministic order. The module's own
        // freshly loaded text is hashed directly; other members contribute
        // their loaded document text (source) or package image content hash.
        var closure = new SortedSet<string>(StringComparer.Ordinal);
        if (!CollectClosure(moduleName, graphModules, closure))
        {
            return null;
        }

        foreach (var memberName in closure)
        {
            builder.Append("module:").Append(memberName).Append('\n');
            if (string.Equals(memberName, moduleName, StringComparison.Ordinal))
            {
                builder.Append(moduleSourceText).Append('\n');
                continue;
            }

            var member = graphModules[memberName];
            if (member.Reference.ManifestPath is { } manifestPath)
            {
                builder.Append("manifest:").Append(HashFile(manifestPath)).Append('\n');
            }
            else
            {
                builder.Append(member.ParseResult.SourceText).Append('\n');
            }
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    /// <summary>
    /// Captures the active SDK once per compiler invocation. Do not use the
    /// package-manifest hash cache here: one long-lived host process can select
    /// a rewritten development SDK on a later invocation.
    /// </summary>
    internal static string ComputeSdkManifestIdentity(string manifestPath)
    {
        var fullPath = Path.GetFullPath(manifestPath);
        try
        {
            using var stream = File.OpenRead(fullPath);
            return $"{fullPath}|{Convert.ToHexStringLower(SHA256.HashData(stream))}";
        }
        catch (IOException)
        {
            return $"{fullPath}|unreadable";
        }
        catch (UnauthorizedAccessException)
        {
            return $"{fullPath}|unreadable";
        }
    }

    public static bool TryGet(string cacheDirectory, string key, out string llvmText)
    {
        var path = EntryPath(cacheDirectory, key);
        try
        {
            if (File.Exists(path))
            {
                llvmText = File.ReadAllText(path);
                Interlocked.Increment(ref _hits);
                return true;
            }
        }
        catch (IOException)
        {
            // A concurrent writer or unreadable entry degrades to a miss.
        }

        llvmText = string.Empty;
        Interlocked.Increment(ref _misses);
        return false;
    }

    public static void Store(string cacheDirectory, string key, string llvmText)
    {
        var path = EntryPath(cacheDirectory, key);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporaryPath = $"{path}.{Environment.ProcessId}.tmp";
            File.WriteAllText(temporaryPath, llvmText);
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (IOException)
        {
            // Cache writes are best-effort; the compile already succeeded.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static void WriteLogSummary()
    {
        var hits = Interlocked.Read(ref _hits);
        var misses = Interlocked.Read(ref _misses);
        if (LogMode && hits + misses > 0)
        {
            Console.Error.WriteLine($"stark: dependency llvm cache: {hits} hit(s), {misses} miss(es)");
        }
    }

    private static string EntryPath(string cacheDirectory, string key) =>
        Path.Combine(cacheDirectory, key[..2], $"{key}.ll");

    private static bool CollectClosure(
        string moduleName,
        IReadOnlyDictionary<string, LoadedModuleDocument> graphModules,
        SortedSet<string> closure)
    {
        if (!closure.Add(moduleName))
        {
            return true;
        }

        if (!graphModules.TryGetValue(moduleName, out var document))
        {
            return false;
        }

        foreach (var import in document.SyntaxModel.Imports)
        {
            if (!CollectClosure(import.ModuleName, graphModules, closure))
            {
                return false;
            }
        }

        return true;
    }

    private static string CompilerIdentity()
    {
        if (_compilerIdentity is not null)
        {
            return _compilerIdentity;
        }

        var assembly = typeof(DependencyLlvmCache).Assembly;
        var identity = assembly.GetName().Version?.ToString() ?? "0";
        try
        {
            // Local dev builds share a version number, so the binary's own
            // stamp joins the key: a rebuilt compiler must never serve
            // emissions cached by the previous binary.
            var info = new FileInfo(assembly.Location);
            if (info.Exists)
            {
                identity = $"{identity}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            }
        }
        catch (IOException)
        {
        }

        _compilerIdentity = identity;
        return identity;
    }

    private static string HashFile(string path)
    {
        lock (ManifestHashLock)
        {
            if (ManifestHashCache.TryGetValue(path, out var cached))
            {
                return cached;
            }
        }

        string hash;
        try
        {
            using var stream = File.OpenRead(path);
            hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        catch (IOException)
        {
            hash = "unreadable";
        }
        catch (UnauthorizedAccessException)
        {
            hash = "unreadable";
        }

        lock (ManifestHashLock)
        {
            ManifestHashCache[path] = hash;
        }

        return hash;
    }
}
