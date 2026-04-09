namespace Stark.Compiler;

internal static partial class PackageImageLoader
{
    public static bool TryBuildLoadedPackageImageFacts(ResolvedPackageModule module, out LoadedPackageImageFacts facts)
    {
        facts = default!;

        var loadedFunctionEffects = new Dictionary<string, FunctionEffectProfile>(StringComparer.Ordinal);
        var loadedTypeAliases = new Dictionary<string, TypeAliasSymbol>(StringComparer.Ordinal);
        var loadedFunctionSignatures = new Dictionary<string, TypedFunctionSignature>(StringComparer.Ordinal);
        var loadedGlobals = new Dictionary<string, TypedGlobalSymbol>(StringComparer.Ordinal);
        var loadedNamedTypes = new Dictionary<string, NamedTypeSymbol>(StringComparer.Ordinal);
        var loadedConstructors = new Dictionary<string, IReadOnlyList<TypedConstructorShape>>(StringComparer.Ordinal);
        var loadedAbiFunctions = new Dictionary<string, AbiFunctionSignature>(StringComparer.Ordinal);
        var loadedConcreteLayouts = new Dictionary<string, ConcreteTypeLayout>(StringComparer.Ordinal);
        var loadedEnumLayouts = new Dictionary<string, EnumLayoutSymbol>(StringComparer.Ordinal);
        var loadedFunctionSemantics = new Dictionary<string, ImportedFunctionSemanticSummary>(StringComparer.Ordinal);
        var loadedFunctionTemplates = new Dictionary<string, ImportedFunctionTemplateSummary>(StringComparer.Ordinal);
        var localNamedTypes = CollectLocalNamedTypes(module);

        foreach (var function in module.Module.EffectiveTypedInterface?.Functions ?? [])
        {
            var qualifiedResolvedName = function.QualifiedResolvedName ?? function.QualifiedName;
            loadedFunctionSignatures[qualifiedResolvedName] = new TypedFunctionSignature(
                qualifiedResolvedName,
                BuildTypeSymbol(function.ReturnType, module.Module.ModuleName, localNamedTypes),
                function.Parameters
                    .Select(parameter => new TypedParameterSymbol(
                        parameter.Name,
                        BuildTypeSymbol(parameter.Type, module.Module.ModuleName, localNamedTypes)))
                    .ToArray(),
                SourceName: function.QualifiedName,
                GenericParameterNames: function.GenericParameters?.Count > 0 ? function.GenericParameters.ToArray() : null);
        }

        foreach (var type in module.Module.EffectiveTypedInterface?.Types ?? [])
        {
            foreach (var method in type.Methods ?? [])
            {
                var qualifiedResolvedName = method.QualifiedResolvedName ?? method.QualifiedName;
                loadedFunctionSignatures[qualifiedResolvedName] = new TypedFunctionSignature(
                    qualifiedResolvedName,
                    BuildTypeSymbol(method.ReturnType, module.Module.ModuleName, localNamedTypes),
                    method.Parameters
                        .Select(parameter => new TypedParameterSymbol(
                            parameter.Name,
                            BuildTypeSymbol(parameter.Type, module.Module.ModuleName, localNamedTypes)))
                        .ToArray(),
                    SourceName: method.QualifiedName,
                    GenericParameterNames: method.GenericParameters?.Count > 0 ? method.GenericParameters.ToArray() : null);
            }
        }

        foreach (var typeAlias in module.Module.EffectiveTypedInterface?.TypeAliases ?? [])
        {
            if (!TryParseVisibility(typeAlias.Visibility, out var visibility))
            {
                return false;
            }

            loadedTypeAliases[typeAlias.QualifiedName] = new TypeAliasSymbol(
                typeAlias.QualifiedName,
                module.Module.ModuleName,
                visibility,
                BuildTypeSymbol(typeAlias.TargetType, module.Module.ModuleName, localNamedTypes),
                typeAlias.GenericParameters?.Count > 0 ? typeAlias.GenericParameters.ToArray() : null,
                IsExternal: true);
        }

        foreach (var global in module.Module.EffectiveTypedInterface?.Globals ?? [])
        {
            if (!TryParseGlobalDeclarationKind(global.Kind, out var declarationKind))
            {
                return false;
            }

            var bindingKind = declarationKind == DeclarationKind.GlobalConstant
                ? GlobalBindingKind.Const
                : global.IsMutable
                    ? GlobalBindingKind.Mutable
                    : GlobalBindingKind.Immutable;

            loadedGlobals[global.QualifiedName] = new TypedGlobalSymbol(
                global.QualifiedName,
                BuildTypeSymbol(global.Type, module.Module.ModuleName, localNamedTypes),
                bindingKind);
        }

        foreach (var type in module.Module.EffectiveTypedInterface?.Types ?? [])
        {
            if (!TryParseTypeDeclarationKind(type.Kind, out var declarationKind))
            {
                return false;
            }

            var qualifiedName = type.QualifiedName;
            var genericParameterNames = type.GenericParameters?.Count > 0 ? type.GenericParameters.ToList() : null;
            if (declarationKind == DeclarationKind.Enum)
            {
                var variants = (type.Variants ?? [])
                    .Select(variant => new EnumVariantSymbol(
                        variant.Name,
                        variant.UsesNamedFields,
                        variant.Fields
                            .Select((field, index) => new EnumVariantFieldSymbol(
                                index,
                                variant.UsesNamedFields ? field.Name : null,
                                BuildTypeSymbol(field.Type, module.Module.ModuleName, localNamedTypes)))
                            .ToArray()))
                    .ToArray();
                loadedNamedTypes[qualifiedName] = new NamedTypeSymbol(
                    qualifiedName,
                    declarationKind,
                    new Dictionary<string, FieldSymbol>(StringComparer.Ordinal),
                    [],
                    EnumVariants: variants,
                    GenericParameterNames: genericParameterNames);
            }
            else
            {
                var fields = new Dictionary<string, FieldSymbol>(StringComparer.Ordinal);
                var orderedFields = new List<FieldSymbol>(type.Fields.Count);
                foreach (var field in type.Fields)
                {
                    var fieldSymbol = new FieldSymbol(
                        field.Name,
                        BuildTypeSymbol(field.Type, module.Module.ModuleName, localNamedTypes));
                    fields[field.Name] = fieldSymbol;
                    orderedFields.Add(fieldSymbol);
                }

                loadedNamedTypes[qualifiedName] = new NamedTypeSymbol(
                    qualifiedName,
                    declarationKind,
                    fields,
                    orderedFields,
                    GenericParameterNames: genericParameterNames);
            }

            var constructors = new List<TypedConstructorShape>();

            if (type.PrimaryConstructorParameters is { Count: > 0 })
            {
                constructors.Add(new TypedConstructorShape(
                    type.Name,
                    type.PrimaryConstructorParameters
                        .Select(parameter => new TypedParameterSymbol(
                            parameter.Name,
                            BuildTypeSymbol(parameter.Type, module.Module.ModuleName, localNamedTypes)))
                        .ToArray(),
                    IsPrimaryShape: true));
            }

            foreach (var constructor in type.Constructors ?? [])
            {
                constructors.Add(new TypedConstructorShape(
                    type.Name,
                    constructor.Parameters
                        .Select(parameter => new TypedParameterSymbol(
                            parameter.Name,
                            BuildTypeSymbol(parameter.Type, module.Module.ModuleName, localNamedTypes)))
                        .ToArray(),
                    IsPrimaryShape: false));
            }

            if (constructors.Count > 0)
            {
                loadedConstructors[qualifiedName] = constructors;
            }
        }

        if (module.Module.EffectiveCompilerFacts is { } compilerFacts)
        {
            foreach (var functionEffect in compilerFacts.FunctionEffects)
            {
                if (!TryParseFunctionKind(functionEffect.Kind, out var kind)
                    || !TryParseInlinePreference(functionEffect.InlinePreference, out var inlinePreference))
                {
                    return false;
                }

                loadedFunctionEffects[functionEffect.QualifiedResolvedName] = new FunctionEffectProfile(
                    Name: functionEffect.QualifiedResolvedName,
                    Kind: kind,
                    ReadsArgumentMemory: functionEffect.ReadsArgumentMemory,
                    IsPure: functionEffect.IsPure,
                    NoSync: functionEffect.NoSync,
                    NoFree: functionEffect.NoFree,
                    NoUnwind: functionEffect.NoUnwind,
                    WillReturn: functionEffect.WillReturn,
                    MustProgress: functionEffect.MustProgress,
                    UseFastCallingConvention: functionEffect.UseFastCallingConvention,
                    IsFfi: functionEffect.IsFfi,
                    IsHot: functionEffect.IsHot,
                    IsCold: functionEffect.IsCold,
                    InlinePreference: inlinePreference,
                    IsStrictFp: functionEffect.IsStrictFp);
            }

            foreach (var abiFunction in compilerFacts.AbiFunctions ?? [])
            {
                if (!TryBuildAbiFunctionSignature(abiFunction, out var abiSignature))
                {
                    return false;
                }

                loadedAbiFunctions[abiFunction.QualifiedResolvedName] = abiSignature;
            }

            foreach (var concreteLayout in compilerFacts.ConcreteLayouts ?? [])
            {
                loadedConcreteLayouts[concreteLayout.QualifiedTypeName] = new ConcreteTypeLayout(
                    concreteLayout.SizeBytes,
                    concreteLayout.AlignmentBytes);
            }

            foreach (var enumLayout in compilerFacts.EnumLayouts ?? [])
            {
                if (!TryBuildEnumLayoutSymbol(enumLayout, out var layout))
                {
                    return false;
                }

                loadedEnumLayouts[enumLayout.QualifiedTypeName] = layout;
            }

            foreach (var functionSemantic in compilerFacts.FunctionSemantics ?? [])
            {
                if (!TryBuildImportedFunctionSemanticSummary(functionSemantic, out var summary))
                {
                    return false;
                }

                loadedFunctionSemantics[functionSemantic.QualifiedResolvedName] = summary;
            }
        }

        foreach (var functionTemplate in module.Module.EffectiveGenericTemplates?.Functions ?? [])
        {
            ImportedFunctionSemanticSummary? templateSemantics = null;
            if (functionTemplate.Semantics is not null)
            {
                if (!TryBuildImportedFunctionSemanticSummary(functionTemplate.Semantics, out var importedTemplateSemantics))
                {
                    return false;
                }

                templateSemantics = importedTemplateSemantics;
                loadedFunctionSemantics.TryAdd(functionTemplate.QualifiedResolvedName, importedTemplateSemantics);
            }

            loadedFunctionTemplates[functionTemplate.QualifiedResolvedName] = new ImportedFunctionTemplateSummary(
                TopLevelStatementCount: functionTemplate.TopLevelStatementCount,
                EstimatedBodyCost: functionTemplate.EstimatedBodyCost,
                SemanticSummary: templateSemantics,
                TypedBodySummary: TryBuildImportedTypedTemplateBody(functionTemplate.TypedBody, out var typedBody)
                    ? typedBody
                    : null,
                DeferredFunctionInstantiations: functionTemplate.DeferredFunctionInstantiations?
                    .Select(trigger => new ImportedDeferredFunctionInstantiationSummary(
                        trigger.CalleeTemplateName,
                        trigger.TypeArguments.Select(BuildTypeSymbol).ToArray()))
                    .ToArray(),
                DeferredTypeInstantiations: functionTemplate.DeferredTypeInstantiations?
                    .Select(trigger => new ImportedDeferredTypeInstantiationSummary(
                        BuildTypeSymbol(trigger.Type)))
                    .ToArray(),
                ObjectCreationSummaries: functionTemplate.ObjectCreations?
                    .Select(objectCreation => new ImportedTemplateObjectCreationSummary(
                        BuildTypeSymbol(objectCreation.CreatedType),
                        objectCreation.Constructor is null
                            ? null
                            : new TypedConstructorShape(
                                objectCreation.Constructor.TypeName,
                                objectCreation.Constructor.Parameters
                                    .Select(parameter => new TypedParameterSymbol(
                                        parameter.Name,
                                        BuildTypeSymbol(parameter.Type)))
                                    .ToArray(),
                                objectCreation.Constructor.IsPrimaryShape),
                        objectCreation.InitializerMembers?
                            .Select(initializerMember => new ImportedTemplateObjectInitializerMemberSummary(
                                initializerMember.FieldName,
                                initializerMember.FieldIndex,
                                BuildTypeSymbol(initializerMember.FieldType)))
                            .ToArray()))
                    .ToArray(),
                EnumConstructorSummaries: functionTemplate.EnumConstructors?
                    .Select(enumConstructor => new ImportedTemplateEnumConstructorSummary(
                        enumConstructor.Ordinal,
                        BuildTypeSymbol(enumConstructor.EnumType),
                        enumConstructor.VariantName,
                        enumConstructor.Members?
                            .Select(member => new ImportedTemplateEnumConstructorMemberSummary(
                                member.FieldName,
                                member.FieldIndex,
                                BuildTypeSymbol(member.FieldType)))
                            .ToArray()))
                    .ToArray(),
                EnumCallSummaries: functionTemplate.EnumCalls?
                    .Select(enumCall => new ImportedTemplateEnumCallSummary(
                        enumCall.Ordinal,
                        BuildTypeSymbol(enumCall.EnumType),
                        enumCall.VariantName))
                    .ToArray(),
                EnumValueSummaries: functionTemplate.EnumValues?
                    .Select(enumValue => new ImportedTemplateEnumValueSummary(
                        enumValue.Ordinal,
                        BuildTypeSymbol(enumValue.EnumType),
                        enumValue.VariantName))
                    .ToArray(),
                EnumPatternSummaries: functionTemplate.EnumPatterns?
                    .Select(enumPattern => new ImportedTemplateEnumPatternSummary(
                        enumPattern.Ordinal,
                        BuildTypeSymbol(enumPattern.EnumType),
                        enumPattern.VariantName,
                        enumPattern.Members?
                            .Select(member => new ImportedTemplateEnumPatternMemberSummary(
                                member.FieldName,
                                member.FieldIndex,
                                BuildTypeSymbol(member.FieldType)))
                            .ToArray()))
                    .ToArray(),
                AggregatePatternSummaries: functionTemplate.AggregatePatterns?
                    .Select(aggregatePattern => new ImportedTemplateAggregatePatternSummary(
                        aggregatePattern.Ordinal,
                        BuildTypeSymbol(aggregatePattern.Type)))
                    .ToArray(),
                LocalDeclarationSummaries: functionTemplate.LocalDeclarations?
                    .Select(local => new ImportedTemplateLocalDeclarationSummary(
                        local.Kind,
                        local.Line,
                        local.Column,
                        BuildTypeSymbol(local.Type)))
                    .ToArray(),
                ConversionSummaries: functionTemplate.Conversions?
                    .Select(conversion => new ImportedTemplateConversionSummary(
                        conversion.Ordinal,
                        BuildTypeSymbol(conversion.TargetType)))
                    .ToArray(),
                DirectCallSummaries: functionTemplate.DirectCalls?
                    .Select(directCall => new ImportedTemplateDirectCallSummary(
                        directCall.Ordinal,
                        new TypedFunctionSignature(
                            directCall.QualifiedResolvedName,
                            BuildTypeSymbol(directCall.ReturnType),
                            directCall.Parameters
                                .Select(parameter => new TypedParameterSymbol(
                                    parameter.Name,
                                    BuildTypeSymbol(parameter.Type)))
                                .ToArray(),
                            SourceName: directCall.QualifiedSourceName,
                            TemplateName: directCall.QualifiedTemplateName,
                            TypeArguments: directCall.TypeArguments?.Select(BuildTypeSymbol).ToArray())))
                    .ToArray(),
                FieldAccessSummaries: functionTemplate.FieldAccesses?
                    .Select(fieldAccess => new ImportedTemplateFieldAccessSummary(
                        fieldAccess.Ordinal,
                        fieldAccess.FieldName,
                        fieldAccess.FieldIndex,
                        BuildTypeSymbol(fieldAccess.FieldType)))
                    .ToArray(),
                MemberCallSummaries: functionTemplate.MemberCalls?
                    .Select(memberCall => new ImportedTemplateMemberCallSummary(
                        memberCall.Ordinal,
                        new TypedFunctionSignature(
                            memberCall.QualifiedResolvedName,
                            BuildTypeSymbol(memberCall.ReturnType),
                            memberCall.Parameters
                                .Select(parameter => new TypedParameterSymbol(
                                    parameter.Name,
                                    BuildTypeSymbol(parameter.Type)))
                                .ToArray(),
                            SourceName: memberCall.QualifiedSourceName,
                            TemplateName: memberCall.QualifiedTemplateName,
                            TypeArguments: memberCall.TypeArguments?.Select(BuildTypeSymbol).ToArray())))
                    .ToArray());
        }

        if (loadedFunctionEffects.Count == 0
            && loadedTypeAliases.Count == 0
            && loadedFunctionSignatures.Count == 0
            && loadedGlobals.Count == 0
            && loadedNamedTypes.Count == 0
            && loadedConstructors.Count == 0
            && loadedAbiFunctions.Count == 0
            && loadedConcreteLayouts.Count == 0
            && loadedEnumLayouts.Count == 0
            && loadedFunctionSemantics.Count == 0
            && loadedFunctionTemplates.Count == 0)
        {
            return false;
        }

        facts = new LoadedPackageImageFacts(
            loadedFunctionEffects,
            loadedTypeAliases,
            loadedFunctionSignatures,
            loadedGlobals,
            loadedNamedTypes,
            loadedConstructors,
            loadedAbiFunctions,
            loadedConcreteLayouts,
            loadedEnumLayouts,
            loadedFunctionSemantics,
            loadedFunctionTemplates);
        return true;
    }

    private static HashSet<string> CollectLocalNamedTypes(ResolvedPackageModule module)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var type in module.Module.EffectiveTypedInterface?.Types ?? [])
        {
            names.Add(type.Name);
        }

        foreach (var typeAlias in module.Module.EffectiveTypedInterface?.TypeAliases ?? [])
        {
            names.Add(typeAlias.Name);
        }

        return names;
    }

    private static bool TryBuildAbiFunctionSignature(
        StarkPackageAbiFunctionManifest abiFunction,
        out AbiFunctionSignature signature)
    {
        signature = default!;

        var parameters = new List<AbiParameterSymbol>(abiFunction.Parameters.Count);
        foreach (var parameter in abiFunction.Parameters)
        {
            if (!TryParseAbiParameterKind(parameter.Kind, out var kind))
            {
                return false;
            }

            parameters.Add(new AbiParameterSymbol(
                parameter.SourceName,
                parameter.LlvmName,
                BuildTypeSymbol(parameter.SourceType),
                BuildTypeSymbol(parameter.LlvmType),
                kind));
        }

        signature = new AbiFunctionSignature(
            abiFunction.QualifiedResolvedName,
            abiFunction.SymbolName,
            BuildTypeSymbol(abiFunction.SourceReturnType),
            BuildTypeSymbol(abiFunction.LlvmReturnType),
            parameters,
            abiFunction.IsFfi,
            SourceName: abiFunction.SourceName,
            UsesFastCallingConvention: abiFunction.UsesFastCallingConvention);
        return true;
    }

    private static bool TryBuildImportedFunctionSemanticSummary(
        StarkPackageFunctionSemanticManifest functionSemantic,
        out ImportedFunctionSemanticSummary summary)
    {
        summary = default!;

        if (!TryParseFunctionKind(functionSemantic.DeclaredKind, out var declaredKind)
            || !TryParseFunctionKind(functionSemantic.EffectiveKind, out var effectiveKind))
        {
            return false;
        }

        var parameters = functionSemantic.Parameters is null
            ? null
            : new List<ParameterMemoryEffectSummary>(functionSemantic.Parameters.Count);
        if (parameters is not null)
        {
            foreach (var parameter in functionSemantic.Parameters!)
            {
                if (!TryParseParameterCaptureKind(parameter.CaptureKind, out var captureKind))
                {
                    return false;
                }

                parameters.Add(new ParameterMemoryEffectSummary(
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
                    captureKind));
            }
        }

        var memoryEffects = functionSemantic.MemoryEffects is null
            ? null
            : new FunctionMemoryEffectSummary(
                functionSemantic.MemoryEffects.ReadsArgumentMemory,
                functionSemantic.MemoryEffects.WritesArgumentMemory,
                functionSemantic.MemoryEffects.CapturesArgumentMemory,
                functionSemantic.MemoryEffects.ReadsOtherMemory,
                functionSemantic.MemoryEffects.WritesOtherMemory);

        var optimizationSummary = functionSemantic.Optimization is null
            ? null
            : new FunctionOptimizationSummary(
                functionSemantic.Optimization.DirectCallCount,
                functionSemantic.Optimization.MemberCallCount,
                functionSemantic.Optimization.FieldAccessCount,
                functionSemantic.Optimization.IndexAccessCount,
                functionSemantic.Optimization.BranchStatementCount,
                functionSemantic.Optimization.LoopStatementCount,
                functionSemantic.Optimization.ObjectCreationCount,
                functionSemantic.Optimization.IsSingleReturnDirectCallForwarder,
                functionSemantic.Optimization.IsSingleReturnMemberCallForwarder,
                functionSemantic.Optimization.IsSingleReturnFieldAccessWrapper,
                functionSemantic.Optimization.IsSingleReturnIndexAccessWrapper,
                functionSemantic.Optimization.IsSingleReturnConversionWrapper,
                functionSemantic.Optimization.IsSingleReturnAddressOfWrapper,
                functionSemantic.Optimization.IsSingleReturnDereferenceWrapper);

        summary = new ImportedFunctionSemanticSummary(
            functionSemantic.QualifiedResolvedName,
            declaredKind,
            effectiveKind,
            functionSemantic.CalledFunctions,
            memoryEffects,
            parameters,
            optimizationSummary);
        return true;
    }
}
