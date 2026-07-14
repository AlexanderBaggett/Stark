using Stark.Compiler.LlvmIrEmission;

namespace Stark.Compiler;

internal static class TargetCompatibilityValidator
{
    private const string Stage = "target-compatibility";

    private static readonly IReadOnlyDictionary<string, NamedTypeSymbol> EmptyNamedTypes =
        new Dictionary<string, NamedTypeSymbol>(StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, EnumLayoutSymbol> EmptyEnumLayouts =
        new Dictionary<string, EnumLayoutSymbol>(StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, ConcreteTypeLayout> EmptyConcreteLayouts =
        new Dictionary<string, ConcreteTypeLayout>(StringComparer.Ordinal);

    private static readonly IReadOnlyList<string> CAliasNames =
    [
        "c_char",
        "c_schar",
        "c_uchar",
        "c_short",
        "c_ushort",
        "c_int",
        "c_uint",
        "c_long",
        "c_ulong",
        "c_longlong",
        "c_ulonglong",
        "c_size_t",
        "c_ptrdiff_t",
        "c_void"
    ];

    public static IReadOnlyList<CompilerDiagnostic> ValidateBeforeBackendUse(
        CompilationResult result,
        LlvmTargetInfo? targetInfo,
        string? inputPath,
        string? activeBuildProfile = null)
    {
        var diagnostics = new List<CompilerDiagnostic>();
        ValidateActiveTarget(targetInfo, inputPath, diagnostics);

        if (targetInfo is not null)
        {
            ValidateCompilationAggregateLayouts(result, targetInfo, inputPath, diagnostics);
        }

        if (result.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules)
            && loadedModules is not null)
        {
            foreach (var module in loadedModules.ImportedModules)
            {
                ValidateLoadedPackageTarget(module, targetInfo, diagnostics);
                ValidateLoadedPackageBuildProfile(module, activeBuildProfile, diagnostics);
            }
        }

        return diagnostics;
    }

    public static void ValidateLoadedPackageTarget(
        LoadedModuleDocument document,
        LlvmTargetInfo? activeTarget,
        List<CompilerDiagnostic> diagnostics)
    {
        if (document.PackageImageFacts?.Target is not { } packageTarget)
        {
            return;
        }

        var location = new SourceLocation(
            document.Reference.ManifestPath ?? document.Reference.FilePath,
            1,
            1);

        if (CreatePackageTargetManifest(activeTarget) is not { } active)
        {
            diagnostics.Add(new CompilerDiagnostic(
                Code: "STK7310",
                Severity: DiagnosticSeverity.Error,
                Message: $"Package image module '{document.Reference.ModuleName}' records target-specific facts for '{packageTarget.Triple}', but the active compilation target is unresolved.",
                Stage: Stage,
                Location: location));
            return;
        }

        ComparePackageTarget(
            packageTarget,
            active,
            document.Reference.ModuleName,
            location,
            diagnostics,
            document.Reference.IsSdkPackage);
    }

    private static void ValidateLoadedPackageBuildProfile(
        LoadedModuleDocument document,
        string? activeBuildProfile,
        List<CompilerDiagnostic> diagnostics)
    {
        if (document.PackageImageFacts?.BuildProfile is not { } packageProfile)
        {
            return;
        }

        if (!IsSupportedPackageBuildProfile(packageProfile.Name))
        {
            diagnostics.Add(new CompilerDiagnostic(
                Code: "STK7325",
                Severity: DiagnosticSeverity.Error,
                Message: $"Package image module '{document.Reference.ModuleName}' was built for unsupported profile '{packageProfile.Name}'. Expected dev or release.",
                Stage: Stage,
                Location: new SourceLocation(
                    document.Reference.ManifestPath ?? document.Reference.FilePath,
                    1,
                    1)));
            return;
        }

        if (string.IsNullOrWhiteSpace(activeBuildProfile))
        {
            return;
        }

        var normalizedActiveProfile = NormalizeBuildProfile(activeBuildProfile);
        if (normalizedActiveProfile is null)
        {
            diagnostics.Add(new CompilerDiagnostic(
                Code: "STK7324",
                Severity: DiagnosticSeverity.Error,
                Message: $"Active build profile '{activeBuildProfile}' is not supported for package compatibility checks. Expected dev or release.",
                Stage: Stage,
                Location: new SourceLocation(
                    document.Reference.ManifestPath ?? document.Reference.FilePath,
                    1,
                    1)));
            return;
        }

        if (!IsPackageBuildProfileCompatible(packageProfile.Name, normalizedActiveProfile))
        {
            diagnostics.Add(new CompilerDiagnostic(
                Code: "STK7325",
                Severity: DiagnosticSeverity.Error,
                Message: $"Package image module '{document.Reference.ModuleName}' was built for profile '{packageProfile.Name}', but the active build profile is '{normalizedActiveProfile}'. Release builds require release-built packages.",
                Stage: Stage,
                Location: new SourceLocation(
                    document.Reference.ManifestPath ?? document.Reference.FilePath,
                    1,
                    1)));
        }
    }

    internal static bool IsPackageBuildProfileCompatible(
        string packageBuildProfile,
        string activeBuildProfile)
    {
        if (!IsSupportedPackageBuildProfile(packageBuildProfile))
        {
            return false;
        }

        var normalizedActiveProfile = NormalizeBuildProfile(activeBuildProfile);
        if (normalizedActiveProfile is null)
        {
            return false;
        }

        // A release package is safe to reuse from either consumer profile.
        // A dev package may contain development-only code generation choices,
        // so it must never flow into a release build.
        return string.Equals(packageBuildProfile, "release", StringComparison.Ordinal)
            || string.Equals(normalizedActiveProfile, "dev", StringComparison.Ordinal);
    }

    private static bool IsSupportedPackageBuildProfile(string value) =>
        string.Equals(value, "dev", StringComparison.Ordinal)
        || string.Equals(value, "release", StringComparison.Ordinal);

    public static StarkPackageTargetManifest? CreatePackageTargetManifest(LlvmTargetInfo? targetInfo)
    {
        if (targetInfo is null || string.IsNullOrWhiteSpace(targetInfo.Triple))
        {
            return null;
        }

        var cDataModel = StarkCDataModelFacts.TryResolve(targetInfo, out var dataModel)
            ? new StarkPackageCDataModelManifest(
                dataModel.Kind.ToString(),
                dataModel.CharIsSigned,
                dataModel.PointerBitWidth,
                dataModel.LongBitWidth,
                dataModel.SizeTBitWidth,
                dataModel.PtrDiffTBitWidth)
            : null;
        var pointerLayout = LlvmAggregateEmissionSupport.TryGetConcreteTypeLayout(
            StarkTypeSymbols.RawPointer(StarkTypeSymbols.Integer(8), isMutable: false),
            targetInfo,
            EmptyNamedTypes,
            EmptyEnumLayouts,
            EmptyConcreteLayouts);

        return new StarkPackageTargetManifest(
            targetInfo.Triple.Trim(),
            NormalizeOptional(targetInfo.DataLayout),
            NormalizeOptional(targetInfo.Cpu),
            NormalizeList(targetInfo.Features),
            FormatRelocationModel(targetInfo.RelocationModel),
            targetInfo.CodeModel?.ToString().ToLowerInvariant(),
            cDataModel,
            pointerLayout is null
                ? null
                : new StarkPackageAggregateLayoutManifest(
                    pointerLayout.SizeBytes,
                    pointerLayout.AlignmentBytes));
    }

    public static void ValidateManifestTarget(
        StarkPackageManifest manifest,
        string? manifestPath,
        List<CompilerDiagnostic> diagnostics)
    {
        if (manifest.Target is not { } target)
        {
            return;
        }

        var location = new SourceLocation(manifestPath, 1, 1);
        if (string.IsNullOrWhiteSpace(target.Triple))
        {
            diagnostics.Add(new CompilerDiagnostic(
                Code: "STK7320",
                Severity: DiagnosticSeverity.Error,
                Message: "Package image target triple must not be empty when target facts are present.",
                Stage: "package-image",
                Location: location));
            return;
        }

        var targetInfo = new LlvmTargetInfo(
            target.Triple,
            target.DataLayout,
            target.Cpu,
            target.Features,
            ParseRelocationModel(target.RelocationModel),
            ParseCodeModel(target.CodeModel));
        ValidateActiveTarget(targetInfo, manifestPath, diagnostics, stage: "package-image");

        if (target.CDataModel is not null
            && StarkCDataModelFacts.TryResolve(targetInfo, out var resolvedDataModel)
            && !CDataModelsMatch(target.CDataModel, ToManifest(resolvedDataModel)))
        {
            diagnostics.Add(new CompilerDiagnostic(
                Code: "STK7321",
                Severity: DiagnosticSeverity.Error,
                Message: $"Package image C data-model facts do not match target triple '{target.Triple}'.",
                Stage: "package-image",
                Location: location));
        }

        if (target.AggregateLayout is not null
            && CreatePackageTargetManifest(targetInfo)?.AggregateLayout is { } resolvedLayout
            && !AggregateLayoutsMatch(target.AggregateLayout, resolvedLayout))
        {
            diagnostics.Add(new CompilerDiagnostic(
                Code: "STK7322",
                Severity: DiagnosticSeverity.Error,
                Message: $"Package image aggregate-layout facts do not match target triple '{target.Triple}'.",
                Stage: "package-image",
                Location: location));
        }
    }

    private static void ValidateActiveTarget(
        LlvmTargetInfo? targetInfo,
        string? inputPath,
        List<CompilerDiagnostic> diagnostics,
        string stage = Stage)
    {
        var location = new SourceLocation(inputPath, 1, 1);
        if (targetInfo is null)
        {
            diagnostics.Add(new CompilerDiagnostic(
                Code: "STK7300",
                Severity: DiagnosticSeverity.Error,
                Message: "Backend emission requires resolved target facts. Pass --target or configure toolchain discovery so the compiler can validate ABI and layout facts before LLVM lowering.",
                Stage: stage,
                Location: location));
            return;
        }

        if (string.IsNullOrWhiteSpace(targetInfo.Triple))
        {
            diagnostics.Add(new CompilerDiagnostic(
                Code: "STK7301",
                Severity: DiagnosticSeverity.Error,
                Message: "Backend emission requires a non-empty target triple.",
                Stage: stage,
                Location: location));
            return;
        }

        if (!TargetFeatureFacts.TryNormalizeDistinct(
                targetInfo.Features,
                out _,
                out var featureError))
        {
            diagnostics.Add(new CompilerDiagnostic(
                Code: "STK7331",
                Severity: DiagnosticSeverity.Error,
                Message: $"Target feature switches are invalid: {featureError}.",
                Stage: stage,
                Location: location));
        }

        if (!TryResolveArchitecture(targetInfo.Triple, out var architecture))
        {
            diagnostics.Add(new CompilerDiagnostic(
                Code: "STK7302",
                Severity: DiagnosticSeverity.Error,
                Message: $"Target triple '{targetInfo.Triple}' uses an unsupported architecture. Supported backend architectures are x86_64, aarch64, riscv64, x86, and arm.",
                Stage: stage,
                Location: location));
        }

        if (!TryResolveOperatingSystem(targetInfo.Triple, out _))
        {
            diagnostics.Add(new CompilerDiagnostic(
                Code: "STK7303",
                Severity: DiagnosticSeverity.Error,
                Message: $"Target triple '{targetInfo.Triple}' uses an unsupported operating system. Supported backend operating systems are Linux, Windows, and macOS.",
                Stage: stage,
                Location: location));
        }

        if (!StarkCDataModelFacts.TryResolve(targetInfo, out var cDataModel))
        {
            diagnostics.Add(new CompilerDiagnostic(
                Code: "STK7304",
                Severity: DiagnosticSeverity.Error,
                Message: $"Target triple '{targetInfo.Triple}' does not resolve to a Stark C data model.",
                Stage: stage,
                Location: location));
        }
        else
        {
            ValidateCAliases(targetInfo, diagnostics, location, stage);
        }

        ValidateDataLayout(targetInfo, architecture, cDataModel, diagnostics, location, stage);

        var pointerLayout = LlvmAggregateEmissionSupport.TryGetConcreteTypeLayout(
            StarkTypeSymbols.RawPointer(StarkTypeSymbols.Integer(8), isMutable: false),
            targetInfo,
            EmptyNamedTypes,
            EmptyEnumLayouts,
            EmptyConcreteLayouts);
        if (pointerLayout is null)
        {
            diagnostics.Add(new CompilerDiagnostic(
                Code: "STK7308",
                Severity: DiagnosticSeverity.Error,
                Message: $"Target triple '{targetInfo.Triple}' does not provide enough pointer layout facts for aggregate lowering.",
                Stage: stage,
                Location: location));
        }
        else if (cDataModel is not null && pointerLayout.SizeBytes * 8 != cDataModel.PointerBitWidth)
        {
            diagnostics.Add(new CompilerDiagnostic(
                Code: "STK7309",
                Severity: DiagnosticSeverity.Error,
                Message: $"Target triple '{targetInfo.Triple}' has inconsistent pointer facts: aggregate layout uses {pointerLayout.SizeBytes * 8}-bit pointers but the C data model uses {cDataModel.PointerBitWidth}-bit pointers.",
                Stage: stage,
                Location: location));
        }
    }

    private static void ValidateCompilationAggregateLayouts(
        CompilationResult result,
        LlvmTargetInfo targetInfo,
        string? inputPath,
        List<CompilerDiagnostic> diagnostics)
    {
        if (!result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeModel)
            || typeModel is null)
        {
            return;
        }

        result.Artifacts.TryGet(CompilerArtifactKeys.EnumLayoutModel, out EnumLayoutModel? enumLayoutModel);
        var publishedLayouts = new Dictionary<string, ConcreteTypeLayout>(StringComparer.Ordinal);
        if (result.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules)
            && loadedModules is not null)
        {
            foreach (var module in loadedModules.Modules.Values)
            {
                if (module.PackageImageFacts is null)
                {
                    continue;
                }

                foreach (var (name, layout) in module.PackageImageFacts.ConcreteLayouts)
                {
                    publishedLayouts.TryAdd(name, layout);
                }
            }
        }

        foreach (var namedType in typeModel.NamedTypes.Values
                     .Where(static type => type.Kind is DeclarationKind.Struct or DeclarationKind.Record or DeclarationKind.Enum)
                     .OrderBy(static type => type.Name, StringComparer.Ordinal))
        {
            if (LlvmAggregateEmissionSupport.TryGetConcreteTypeLayout(
                    StarkTypeSymbols.Named(namedType.Name),
                    targetInfo,
                    typeModel.NamedTypes,
                    enumLayoutModel?.Layouts ?? EmptyEnumLayouts,
                    publishedLayouts.Count == 0 ? EmptyConcreteLayouts : publishedLayouts) is not null)
            {
                continue;
            }

            diagnostics.Add(new CompilerDiagnostic(
                Code: "STK7330",
                Severity: DiagnosticSeverity.Error,
                Message: $"Type '{namedType.Name}' does not have a complete aggregate layout for target '{targetInfo.Triple}'.",
                Stage: Stage,
                Location: new SourceLocation(inputPath, 1, 1)));
        }
    }

    internal static void ComparePackageTarget(
        StarkPackageTargetManifest packageTarget,
        StarkPackageTargetManifest active,
        string moduleName,
        SourceLocation location,
        List<CompilerDiagnostic> diagnostics,
        bool isSdkPackage = false)
    {
        var triplesCompatible = isSdkPackage
            ? SdkTargetCompatibility.ArePackageAndActiveTriplesCompatible(
                packageTarget.Triple,
                active.Triple)
            : StringEquals(packageTarget.Triple, active.Triple);
        if (!triplesCompatible)
        {
            diagnostics.Add(new CompilerDiagnostic(
                Code: "STK7311",
                Severity: DiagnosticSeverity.Error,
                Message: isSdkPackage
                    ? $"SDK package image module '{moduleName}' was built for target triple '{packageTarget.Triple}', whose structured architecture/OS/ABI/deployment facts are incompatible with active target '{active.Triple}'. Install an SDK package compatible with the active target."
                    : $"Package image module '{moduleName}' was built for target triple '{packageTarget.Triple}', but the active target is '{active.Triple}'. Rebuild the package for the active target.",
                Stage: Stage,
                Location: location));
        }

        if (!string.IsNullOrWhiteSpace(packageTarget.DataLayout)
            && !DataLayoutsEqual(packageTarget.DataLayout, active.DataLayout))
        {
            diagnostics.Add(new CompilerDiagnostic(
                Code: "STK7312",
                Severity: DiagnosticSeverity.Error,
                Message: $"Package image module '{moduleName}' was built with a different LLVM data layout than the active target.",
                Stage: Stage,
                Location: location));
        }

        if (!string.IsNullOrWhiteSpace(packageTarget.Cpu)
            && !StringEquals(packageTarget.Cpu, active.Cpu)
            && !(isSdkPackage && IsGenericCpu(packageTarget.Cpu)))
        {
            diagnostics.Add(new CompilerDiagnostic(
                Code: "STK7313",
                Severity: DiagnosticSeverity.Error,
                Message: $"Package image module '{moduleName}' was built for CPU '{packageTarget.Cpu}', but the active target CPU is '{active.Cpu ?? "<default>"}'.",
                Stage: Stage,
                Location: location));
        }

        if (packageTarget.Features is { Count: > 0 })
        {
            var missingSdkFeatures = isSdkPackage
                ? TargetFeatureFacts.GetMissingEnabledFeatures(packageTarget.Features, active.Features)
                : Array.Empty<string>();
            if ((!isSdkPackage && !StringListsEqual(packageTarget.Features, active.Features))
                || missingSdkFeatures.Count != 0)
            {
                diagnostics.Add(new CompilerDiagnostic(
                    Code: "STK7314",
                    Severity: DiagnosticSeverity.Error,
                    Message: isSdkPackage
                        ? $"SDK package image module '{moduleName}' requires target features not enabled by the active target: {string.Join(", ", missingSdkFeatures)}."
                        : $"Package image module '{moduleName}' was built with target features that do not match the active target.",
                    Stage: Stage,
                    Location: location));
            }
        }

        if (!StringEquals(packageTarget.RelocationModel, active.RelocationModel))
        {
            diagnostics.Add(new CompilerDiagnostic(
                Code: "STK7315",
                Severity: DiagnosticSeverity.Error,
                Message: $"Package image module '{moduleName}' was built with relocation model '{packageTarget.RelocationModel}', but the active target uses '{active.RelocationModel}'.",
                Stage: Stage,
                Location: location));
        }

        if (!string.IsNullOrWhiteSpace(packageTarget.CodeModel)
            && !StringEquals(packageTarget.CodeModel, active.CodeModel))
        {
            diagnostics.Add(new CompilerDiagnostic(
                Code: "STK7316",
                Severity: DiagnosticSeverity.Error,
                Message: $"Package image module '{moduleName}' was built with code model '{packageTarget.CodeModel}', but the active target uses '{active.CodeModel ?? "<default>"}'.",
                Stage: Stage,
                Location: location));
        }

        if (packageTarget.CDataModel is not null)
        {
            if (active.CDataModel is null)
            {
                diagnostics.Add(new CompilerDiagnostic(
                    Code: "STK7317",
                    Severity: DiagnosticSeverity.Error,
                    Message: $"Package image module '{moduleName}' records C data-model facts, but the active target does not resolve C aliases.",
                    Stage: Stage,
                    Location: location));
            }
            else if (!CDataModelsMatch(packageTarget.CDataModel, active.CDataModel))
            {
                diagnostics.Add(new CompilerDiagnostic(
                    Code: "STK7318",
                    Severity: DiagnosticSeverity.Error,
                    Message: $"Package image module '{moduleName}' has C data-model facts that do not match the active target.",
                    Stage: Stage,
                    Location: location));
            }
        }

        if (packageTarget.AggregateLayout is not null)
        {
            if (active.AggregateLayout is null)
            {
                diagnostics.Add(new CompilerDiagnostic(
                    Code: "STK7319",
                    Severity: DiagnosticSeverity.Error,
                    Message: $"Package image module '{moduleName}' records aggregate-layout facts, but the active target does not resolve aggregate layout.",
                    Stage: Stage,
                    Location: location));
            }
            else if (!AggregateLayoutsMatch(packageTarget.AggregateLayout, active.AggregateLayout))
            {
                diagnostics.Add(new CompilerDiagnostic(
                    Code: "STK7323",
                    Severity: DiagnosticSeverity.Error,
                    Message: $"Package image module '{moduleName}' has aggregate-layout facts that do not match the active target.",
                    Stage: Stage,
                    Location: location));
            }
        }
    }

    private static void ValidateCAliases(
        LlvmTargetInfo targetInfo,
        List<CompilerDiagnostic> diagnostics,
        SourceLocation location,
        string stage)
    {
        foreach (var aliasName in CAliasNames)
        {
            if (!StarkCDataModelFacts.TryResolveAlias(
                    StarkCDataModelFacts.QualifyAliasName(aliasName),
                    targetInfo,
                    out _,
                    out var diagnostic)
                || string.IsNullOrWhiteSpace(diagnostic))
            {
                continue;
            }

            diagnostics.Add(new CompilerDiagnostic(
                Code: "STK7305",
                Severity: DiagnosticSeverity.Error,
                Message: diagnostic,
                Stage: stage,
                Location: location));
        }
    }

    private static void ValidateDataLayout(
        LlvmTargetInfo targetInfo,
        StarkAsmArchitecture architecture,
        StarkCDataModel? cDataModel,
        List<CompilerDiagnostic> diagnostics,
        SourceLocation location,
        string stage)
    {
        if (string.IsNullOrWhiteSpace(targetInfo.DataLayout))
        {
            return;
        }

        if (!TryGetPointerLayoutFromDataLayout(targetInfo.DataLayout, out var pointerSizeBits, out var pointerAlignmentBits, out var malformedToken))
        {
            diagnostics.Add(new CompilerDiagnostic(
                Code: "STK7306",
                Severity: DiagnosticSeverity.Error,
                Message: malformedToken is null
                    ? $"Target data layout for '{targetInfo.Triple}' does not define a default pointer layout token such as 'p:64:64'."
                    : $"Target data layout for '{targetInfo.Triple}' has malformed pointer layout token '{malformedToken}'.",
                Stage: stage,
                Location: location));
            return;
        }

        if (pointerSizeBits % 8 != 0 || pointerAlignmentBits % 8 != 0 || pointerSizeBits <= 0 || pointerAlignmentBits <= 0)
        {
            diagnostics.Add(new CompilerDiagnostic(
                Code: "STK7306",
                Severity: DiagnosticSeverity.Error,
                Message: $"Target data layout for '{targetInfo.Triple}' must use positive byte-addressable pointer size and alignment facts.",
                Stage: stage,
                Location: location));
            return;
        }

        var expectedPointerBits = cDataModel?.PointerBitWidth ?? ExpectedPointerBitWidth(architecture);
        if (expectedPointerBits is int expected && pointerSizeBits != expected)
        {
            diagnostics.Add(new CompilerDiagnostic(
                Code: "STK7307",
                Severity: DiagnosticSeverity.Error,
                Message: $"Target data layout for '{targetInfo.Triple}' uses {pointerSizeBits}-bit pointers, but the target triple/C data model requires {expected}-bit pointers.",
                Stage: stage,
                Location: location));
        }
    }

    private static bool TryGetPointerLayoutFromDataLayout(
        string dataLayout,
        out int pointerSizeBits,
        out int pointerAlignmentBits,
        out string? malformedToken)
    {
        pointerSizeBits = 0;
        pointerAlignmentBits = 0;
        malformedToken = null;

        foreach (var token in dataLayout.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!token.StartsWith("p:", StringComparison.Ordinal)
                && !token.StartsWith("p0:", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = token.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 3
                || !int.TryParse(parts[1], out pointerSizeBits)
                || !int.TryParse(parts[2], out pointerAlignmentBits))
            {
                malformedToken = token;
                return false;
            }

            return true;
        }

        return false;
    }

    private static string? NormalizeBuildProfile(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized is "dev" or "release" ? normalized : null;
    }

    private static bool TryResolveArchitecture(string triple, out StarkAsmArchitecture architecture)
    {
        var dash = triple.IndexOf('-');
        var architectureText = dash >= 0 ? triple[..dash] : triple;
        return StarkAsmArchitectureFacts.TryParseArchitectureName(architectureText, out architecture);
    }

    private static bool TryResolveOperatingSystem(string triple, out StarkTargetOperatingSystem operatingSystem)
    {
        var lower = triple.ToLowerInvariant();
        if (lower.Contains("windows", StringComparison.Ordinal)
            || lower.Contains("win32", StringComparison.Ordinal)
            || lower.Contains("mingw", StringComparison.Ordinal)
            || lower.Contains("msvc", StringComparison.Ordinal))
        {
            operatingSystem = StarkTargetOperatingSystem.Windows;
            return true;
        }

        if (lower.Contains("linux", StringComparison.Ordinal))
        {
            operatingSystem = StarkTargetOperatingSystem.Linux;
            return true;
        }

        if (lower.Contains("darwin", StringComparison.Ordinal)
            || lower.Contains("macos", StringComparison.Ordinal)
            || lower.Contains("apple", StringComparison.Ordinal))
        {
            operatingSystem = StarkTargetOperatingSystem.MacOS;
            return true;
        }

        operatingSystem = StarkTargetOperatingSystem.Unknown;
        return false;
    }

    private static int? ExpectedPointerBitWidth(StarkAsmArchitecture architecture)
    {
        return architecture switch
        {
            StarkAsmArchitecture.X86 or StarkAsmArchitecture.Arm32 => 32,
            StarkAsmArchitecture.X86_64 or StarkAsmArchitecture.AArch64 or StarkAsmArchitecture.RiscV64 => 64,
            _ => null
        };
    }

    private static StarkPackageCDataModelManifest ToManifest(StarkCDataModel dataModel)
    {
        return new StarkPackageCDataModelManifest(
            dataModel.Kind.ToString(),
            dataModel.CharIsSigned,
            dataModel.PointerBitWidth,
            dataModel.LongBitWidth,
            dataModel.SizeTBitWidth,
            dataModel.PtrDiffTBitWidth);
    }

    private static bool CDataModelsMatch(StarkPackageCDataModelManifest left, StarkPackageCDataModelManifest right)
    {
        return StringEquals(left.Kind, right.Kind)
            && left.CharIsSigned == right.CharIsSigned
            && left.PointerBitWidth == right.PointerBitWidth
            && left.LongBitWidth == right.LongBitWidth
            && left.SizeTBitWidth == right.SizeTBitWidth
            && left.PtrDiffTBitWidth == right.PtrDiffTBitWidth;
    }

    private static bool AggregateLayoutsMatch(StarkPackageAggregateLayoutManifest left, StarkPackageAggregateLayoutManifest right)
    {
        return left.PointerSizeBytes == right.PointerSizeBytes
            && left.PointerAlignmentBytes == right.PointerAlignmentBytes;
    }

    private static IReadOnlyList<string>? NormalizeList(IReadOnlyList<string>? values)
    {
        var normalized = values?
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .ToArray();
        return normalized is { Length: > 0 } ? normalized : null;
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string FormatRelocationModel(LlvmRelocationModel model)
    {
        return model switch
        {
            LlvmRelocationModel.Static => "static",
            LlvmRelocationModel.Pic => "pic",
            LlvmRelocationModel.Pie => "pie",
            _ => "default"
        };
    }

    private static LlvmRelocationModel ParseRelocationModel(string? text)
    {
        return text?.Trim().ToLowerInvariant() switch
        {
            "static" => LlvmRelocationModel.Static,
            "pic" => LlvmRelocationModel.Pic,
            "pie" => LlvmRelocationModel.Pie,
            _ => LlvmRelocationModel.Default
        };
    }

    private static LlvmCodeModel? ParseCodeModel(string? text)
    {
        return text?.Trim().ToLowerInvariant() switch
        {
            "tiny" => LlvmCodeModel.Tiny,
            "small" => LlvmCodeModel.Small,
            "kernel" => LlvmCodeModel.Kernel,
            "medium" => LlvmCodeModel.Medium,
            "large" => LlvmCodeModel.Large,
            _ => null
        };
    }

    private static bool StringEquals(string? left, string? right)
    {
        return string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGenericCpu(string? value)
    {
        var normalized = NormalizeOptional(value);
        return normalized is null || string.Equals(normalized, "generic", StringComparison.OrdinalIgnoreCase);
    }

    private static bool DataLayoutsEqual(string? left, string? right)
    {
        return string.Equals(left?.Trim(), right?.Trim(), StringComparison.Ordinal);
    }

    private static bool StringListsEqual(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
    {
        var normalizedLeft = NormalizeList(left) ?? [];
        var normalizedRight = NormalizeList(right) ?? [];
        return normalizedLeft.Count == normalizedRight.Count
            && normalizedLeft.Zip(normalizedRight).All(static pair => StringEquals(pair.First, pair.Second));
    }
}
