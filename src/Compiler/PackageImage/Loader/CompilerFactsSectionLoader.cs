using System.Globalization;
using System.Numerics;

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
        PackageImageLinkageFacts? loadedLinkage = null;
        var backendOptimizationMode = ModuleBackendOptimizationMode.Default;
        var localNamedTypes = CollectLocalNamedTypes(module);

        foreach (var function in module.Module.EffectiveTypedInterface?.Functions ?? [])
        {
            var qualifiedResolvedName = function.QualifiedResolvedName ?? function.QualifiedName;
            if (!TryParseBackendOptimizationMode(function.BackendOptimizationMode, out var functionBackendOptimizationMode))
            {
                return false;
            }

            _ = TryParseFunctionKind(function.Kind, out var functionKind);
            loadedFunctionSignatures[qualifiedResolvedName] = new TypedFunctionSignature(
                qualifiedResolvedName,
                BuildTypeSymbol(function.ReturnType, module.Module.ModuleName, localNamedTypes),
                function.Parameters
                    .Select(parameter => BuildTypedParameterSymbol(parameter, module.Module.ModuleName, localNamedTypes))
                    .ToArray(),
                SourceName: function.QualifiedName,
                GenericParameterNames: function.GenericParameters?.Count > 0 ? function.GenericParameters.ToArray() : null,
                Kind: functionKind,
                IsUnsafe: function.IsUnsafe,
                IsVarargs: function.IsVarargs,
                FfiAbi: ParsePackageFunctionFfiAbi(function.FfiAbi),
                BackendOptimizationMode: functionBackendOptimizationMode,
                DisjointParameterGroups: BuildParameterDisjointGroups(function.Parameters, function.DisjointParameterGroups),
                OverlapParameterGroups: BuildParameterOverlapGroups(function.OverlapParameterGroups),
                SameParameterGroups: BuildParameterSameGroups(function.SameParameterGroups),
                HasBody: function.HasBody);
        }

        foreach (var type in module.Module.EffectiveTypedInterface?.Types ?? [])
        {
            if (!TryParseBackendOptimizationMode(type.BackendOptimizationMode, out var typeBackendOptimizationMode))
            {
                return false;
            }

            foreach (var method in type.Methods ?? [])
            {
                var qualifiedResolvedName = method.QualifiedResolvedName ?? method.QualifiedName;
                var genericParameterNames = FunctionGenericParameterFacts.CombineGenericParameterNames(
                    type.GenericParameters,
                    method.GenericParameters);
                if (!TryParseBackendOptimizationMode(method.BackendOptimizationMode, out var methodBackendOptimizationMode))
                {
                    return false;
                }

                if (methodBackendOptimizationMode == ModuleBackendOptimizationMode.Default)
                {
                    methodBackendOptimizationMode = typeBackendOptimizationMode;
                }

                _ = TryParseFunctionKind(method.Kind, out var methodKind);
                loadedFunctionSignatures[qualifiedResolvedName] = new TypedFunctionSignature(
                    qualifiedResolvedName,
                    BuildTypeSymbol(method.ReturnType, module.Module.ModuleName, localNamedTypes),
                    method.Parameters
                        .Select(parameter => BuildTypedParameterSymbol(parameter, module.Module.ModuleName, localNamedTypes))
                    .ToArray(),
                    SourceName: method.QualifiedName,
                    GenericParameterNames: genericParameterNames.Count == 0 ? null : genericParameterNames.ToArray(),
                    IsStatic: method.IsStatic,
                    Kind: methodKind,
                    IsUnsafe: method.IsUnsafe,
                    IsVarargs: method.IsVarargs,
                    FfiAbi: ParsePackageFunctionFfiAbi(method.FfiAbi),
                    BackendOptimizationMode: methodBackendOptimizationMode,
                    DisjointParameterGroups: BuildParameterDisjointGroups(method.Parameters, method.DisjointParameterGroups),
                    OverlapParameterGroups: BuildParameterOverlapGroups(method.OverlapParameterGroups),
                    SameParameterGroups: BuildParameterSameGroups(method.SameParameterGroups),
                    HasBody: method.HasBody);
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
                bindingKind,
                BuildTypedConstantInitializer(global.ConstantInitializer, module.Module.ModuleName, localNamedTypes));
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
                            .ToArray(),
                        AbsorbsErrorType: variant.AbsorbsErrorType is { } absorbedErrorType
                            ? BuildTypeSymbol(absorbedErrorType, module.Module.ModuleName, localNamedTypes)
                            : null,
                        Role: ParseEnumVariantRole(variant.Role)))
                    .ToArray();
                loadedNamedTypes[qualifiedName] = new NamedTypeSymbol(
                    qualifiedName,
                    declarationKind,
                    new Dictionary<string, FieldSymbol>(StringComparer.Ordinal),
                    [],
                    EnumVariants: variants,
                    GenericParameterNames: genericParameterNames,
                    ImplementedTraitNames: QualifyImplementedTraitNames(
                        type.ImplementedTraits,
                        module.Module.ModuleName,
                        localNamedTypes));
            }
            else
            {
                var fields = new Dictionary<string, FieldSymbol>(StringComparer.Ordinal);
                var orderedFields = new List<FieldSymbol>(type.Fields.Count);
                foreach (var field in type.Fields)
                {
                    if (!TryParseVisibility(field.Visibility ?? "public", out var fieldVisibility))
                    {
                        return false;
                    }

                    var fieldSymbol = new FieldSymbol(
                        field.Name,
                        BuildTypeSymbol(field.Type, module.Module.ModuleName, localNamedTypes),
                        fieldVisibility,
                        module.Module.ModuleName,
                        field.ExplicitOffsetBytes);
                    fields[field.Name] = fieldSymbol;
                    orderedFields.Add(fieldSymbol);
                }

                loadedNamedTypes[qualifiedName] = new NamedTypeSymbol(
                    qualifiedName,
                    declarationKind,
                    fields,
                    orderedFields,
                    GenericParameterNames: genericParameterNames,
                    ImplementedTraitNames: QualifyImplementedTraitNames(
                        type.ImplementedTraits,
                        module.Module.ModuleName,
                        localNamedTypes),
                    Layout: BuildStructLayoutMetadata(type.StructLayout, type.PackBytes, type.AlignBytes));
            }

            var constructors = new List<TypedConstructorShape>();

            if (type.PrimaryConstructorParameters is { Count: > 0 })
            {
                constructors.Add(new TypedConstructorShape(
                    type.Name,
                    type.PrimaryConstructorParameters
                        .Select(parameter => BuildTypedParameterSymbol(parameter, module.Module.ModuleName, localNamedTypes))
                        .ToArray(),
                    IsPrimaryShape: true));
            }

            foreach (var constructor in type.Constructors ?? [])
            {
                constructors.Add(new TypedConstructorShape(
                    type.Name,
                    constructor.Parameters
                        .Select(parameter => BuildTypedParameterSymbol(parameter, module.Module.ModuleName, localNamedTypes))
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
            if (!TryParseBackendOptimizationMode(compilerFacts.BackendOptimizationMode, out backendOptimizationMode))
            {
                return false;
            }

            foreach (var type in compilerFacts.NamedTypes ?? [])
            {
                if (!TryLoadTypedTypeManifest(
                        type,
                        module.Module.ModuleName,
                        localNamedTypes,
                        loadedNamedTypes,
                        loadedConstructors))
                {
                    return false;
                }
            }

            foreach (var functionEffect in compilerFacts.FunctionEffects)
            {
                if (!TryParseFunctionKind(functionEffect.Kind, out var kind)
                    || !TryParseInlinePreference(functionEffect.InlinePreference, out var inlinePreference)
                    || !TryParseBackendOptimizationMode(functionEffect.BackendOptimizationMode, out var functionBackendOptimizationMode))
                {
                    return false;
                }

                if (functionBackendOptimizationMode == ModuleBackendOptimizationMode.Opaque)
                {
                    inlinePreference = InlinePreference.NoInline;
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
                    IsVarargs: functionEffect.IsVarargs,
                    IsHot: functionEffect.IsHot,
                    IsCold: functionEffect.IsCold,
                    InlinePreference: inlinePreference,
                    IsStrictFp: functionEffect.IsStrictFp,
                    BackendOptimizationMode: functionBackendOptimizationMode,
                    FfiAbi: ParsePackageFunctionFfiAbi(functionEffect.FfiAbi));
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
                    concreteLayout.AlignmentBytes,
                    concreteLayout.Fields?
                        .Select(field => new ConcreteFieldLayout(
                            field.Name,
                            field.OffsetBytes,
                            field.SizeBytes,
                            field.NaturalAlignmentBytes,
                            field.EffectiveAlignmentBytes,
                            field.IsMisaligned))
                        .ToArray());
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

            if (compilerFacts.Linkage is not null)
            {
                if (!TryBuildPackageImageLinkageFacts(compilerFacts.Linkage, out loadedLinkage))
                {
                    return false;
                }
            }
        }

        foreach (var functionTemplate in module.Module.EffectiveGenericTemplates?.Functions ?? [])
        {
            if (!TryParseBackendOptimizationMode(functionTemplate.BackendOptimizationMode, out var templateBackendOptimizationMode))
            {
                return false;
            }

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

            var boundOperationSummaries = BuildImportedTemplateBoundOperations(
                functionTemplate.BoundOperations,
                functionTemplate.QualifiedResolvedName);
            if (functionTemplate.BoundOperations is not null && boundOperationSummaries is null)
            {
                return false;
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
                                    .Select(BuildTypedParameterSymbol)
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
                                .Select(BuildTypedParameterSymbol)
                                .ToArray(),
                            SourceName: directCall.QualifiedSourceName,
                            TemplateName: directCall.QualifiedTemplateName,
                            TypeArguments: directCall.TypeArguments?.Select(BuildTypeSymbol).ToArray(),
                            DisjointParameterGroups: BuildParameterDisjointGroups(directCall.Parameters, directCall.DisjointParameterGroups),
                            OverlapParameterGroups: BuildParameterOverlapGroups(directCall.OverlapParameterGroups),
                            SameParameterGroups: BuildParameterSameGroups(directCall.SameParameterGroups))))
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
                                .Select(BuildTypedParameterSymbol)
                                .ToArray(),
                            SourceName: memberCall.QualifiedSourceName,
                            TemplateName: memberCall.QualifiedTemplateName,
                            TypeArguments: memberCall.TypeArguments?.Select(BuildTypeSymbol).ToArray(),
                            DisjointParameterGroups: BuildParameterDisjointGroups(memberCall.Parameters, memberCall.DisjointParameterGroups),
                            OverlapParameterGroups: BuildParameterOverlapGroups(memberCall.OverlapParameterGroups),
                            SameParameterGroups: BuildParameterSameGroups(memberCall.SameParameterGroups))))
                    .ToArray(),
                FunctionAddressSummaries: functionTemplate.FunctionAddresses?
                    .Select(functionAddress => new ImportedTemplateFunctionAddressSummary(
                        functionAddress.Ordinal,
                        new TypedFunctionSignature(
                            functionAddress.QualifiedResolvedName,
                            BuildTypeSymbol(functionAddress.ReturnType),
                            functionAddress.Parameters
                                .Select(BuildTypedParameterSymbol)
                                .ToArray(),
                            SourceName: functionAddress.QualifiedSourceName,
                            TemplateName: functionAddress.QualifiedTemplateName,
                            TypeArguments: functionAddress.TypeArguments?.Select(BuildTypeSymbol).ToArray(),
                            DisjointParameterGroups: BuildParameterDisjointGroups(functionAddress.Parameters, functionAddress.DisjointParameterGroups),
                            OverlapParameterGroups: BuildParameterOverlapGroups(functionAddress.OverlapParameterGroups),
                            SameParameterGroups: BuildParameterSameGroups(functionAddress.SameParameterGroups)),
                        BuildTypeSymbol(functionAddress.TargetType)))
                    .ToArray(),
                BoundOperationSummaries: boundOperationSummaries,
                BackendOptimizationMode: templateBackendOptimizationMode,
                TryPropagationSummaries: functionTemplate.TryPropagations?
                    .Select(tryPropagation => new ImportedTemplateTryPropagationSummary(
                        tryPropagation.Ordinal,
                        BuildTypeSymbol(tryPropagation.OperandType),
                        tryPropagation.OperandOkVariantName,
                        tryPropagation.OperandErrVariantName,
                        tryPropagation.SuccessPayloadType is { } successPayloadType
                            ? BuildTypeSymbol(successPayloadType)
                            : null,
                        tryPropagation.OperandFailurePayloadType is { } operandFailurePayloadType
                            ? BuildTypeSymbol(operandFailurePayloadType)
                            : null,
                        BuildTypeSymbol(tryPropagation.ReturnType),
                        tryPropagation.EnclosingErrVariantName,
                        tryPropagation.EnclosingFailurePayloadType is { } enclosingFailurePayloadType
                            ? BuildTypeSymbol(enclosingFailurePayloadType)
                            : null,
                        tryPropagation.ConversionFunnelVariant))
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
            if (module.Module.EffectiveTypedInterface is null
                && module.Module.EffectiveCompilerFacts is null
                && module.Module.EffectiveGenericTemplates is null)
            {
                return false;
            }
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
            loadedFunctionTemplates,
            loadedLinkage,
            backendOptimizationMode);
        return true;
    }

    private static TypedConstantInitializer? BuildTypedConstantInitializer(
        StarkPackageTypedConstantInitializerManifest? manifest,
        string moduleName,
        ISet<string>? localNamedTypes)
    {
        if (manifest is null)
        {
            return null;
        }

        var type = BuildTypeSymbol(manifest.Type, moduleName, localNamedTypes);
        return manifest.Kind switch
        {
            "integer" when manifest.IntegerValue is { } integerText
                           && BigInteger.TryParse(
                               integerText,
                               NumberStyles.Integer,
                               CultureInfo.InvariantCulture,
                               out var integerValue)
                => new TypedConstantInitializer(
                    TypedConstantInitializerKind.Integer,
                    type,
                    IntegerValue: integerValue),
            "float" when manifest.FloatLiteralText is not null
                => new TypedConstantInitializer(
                    TypedConstantInitializerKind.Float,
                    type,
                    FloatLiteralText: manifest.FloatLiteralText),
            "bool" when manifest.BoolValue is { } boolValue
                => new TypedConstantInitializer(
                    TypedConstantInitializerKind.Bool,
                    type,
                    BoolValue: boolValue),
            "text" when manifest.TextLiteralText is not null
                => new TypedConstantInitializer(
                    TypedConstantInitializerKind.Text,
                    type,
                    TextLiteralText: manifest.TextLiteralText),
            "null" => new TypedConstantInitializer(
                TypedConstantInitializerKind.Null,
                type),
            "fixedarray" when manifest.Elements is not null
                => BuildTypedConstantArrayInitializer(manifest, type, moduleName, localNamedTypes),
            _ => null
        };
    }

    private static TypedConstantInitializer? BuildTypedConstantArrayInitializer(
        StarkPackageTypedConstantInitializerManifest manifest,
        StarkTypeSymbol type,
        string moduleName,
        ISet<string>? localNamedTypes)
    {
        if (manifest.Elements is null)
        {
            return null;
        }

        var elements = new TypedConstantInitializer[manifest.Elements.Count];
        for (var index = 0; index < manifest.Elements.Count; index++)
        {
            if (BuildTypedConstantInitializer(manifest.Elements[index], moduleName, localNamedTypes) is not { } element)
            {
                return null;
            }

            elements[index] = element;
        }

        return new TypedConstantInitializer(
            TypedConstantInitializerKind.FixedArray,
            type,
            Elements: elements);
    }

    private static bool TryParseBackendOptimizationMode(
        string? value,
        out ModuleBackendOptimizationMode mode)
    {
        mode = ModuleBackendOptimizationMode.Default;
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "default", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(value, "opaque", StringComparison.OrdinalIgnoreCase))
        {
            mode = ModuleBackendOptimizationMode.Opaque;
            return true;
        }

        return false;
    }

    private static bool TryBuildPackageImageLinkageFacts(
        StarkPackageLinkageManifest linkage,
        out PackageImageLinkageFacts facts)
    {
        facts = default!;
        if (string.IsNullOrWhiteSpace(linkage.ObjectFileName)
            || linkage.DefinedSymbols is null)
        {
            return false;
        }

        facts = new PackageImageLinkageFacts(
            linkage.ObjectFileName,
            linkage.DefinedSymbols
                .Where(static symbol => !string.IsNullOrWhiteSpace(symbol))
                .ToHashSet(StringComparer.Ordinal),
            (linkage.ReferencedSymbols ?? [])
                .Where(static symbol => !string.IsNullOrWhiteSpace(symbol))
                .ToHashSet(StringComparer.Ordinal));
        return true;
    }

    private static HashSet<string> CollectLocalNamedTypes(ResolvedPackageModule module)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var type in module.Module.EffectiveTypedInterface?.Types ?? [])
        {
            names.Add(type.Name);
        }

        foreach (var type in module.Module.EffectiveCompilerFacts?.NamedTypes ?? [])
        {
            names.Add(type.Name);
        }

        foreach (var typeAlias in module.Module.EffectiveTypedInterface?.TypeAliases ?? [])
        {
            names.Add(typeAlias.Name);
        }

        return names;
    }

    private static bool TryLoadTypedTypeManifest(
        StarkPackageTypedTypeManifest type,
        string moduleName,
        ISet<string> localNamedTypes,
        IDictionary<string, NamedTypeSymbol> loadedNamedTypes,
        IDictionary<string, IReadOnlyList<TypedConstructorShape>> loadedConstructors)
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
                            BuildTypeSymbol(field.Type, moduleName, localNamedTypes)))
                        .ToArray(),
                    AbsorbsErrorType: variant.AbsorbsErrorType is { } absorbedErrorType
                        ? BuildTypeSymbol(absorbedErrorType, moduleName, localNamedTypes)
                        : null,
                    Role: ParseEnumVariantRole(variant.Role)))
                .ToArray();
            loadedNamedTypes[qualifiedName] = new NamedTypeSymbol(
                qualifiedName,
                declarationKind,
                new Dictionary<string, FieldSymbol>(StringComparer.Ordinal),
                [],
                EnumVariants: variants,
                GenericParameterNames: genericParameterNames,
                ImplementedTraitNames: QualifyImplementedTraitNames(
                    type.ImplementedTraits,
                    moduleName,
                    localNamedTypes));
        }
        else
        {
            var fields = new Dictionary<string, FieldSymbol>(StringComparer.Ordinal);
            var orderedFields = new List<FieldSymbol>(type.Fields.Count);
            foreach (var field in type.Fields)
            {
                if (!TryParseVisibility(field.Visibility ?? "public", out var fieldVisibility))
                {
                    return false;
                }

                var fieldSymbol = new FieldSymbol(
                    field.Name,
                    BuildTypeSymbol(field.Type, moduleName, localNamedTypes),
                    fieldVisibility,
                    moduleName,
                    field.ExplicitOffsetBytes);
                fields[field.Name] = fieldSymbol;
                orderedFields.Add(fieldSymbol);
            }

            loadedNamedTypes[qualifiedName] = new NamedTypeSymbol(
                qualifiedName,
                declarationKind,
                fields,
                orderedFields,
                GenericParameterNames: genericParameterNames,
                ImplementedTraitNames: QualifyImplementedTraitNames(
                    type.ImplementedTraits,
                    moduleName,
                    localNamedTypes),
                Layout: BuildStructLayoutMetadata(type.StructLayout, type.PackBytes, type.AlignBytes));
        }

        var constructors = new List<TypedConstructorShape>();
        if (type.PrimaryConstructorParameters is { Count: > 0 })
        {
            constructors.Add(new TypedConstructorShape(
                type.Name,
                type.PrimaryConstructorParameters
                    .Select(parameter => BuildTypedParameterSymbol(parameter, moduleName, localNamedTypes))
                    .ToArray(),
                IsPrimaryShape: true));
        }

        foreach (var constructor in type.Constructors ?? [])
        {
            constructors.Add(new TypedConstructorShape(
                type.Name,
                constructor.Parameters
                    .Select(parameter => BuildTypedParameterSymbol(parameter, moduleName, localNamedTypes))
                    .ToArray(),
                IsPrimaryShape: false));
        }

        if (constructors.Count > 0)
        {
            loadedConstructors[qualifiedName] = constructors;
        }

        return true;
    }

    private static TypedParameterSymbol BuildTypedParameterSymbol(
        StarkPackageTypedParameterManifest parameter,
        string moduleName,
        ISet<string> localNamedTypes)
    {
        return new TypedParameterSymbol(
            parameter.Name,
            BuildTypeSymbol(parameter.Type, moduleName, localNamedTypes),
            parameter.IsDisjoint,
            parameter.IsConst,
            parameter.RawPointerElementCountExpression);
    }

    private static IReadOnlyList<string>? QualifyImplementedTraitNames(
        IReadOnlyList<string>? implementedTraits,
        string moduleName,
        ISet<string> localNamedTypes)
    {
        if (implementedTraits is not { Count: > 0 })
        {
            return null;
        }

        var qualified = implementedTraits
            .Select(traitName => QualifyLoadedNamedType(traitName, moduleName, localNamedTypes))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static traitName => traitName, StringComparer.Ordinal)
            .ToArray();
        return qualified.Length == 0 ? null : qualified;
    }

    private static TypedParameterSymbol BuildTypedParameterSymbol(StarkPackageTypedParameterManifest parameter)
    {
        return new TypedParameterSymbol(
            parameter.Name,
            BuildTypeSymbol(parameter.Type),
            parameter.IsDisjoint,
            parameter.IsConst,
            parameter.RawPointerElementCountExpression);
    }

    private static IReadOnlyList<ImportedTemplateBoundOperationSummary>? BuildImportedTemplateBoundOperations(
        IReadOnlyList<StarkPackageTemplateBoundOperationManifest>? operations,
        string fallbackEnclosingFunctionName)
    {
        if (operations is not { Count: > 0 })
        {
            return null;
        }

        var summaries = new List<ImportedTemplateBoundOperationSummary>(operations.Count);
        foreach (var operation in operations)
        {
            if (!TryBuildImportedTemplateBoundOperation(operation, fallbackEnclosingFunctionName, out var summary))
            {
                return null;
            }

            summaries.Add(summary);
        }

        return summaries;
    }

    private static bool TryBuildImportedTemplateBoundOperation(
        StarkPackageTemplateBoundOperationManifest operation,
        string fallbackEnclosingFunctionName,
        out ImportedTemplateBoundOperationSummary summary)
    {
        summary = null!;
        var location = new SourceLocation(
            FilePath: null,
            operation.Line,
            operation.Column);
        var enclosingFunctionName = string.IsNullOrWhiteSpace(operation.EnclosingFunctionName)
            ? fallbackEnclosingFunctionName
            : operation.EnclosingFunctionName;
        var resultType = BuildTypeSymbol(operation.ResultType);

        BoundOperation boundOperation;
        switch (operation.Kind)
        {
            case "direct-call":
                if (!TryBuildImportedTemplateBoundSignature(operation, out var directSignature))
                {
                    return false;
                }

                boundOperation = new BoundDirectCallOperation(
                    directSignature,
                    BuildImportedTemplateCallArguments(operation.CallArguments),
                    location,
                    enclosingFunctionName);
                break;

            case "member-call":
                if (!TryBuildImportedTemplateBoundSignature(operation, out var memberSignature)
                    || operation.ReceiverType is null)
                {
                    return false;
                }

                boundOperation = new BoundMemberCallOperation(
                    memberSignature,
                    BuildTypeSymbol(operation.ReceiverType),
                    operation.ReceiverIsAddressable ?? false,
                    operation.ReceiverIsMutable ?? false,
                    BuildImportedTemplateCallArguments(operation.CallArguments),
                    location,
                    enclosingFunctionName);
                break;

            case "function-pointer-call":
                if (operation.FunctionPointerType is null)
                {
                    return false;
                }

                boundOperation = new BoundFunctionPointerCallOperation(
                    BuildTypeSymbol(operation.FunctionPointerType),
                    BuildImportedTemplateCallArguments(operation.CallArguments),
                    location,
                    enclosingFunctionName);
                break;

            case "closure-call":
                if (operation.ClosureType is null)
                {
                    return false;
                }

                boundOperation = new BoundClosureCallOperation(
                    BuildTypeSymbol(operation.ClosureType),
                    BuildImportedTemplateCallArguments(operation.CallArguments),
                    location,
                    enclosingFunctionName);
                break;

            case "index-access":
            case "slice-access":
                if (operation.AccessKind is null
                    || operation.SourceKind is null
                    || operation.SourceType is null
                    || operation.IndexCount is null)
                {
                    return false;
                }

                boundOperation = new BoundIndexAccessOperation(
                    ParseBoundIndexAccessKind(operation.AccessKind),
                    operation.SourceKind,
                    BuildTypeSymbol(operation.SourceType),
                    resultType,
                    operation.IndexCount.Value,
                    location,
                    enclosingFunctionName);
                break;

            case "dynamic-storage-operation":
                if (operation.OperationName is null
                    || operation.ReceiverType is null
                    || operation.ArgumentCount is null)
                {
                    return false;
                }

                boundOperation = new BoundDynamicStorageOperation(
                    operation.OperationName,
                    BuildTypeSymbol(operation.ReceiverType),
                    resultType,
                    operation.ArgumentCount.Value,
                    operation.ReceiverIsAddressable ?? false,
                    operation.ReceiverIsMutable ?? false,
                    location,
                    enclosingFunctionName);
                break;

            case "object-creation":
                if (operation.CreatedType is null)
                {
                    return false;
                }

                boundOperation = new BoundObjectCreationOperation(
                    operation.ExpressionText ?? string.Empty,
                    BuildTypeSymbol(operation.CreatedType),
                    BuildImportedTemplateConstructorShape(operation.Constructor),
                    operation.InitializerMembers?
                        .Select(member => new ObjectInitializerMemberTypingRecord(
                            member.FieldName,
                            member.FieldIndex,
                            BuildTypeSymbol(member.FieldType)))
                        .ToArray(),
                    location,
                    enclosingFunctionName);
                break;

            case "enum-construction":
                if (operation.EnumType is null || operation.VariantName is null)
                {
                    return false;
                }

                boundOperation = new BoundEnumConstructionOperation(
                    BuildTypeSymbol(operation.EnumType),
                    operation.VariantName,
                    operation.EnumMembers?
                        .Select(member => new EnumConstructorMemberTypingRecord(
                            member.FieldName,
                            member.FieldIndex,
                            BuildTypeSymbol(member.FieldType)))
                        .ToArray(),
                    location,
                    enclosingFunctionName);
                break;

            case "enum-call":
                if (operation.EnumType is null || operation.VariantName is null)
                {
                    return false;
                }

                boundOperation = new BoundEnumCallOperation(
                    BuildTypeSymbol(operation.EnumType),
                    operation.VariantName,
                    location,
                    enclosingFunctionName);
                break;

            case "enum-value":
                if (operation.EnumType is null || operation.VariantName is null)
                {
                    return false;
                }

                boundOperation = new BoundEnumValueOperation(
                    BuildTypeSymbol(operation.EnumType),
                    operation.VariantName,
                    location,
                    enclosingFunctionName);
                break;

            case "text-interpolation":
                if (operation.SegmentCount is null
                    || operation.HoleCount is null
                    || operation.UsesFixedStorage is null)
                {
                    return false;
                }

                boundOperation = new BoundTextInterpolationOperation(
                    resultType,
                    operation.SegmentCount.Value,
                    operation.HoleCount.Value,
                    operation.UsesFixedStorage.Value,
                    location,
                    enclosingFunctionName);
                break;

            case "text-build":
                if (operation.BuildKind is null
                    || operation.OperandCount is null
                    || operation.UsesFixedStorage is null)
                {
                    return false;
                }

                boundOperation = new BoundTextBuildOperation(
                    operation.BuildKind,
                    resultType,
                    operation.OperandCount.Value,
                    operation.UsesFixedStorage.Value,
                    location,
                    enclosingFunctionName);
                break;

            case "layout-query":
                if (operation.QueryKind is null || operation.TargetType is null)
                {
                    return false;
                }

                boundOperation = new BoundLayoutQueryOperation(
                    ParseBoundLayoutQueryKind(operation.QueryKind),
                    BuildTypeSymbol(operation.TargetType),
                    resultType,
                    location,
                    enclosingFunctionName);
                break;

            case "switch-dispatch":
                if (operation.SwitchFamily is null
                    || operation.SwitchType is null
                    || operation.SectionCount is null
                    || operation.LabelCount is null
                    || operation.ExplicitDefaultLabelCount is null
                    || operation.LoweredDefaultLabelCount is null
                    || operation.LiteralLabelCount is null
                    || operation.MatchAllLabelCount is null
                    || operation.CaptureLabelCount is null
                    || operation.StructuredPatternLabelCount is null
                    || operation.GuardedLabelCount is null)
                {
                    return false;
                }

                boundOperation = new BoundSwitchDispatchOperation(
                    operation.SwitchFamily,
                    BuildTypeSymbol(operation.SwitchType),
                    operation.SectionCount.Value,
                    operation.LabelCount.Value,
                    operation.ExplicitDefaultLabelCount.Value,
                    operation.LoweredDefaultLabelCount.Value,
                    operation.LiteralLabelCount.Value,
                    operation.MatchAllLabelCount.Value,
                    operation.CaptureLabelCount.Value,
                    operation.StructuredPatternLabelCount.Value,
                    operation.GuardedLabelCount.Value,
                    location,
                    enclosingFunctionName);
                break;

            default:
                return false;
        }

        summary = new ImportedTemplateBoundOperationSummary(operation.Ordinal, boundOperation);
        return true;
    }

    private static bool TryBuildImportedTemplateBoundSignature(
        StarkPackageTemplateBoundOperationManifest operation,
        out TypedFunctionSignature signature)
    {
        signature = null!;

        if (operation.QualifiedResolvedName is null)
        {
            return false;
        }

        var parameters = operation.Parameters ?? [];
        signature = new TypedFunctionSignature(
            operation.QualifiedResolvedName,
            BuildTypeSymbol(operation.ReturnType ?? operation.ResultType),
            parameters.Select(BuildTypedParameterSymbol).ToArray(),
            SourceName: operation.QualifiedSourceName,
            TemplateName: operation.QualifiedTemplateName,
            TypeArguments: operation.TypeArguments?.Select(BuildTypeSymbol).ToArray(),
            DisjointParameterGroups: BuildParameterDisjointGroups(parameters, operation.DisjointParameterGroups),
            OverlapParameterGroups: BuildParameterOverlapGroups(operation.OverlapParameterGroups),
            SameParameterGroups: BuildParameterSameGroups(operation.SameParameterGroups));
        return true;
    }

    private static IReadOnlyList<CallArgumentTypingRecord>? BuildImportedTemplateCallArguments(
        IReadOnlyList<StarkPackageTemplateCallArgumentManifest>? arguments)
    {
        return arguments is { Count: > 0 }
            ? arguments
                .Select(argument => new CallArgumentTypingRecord(
                    argument.ParameterIndex,
                    argument.SourceArgumentIndex,
                    BuildTypeSymbol(argument.ParameterType),
                    BuildTypeSymbol(argument.ArgumentType),
                    argument.IsReceiver,
                    argument.RequiresAddressable,
                    argument.RequiresMutable,
                    argument.RequiresConstProvenance,
                    argument.ArgumentIsAddressable,
                    argument.ArgumentIsMutable,
                    argument.ArgumentHasConstProvenance))
                .ToArray()
            : null;
    }

    private static TypedConstructorShape? BuildImportedTemplateConstructorShape(
        StarkPackagePublishedConstructorShapeManifest? constructor)
    {
        return constructor is null
            ? null
            : new TypedConstructorShape(
                constructor.TypeName,
                constructor.Parameters
                    .Select(BuildTypedParameterSymbol)
                    .ToArray(),
                constructor.IsPrimaryShape);
    }

    private static BoundIndexAccessKind ParseBoundIndexAccessKind(string kind)
    {
        return kind switch
        {
            "slice" => BoundIndexAccessKind.Slice,
            "text-element" => BoundIndexAccessKind.TextElement,
            "text-slice" => BoundIndexAccessKind.TextSlice,
            "dynamic-element" => BoundIndexAccessKind.DynamicElement,
            "dynamic-slice" => BoundIndexAccessKind.DynamicSlice,
            "raw-pointer-region" => BoundIndexAccessKind.RawPointerRegion,
            _ => BoundIndexAccessKind.Element
        };
    }

    private static BoundLayoutQueryKind ParseBoundLayoutQueryKind(string kind)
    {
        return string.Equals(kind, "alignof", StringComparison.Ordinal)
            ? BoundLayoutQueryKind.AlignOf
            : BoundLayoutQueryKind.SizeOf;
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
                kind,
                parameter.RawPointerElementCountExpression));
        }

        signature = new AbiFunctionSignature(
            abiFunction.QualifiedResolvedName,
            abiFunction.SymbolName,
            BuildTypeSymbol(abiFunction.SourceReturnType),
            BuildTypeSymbol(abiFunction.LlvmReturnType),
            parameters,
            abiFunction.IsFfi,
            SourceName: abiFunction.SourceName,
            UsesFastCallingConvention: abiFunction.UsesFastCallingConvention,
            IsVarargs: abiFunction.IsVarargs,
            FfiAbi: ParsePackageFunctionFfiAbi(abiFunction.FfiAbi));
        return true;
    }

    private static StarkFfiAbi? ParsePackageFunctionFfiAbi(string? abiText)
    {
        return StarkFfiAbiFacts.TryParse(abiText, out var abi)
            ? abi
            : null;
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

        List<CallMemoryEffectSummary>? calls = null;
        if (functionSemantic.Calls is { } publishedCalls)
        {
            calls = new List<CallMemoryEffectSummary>(publishedCalls.Count);
            foreach (var call in publishedCalls)
            {
                var arguments = new List<CallArgumentMemoryEffectSummary>(call.Arguments.Count);
                foreach (var argument in call.Arguments)
                {
                    if (!TryParseParameterCaptureKind(argument.CaptureKind, out var captureKind))
                    {
                        return false;
                    }

                    arguments.Add(new CallArgumentMemoryEffectSummary(
                        argument.ArgumentIndex,
                        argument.CallerParameterName,
                        argument.CalleeParameterName,
                        argument.Reads,
                        argument.Writes,
                        captureKind));
                }

                calls.Add(new CallMemoryEffectSummary(
                    call.CalleeName,
                    new FunctionMemoryEffectSummary(
                        call.MemoryEffects.ReadsArgumentMemory,
                        call.MemoryEffects.WritesArgumentMemory,
                        call.MemoryEffects.CapturesArgumentMemory,
                        call.MemoryEffects.ReadsOtherMemory,
                        call.MemoryEffects.WritesOtherMemory),
                    arguments));
            }
        }

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
                functionSemantic.Optimization.IsSingleReturnDereferenceWrapper,
                functionSemantic.Optimization.IsSingleReturnBinaryOperatorWrapper,
                functionSemantic.Optimization.IsSingleReturnComparisonWrapper,
                functionSemantic.Optimization.IsSingleReturnAggregateConstructionWrapper,
                functionSemantic.Optimization.IsSimpleLocalUpdateWrapper,
                functionSemantic.Optimization.IsTerminalSelectionWrapper);

        if (!TryBuildFunctionOwnershipSummary(functionSemantic, out var ownershipSummary))
        {
            return false;
        }

        summary = new ImportedFunctionSemanticSummary(
            functionSemantic.QualifiedResolvedName,
            declaredKind,
            effectiveKind,
            functionSemantic.CalledFunctions,
            memoryEffects,
            parameters,
            calls,
            optimizationSummary,
            ownershipSummary);
        return true;
    }

    private static bool TryBuildFunctionOwnershipSummary(
        StarkPackageFunctionSemanticManifest functionSemantic,
        out FunctionOwnershipSummary? summary)
    {
        summary = null;
        if (functionSemantic.Ownership is not { } ownership)
        {
            return true;
        }

        var events = new List<OwnershipEventSummary>();
        foreach (var ev in ownership.Events ?? [])
        {
            if (!TryParseOwnershipEventKind(ev.Kind, out var eventKind))
            {
                return false;
            }

            events.Add(new OwnershipEventSummary(
                eventKind,
                BuildOwnershipPlaceSummary(ev.Place),
                ev.Location is null
                    ? null
                    : new SourceLocation(ev.Location.FilePath, ev.Location.Line, ev.Location.Column)));
        }

        var roots = new List<OwnershipRootSummary>();
        foreach (var root in ownership.Roots ?? [])
        {
            if (!TryParseOwnershipRootKind(root.RootKind, out var rootKind)
                || !TryParseOwnershipAvailability(root.FinalAvailability, out var availability))
            {
                return false;
            }

            roots.Add(new OwnershipRootSummary(
                root.Name,
                BuildTypeSymbol(root.Type),
                rootKind,
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
                availability));
        }

        summary = new FunctionOwnershipSummary(
            functionSemantic.QualifiedResolvedName,
            ownership.OwnershipValid,
            ownership.ImplicitDrops.ToArray(),
            ownership.Moves.ToArray(),
            events,
            roots);
        return true;
    }

    private static OwnershipPlaceSummary BuildOwnershipPlaceSummary(StarkPackageOwnershipPlaceManifest place)
    {
        return new OwnershipPlaceSummary(
            place.RootName,
            BuildTypeSymbol(place.Type),
            place.ProjectionPath?.ToArray(),
            place.HasIndexProjection);
    }

    private static bool TryParseOwnershipEventKind(string kind, out OwnershipEventKind parsed)
    {
        parsed = kind switch
        {
            "move" => OwnershipEventKind.Move,
            "field-move" => OwnershipEventKind.FieldMove,
            "implicit-drop" => OwnershipEventKind.ImplicitDrop,
            "assignment-drop" => OwnershipEventKind.AssignmentDrop,
            "reinitialize" => OwnershipEventKind.Reinitialize,
            "address-taken" => OwnershipEventKind.AddressTaken,
            _ => default
        };
        return kind is "move" or "field-move" or "implicit-drop" or "assignment-drop" or "reinitialize" or "address-taken";
    }

    private static bool TryParseOwnershipRootKind(string kind, out OwnershipStorageRootKind parsed)
    {
        parsed = kind switch
        {
            "local" => OwnershipStorageRootKind.Local,
            "parameter" => OwnershipStorageRootKind.Parameter,
            "global" => OwnershipStorageRootKind.Global,
            _ => default
        };
        return kind is "local" or "parameter" or "global";
    }

    private static bool TryParseOwnershipAvailability(string availability, out OwnershipRootAvailabilityKind parsed)
    {
        parsed = availability switch
        {
            "initialized" => OwnershipRootAvailabilityKind.Initialized,
            "uninitialized" => OwnershipRootAvailabilityKind.Uninitialized,
            "partially-initialized" => OwnershipRootAvailabilityKind.PartiallyInitialized,
            "moved" => OwnershipRootAvailabilityKind.Moved,
            "control-flow" => OwnershipRootAvailabilityKind.ControlFlow,
            "unknown" => OwnershipRootAvailabilityKind.Unknown,
            _ => default
        };
        return availability is "initialized" or "uninitialized" or "partially-initialized" or "moved" or "control-flow" or "unknown";
    }
}
