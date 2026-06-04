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
            IsStrictFp: effects.IsStrictFp,
            IsVarargs: effects.IsVarargs,
            FfiAbi: effects.FfiAbi is { } ffiAbi ? StarkFfiAbiFacts.DisplayName(ffiAbi) : null,
            BackendOptimizationMode: RenderBackendOptimizationMode(effects.BackendOptimizationMode));
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
                    parameter.Kind.ToString().ToLowerInvariant(),
                    parameter.RawPointerElementCountExpression))
                .ToArray(),
            IsFfi: abiFunction.IsFfi,
            SourceName: abiFunction.SourceName,
            UsesFastCallingConvention: abiFunction.UsesFastCallingConvention,
            IsVarargs: abiFunction.IsVarargs,
            FfiAbi: abiFunction.FfiAbi is { } ffiAbi ? StarkFfiAbiFacts.DisplayName(ffiAbi) : null);
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
            layout.AlignmentBytes,
            layout.Fields.Count == 0
                ? null
                : layout.Fields
                    .Select(field => new StarkPackageConcreteFieldLayoutManifest(
                        field.Name,
                        field.OffsetBytes,
                        field.SizeBytes,
                        field.NaturalAlignmentBytes,
                        field.EffectiveAlignmentBytes,
                        field.IsMisaligned))
                    .ToArray());
        return true;
    }

    private static bool TryBuildFunctionSemanticManifest(
        LoadedModuleDocument module,
        TopLevelDeclarationModel declaration,
        SemanticValidationModel validationModel,
        OwnershipValidationModel? ownershipModel,
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
            ownershipModel,
            out manifest);
    }

    private static bool TryBuildPublishedFunctionSemanticManifest(
        LoadedModuleDocument module,
        string lookupName,
        string qualifiedResolvedName,
        SemanticValidationModel validationModel,
        OwnershipValidationModel? ownershipModel,
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
            Calls: validation.Calls is { Count: > 0 }
                ? validation.Calls
                    .Select(call => new StarkPackageFunctionCallManifest(
                        QualifyPublishedCalledFunctionName(module, call.CalleeName),
                        new StarkPackageFunctionMemoryEffectsManifest(
                            call.MemoryEffects.ReadsArgumentMemory,
                            call.MemoryEffects.WritesArgumentMemory,
                            call.MemoryEffects.CapturesArgumentMemory,
                            call.MemoryEffects.ReadsOtherMemory,
                            call.MemoryEffects.WritesOtherMemory),
                        call.Arguments
                            .OrderBy(static argument => argument.ArgumentIndex)
                            .Select(argument => new StarkPackageCallArgumentMemoryEffectsManifest(
                                argument.ArgumentIndex,
                                argument.CallerParameterName,
                                argument.CalleeParameterName,
                                argument.Reads,
                                argument.Writes,
                                argument.CaptureKind.ToString().ToLowerInvariant()))
                            .ToArray()))
                    .ToArray()
                : null,
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
                    validation.OptimizationSummary.IsSingleReturnDereferenceWrapper,
                    validation.OptimizationSummary.IsSingleReturnBinaryOperatorWrapper,
                    validation.OptimizationSummary.IsSingleReturnComparisonWrapper,
                    validation.OptimizationSummary.IsSingleReturnAggregateConstructionWrapper,
                    validation.OptimizationSummary.IsSimpleLocalUpdateWrapper,
                    validation.OptimizationSummary.IsTerminalSelectionWrapper),
            Ownership: TryBuildFunctionOwnershipManifest(
                module,
                lookupName,
                qualifiedResolvedName,
                ownershipModel,
                out var ownershipManifest)
                ? ownershipManifest
                : null);
        return true;
    }

    private static bool TryBuildFunctionOwnershipManifest(
        LoadedModuleDocument module,
        string lookupName,
        string qualifiedResolvedName,
        OwnershipValidationModel? ownershipModel,
        out StarkPackageFunctionOwnershipManifest manifest)
    {
        manifest = default!;
        if (ownershipModel is null)
        {
            return false;
        }

        if (!ownershipModel.Functions.TryGetValue(lookupName, out var ownership)
            && !ownershipModel.Functions.TryGetValue(qualifiedResolvedName, out ownership))
        {
            return false;
        }

        manifest = new StarkPackageFunctionOwnershipManifest(
            ownership.OwnershipValid,
            ownership.ImplicitDrops.ToArray(),
            ownership.Moves.ToArray(),
            ownership.Events.Count == 0
                ? null
                : ownership.Events
                    .Select(ev => new StarkPackageOwnershipEventManifest(
                        RenderOwnershipEventKind(ev.Kind),
                        BuildOwnershipPlaceManifest(module, ev.Place),
                        ev.Location is null
                            ? null
                            : new StarkPackageSourceLocation(ev.Location.FilePath, ev.Location.Line, ev.Location.Column)))
                    .ToArray(),
            ownership.Roots.Count == 0
                ? null
                : ownership.Roots
                    .Select(root => new StarkPackageOwnershipRootManifest(
                        root.Name,
                        BuildPublishedAbiTypeReference(root.Type, module),
                        RenderOwnershipRootKind(root.RootKind),
                        root.IsMutable,
                        root.IsConstant,
                        root.IsAddressTaken,
                        root.HasRawPointerEscape,
                        root.HasMove,
                        root.HasPartialMove,
                        root.HasImplicitDrop,
                        root.HasAssignmentDrop,
                        root.HasReinitialization,
                        root.RequiresDrop,
                        RenderOwnershipAvailability(root.FinalAvailability)))
                    .ToArray());
        return true;
    }

    private static StarkPackageOwnershipPlaceManifest BuildOwnershipPlaceManifest(
        LoadedModuleDocument module,
        OwnershipPlaceSummary place)
    {
        return new StarkPackageOwnershipPlaceManifest(
            place.RootName,
            BuildPublishedAbiTypeReference(place.Type, module),
            place.Projections.Count == 0 ? null : place.Projections.ToArray(),
            place.HasIndexProjection);
    }

    private static string RenderOwnershipEventKind(OwnershipEventKind kind)
    {
        return kind switch
        {
            OwnershipEventKind.Move => "move",
            OwnershipEventKind.FieldMove => "field-move",
            OwnershipEventKind.ImplicitDrop => "implicit-drop",
            OwnershipEventKind.AssignmentDrop => "assignment-drop",
            OwnershipEventKind.Reinitialize => "reinitialize",
            OwnershipEventKind.AddressTaken => "address-taken",
            _ => kind.ToString().ToLowerInvariant()
        };
    }

    private static string RenderOwnershipRootKind(OwnershipStorageRootKind kind)
    {
        return kind switch
        {
            OwnershipStorageRootKind.Local => "local",
            OwnershipStorageRootKind.Parameter => "parameter",
            OwnershipStorageRootKind.Global => "global",
            _ => kind.ToString().ToLowerInvariant()
        };
    }

    private static string RenderOwnershipAvailability(OwnershipRootAvailabilityKind kind)
    {
        return kind switch
        {
            OwnershipRootAvailabilityKind.Initialized => "initialized",
            OwnershipRootAvailabilityKind.Uninitialized => "uninitialized",
            OwnershipRootAvailabilityKind.PartiallyInitialized => "partially-initialized",
            OwnershipRootAvailabilityKind.Moved => "moved",
            OwnershipRootAvailabilityKind.ControlFlow => "control-flow",
            _ => "unknown"
        };
    }
}
