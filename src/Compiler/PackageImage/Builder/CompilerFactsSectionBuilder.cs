namespace Stark.Compiler;

internal static partial class PackageImageBuilder
{
    private static bool TryBuildFunctionEffectManifest(
        LoadedModuleDocument module,
        TopLevelDeclarationModel declaration,
        FunctionEffectModel effectModel,
        out StarkPackageFunctionEffectManifest manifest)
    {
        manifest = default!;

        var resolvedLocalName = FunctionOverloadFacts.GetResolvedLocalName(module.SyntaxModel, declaration);
        var lookupName = FunctionOverloadFacts.QualifyResolvedName(module, resolvedLocalName);
        if (!effectModel.Functions.TryGetValue(lookupName, out var effects))
        {
            return false;
        }

        manifest = new StarkPackageFunctionEffectManifest(
            QualifiedResolvedName: $"{module.SyntaxModel.ModuleName}.{resolvedLocalName}",
            Kind: effects.Kind.ToString().ToLowerInvariant(),
            ReadsArgumentMemory: effects.ReadsArgumentMemory,
            IsPure: effects.IsPure,
            NoSync: effects.NoSync,
            NoFree: effects.NoFree,
            NoUnwind: effects.NoUnwind,
            WillReturn: effects.WillReturn,
            MustProgress: effects.MustProgress,
            UseFastCallingConvention: effects.UseFastCallingConvention,
            IsFfi: effects.IsFfi,
            IsHot: effects.IsHot,
            IsCold: effects.IsCold,
            InlinePreference: RenderInlinePreference(effects.InlinePreference),
            IsStrictFp: effects.IsStrictFp);
        return true;
    }

    private static bool TryBuildAbiFunctionManifest(
        LoadedModuleDocument module,
        TopLevelDeclarationModel declaration,
        AbiModel abiModel,
        out StarkPackageAbiFunctionManifest manifest)
    {
        manifest = default!;

        var resolvedLocalName = FunctionOverloadFacts.GetResolvedLocalName(module.SyntaxModel, declaration);
        var lookupName = FunctionOverloadFacts.QualifyResolvedName(module, resolvedLocalName);
        if (!abiModel.Functions.TryGetValue(lookupName, out var abiFunction))
        {
            return false;
        }

        manifest = new StarkPackageAbiFunctionManifest(
            QualifiedResolvedName: $"{module.SyntaxModel.ModuleName}.{resolvedLocalName}",
            SymbolName: ComputePublishedPackageAbiSymbolName(
                module.SyntaxModel.ModuleName,
                declaration,
                resolvedLocalName,
                abiFunction.IsFfi),
            SourceReturnType: BuildPublishedAbiTypeReference(abiFunction.SourceReturnType, module),
            LlvmReturnType: BuildPublishedAbiTypeReference(abiFunction.LlvmReturnType, module),
            Parameters: abiFunction.Parameters
                .Select(parameter => new StarkPackageAbiParameterManifest(
                    parameter.SourceName,
                    parameter.LlvmName,
                    BuildPublishedAbiTypeReference(parameter.SourceType, module),
                    BuildPublishedAbiTypeReference(parameter.LlvmType, module),
                    parameter.Kind.ToString().ToLowerInvariant()))
                .ToArray(),
            IsFfi: abiFunction.IsFfi,
            SourceName: abiFunction.SourceName,
            UsesFastCallingConvention: abiFunction.UsesFastCallingConvention);
        return true;
    }

    private static bool TryBuildConcreteLayoutManifest(
        NamedTypeSymbol namedType,
        string qualifiedTypeName,
        TypeCheckModel typeModel,
        EnumLayoutModel enumLayoutModel,
        out StarkPackageConcreteTypeLayoutManifest manifest)
    {
        manifest = default!;

        var concreteType = StarkTypeSymbols.Named(namedType.Name);
        if (ConcreteTypeLayoutHelper.TryGetConcreteTypeLayout(concreteType, typeModel.NamedTypes, enumLayoutModel.Layouts) is not { } layout)
        {
            return false;
        }

        manifest = new StarkPackageConcreteTypeLayoutManifest(
            qualifiedTypeName,
            layout.SizeBytes,
            layout.AlignmentBytes);
        return true;
    }

    private static bool TryBuildFunctionSemanticManifest(
        LoadedModuleDocument module,
        TopLevelDeclarationModel declaration,
        SemanticValidationModel validationModel,
        out StarkPackageFunctionSemanticManifest manifest)
    {
        manifest = default!;

        var resolvedLocalName = FunctionOverloadFacts.GetResolvedLocalName(module.SyntaxModel, declaration);
        var lookupName = FunctionOverloadFacts.QualifyResolvedName(module, resolvedLocalName);
        return TryBuildPublishedFunctionSemanticManifest(
            module,
            lookupName,
            $"{module.SyntaxModel.ModuleName}.{resolvedLocalName}",
            validationModel,
            out manifest);
    }

    private static bool TryBuildPublishedFunctionSemanticManifest(
        LoadedModuleDocument module,
        string lookupName,
        string qualifiedResolvedName,
        SemanticValidationModel validationModel,
        out StarkPackageFunctionSemanticManifest manifest)
    {
        manifest = default!;

        if (!validationModel.Functions.TryGetValue(lookupName, out var validation))
        {
            return false;
        }

        manifest = new StarkPackageFunctionSemanticManifest(
            QualifiedResolvedName: qualifiedResolvedName,
            DeclaredKind: validation.DeclaredKind.ToString().ToLowerInvariant(),
            EffectiveKind: validation.EffectiveKind.ToString().ToLowerInvariant(),
            CalledFunctions: validation.CalledFunctions
                .Select(callee => QualifyPublishedCalledFunctionName(module, callee))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static callee => callee, StringComparer.Ordinal)
                .ToArray(),
            MemoryEffects: validation.MemoryEffects is null
                ? null
                : new StarkPackageFunctionMemoryEffectsManifest(
                    validation.MemoryEffects.ReadsArgumentMemory,
                    validation.MemoryEffects.WritesArgumentMemory,
                    validation.MemoryEffects.CapturesArgumentMemory,
                    validation.MemoryEffects.ReadsOtherMemory,
                    validation.MemoryEffects.WritesOtherMemory),
            Parameters: validation.Parameters?
                .Select(parameter => new StarkPackageParameterMemoryEffectsManifest(
                    parameter.Name,
                    parameter.Type,
                    parameter.IsMemoryBacked,
                    parameter.GuaranteedNonNull,
                    parameter.GuaranteedReadOnly,
                    parameter.GuaranteedWriteOnly,
                    parameter.GuaranteedNoAlias,
                    parameter.DereferenceableBytes,
                    parameter.AlignmentBytes,
                    parameter.Reads,
                    parameter.Writes,
                    parameter.CaptureKind.ToString().ToLowerInvariant()))
                .ToArray(),
            Optimization: validation.OptimizationSummary is null
                ? null
                : new StarkPackageFunctionOptimizationManifest(
                    validation.OptimizationSummary.DirectCallCount,
                    validation.OptimizationSummary.MemberCallCount,
                    validation.OptimizationSummary.FieldAccessCount,
                    validation.OptimizationSummary.IndexAccessCount,
                    validation.OptimizationSummary.BranchStatementCount,
                    validation.OptimizationSummary.LoopStatementCount,
                    validation.OptimizationSummary.ObjectCreationCount,
                    validation.OptimizationSummary.IsSingleReturnDirectCallForwarder,
                    validation.OptimizationSummary.IsSingleReturnMemberCallForwarder,
                    validation.OptimizationSummary.IsSingleReturnFieldAccessWrapper,
                    validation.OptimizationSummary.IsSingleReturnIndexAccessWrapper,
                    validation.OptimizationSummary.IsSingleReturnConversionWrapper,
                    validation.OptimizationSummary.IsSingleReturnAddressOfWrapper,
                    validation.OptimizationSummary.IsSingleReturnDereferenceWrapper));
        return true;
    }
}
