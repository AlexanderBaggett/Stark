namespace Stark.Compiler;

internal sealed record SdkTargetCompatibilityResult(
    IReadOnlyList<SdkDiagnostic> Diagnostics)
{
    public bool IsCompatible => Diagnostics.Count == 0;
}

internal static class SdkTargetCompatibility
{
    public static bool TryCreateDescriptorFromPackageTarget(
        string id,
        StarkPackageTargetManifest? packageTarget,
        out SdkTargetDescriptor descriptor,
        out string error)
    {
        descriptor = default!;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(id))
        {
            error = "SDK target ID must not be empty.";
            return false;
        }

        if (packageTarget is null || string.IsNullOrWhiteSpace(packageTarget.Triple))
        {
            error = "The package image does not contain a complete target descriptor.";
            return false;
        }

        var facts = CreatePackageFacts(packageTarget);
        if (facts.Architecture is null
            || facts.OperatingSystem is null
            || facts.Abi is null
            || facts.PointerBitWidth is null
            || facts.Endianness is null)
        {
            error = $"Package target triple '{packageTarget.Triple}' does not resolve complete architecture/OS/ABI/layout facts.";
            return false;
        }

        if (!TargetFeatureFacts.TryNormalizeDistinct(
                facts.Features,
                out var baselineFeatures,
                out var featureError))
        {
            error = $"Package target features are invalid: {featureError}.";
            return false;
        }

        if (!IsSupportedRelocationModel(facts.RelocationModel)
            || facts.CodeModel is not null && !IsSupportedCodeModel(facts.CodeModel))
        {
            error = $"Package target '{packageTarget.Triple}' contains an unsupported relocation or code model.";
            return false;
        }

        descriptor = new SdkTargetDescriptor(
            id.Trim(),
            packageTarget.Triple.Trim(),
            facts.Architecture,
            facts.OperatingSystem,
            facts.Abi,
            facts.PointerBitWidth.Value,
            facts.Endianness.Value,
            facts.DataLayout,
            facts.Cpu,
            baselineFeatures,
            facts.RelocationModel,
            facts.CodeModel,
            facts.CDataModel,
            facts.DeploymentMinimum?.ToString());

        var diagnostics = new List<SdkDiagnostic>();
        ValidateSdkDescriptor(descriptor, diagnosticPath: null, diagnostics);
        if (diagnostics.Count == 0)
        {
            return true;
        }

        error = string.Join("; ", diagnostics.Select(static diagnostic => diagnostic.Message));
        descriptor = default!;
        return false;
    }

    /// <summary>
    /// Compares the structured ABI identity carried by two LLVM triples for
    /// the final package-to-active-target validation pass. SDK packages have
    /// already been validated against the active SDK descriptor, so this
    /// helper exists only to avoid reintroducing raw spelling equality after
    /// that validation accepted a safe architecture/vendor alias.
    /// </summary>
    public static bool ArePackageAndActiveTriplesCompatible(
        string? packageTriple,
        string? activeTriple)
    {
        if (!TryParseTriple(packageTriple, out var packageFacts)
            || !TryParseTriple(activeTriple, out var activeFacts))
        {
            return false;
        }

        if (!StringEquals(packageFacts!.Architecture, activeFacts!.Architecture)
            || !StringEquals(packageFacts.OperatingSystem, activeFacts.OperatingSystem)
            || !StringEquals(packageFacts.Abi, activeFacts.Abi))
        {
            return false;
        }

        // A package built for an explicit deployment minimum may be consumed
        // only by an equal-or-newer application target. An unspecified package
        // minimum adds no constraint; an unspecified active minimum cannot
        // prove an explicit package requirement.
        return packageFacts.DeploymentMinimum is null
            || activeFacts.DeploymentMinimum is not null
                && activeFacts.DeploymentMinimum >= packageFacts.DeploymentMinimum;
    }

    public static SdkTargetCompatibilityResult ValidateActiveTarget(
        SdkTargetDescriptor sdkTarget,
        LlvmTargetInfo? activeTarget,
        string? diagnosticPath = null)
    {
        ArgumentNullException.ThrowIfNull(sdkTarget);
        var diagnostics = new List<SdkDiagnostic>();
        ValidateSdkDescriptor(sdkTarget, diagnosticPath, diagnostics);

        if (activeTarget is null || string.IsNullOrWhiteSpace(activeTarget.Triple))
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7480",
                "The active LLVM target is unresolved, so SDK target compatibility cannot be established.",
                diagnosticPath));
            return new SdkTargetCompatibilityResult(diagnostics.ToArray());
        }

        var activeFacts = CreateActiveFacts(activeTarget);
        CompareCommonFacts(
            sdkTarget,
            activeFacts,
            $"active target '{activeTarget.Triple}'",
            diagnosticPath,
            diagnostics);
        ValidateActiveCpu(sdkTarget, activeFacts, diagnosticPath, diagnostics);
        ValidateActiveFeatures(sdkTarget, activeFacts, diagnosticPath, diagnostics);
        ValidateActiveDeploymentMinimum(sdkTarget, activeFacts, diagnosticPath, diagnostics);
        return new SdkTargetCompatibilityResult(diagnostics.ToArray());
    }

    public static SdkTargetCompatibilityResult ValidatePackageTarget(
        SdkTargetDescriptor sdkTarget,
        StarkPackageTargetManifest? packageTarget,
        string packageId,
        string? diagnosticPath = null)
    {
        ArgumentNullException.ThrowIfNull(sdkTarget);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        var diagnostics = new List<SdkDiagnostic>();
        ValidateSdkDescriptor(sdkTarget, diagnosticPath, diagnostics);

        // Target-neutral package images remain valid. Once any package target
        // facts are present, every advertised fact is treated as authoritative.
        if (packageTarget is null)
        {
            return new SdkTargetCompatibilityResult(diagnostics.ToArray());
        }

        if (string.IsNullOrWhiteSpace(packageTarget.Triple))
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7480",
                $"SDK package '{packageId}' has target facts but no LLVM target triple.",
                diagnosticPath));
            return new SdkTargetCompatibilityResult(diagnostics.ToArray());
        }

        var packageFacts = CreatePackageFacts(packageTarget);
        CompareCommonFacts(
            sdkTarget,
            packageFacts,
            $"SDK package '{packageId}' target '{packageTarget.Triple}'",
            diagnosticPath,
            diagnostics);
        ValidatePackageCpu(sdkTarget, packageFacts, packageId, diagnosticPath, diagnostics);
        ValidatePackageFeatures(sdkTarget, packageFacts, packageId, diagnosticPath, diagnostics);
        ValidatePackageDeploymentMinimum(sdkTarget, packageFacts, packageId, diagnosticPath, diagnostics);
        ValidatePackageCDataModel(sdkTarget, packageTarget, packageId, diagnosticPath, diagnostics);
        return new SdkTargetCompatibilityResult(diagnostics.ToArray());
    }

    private static void ValidateSdkDescriptor(
        SdkTargetDescriptor sdkTarget,
        string? diagnosticPath,
        List<SdkDiagnostic> diagnostics)
    {
        if (!TryParseTriple(sdkTarget.LlvmTriple, out var tripleFacts))
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7481",
                $"SDK target '{sdkTarget.Id}' has an unsupported or incomplete LLVM triple '{sdkTarget.LlvmTriple}'.",
                diagnosticPath));
            return;
        }

        var parsedTriple = tripleFacts!;

        if (!StringEquals(NormalizeArchitecture(sdkTarget.Architecture), parsedTriple.Architecture))
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7481",
                $"SDK target '{sdkTarget.Id}' records architecture '{sdkTarget.Architecture}', which disagrees with LLVM triple '{sdkTarget.LlvmTriple}'.",
                diagnosticPath));
        }

        if (!StringEquals(NormalizeOperatingSystem(sdkTarget.OperatingSystem), parsedTriple.OperatingSystem))
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7481",
                $"SDK target '{sdkTarget.Id}' records operating system '{sdkTarget.OperatingSystem}', which disagrees with LLVM triple '{sdkTarget.LlvmTriple}'.",
                diagnosticPath));
        }

        if (!StringEquals(NormalizeAbi(sdkTarget.Abi), parsedTriple.Abi))
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7481",
                $"SDK target '{sdkTarget.Id}' records ABI/environment '{sdkTarget.Abi}', which disagrees with LLVM triple '{sdkTarget.LlvmTriple}'.",
                diagnosticPath));
        }

        var expectedPointerWidth = ExpectedPointerBitWidth(parsedTriple.Architecture);
        if (expectedPointerWidth is not null && expectedPointerWidth != sdkTarget.PointerBitWidth)
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7481",
                $"SDK target '{sdkTarget.Id}' records {sdkTarget.PointerBitWidth}-bit pointers, but architecture '{sdkTarget.Architecture}' requires {expectedPointerWidth}-bit pointers.",
                diagnosticPath));
        }

        var dataLayoutEndianness = TryResolveEndianness(sdkTarget.DataLayout, parsedTriple.Architecture);
        if (dataLayoutEndianness is not null && dataLayoutEndianness != sdkTarget.Endianness)
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7481",
                $"SDK target '{sdkTarget.Id}' endianness '{FormatEndianness(sdkTarget.Endianness)}' disagrees with its LLVM data layout.",
                diagnosticPath));
        }

        var dataLayoutPointerWidth = TryResolvePointerBitWidth(sdkTarget.DataLayout);
        if (dataLayoutPointerWidth is not null && dataLayoutPointerWidth != sdkTarget.PointerBitWidth)
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7481",
                $"SDK target '{sdkTarget.Id}' records {sdkTarget.PointerBitWidth}-bit pointers, but its LLVM data layout records {dataLayoutPointerWidth}-bit pointers.",
                diagnosticPath));
        }

        if (!IsSupportedRelocationModel(sdkTarget.RelocationModel))
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7481",
                $"SDK target '{sdkTarget.Id}' has unsupported relocation model '{sdkTarget.RelocationModel}'.",
                diagnosticPath));
        }

        if (sdkTarget.CodeModel is not null && !IsSupportedCodeModel(sdkTarget.CodeModel))
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7481",
                $"SDK target '{sdkTarget.Id}' has unsupported code model '{sdkTarget.CodeModel}'.",
                diagnosticPath));
        }

        if (!TargetFeatureFacts.TryNormalizeDistinct(
                sdkTarget.BaselineFeatures,
                out _,
                out var featureError))
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7481",
                $"SDK target '{sdkTarget.Id}' has invalid baseline features: {featureError}.",
                diagnosticPath));
        }

        if (sdkTarget.CDataModel is not null
            && TryResolveCDataModel(new LlvmTargetInfo(sdkTarget.LlvmTriple, sdkTarget.DataLayout), out var sdkCDataModel)
            && !StringEquals(NormalizeCDataModel(sdkTarget.CDataModel), sdkCDataModel.Kind))
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7481",
                $"SDK target '{sdkTarget.Id}' records C data model '{sdkTarget.CDataModel}', but LLVM triple '{sdkTarget.LlvmTriple}' resolves to '{sdkCDataModel.Kind}'.",
                diagnosticPath));
        }

        if (sdkTarget.MinimumOperatingSystemVersion is not null
            && !TryParseVersion(sdkTarget.MinimumOperatingSystemVersion, out _))
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7481",
                $"SDK target '{sdkTarget.Id}' has invalid deployment minimum '{sdkTarget.MinimumOperatingSystemVersion}'.",
                diagnosticPath));
        }

        else if (sdkTarget.MinimumOperatingSystemVersion is not null
            && parsedTriple.DeploymentMinimum is not null
            && TryParseVersion(sdkTarget.MinimumOperatingSystemVersion, out var explicitMinimum)
            && explicitMinimum != parsedTriple.DeploymentMinimum)
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7481",
                $"SDK target '{sdkTarget.Id}' deployment minimum '{sdkTarget.MinimumOperatingSystemVersion}' disagrees with LLVM triple '{sdkTarget.LlvmTriple}'.",
                diagnosticPath));
        }
    }

    private static void CompareCommonFacts(
        SdkTargetDescriptor sdkTarget,
        TargetFacts candidate,
        string candidateLabel,
        string? diagnosticPath,
        List<SdkDiagnostic> diagnostics)
    {
        var sdkArchitecture = NormalizeArchitecture(sdkTarget.Architecture);
        if (candidate.Architecture is null || !StringEquals(sdkArchitecture, candidate.Architecture))
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7482",
                $"{candidateLabel} architecture '{candidate.Architecture ?? "<unresolved>"}' is incompatible with SDK architecture '{sdkTarget.Architecture}'.",
                diagnosticPath));
        }

        var sdkOperatingSystem = NormalizeOperatingSystem(sdkTarget.OperatingSystem);
        if (candidate.OperatingSystem is null || !StringEquals(sdkOperatingSystem, candidate.OperatingSystem))
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7483",
                $"{candidateLabel} operating system '{candidate.OperatingSystem ?? "<unresolved>"}' is incompatible with SDK operating system '{sdkTarget.OperatingSystem}'.",
                diagnosticPath));
        }

        var sdkAbi = NormalizeAbi(sdkTarget.Abi);
        if (candidate.Abi is null || !StringEquals(sdkAbi, candidate.Abi))
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7484",
                $"{candidateLabel} ABI/environment '{candidate.Abi ?? "<unresolved>"}' is incompatible with SDK ABI/environment '{sdkTarget.Abi}'.",
                diagnosticPath));
        }

        if (candidate.PointerBitWidth is null || candidate.PointerBitWidth != sdkTarget.PointerBitWidth)
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7485",
                $"{candidateLabel} pointer width '{FormatOptional(candidate.PointerBitWidth)}' is incompatible with SDK pointer width '{sdkTarget.PointerBitWidth}'.",
                diagnosticPath));
        }

        if (candidate.Endianness is null || candidate.Endianness != sdkTarget.Endianness)
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7486",
                $"{candidateLabel} endianness '{FormatOptionalEndianness(candidate.Endianness)}' is incompatible with SDK endianness '{FormatEndianness(sdkTarget.Endianness)}'.",
                diagnosticPath));
        }

        if (sdkTarget.DataLayout is not null
            && !StringEquals(NormalizeDataLayout(sdkTarget.DataLayout), NormalizeDataLayout(candidate.DataLayout)))
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7487",
                $"{candidateLabel} LLVM data layout does not exactly match the SDK data layout.",
                diagnosticPath));
        }

        if (sdkTarget.CDataModel is not null
            && !StringEquals(NormalizeCDataModel(sdkTarget.CDataModel), NormalizeCDataModel(candidate.CDataModel)))
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7488",
                $"{candidateLabel} C data model '{candidate.CDataModel ?? "<unresolved>"}' is incompatible with SDK C data model '{sdkTarget.CDataModel}'.",
                diagnosticPath));
        }

        if (!StringEquals(NormalizeRelocationModel(sdkTarget.RelocationModel), NormalizeRelocationModel(candidate.RelocationModel)))
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7490",
                $"{candidateLabel} relocation model '{candidate.RelocationModel}' is incompatible with SDK relocation model '{sdkTarget.RelocationModel}'.",
                diagnosticPath));
        }

        if (sdkTarget.CodeModel is not null
            && !StringEquals(NormalizeCodeModel(sdkTarget.CodeModel), NormalizeCodeModel(candidate.CodeModel)))
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7491",
                $"{candidateLabel} code model '{candidate.CodeModel ?? "<default>"}' is incompatible with SDK code model '{sdkTarget.CodeModel}'.",
                diagnosticPath));
        }
    }

    private static void ValidateActiveCpu(
        SdkTargetDescriptor sdkTarget,
        TargetFacts activeFacts,
        string? diagnosticPath,
        List<SdkDiagnostic> diagnostics)
    {
        var sdkCpu = NormalizeCpu(sdkTarget.BaselineCpu);
        if (sdkCpu is null or "generic")
        {
            return;
        }

        if (!StringEquals(sdkCpu, NormalizeCpu(activeFacts.Cpu)))
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7493",
                $"Active target CPU '{activeFacts.Cpu ?? "<default>"}' does not prove compatibility with SDK baseline CPU '{sdkTarget.BaselineCpu}'.",
                diagnosticPath));
        }
    }

    private static void ValidateActiveFeatures(
        SdkTargetDescriptor sdkTarget,
        TargetFacts activeFacts,
        string? diagnosticPath,
        List<SdkDiagnostic> diagnostics)
    {
        if (!TargetFeatureFacts.TryNormalizeDistinct(activeFacts.Features, out _, out var featureError))
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7492",
                $"Active target features are invalid: {featureError}.",
                diagnosticPath));
            return;
        }

        var missingFeatures = TargetFeatureFacts.GetMissingEnabledFeatures(
            sdkTarget.BaselineFeatures,
            activeFacts.Features);
        if (missingFeatures.Count != 0)
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7492",
                $"Active target is missing SDK-required target features: {string.Join(", ", missingFeatures)}.",
                diagnosticPath));
        }
    }

    private static void ValidatePackageCpu(
        SdkTargetDescriptor sdkTarget,
        TargetFacts packageFacts,
        string packageId,
        string? diagnosticPath,
        List<SdkDiagnostic> diagnostics)
    {
        var packageCpu = NormalizeCpu(packageFacts.Cpu);
        if (packageCpu is null or "generic")
        {
            return;
        }

        if (!StringEquals(packageCpu, NormalizeCpu(sdkTarget.BaselineCpu)))
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7493",
                $"SDK package '{packageId}' CPU '{packageFacts.Cpu}' is not covered by SDK baseline CPU '{sdkTarget.BaselineCpu ?? "<default>"}'.",
                diagnosticPath));
        }
    }

    private static void ValidatePackageFeatures(
        SdkTargetDescriptor sdkTarget,
        TargetFacts packageFacts,
        string packageId,
        string? diagnosticPath,
        List<SdkDiagnostic> diagnostics)
    {
        if (!TargetFeatureFacts.TryNormalizeDistinct(packageFacts.Features, out _, out var featureError))
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7492",
                $"SDK package '{packageId}' target features are invalid: {featureError}.",
                diagnosticPath));
            return;
        }

        var sdkFeatures = TargetFeatureFacts.GetEnabledFeatures(sdkTarget.BaselineFeatures)
            .ToHashSet(StringComparer.Ordinal);
        var excessFeatures = TargetFeatureFacts.GetEnabledFeatures(packageFacts.Features)
            .Where(feature => !sdkFeatures.Contains(feature))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (excessFeatures.Length != 0)
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7492",
                $"SDK package '{packageId}' requires target features outside the SDK baseline: {string.Join(", ", excessFeatures)}.",
                diagnosticPath));
        }
    }

    private static void ValidateActiveDeploymentMinimum(
        SdkTargetDescriptor sdkTarget,
        TargetFacts activeFacts,
        string? diagnosticPath,
        List<SdkDiagnostic> diagnostics)
    {
        if (!TryGetSdkDeploymentMinimum(sdkTarget, out var sdkMinimum))
        {
            return;
        }

        if (activeFacts.DeploymentMinimum is null)
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7489",
                $"Active target triple '{activeFacts.Triple}' does not expose a deployment minimum required to validate SDK minimum '{FormatVersion(sdkMinimum)}'.",
                diagnosticPath));
        }
        else if (activeFacts.DeploymentMinimum < sdkMinimum)
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7489",
                $"Active target deployment minimum '{FormatVersion(activeFacts.DeploymentMinimum)}' is older than SDK minimum '{FormatVersion(sdkMinimum)}'.",
                diagnosticPath));
        }
    }

    private static void ValidatePackageDeploymentMinimum(
        SdkTargetDescriptor sdkTarget,
        TargetFacts packageFacts,
        string packageId,
        string? diagnosticPath,
        List<SdkDiagnostic> diagnostics)
    {
        if (!TryGetSdkDeploymentMinimum(sdkTarget, out var sdkMinimum))
        {
            return;
        }

        if (packageFacts.DeploymentMinimum is null)
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7489",
                $"SDK package '{packageId}' target triple '{packageFacts.Triple}' does not expose a deployment minimum required to validate SDK minimum '{FormatVersion(sdkMinimum)}'.",
                diagnosticPath));
        }
        else if (packageFacts.DeploymentMinimum > sdkMinimum)
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7489",
                $"SDK package '{packageId}' deployment minimum '{FormatVersion(packageFacts.DeploymentMinimum)}' is newer than SDK minimum '{FormatVersion(sdkMinimum)}'.",
                diagnosticPath));
        }
    }

    private static void ValidatePackageCDataModel(
        SdkTargetDescriptor sdkTarget,
        StarkPackageTargetManifest packageTarget,
        string packageId,
        string? diagnosticPath,
        List<SdkDiagnostic> diagnostics)
    {
        if (packageTarget.CDataModel is null
            || !TryResolveCDataModel(new LlvmTargetInfo(sdkTarget.LlvmTriple, sdkTarget.DataLayout), out var expected))
        {
            return;
        }

        var actual = packageTarget.CDataModel;
        if (!StringEquals(NormalizeCDataModel(actual.Kind), expected.Kind)
            || actual.CharIsSigned != expected.CharIsSigned
            || actual.PointerBitWidth != expected.PointerBitWidth
            || actual.LongBitWidth != expected.LongBitWidth
            || actual.SizeTBitWidth != expected.SizeTBitWidth
            || actual.PtrDiffTBitWidth != expected.PtrDiffTBitWidth)
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7488",
                $"SDK package '{packageId}' detailed C data-model facts do not match SDK target '{sdkTarget.Id}'.",
                diagnosticPath));
        }
    }

    private static TargetFacts CreateActiveFacts(LlvmTargetInfo targetInfo)
    {
        TryParseTriple(targetInfo.Triple, out var tripleFacts);
        var hasCDataModel = TryResolveCDataModel(targetInfo, out var cDataModel);
        return new TargetFacts(
            targetInfo.Triple,
            tripleFacts?.Architecture,
            tripleFacts?.OperatingSystem,
            tripleFacts?.Abi,
            TryResolvePointerBitWidth(targetInfo.DataLayout)
                ?? (hasCDataModel ? cDataModel.PointerBitWidth : ExpectedPointerBitWidth(tripleFacts?.Architecture)),
            TryResolveEndianness(targetInfo.DataLayout, tripleFacts?.Architecture),
            NormalizeOptional(targetInfo.DataLayout),
            NormalizeOptional(targetInfo.Cpu),
            targetInfo.Features ?? Array.Empty<string>(),
            FormatRelocationModel(targetInfo.RelocationModel),
            targetInfo.CodeModel?.ToString().ToLowerInvariant(),
            hasCDataModel ? cDataModel.Kind : null,
            tripleFacts?.DeploymentMinimum);
    }

    private static TargetFacts CreatePackageFacts(StarkPackageTargetManifest target)
    {
        TryParseTriple(target.Triple, out var tripleFacts);
        var pointerWidth = TryResolvePointerBitWidth(target.DataLayout)
            ?? target.CDataModel?.PointerBitWidth
            ?? target.AggregateLayout?.PointerSizeBytes * 8
            ?? ExpectedPointerBitWidth(tripleFacts?.Architecture);
        return new TargetFacts(
            target.Triple,
            tripleFacts?.Architecture,
            tripleFacts?.OperatingSystem,
            tripleFacts?.Abi,
            pointerWidth,
            TryResolveEndianness(target.DataLayout, tripleFacts?.Architecture),
            NormalizeOptional(target.DataLayout),
            NormalizeOptional(target.Cpu),
            target.Features ?? Array.Empty<string>(),
            NormalizeRelocationModel(target.RelocationModel) ?? "<unresolved>",
            NormalizeCodeModel(target.CodeModel),
            NormalizeCDataModel(target.CDataModel?.Kind),
            tripleFacts?.DeploymentMinimum);
    }

    private static bool TryParseTriple(string? triple, out ParsedTriple? facts)
    {
        facts = null;
        if (string.IsNullOrWhiteSpace(triple))
        {
            return false;
        }

        var components = triple.Trim().Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (components.Length < 2 || NormalizeArchitecture(components[0]) is not { } architecture)
        {
            return false;
        }

        string? operatingSystem = null;
        string? abi = null;
        Version? deploymentMinimum = null;
        for (var index = 1; index < components.Length; index++)
        {
            if (NormalizeOperatingSystemComponent(components[index]) is not { } resolvedOperatingSystem)
            {
                continue;
            }

            operatingSystem = resolvedOperatingSystem;
            if (string.Equals(operatingSystem, "macos", StringComparison.Ordinal))
            {
                abi = "darwin";
                deploymentMinimum = TryParseMacOSDeploymentMinimum(components[index]);
            }
            else if (index + 1 < components.Length)
            {
                abi = NormalizeAbi(components[index + 1]);
            }
            else if (components[index].StartsWith("mingw", StringComparison.OrdinalIgnoreCase))
            {
                abi = components[index].Trim().ToLowerInvariant();
            }

            break;
        }

        if (operatingSystem is null)
        {
            return false;
        }

        facts = new ParsedTriple(architecture, operatingSystem, abi, deploymentMinimum);
        return true;
    }

    private static string? NormalizeArchitecture(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "arm64" or "aarch64" => "aarch64",
            "x86_64" or "amd64" => "x86_64",
            "x86" => "x86",
            "arm" => "arm",
            "riscv64" => "riscv64",
            _ => null
        };
    }

    private static string? NormalizeOperatingSystem(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "macos" or "macosx" or "darwin" => "macos",
            "linux" => "linux",
            "windows" => "windows",
            _ => null
        };
    }

    private static string? NormalizeOperatingSystemComponent(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.StartsWith("macosx", StringComparison.Ordinal)
            || normalized.StartsWith("macos", StringComparison.Ordinal)
            || normalized.StartsWith("darwin", StringComparison.Ordinal))
        {
            return "macos";
        }

        if (normalized.StartsWith("linux", StringComparison.Ordinal))
        {
            return "linux";
        }

        if (normalized.StartsWith("windows", StringComparison.Ordinal)
            || normalized.StartsWith("win32", StringComparison.Ordinal)
            || normalized.StartsWith("mingw", StringComparison.Ordinal))
        {
            return "windows";
        }

        return null;
    }

    private static string? NormalizeAbi(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            null => null,
            "darwin" or "macos" or "macosx" => "darwin",
            "" => null,
            { } normalized => normalized
        };
    }

    private static string? NormalizeCDataModel(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    private static string? NormalizeCpu(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    private static string? NormalizeDataLayout(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeRelocationModel(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
    }

    private static string? NormalizeCodeModel(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsSupportedRelocationModel(string value) =>
        NormalizeRelocationModel(value) is "default" or "static" or "pic" or "pie";

    private static bool IsSupportedCodeModel(string value) =>
        NormalizeCodeModel(value) is "tiny" or "small" or "kernel" or "medium" or "large";

    private static int? ExpectedPointerBitWidth(string? architecture)
    {
        return architecture switch
        {
            "x86_64" or "aarch64" or "riscv64" => 64,
            "x86" or "arm" => 32,
            _ => null
        };
    }

    private static SdkEndianness? TryResolveEndianness(string? dataLayout, string? architecture)
    {
        if (!string.IsNullOrWhiteSpace(dataLayout))
        {
            var marker = dataLayout.TrimStart()[0];
            if (marker == 'e')
            {
                return SdkEndianness.Little;
            }

            if (marker == 'E')
            {
                return SdkEndianness.Big;
            }
        }

        return architecture is "x86_64" or "aarch64" or "riscv64" or "x86" or "arm"
            ? SdkEndianness.Little
            : null;
    }

    private static Version? TryParseMacOSDeploymentMinimum(string component)
    {
        var normalized = component.Trim().ToLowerInvariant();
        var prefixLength = normalized.StartsWith("macosx", StringComparison.Ordinal)
            ? "macosx".Length
            : normalized.StartsWith("macos", StringComparison.Ordinal)
                ? "macos".Length
                : 0;
        if (prefixLength == 0 || prefixLength == normalized.Length)
        {
            return null;
        }

        return TryParseVersion(normalized[prefixLength..], out var version) ? version : null;
    }

    private static bool TryGetSdkDeploymentMinimum(SdkTargetDescriptor sdkTarget, out Version minimum)
    {
        if (sdkTarget.MinimumOperatingSystemVersion is not null
            && TryParseVersion(sdkTarget.MinimumOperatingSystemVersion, out minimum!))
        {
            return true;
        }

        if (TryParseTriple(sdkTarget.LlvmTriple, out var tripleFacts)
            && tripleFacts!.DeploymentMinimum is not null)
        {
            minimum = tripleFacts.DeploymentMinimum;
            return true;
        }

        minimum = null!;
        return false;
    }

    private static int? TryResolvePointerBitWidth(string? dataLayout)
    {
        if (string.IsNullOrWhiteSpace(dataLayout))
        {
            return null;
        }

        foreach (var token in dataLayout.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!token.StartsWith("p:", StringComparison.Ordinal)
                && !token.StartsWith("p0:", StringComparison.Ordinal))
            {
                continue;
            }

            var fields = token.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return fields.Length >= 2 && int.TryParse(fields[1], out var pointerBitWidth)
                ? pointerBitWidth
                : null;
        }

        return null;
    }

    private static bool TryParseVersion(string value, out Version version)
    {
        if (!Version.TryParse(value, out var parsed))
        {
            version = null!;
            return false;
        }

        version = new Version(
            parsed.Major,
            parsed.Minor,
            Math.Max(parsed.Build, 0),
            Math.Max(parsed.Revision, 0));
        return true;
    }

    private static string FormatVersion(Version version)
    {
        if (version.Revision > 0)
        {
            return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
        }

        if (version.Build > 0)
        {
            return $"{version.Major}.{version.Minor}.{version.Build}";
        }

        return $"{version.Major}.{version.Minor}";
    }

    private static bool TryResolveCDataModel(LlvmTargetInfo targetInfo, out CDataModelFacts facts)
    {
        if (NormalizeArchitecture(targetInfo.Triple.Split('-', 2)[0]) is null
            || !StarkCDataModelFacts.TryResolve(targetInfo, out var resolved))
        {
            facts = default!;
            return false;
        }

        facts = new CDataModelFacts(
            resolved.Kind.ToString().ToLowerInvariant(),
            resolved.CharIsSigned,
            resolved.PointerBitWidth,
            resolved.LongBitWidth,
            resolved.SizeTBitWidth,
            resolved.PtrDiffTBitWidth);
        return true;
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

    private static bool StringEquals(string? left, string? right) =>
        string.Equals(left, right, StringComparison.Ordinal);

    private static string FormatEndianness(SdkEndianness endianness) =>
        endianness == SdkEndianness.Little ? "little" : "big";

    private static string FormatOptionalEndianness(SdkEndianness? endianness) =>
        endianness is null ? "<unresolved>" : FormatEndianness(endianness.Value);

    private static string FormatOptional(int? value) => value?.ToString() ?? "<unresolved>";

    private sealed record ParsedTriple(
        string Architecture,
        string OperatingSystem,
        string? Abi,
        Version? DeploymentMinimum);

    private sealed record TargetFacts(
        string Triple,
        string? Architecture,
        string? OperatingSystem,
        string? Abi,
        int? PointerBitWidth,
        SdkEndianness? Endianness,
        string? DataLayout,
        string? Cpu,
        IReadOnlyList<string> Features,
        string RelocationModel,
        string? CodeModel,
        string? CDataModel,
        Version? DeploymentMinimum);

    private sealed record CDataModelFacts(
        string Kind,
        bool CharIsSigned,
        int PointerBitWidth,
        int LongBitWidth,
        int SizeTBitWidth,
        int PtrDiffTBitWidth);
}
