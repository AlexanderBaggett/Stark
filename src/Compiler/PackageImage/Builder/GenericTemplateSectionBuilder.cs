using Antlr4.Runtime;
using System.Text;
using Stark.Parsing;

namespace Stark.Compiler;

internal static partial class PackageImageBuilder
{
    private sealed record PublishedTypedTemplateObjectCreationLookup(
        int Ordinal,
        ObjectCreationTypingRecord? Record);

    private sealed record PublishedTemplateEnumValueReference(
        ParserRuleContext Context,
        StarkTypeSymbol EnumType,
        string VariantName);

    private sealed record PublishedTemplateEnumCallReference(
        StarkParser.ArgumentListContext Context,
        StarkTypeSymbol EnumType,
        string VariantName);

    private readonly record struct PublishedBoundOperationOrdinalKey(
        BoundOperationKind Kind,
        int Line,
        int Column);

    private sealed record GenericFunctionTemplateCandidate(
        DeclaredFunctionSyntax Function,
        string QualifiedResolvedName,
        string LookupName,
        TypedFunctionSignature Signature);

    private static readonly IReadOnlyDictionary<string, StarkTypeSymbol> EmptyTypeSubstitution =
        new Dictionary<string, StarkTypeSymbol>(StringComparer.Ordinal);

    private static IReadOnlyList<StarkPackageFunctionTemplateManifest> BuildGenericFunctionTemplates(
        LoadedModuleDocument module,
        TypeCheckModel typeModel,
        SemanticValidationModel? validationModel,
        OwnershipValidationModel? ownershipModel)
    {
        var literalsByLocation = typeModel.Literals
            .Where(record => string.Equals(record.Location.FilePath, module.Reference.FilePath, StringComparison.Ordinal))
            .GroupBy(static record => BuildTemplateLiteralLookupKey(record.Location.Line, record.Location.Column))
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.Ordinal);
        var deferredTriggersByFunction = typeModel.DeferredInstantiationTriggers
            .GroupBy(static trigger => trigger.EnclosingFunctionName, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<DeferredFunctionInstantiationTriggerRecord>)group.ToArray(),
                StringComparer.Ordinal);
        var deferredTypeTriggersByFunction = typeModel.DeferredTypeTriggers
            .GroupBy(static trigger => trigger.EnclosingFunctionName, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<DeferredTypeInstantiationTriggerRecord>)group.ToArray(),
                StringComparer.Ordinal);
        var objectCreationsByFunction = typeModel.ObjectCreations
            .Where(static record => record.EnclosingFunctionName is not null)
            .GroupBy(static record => record.EnclosingFunctionName!, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<ObjectCreationTypingRecord>)group.ToArray(),
                StringComparer.Ordinal);
        var enumConstructorsByFunction = typeModel.EnumConstructors
            .Where(static record => record.EnclosingFunctionName is not null)
            .GroupBy(static record => record.EnclosingFunctionName!, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<EnumConstructorTypingRecord>)group.ToArray(),
                StringComparer.Ordinal);
        var enumCallsByFunction = typeModel.EnumCalls
            .Where(static record => record.EnclosingFunctionName is not null)
            .GroupBy(static record => record.EnclosingFunctionName!, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<EnumCallTypingRecord>)group.ToArray(),
                StringComparer.Ordinal);
        var enumValuesByFunction = typeModel.EnumValues
            .Where(static record => record.EnclosingFunctionName is not null)
            .GroupBy(static record => record.EnclosingFunctionName!, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<EnumValueTypingRecord>)group.ToArray(),
                StringComparer.Ordinal);
        var enumPatternsByFunction = typeModel.EnumPatterns
            .Where(static record => record.EnclosingFunctionName is not null)
            .GroupBy(static record => record.EnclosingFunctionName!, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<EnumPatternTypingRecord>)group.ToArray(),
                StringComparer.Ordinal);
        var aggregatePatternsByFunction = typeModel.AggregatePatterns
            .Where(static record => record.EnclosingFunctionName is not null)
            .GroupBy(static record => record.EnclosingFunctionName!, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<AggregatePatternTypingRecord>)group.ToArray(),
                StringComparer.Ordinal);
        var localDeclarationsByFunction = typeModel.LocalDeclarations
            .Where(static record => record.EnclosingFunctionName is not null)
            .GroupBy(static record => record.EnclosingFunctionName!, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<LocalDeclarationTypingRecord>)group.ToArray(),
                StringComparer.Ordinal);
        var localStorageCapacitiesByFunction = typeModel.LocalStorageCapacities
            .Where(static record => record.EnclosingFunctionName is not null)
            .GroupBy(static record => record.EnclosingFunctionName!, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<LocalStorageCapacityTypingRecord>)group.ToArray(),
                StringComparer.Ordinal);
        var conversionsByFunction = typeModel.Conversions
            .Where(static record => record.EnclosingFunctionName is not null)
            .GroupBy(static record => record.EnclosingFunctionName!, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<ConversionTypingRecord>)group.ToArray(),
                StringComparer.Ordinal);
        var directCallsByFunction = typeModel.DirectCalls
            .Where(static record => record.EnclosingFunctionName is not null)
            .GroupBy(static record => record.EnclosingFunctionName!, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<DirectCallTypingRecord>)group.ToArray(),
                StringComparer.Ordinal);
        var fieldAccessesByFunction = typeModel.FieldAccesses
            .Where(static record => record.EnclosingFunctionName is not null)
            .GroupBy(static record => record.EnclosingFunctionName!, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<FieldAccessTypingRecord>)group.ToArray(),
                StringComparer.Ordinal);
        var memberCallsByFunction = typeModel.MemberCalls
            .Where(static record => record.EnclosingFunctionName is not null)
            .GroupBy(static record => record.EnclosingFunctionName!, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<MemberCallTypingRecord>)group.ToArray(),
                StringComparer.Ordinal);
        var functionPointerPromotionsByFunction = typeModel.FunctionPointerPromotions
            .Where(static record => record.EnclosingFunctionName is not null)
            .GroupBy(static record => record.EnclosingFunctionName!, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<FunctionPointerPromotionTypingRecord>)group.ToArray(),
                StringComparer.Ordinal);
        var boundOperationsByFunction = typeModel.BoundOperations
            .Where(static operation => operation.EnclosingFunctionName is not null)
            .GroupBy(static operation => operation.EnclosingFunctionName!, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<BoundOperation>)group.ToArray(),
                StringComparer.Ordinal);

        var candidates = DeclaredFunctionSyntaxCollector.Collect(module.ParseResult, module.SyntaxModel)
            .Where(static function => function.HasBody)
            .Select(function =>
            {
                var qualifiedResolvedName = $"{module.SyntaxModel.ModuleName}.{function.Name}";
                var lookupName = LookupName(module.SyntaxModel.ModuleName, module.Reference.IsRoot, function.Name);
                if (!typeModel.Functions.TryGetValue(lookupName, out var functionSignature)
                    || !functionSignature.IsGeneric)
                {
                    return null;
                }

                return new GenericFunctionTemplateCandidate(
                    function,
                    qualifiedResolvedName,
                    lookupName,
                    functionSignature);
            })
            .Where(static candidate => candidate is not null)
            .Cast<GenericFunctionTemplateCandidate>()
            .ToArray();
        var publishedLookupNames = CollectPublishedGenericTemplateLookupNames(
            candidates,
            deferredTriggersByFunction,
            boundOperationsByFunction);

        return candidates
            .Where(candidate => publishedLookupNames.Contains(candidate.LookupName))
            .Select(candidate =>
            {
                var function = candidate.Function;
                var qualifiedResolvedName = candidate.QualifiedResolvedName;
                var lookupName = candidate.LookupName;
                var functionSignature = candidate.Signature;

                deferredTriggersByFunction.TryGetValue(lookupName, out var deferredTriggers);
                deferredTypeTriggersByFunction.TryGetValue(lookupName, out var deferredTypeTriggers);
                objectCreationsByFunction.TryGetValue(lookupName, out var objectCreations);
                enumConstructorsByFunction.TryGetValue(lookupName, out var enumConstructors);
                enumCallsByFunction.TryGetValue(lookupName, out var enumCalls);
                enumValuesByFunction.TryGetValue(lookupName, out var enumValues);
                enumPatternsByFunction.TryGetValue(lookupName, out var enumPatterns);
                aggregatePatternsByFunction.TryGetValue(lookupName, out var aggregatePatterns);
                localDeclarationsByFunction.TryGetValue(lookupName, out var localDeclarations);
                localStorageCapacitiesByFunction.TryGetValue(lookupName, out var localStorageCapacities);
                conversionsByFunction.TryGetValue(lookupName, out var conversions);
                directCallsByFunction.TryGetValue(lookupName, out var directCalls);
                fieldAccessesByFunction.TryGetValue(lookupName, out var fieldAccesses);
                memberCallsByFunction.TryGetValue(lookupName, out var memberCalls);
                functionPointerPromotionsByFunction.TryGetValue(lookupName, out var functionPointerPromotions);
                boundOperationsByFunction.TryGetValue(lookupName, out var boundOperations);
                var effectiveEnumValues = MergeTemplateEnumValues(enumValues, boundOperations);

                var typedBody = BuildPublishedTypedTemplateBody(
                    module,
                    functionSignature.ReturnType,
                    function.Body,
                    typeModel.NamedTypes,
                    literalsByLocation,
                    objectCreations,
                    enumConstructors,
                    enumCalls,
                    effectiveEnumValues,
                    enumPatterns,
                    aggregatePatterns,
                    localDeclarations,
                    localStorageCapacities,
                    conversions,
                    directCalls,
                    memberCalls,
                    functionPointerPromotions,
                    boundOperations);

                return new StarkPackageFunctionTemplateManifest(
                    QualifiedResolvedName: qualifiedResolvedName,
                    QualifiedName: $"{module.SyntaxModel.ModuleName}.{function.DisplaySourceName}",
                    OverloadKey: FunctionOverloadFacts.BuildOverloadKey(function.ParameterList),
                    BodyText: typedBody is null
                        ? GetContextSourceText(module.ParseResult, function.Body)
                        : null,
                    TopLevelStatementCount: function.Body.block()?.statement().Length,
                    EstimatedBodyCost: GenericTemplateBodyComplexityEstimator.Estimate(function.Body),
                    Semantics: validationModel is not null
                        && TryBuildPublishedFunctionSemanticManifest(
                            module,
                            lookupName,
                            qualifiedResolvedName,
                            validationModel,
                            ownershipModel,
                            out var semanticManifest)
                            ? semanticManifest
                            : null,
                    TypedBody: typedBody,
                    DeferredFunctionInstantiations: deferredTriggers is { Count: > 0 }
                        ? deferredTriggers
                            .Where(static trigger => trigger.Signature.TemplateName is not null && trigger.Signature.TypeArguments is { Count: > 0 })
                            .Select(trigger => new StarkPackageDeferredFunctionInstantiationManifest(
                                QualifyPublishedCalledFunctionName(module, trigger.Signature.TemplateName!),
                                trigger.Signature.TypeArguments!
                                    .Select(typeArgument => BuildPublishedAbiTypeReference(typeArgument, module))
                                    .ToArray()))
                            .ToArray()
                        : null,
                    DeferredTypeInstantiations: deferredTypeTriggers is { Count: > 0 }
                        ? deferredTypeTriggers
                            .Select(trigger => new StarkPackageDeferredTypeInstantiationManifest(
                                BuildPublishedAbiTypeReference(trigger.Type, module)))
                            .ToArray()
                        : null,
                    ObjectCreations: BuildPublishedTemplateObjectCreations(module, function.Body, objectCreations),
                    EnumConstructors: BuildPublishedTemplateEnumConstructors(module, function.Body, enumConstructors),
                    EnumCalls: BuildPublishedTemplateEnumCalls(module, function.Body, typeModel.NamedTypes, enumCalls),
                    EnumValues: BuildPublishedTemplateEnumValues(module, function.Body, typeModel.NamedTypes, effectiveEnumValues),
                    EnumPatterns: BuildPublishedTemplateEnumPatterns(module, function.Body, enumPatterns),
                    AggregatePatterns: BuildPublishedTemplateAggregatePatterns(module, function.Body, aggregatePatterns),
                    LocalDeclarations: BuildPublishedTemplateLocalDeclarations(module, localDeclarations),
                    Conversions: BuildPublishedTemplateConversions(module, function.Body, conversions),
                    DirectCalls: BuildPublishedTemplateDirectCalls(module, function.Body, directCalls),
                    FieldAccesses: BuildPublishedTemplateFieldAccesses(module, function.Body, fieldAccesses),
                    MemberCalls: BuildPublishedTemplateMemberCalls(module, function.Body, memberCalls),
                    FunctionAddresses: BuildPublishedTemplateFunctionAddresses(module, functionPointerPromotions),
                    BoundOperations: BuildPublishedTemplateBoundOperations(module, function.Body, boundOperations),
                    BackendOptimizationMode: RenderBackendOptimizationMode(functionSignature.BackendOptimizationMode));
            })
            .Where(static template => template is not null)
            .Cast<StarkPackageFunctionTemplateManifest>()
            .OrderBy(static template => template.QualifiedResolvedName, StringComparer.Ordinal)
            .ThenBy(static template => template.OverloadKey, StringComparer.Ordinal)
            .ToArray();
    }

    private static HashSet<string> CollectPublishedGenericTemplateLookupNames(
        IReadOnlyList<GenericFunctionTemplateCandidate> candidates,
        IReadOnlyDictionary<string, IReadOnlyList<DeferredFunctionInstantiationTriggerRecord>> deferredTriggersByFunction,
        IReadOnlyDictionary<string, IReadOnlyList<BoundOperation>> boundOperationsByFunction)
    {
        var candidatesByLookupName = candidates.ToDictionary(
            static candidate => candidate.LookupName,
            StringComparer.Ordinal);
        var lookupNameByQualifiedName = candidates.ToDictionary(
            static candidate => candidate.QualifiedResolvedName,
            static candidate => candidate.LookupName,
            StringComparer.Ordinal);
        var published = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>();

        foreach (var candidate in candidates)
        {
            if (GenericTemplatePublicationPolicy.HasPublishedApiVisibility(candidate.Function.Visibility))
            {
                Enqueue(candidate.LookupName);
            }
        }

        while (pending.Count != 0)
        {
            var lookupName = pending.Dequeue();

            if (deferredTriggersByFunction.TryGetValue(lookupName, out var deferredTriggers))
            {
                foreach (var trigger in deferredTriggers)
                {
                    EnqueueTemplate(trigger.Signature.TemplateName);
                }
            }

            if (boundOperationsByFunction.TryGetValue(lookupName, out var boundOperations))
            {
                foreach (var operation in boundOperations)
                {
                    switch (operation)
                    {
                        case BoundDirectCallOperation directCall:
                            EnqueueTemplate(directCall.Signature.TemplateName);
                            break;
                        case BoundMemberCallOperation memberCall:
                            EnqueueTemplate(memberCall.Signature.TemplateName);
                            break;
                    }
                }
            }
        }

        return published;

        void EnqueueTemplate(string? templateName)
        {
            if (templateName is null)
            {
                return;
            }

            if (candidatesByLookupName.ContainsKey(templateName))
            {
                Enqueue(templateName);
                return;
            }

            if (lookupNameByQualifiedName.TryGetValue(templateName, out var lookupName))
            {
                Enqueue(lookupName);
            }
        }

        void Enqueue(string lookupName)
        {
            if (published.Add(lookupName))
            {
                pending.Enqueue(lookupName);
            }
        }
    }

    private static IReadOnlyList<EnumValueTypingRecord>? MergeTemplateEnumValues(
        IReadOnlyList<EnumValueTypingRecord>? enumValues,
        IReadOnlyList<BoundOperation>? boundOperations)
    {
        var boundEnumValues = (boundOperations ?? [])
            .OfType<BoundEnumValueOperation>()
            .Select(static operation => new EnumValueTypingRecord(
                operation.EnumType,
                operation.VariantName,
                operation.Location,
                operation.EnclosingFunctionName))
            .ToArray();
        if (boundEnumValues.Length == 0)
        {
            return enumValues;
        }

        var merged = new Dictionary<string, EnumValueTypingRecord>(StringComparer.Ordinal);
        foreach (var enumValue in enumValues ?? [])
        {
            merged[TemplateDirectCallFacts.BuildLookupKey(enumValue.Location)] = enumValue;
        }

        foreach (var enumValue in boundEnumValues)
        {
            merged[TemplateDirectCallFacts.BuildLookupKey(enumValue.Location)] = enumValue;
        }

        return merged.Count == 0 ? null : merged.Values.ToArray();
    }

    private static StarkPackageTypedTemplateBodyManifest? BuildPublishedTypedTemplateBody(
        LoadedModuleDocument module,
        StarkTypeSymbol returnType,
        ParserRuleContext functionBody,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, LiteralTypingRecord> literalsByLocation,
        IReadOnlyList<ObjectCreationTypingRecord>? objectCreations,
        IReadOnlyList<EnumConstructorTypingRecord>? enumConstructors,
        IReadOnlyList<EnumCallTypingRecord>? enumCalls,
        IReadOnlyList<EnumValueTypingRecord>? enumValues,
        IReadOnlyList<EnumPatternTypingRecord>? enumPatterns,
        IReadOnlyList<AggregatePatternTypingRecord>? aggregatePatterns,
        IReadOnlyList<LocalDeclarationTypingRecord>? localDeclarations,
        IReadOnlyList<LocalStorageCapacityTypingRecord>? localStorageCapacities,
        IReadOnlyList<ConversionTypingRecord>? conversions,
        IReadOnlyList<DirectCallTypingRecord>? directCalls,
        IReadOnlyList<MemberCallTypingRecord>? memberCalls,
        IReadOnlyList<FunctionPointerPromotionTypingRecord>? functionPointerPromotions,
        IReadOnlyList<BoundOperation>? boundOperations)
    {
        var block = functionBody switch
        {
            StarkParser.FunctionBodyContext functionBodyContext => functionBodyContext.block(),
            StarkParser.BlockContext directBlock => directBlock,
            _ => null
        };
        if (block is null)
        {
            return null;
        }

        var statements = block.statement();
        if (statements.Length == 0)
        {
            return returnType.Kind == StarkTypeKind.Void
                ? new StarkPackageTypedTemplateBodyManifest([])
                : null;
        }

        var localDeclarationsByLocation = (localDeclarations ?? [])
            .GroupBy(static record => TemplateLocalDeclarationFacts.BuildLookupKey(record.Kind, record.Location))
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.Ordinal);
        var localStorageCapacitiesByLocation = (localStorageCapacities ?? [])
            .GroupBy(static record => TemplateDirectCallFacts.BuildLookupKey(record.Location))
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.Ordinal);
        var conversionsByLocation = (conversions ?? [])
            .GroupBy(static record => BuildTemplateConversionLookupKey(record.Location.Line, record.Location.Column))
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.Ordinal);
        var objectCreationsByKey = (objectCreations ?? [])
            .GroupBy(static record => BuildTemplateObjectCreationLookupKey(record.ExpressionText, record.Location.Line, record.Location.Column))
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.Ordinal);
        var objectCreationOrdinals = CollectTrackedTemplateObjectCreations(functionBody)
            .Select((objectCreation, ordinal) =>
            {
                objectCreationsByKey.TryGetValue(
                    BuildTemplateObjectCreationLookupKey(
                        objectCreation.GetText(),
                        objectCreation.Start.Line,
                        objectCreation.Start.Column + 1),
                    out var record);

                return (
                    objectCreation,
                    lookup: new PublishedTypedTemplateObjectCreationLookup(
                        ordinal,
                        record));
            })
            .ToDictionary(static item => item.objectCreation, static item => item.lookup);
        var enumConstructorsByLocation = (enumConstructors ?? [])
            .GroupBy(static record => BuildTemplateEnumConstructorLookupKey(record.Location.Line, record.Location.Column))
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.Ordinal);
        var enumConstructorOrdinals = CollectTemplateEnumConstructorExpressions(functionBody)
            .Select((enumConstructor, ordinal) => (enumConstructor, ordinal))
            .Where(item => enumConstructorsByLocation.ContainsKey(
                BuildTemplateEnumConstructorLookupKey(item.enumConstructor.Start.Line, item.enumConstructor.Start.Column + 1)))
            .ToDictionary(static item => item.enumConstructor, static item => item.ordinal);
        var enumCallsByLocation = (enumCalls ?? [])
            .GroupBy(static record => TemplateDirectCallFacts.BuildLookupKey(record.Location))
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.Ordinal);
        var syntacticEnumCallReferences = CollectPublishedTemplateEnumCallReferences(module, functionBody, namedTypes);
        var enumCallOrdinals = syntacticEnumCallReferences.Count > 0
            ? syntacticEnumCallReferences
                .Select((reference, ordinal) => (reference.Context, ordinal))
                .ToDictionary(static item => item.Context, static item => item.ordinal)
            : CollectTemplateDirectCallArgumentLists(functionBody)
                .Select((argumentList, ordinal) => (argumentList, ordinal))
                .Where(item => enumCallsByLocation.ContainsKey(
                    TemplateDirectCallFacts.BuildLookupKey(item.argumentList.Start.Line, item.argumentList.Start.Column + 1)))
                .ToDictionary(static item => item.argumentList, static item => item.ordinal);
        var enumValuesByLocation = (enumValues ?? [])
            .GroupBy(static record => TemplateDirectCallFacts.BuildLookupKey(record.Location))
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.Ordinal);
        var syntacticEnumValueReferences = CollectPublishedTemplateEnumValueReferences(module, functionBody, namedTypes);
        var enumValueOrdinals = syntacticEnumValueReferences.Count > 0
            ? syntacticEnumValueReferences
                .Select((reference, ordinal) => (reference.Context, ordinal))
                .ToDictionary(static item => item.Context, static item => item.ordinal)
            : CollectTemplateEnumValueContexts(functionBody)
                .Select((context, ordinal) => (context, ordinal))
                .Where(item => enumValuesByLocation.ContainsKey(BuildTemplateEnumValueLookupKey(item.context)))
                .ToDictionary(static item => item.context, static item => item.ordinal);
        var functionAddressPromotionsByLocation = BuildFunctionAddressPromotionsByLocation(functionPointerPromotions);
        var functionAddressOrdinals = CollectTemplateFunctionAddressPrimaryExpressions(functionBody)
            .Select(primaryExpression => (
                primaryExpression,
                key: TemplateDirectCallFacts.BuildLookupKey(primaryExpression.Start.Line, primaryExpression.Start.Column + 1)))
            .Where(item => functionAddressPromotionsByLocation.ContainsKey(item.key))
            .ToDictionary(
                static item => (ParserRuleContext)item.primaryExpression,
                item => functionAddressPromotionsByLocation[item.key]);
        var templatePatternContexts = CollectTemplateEnumPatternContexts(functionBody)
            .Select((patternContext, ordinal) => (patternContext, ordinal))
            .ToArray();
        var enumPatternsByLocation = (enumPatterns ?? [])
            .GroupBy(static record => TemplateDirectCallFacts.BuildLookupKey(record.Location))
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.Ordinal);
        var enumPatternOrdinals = templatePatternContexts
            .Where(item => enumPatternsByLocation.ContainsKey(
                TemplateDirectCallFacts.BuildLookupKey(item.patternContext.Start.Line, item.patternContext.Start.Column + 1)))
            .ToDictionary(static item => item.patternContext, static item => item.ordinal);
        var aggregatePatternsByLocation = (aggregatePatterns ?? [])
            .GroupBy(static record => TemplateDirectCallFacts.BuildLookupKey(record.Location))
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.Ordinal);
        var aggregatePatternOrdinals = templatePatternContexts
            .Where(item => item.patternContext is StarkParser.AggregatePatternContext
                && aggregatePatternsByLocation.ContainsKey(
                    TemplateDirectCallFacts.BuildLookupKey(item.patternContext.Start.Line, item.patternContext.Start.Column + 1)))
            .ToDictionary(static item => item.patternContext, static item => item.ordinal);
        var directCallsByLocation = (directCalls ?? [])
            .GroupBy(static record => TemplateDirectCallFacts.BuildLookupKey(record.Location))
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.Ordinal);
        var directCallOrdinals = CollectTemplateArgumentListsByPublishedLocations(functionBody, directCallsByLocation.Keys)
            .Select((argumentList, ordinal) => (argumentList, ordinal))
            .ToDictionary(static item => item.argumentList, static item => item.ordinal);
        var dynamicStorageOperationLocations = (boundOperations ?? [])
            .Where(static operation => operation.Kind == BoundOperationKind.DynamicStorageOperation)
            .Select(static operation => TemplateDirectCallFacts.BuildLookupKey(operation.Location))
            .ToHashSet(StringComparer.Ordinal);
        var dynamicStorageOperationOrdinals = CollectTemplateArgumentListsByPublishedLocations(
                functionBody,
                dynamicStorageOperationLocations)
            .Select((argumentList, ordinal) => (argumentList, ordinal))
            .ToDictionary(static item => item.argumentList, static item => item.ordinal);
        var memberCallOrdinals = CollectTemplateMemberCallArgumentLists(functionBody)
            .Select((argumentList, ordinal) => (argumentList, ordinal))
            .ToDictionary(static item => item.argumentList, static item => item.ordinal);
        var fieldAccessOrdinals = CollectTemplateMemberAccessParts(functionBody)
            .Select((postfixPart, ordinal) => (postfixPart, ordinal))
            .ToDictionary(static item => item.postfixPart, static item => item.ordinal);
        if (!TryBuildPublishedTypedTemplateStatementList(
                module,
                statements,
                namedTypes,
                literalsByLocation,
                localDeclarationsByLocation,
                conversionsByLocation,
                objectCreationOrdinals,
                enumConstructorOrdinals,
                enumCallOrdinals,
                enumValueOrdinals,
                enumPatternOrdinals,
                aggregatePatternOrdinals,
                localStorageCapacitiesByLocation,
                functionAddressOrdinals,
                directCallOrdinals,
                dynamicStorageOperationOrdinals,
                memberCallOrdinals,
                fieldAccessOrdinals,
                out var publishedStatements))
        {
            return null;
        }

        var lastStatement = publishedStatements[^1];
        var lastStatementKind = lastStatement.Kind;
        if (!CanUseTypedTemplateStatementAsTerminal(lastStatement, returnType)
            && !(returnType.Kind == StarkTypeKind.Void
                 && CanUsePublishedTypedTemplateImplicitVoidReturnStatement(lastStatement)))
        {
            return null;
        }

        for (var index = 0; index < publishedStatements.Count - 1; index++)
        {
            if (!string.Equals(publishedStatements[index].Kind, "local-variable", StringComparison.Ordinal)
                && !string.Equals(publishedStatements[index].Kind, "block", StringComparison.Ordinal)
                && !string.Equals(publishedStatements[index].Kind, "empty", StringComparison.Ordinal)
                && !string.Equals(publishedStatements[index].Kind, "expression", StringComparison.Ordinal)
                && !string.Equals(publishedStatements[index].Kind, "assignment", StringComparison.Ordinal)
                && !string.Equals(publishedStatements[index].Kind, "switch", StringComparison.Ordinal)
                && !string.Equals(publishedStatements[index].Kind, "for", StringComparison.Ordinal)
                && !string.Equals(publishedStatements[index].Kind, "while", StringComparison.Ordinal)
                && !string.Equals(publishedStatements[index].Kind, "if", StringComparison.Ordinal))
            {
                return null;
            }
        }

        return new StarkPackageTypedTemplateBodyManifest(publishedStatements);
    }

    private static bool CanUsePublishedTypedTemplateImplicitVoidReturnStatement(
        StarkPackageTypedTemplateStatementManifest statement)
    {
        return string.Equals(statement.Kind, "local-variable", StringComparison.Ordinal)
            || string.Equals(statement.Kind, "block", StringComparison.Ordinal)
            || string.Equals(statement.Kind, "empty", StringComparison.Ordinal)
            || string.Equals(statement.Kind, "expression", StringComparison.Ordinal)
            || string.Equals(statement.Kind, "assignment", StringComparison.Ordinal)
            || string.Equals(statement.Kind, "switch", StringComparison.Ordinal)
            || string.Equals(statement.Kind, "for", StringComparison.Ordinal)
            || string.Equals(statement.Kind, "while", StringComparison.Ordinal)
            || string.Equals(statement.Kind, "if", StringComparison.Ordinal);
    }

    private static bool TryBuildPublishedTypedTemplateStatementList(
        LoadedModuleDocument module,
        IReadOnlyList<StarkParser.StatementContext> statements,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, LiteralTypingRecord> literalsByLocation,
        IReadOnlyDictionary<string, LocalDeclarationTypingRecord> localDeclarationsByLocation,
        IReadOnlyDictionary<string, ConversionTypingRecord> conversionsByLocation,
        IReadOnlyDictionary<StarkParser.ObjectCreationExpressionContext, PublishedTypedTemplateObjectCreationLookup> objectCreationOrdinals,
        IReadOnlyDictionary<StarkParser.EnumConstructorExpressionContext, int> enumConstructorOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> enumCallOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> enumValueOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> enumPatternOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> aggregatePatternOrdinals,
        IReadOnlyDictionary<string, LocalStorageCapacityTypingRecord> localStorageCapacitiesByLocation,
        IReadOnlyDictionary<ParserRuleContext, int> functionAddressOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> directCallOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> dynamicStorageOperationOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> memberCallOrdinals,
        IReadOnlyDictionary<StarkParser.PostfixPartContext, int> fieldAccessOrdinals,
        out IReadOnlyList<StarkPackageTypedTemplateStatementManifest> publishedStatements)
    {
        var builtStatements = new List<StarkPackageTypedTemplateStatementManifest>(statements.Count);
        foreach (var statement in statements)
        {
            if (!TryBuildPublishedTypedTemplateStatements(
                    module,
                    statement,
                    namedTypes,
                    literalsByLocation,
                    localDeclarationsByLocation,
                    conversionsByLocation,
                    objectCreationOrdinals,
                    enumConstructorOrdinals,
                    enumCallOrdinals,
                    enumValueOrdinals,
                    enumPatternOrdinals,
                    aggregatePatternOrdinals,
                    localStorageCapacitiesByLocation,
                    functionAddressOrdinals,
                    directCallOrdinals,
                    dynamicStorageOperationOrdinals,
                    memberCallOrdinals,
                    fieldAccessOrdinals,
                    out var publishedStatementGroup))
            {
                publishedStatements = [];
                return false;
            }

            builtStatements.AddRange(publishedStatementGroup);
        }

        publishedStatements = builtStatements;
        return true;
    }

    private static bool TryBuildPublishedTypedTemplateStatements(
        LoadedModuleDocument module,
        StarkParser.StatementContext statement,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, LiteralTypingRecord> literalsByLocation,
        IReadOnlyDictionary<string, LocalDeclarationTypingRecord> localDeclarationsByLocation,
        IReadOnlyDictionary<string, ConversionTypingRecord> conversionsByLocation,
        IReadOnlyDictionary<StarkParser.ObjectCreationExpressionContext, PublishedTypedTemplateObjectCreationLookup> objectCreationOrdinals,
        IReadOnlyDictionary<StarkParser.EnumConstructorExpressionContext, int> enumConstructorOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> enumCallOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> enumValueOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> enumPatternOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> aggregatePatternOrdinals,
        IReadOnlyDictionary<string, LocalStorageCapacityTypingRecord> localStorageCapacitiesByLocation,
        IReadOnlyDictionary<ParserRuleContext, int> functionAddressOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> directCallOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> dynamicStorageOperationOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> memberCallOrdinals,
        IReadOnlyDictionary<StarkParser.PostfixPartContext, int> fieldAccessOrdinals,
        out IReadOnlyList<StarkPackageTypedTemplateStatementManifest> publishedStatements)
    {
        if (statement.block() is { } block)
        {
            if (!TryBuildPublishedTypedTemplateStatementList(
                    module,
                    block.statement(),
                    namedTypes,
                    literalsByLocation,
                    localDeclarationsByLocation,
                    conversionsByLocation,
                    objectCreationOrdinals,
                    enumConstructorOrdinals,
                    enumCallOrdinals,
                    enumValueOrdinals,
                    enumPatternOrdinals,
                    aggregatePatternOrdinals,
                    localStorageCapacitiesByLocation,
                    functionAddressOrdinals,
                    directCallOrdinals,
                    dynamicStorageOperationOrdinals,
                    memberCallOrdinals,
                    fieldAccessOrdinals,
                    out var blockStatements))
            {
                publishedStatements = [];
                return false;
            }

            publishedStatements =
            [
                new StarkPackageTypedTemplateStatementManifest(
                    Kind: "block",
                    BodyStatements: blockStatements)
            ];
            return true;
        }

        if (statement.emptyStatement() is not null)
        {
            publishedStatements =
            [
                new StarkPackageTypedTemplateStatementManifest(Kind: "empty")
            ];
            return true;
        }

        if (statement.localVariableDeclaration() is { } localVariable)
        {
            if (!localDeclarationsByLocation.TryGetValue(
                    TemplateLocalDeclarationFacts.BuildLookupKey(
                        TemplateLocalDeclarationFacts.VariableKind,
                        localVariable.Start.Line,
                        localVariable.Start.Column + 1),
                    out var localDeclaration))
            {
                publishedStatements = [];
                return false;
            }

            var builtStatements = new List<StarkPackageTypedTemplateStatementManifest>(
                localVariable.variableDeclarators().variableDeclarator().Length);
            foreach (var declarator in localVariable.variableDeclarators().variableDeclarator())
            {
                var declaratorName = declarator.Identifier().GetText();
                var constProvenance = GetPublishedDeclaratorConstProvenance(localDeclaration, declaratorName);
                var storageCapacity = TryGetPublishedStorageCapacity(
                        declarator,
                        localStorageCapacitiesByLocation,
                        out var parsedStorageCapacity)
                    ? parsedStorageCapacity
                    : (int?)null;
                StarkPackageTypedTemplateExpressionManifest? initializer = null;
                if (declarator.variableInitializer() is { } variableInitializer
                    && !TryBuildPublishedTypedTemplateVariableInitializer(
                        module,
                        variableInitializer,
                        localDeclaration.Type,
                        namedTypes,
                        literalsByLocation,
                        conversionsByLocation,
                        objectCreationOrdinals,
                        enumConstructorOrdinals,
                        enumCallOrdinals,
                        enumValueOrdinals,
                        functionAddressOrdinals,
                        directCallOrdinals,
                        dynamicStorageOperationOrdinals,
                        memberCallOrdinals,
                        fieldAccessOrdinals,
                        out initializer))
                {
                    publishedStatements = [];
                    return false;
                }

                builtStatements.Add(
                    initializer is null
                        ? new StarkPackageTypedTemplateStatementManifest(
                            Kind: "local-variable",
                            Name: declaratorName,
                            StorageClass: localVariable.storageClass().GetText(),
                            IsMutable: localVariable.MUT() is not null,
                            Type: BuildPublishedAbiTypeReference(localDeclaration.Type, module),
                            StorageCapacity: storageCapacity,
                            ConstProvenance: constProvenance)
                        : new StarkPackageTypedTemplateStatementManifest(
                            Kind: "local-variable",
                            Expression: initializer,
                            Name: declaratorName,
                            StorageClass: localVariable.storageClass().GetText(),
                            IsMutable: localVariable.MUT() is not null,
                            Type: BuildPublishedAbiTypeReference(localDeclaration.Type, module),
                            StorageCapacity: storageCapacity,
                            ConstProvenance: constProvenance));
            }

            publishedStatements = builtStatements;
            return true;
        }

        if (statement.localConstantDeclaration() is { } localConstant)
        {
            if (!localDeclarationsByLocation.TryGetValue(
                    TemplateLocalDeclarationFacts.BuildLookupKey(
                        TemplateLocalDeclarationFacts.ConstantKind,
                        localConstant.Start.Line,
                        localConstant.Start.Column + 1),
                    out var localDeclaration))
            {
                publishedStatements = [];
                return false;
            }

            var builtStatements = new List<StarkPackageTypedTemplateStatementManifest>(
                localConstant.constantDeclarators().constantDeclarator().Length);
            foreach (var declarator in localConstant.constantDeclarators().constantDeclarator())
            {
                if (declarator.variableInitializer() is not { } variableInitializer
                    || !TryBuildPublishedTypedTemplateVariableInitializer(
                        module,
                        variableInitializer,
                        localDeclaration.Type,
                        namedTypes,
                        literalsByLocation,
                        conversionsByLocation,
                        objectCreationOrdinals,
                        enumConstructorOrdinals,
                        enumCallOrdinals,
                        enumValueOrdinals,
                        functionAddressOrdinals,
                        directCallOrdinals,
                        dynamicStorageOperationOrdinals,
                        memberCallOrdinals,
                        fieldAccessOrdinals,
                        out var initializer))
                {
                    publishedStatements = [];
                    return false;
                }

                builtStatements.Add(new StarkPackageTypedTemplateStatementManifest(
                    Kind: "local-variable",
                    Expression: initializer,
                    Name: declarator.Identifier().GetText(),
                    StorageClass: "local",
                    IsMutable: false,
                    IsConstant: true,
                    Type: BuildPublishedAbiTypeReference(localDeclaration.Type, module),
                    ConstProvenance: GetPublishedDeclaratorConstProvenance(localDeclaration, declarator.Identifier().GetText())));
            }

            publishedStatements = builtStatements;
            return true;
        }

        if (TryBuildPublishedTypedTemplateStatement(
                module,
                statement,
                namedTypes,
                literalsByLocation,
                localDeclarationsByLocation,
                conversionsByLocation,
                objectCreationOrdinals,
                enumConstructorOrdinals,
                enumCallOrdinals,
                enumValueOrdinals,
                enumPatternOrdinals,
                aggregatePatternOrdinals,
                localStorageCapacitiesByLocation,
                functionAddressOrdinals,
                directCallOrdinals,
                dynamicStorageOperationOrdinals,
                memberCallOrdinals,
                fieldAccessOrdinals,
                out var publishedStatement))
        {
            publishedStatements = [publishedStatement];
            return true;
        }

        publishedStatements = [];
        return false;
    }

    private static bool TryBuildPublishedTypedTemplateStatement(
        LoadedModuleDocument module,
        StarkParser.StatementContext statement,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, LiteralTypingRecord> literalsByLocation,
        IReadOnlyDictionary<string, LocalDeclarationTypingRecord> localDeclarationsByLocation,
        IReadOnlyDictionary<string, ConversionTypingRecord> conversionsByLocation,
        IReadOnlyDictionary<StarkParser.ObjectCreationExpressionContext, PublishedTypedTemplateObjectCreationLookup> objectCreationOrdinals,
        IReadOnlyDictionary<StarkParser.EnumConstructorExpressionContext, int> enumConstructorOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> enumCallOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> enumValueOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> enumPatternOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> aggregatePatternOrdinals,
        IReadOnlyDictionary<string, LocalStorageCapacityTypingRecord> localStorageCapacitiesByLocation,
        IReadOnlyDictionary<ParserRuleContext, int> functionAddressOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> directCallOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> dynamicStorageOperationOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> memberCallOrdinals,
        IReadOnlyDictionary<StarkParser.PostfixPartContext, int> fieldAccessOrdinals,
        out StarkPackageTypedTemplateStatementManifest publishedStatement)
    {
        publishedStatement = null!;

        if (statement.expressionStatement()?.expression().assignmentExpression() is { } initAssignmentExpression
            && initAssignmentExpression.INIT() is not null
            && initAssignmentExpression.ASSIGN() is not null
            && initAssignmentExpression.assignmentOperator() is null
            && TryBuildPublishedTypedTemplateAssignmentTarget(
                module,
                initAssignmentExpression.unaryExpression(),
                namedTypes,
                literalsByLocation,
                conversionsByLocation,
                objectCreationOrdinals,
                enumConstructorOrdinals,
                enumCallOrdinals,
                enumValueOrdinals,
                functionAddressOrdinals,
                directCallOrdinals,
                dynamicStorageOperationOrdinals,
                memberCallOrdinals,
                fieldAccessOrdinals,
                out var initAssignmentTargetName,
                out var initAssignmentTarget)
            && TryBuildPublishedTypedTemplateAssignmentExpression(
                module,
                initAssignmentExpression.assignmentExpression(),
                namedTypes,
                literalsByLocation,
                conversionsByLocation,
                objectCreationOrdinals,
                enumConstructorOrdinals,
                enumCallOrdinals,
                enumValueOrdinals,
                functionAddressOrdinals,
                directCallOrdinals,
                dynamicStorageOperationOrdinals,
                memberCallOrdinals,
                fieldAccessOrdinals,
                out var initAssignmentValue))
        {
            publishedStatement = new StarkPackageTypedTemplateStatementManifest(
                Kind: "assignment",
                Expression: initAssignmentValue,
                Name: initAssignmentTargetName,
                AssignmentOperator: "init =",
                TargetExpression: initAssignmentTarget);
            return true;
        }

        if (statement.expressionStatement()?.expression() is { } expressionStatementExpression
            && expressionStatementExpression.assignmentExpression().assignmentOperator() is null
            && expressionStatementExpression.assignmentExpression().INIT() is null
            && TryBuildPublishedTypedTemplateExpression(
                module,
                expressionStatementExpression,
                namedTypes,
                literalsByLocation,
                conversionsByLocation,
                objectCreationOrdinals,
                enumConstructorOrdinals,
                enumCallOrdinals,
                enumValueOrdinals,
                functionAddressOrdinals,
                directCallOrdinals,
                dynamicStorageOperationOrdinals,
                memberCallOrdinals,
                fieldAccessOrdinals,
                out var expressionStatementValue))
        {
            publishedStatement = new StarkPackageTypedTemplateStatementManifest(
                Kind: "expression",
                Expression: expressionStatementValue);
            return true;
        }

        if (statement.forStatement() is { } forStatement
            && TryBuildPublishedTypedTemplateForInitializerStatements(
                module,
                forStatement.forInitializer(),
                namedTypes,
                literalsByLocation,
                localDeclarationsByLocation,
                conversionsByLocation,
                objectCreationOrdinals,
                enumConstructorOrdinals,
                enumCallOrdinals,
                enumValueOrdinals,
                localStorageCapacitiesByLocation,
                functionAddressOrdinals,
                directCallOrdinals,
                dynamicStorageOperationOrdinals,
                memberCallOrdinals,
                fieldAccessOrdinals,
                out var initializerStatements)
            && TryBuildPublishedTypedTemplateForIteratorStatements(
                module,
                forStatement.forIterator(),
                namedTypes,
                literalsByLocation,
                conversionsByLocation,
                objectCreationOrdinals,
                enumConstructorOrdinals,
                enumCallOrdinals,
                enumValueOrdinals,
                functionAddressOrdinals,
                directCallOrdinals,
                dynamicStorageOperationOrdinals,
                memberCallOrdinals,
                fieldAccessOrdinals,
                out var iteratorStatements)
            && TryGetPublishedTypedTemplateForCondition(
                module,
                forStatement,
                namedTypes,
                literalsByLocation,
                conversionsByLocation,
                objectCreationOrdinals,
                enumConstructorOrdinals,
                enumCallOrdinals,
                enumValueOrdinals,
                functionAddressOrdinals,
                directCallOrdinals,
                dynamicStorageOperationOrdinals,
                memberCallOrdinals,
                fieldAccessOrdinals,
                out var forCondition)
            && TryBuildPublishedTypedTemplateBranchStatement(
                module,
                forStatement.statement(),
                namedTypes,
                literalsByLocation,
                localDeclarationsByLocation,
                conversionsByLocation,
                objectCreationOrdinals,
                enumConstructorOrdinals,
                enumCallOrdinals,
                enumValueOrdinals,
                enumPatternOrdinals,
                aggregatePatternOrdinals,
                localStorageCapacitiesByLocation,
                functionAddressOrdinals,
                directCallOrdinals,
                dynamicStorageOperationOrdinals,
                memberCallOrdinals,
                fieldAccessOrdinals,
                out var forBodyStatements))
        {
            publishedStatement = new StarkPackageTypedTemplateStatementManifest(
                Kind: "for",
                Expression: forCondition!,
                LoopBehavior: forStatement.loopBehavior().GetText(),
                InitializerStatements: initializerStatements,
                IteratorStatements: iteratorStatements,
                BodyStatements: forBodyStatements,
                LoopContracts: BuildLoopContracts(forStatement.loopContract()));
            return true;
        }

        if (statement.whileStatement() is { } whileStatement
            && TryBuildPublishedTypedTemplateExpression(
                module,
                whileStatement.expression(),
                namedTypes,
                literalsByLocation,
                conversionsByLocation,
                objectCreationOrdinals,
                enumConstructorOrdinals,
                enumCallOrdinals,
                enumValueOrdinals,
                functionAddressOrdinals,
                directCallOrdinals,
                dynamicStorageOperationOrdinals,
                memberCallOrdinals,
                fieldAccessOrdinals,
                out var whileCondition)
            && TryBuildPublishedTypedTemplateBranchStatement(
                module,
                whileStatement.statement(),
                namedTypes,
                literalsByLocation,
                localDeclarationsByLocation,
                conversionsByLocation,
                objectCreationOrdinals,
                enumConstructorOrdinals,
                enumCallOrdinals,
                enumValueOrdinals,
                enumPatternOrdinals,
                aggregatePatternOrdinals,
                localStorageCapacitiesByLocation,
                functionAddressOrdinals,
                directCallOrdinals,
                dynamicStorageOperationOrdinals,
                memberCallOrdinals,
                fieldAccessOrdinals,
                out var whileBodyStatements))
        {
            publishedStatement = new StarkPackageTypedTemplateStatementManifest(
                Kind: "while",
                Expression: whileCondition,
                LoopBehavior: whileStatement.loopBehavior().GetText(),
                BodyStatements: whileBodyStatements,
                LoopContracts: BuildLoopContracts(whileStatement.loopContract()));
            return true;
        }

        if (statement.ifStatement() is { } ifStatement
            && ifStatement.expression() is { } ifCondition
            && TryBuildPublishedTypedTemplateExpression(
                module,
                ifCondition,
                namedTypes,
                literalsByLocation,
                conversionsByLocation,
                objectCreationOrdinals,
                enumConstructorOrdinals,
                enumCallOrdinals,
                enumValueOrdinals,
                functionAddressOrdinals,
                directCallOrdinals,
                dynamicStorageOperationOrdinals,
                memberCallOrdinals,
                fieldAccessOrdinals,
                out var condition)
            && TryBuildPublishedTypedTemplateBranchStatement(
                module,
                ifStatement.statement(0),
                namedTypes,
                literalsByLocation,
                localDeclarationsByLocation,
                conversionsByLocation,
                objectCreationOrdinals,
                enumConstructorOrdinals,
                enumCallOrdinals,
                enumValueOrdinals,
                enumPatternOrdinals,
                aggregatePatternOrdinals,
                localStorageCapacitiesByLocation,
                functionAddressOrdinals,
                directCallOrdinals,
                dynamicStorageOperationOrdinals,
                memberCallOrdinals,
                fieldAccessOrdinals,
                out var thenStatements))
        {
            IReadOnlyList<StarkPackageTypedTemplateStatementManifest>? elseStatements = null;
            if (ifStatement.statement().Length >= 2
                && !TryBuildPublishedTypedTemplateBranchStatement(
                    module,
                    ifStatement.statement(1),
                    namedTypes,
                    literalsByLocation,
                    localDeclarationsByLocation,
                    conversionsByLocation,
                    objectCreationOrdinals,
                    enumConstructorOrdinals,
                    enumCallOrdinals,
                    enumValueOrdinals,
                    enumPatternOrdinals,
                    aggregatePatternOrdinals,
                    localStorageCapacitiesByLocation,
                    functionAddressOrdinals,
                    directCallOrdinals,
                    dynamicStorageOperationOrdinals,
                    memberCallOrdinals,
                    fieldAccessOrdinals,
                    out elseStatements))
            {
                return false;
            }

            publishedStatement = new StarkPackageTypedTemplateStatementManifest(
                Kind: "if",
                Expression: condition,
                ThenStatements: thenStatements,
                ElseStatements: elseStatements);
            return true;
        }

        if (statement.switchStatement() is { } switchStatement
            && TryBuildPublishedTypedTemplateExpression(
                module,
                switchStatement.expression(),
                namedTypes,
                literalsByLocation,
                conversionsByLocation,
                objectCreationOrdinals,
                enumConstructorOrdinals,
                enumCallOrdinals,
                enumValueOrdinals,
                functionAddressOrdinals,
                directCallOrdinals,
                dynamicStorageOperationOrdinals,
                memberCallOrdinals,
                fieldAccessOrdinals,
                out var switchExpression)
            && TryBuildPublishedTypedTemplateSwitchCaseList(
                module,
                switchStatement.switchSection(),
                namedTypes,
                literalsByLocation,
                localDeclarationsByLocation,
                conversionsByLocation,
                objectCreationOrdinals,
                enumConstructorOrdinals,
                enumCallOrdinals,
                enumValueOrdinals,
                enumPatternOrdinals,
                aggregatePatternOrdinals,
                localStorageCapacitiesByLocation,
                functionAddressOrdinals,
                directCallOrdinals,
                dynamicStorageOperationOrdinals,
                memberCallOrdinals,
                fieldAccessOrdinals,
                out var switchCases))
        {
            publishedStatement = new StarkPackageTypedTemplateStatementManifest(
                Kind: "switch",
                Expression: switchExpression,
                SwitchCases: switchCases);
            return true;
        }

        if (statement.expressionStatement()?.expression().assignmentExpression() is { } assignmentExpression
            && assignmentExpression.assignmentOperator() is not null
            && TryBuildPublishedTypedTemplateAssignmentTarget(
                module,
                assignmentExpression.unaryExpression(),
                namedTypes,
                literalsByLocation,
                conversionsByLocation,
                objectCreationOrdinals,
                enumConstructorOrdinals,
                enumCallOrdinals,
                enumValueOrdinals,
                functionAddressOrdinals,
                directCallOrdinals,
                dynamicStorageOperationOrdinals,
                memberCallOrdinals,
                fieldAccessOrdinals,
                out var assignmentTargetName,
                out var assignmentTarget)
            && TryBuildPublishedTypedTemplateAssignmentExpression(
                module,
                assignmentExpression.assignmentExpression(),
                namedTypes,
                literalsByLocation,
                conversionsByLocation,
                objectCreationOrdinals,
                enumConstructorOrdinals,
                enumCallOrdinals,
                enumValueOrdinals,
                functionAddressOrdinals,
                directCallOrdinals,
                dynamicStorageOperationOrdinals,
                memberCallOrdinals,
                fieldAccessOrdinals,
                out var assignmentValue))
        {
            publishedStatement = new StarkPackageTypedTemplateStatementManifest(
                Kind: "assignment",
                Expression: assignmentValue,
                Name: assignmentTargetName,
                AssignmentOperator: assignmentExpression.assignmentOperator().GetText(),
                TargetExpression: assignmentTarget);
            return true;
        }

        if (statement.returnStatement() is { } returnStatement)
        {
            if (returnStatement.expression() is null)
            {
                publishedStatement = new StarkPackageTypedTemplateStatementManifest(Kind: "return");
                return true;
            }

            if (TryBuildPublishedTypedTemplateExpression(module, returnStatement.expression(), namedTypes, literalsByLocation, conversionsByLocation, objectCreationOrdinals, enumConstructorOrdinals, enumCallOrdinals, enumValueOrdinals, functionAddressOrdinals, directCallOrdinals, dynamicStorageOperationOrdinals, memberCallOrdinals, fieldAccessOrdinals, out var returnExpression))
            {
                publishedStatement = new StarkPackageTypedTemplateStatementManifest(
                    Kind: "return",
                    Expression: returnExpression);
                return true;
            }
        }

        if (statement.breakStatement() is not null)
        {
            publishedStatement = new StarkPackageTypedTemplateStatementManifest(
                Kind: "break");
            return true;
        }

        if (statement.continueStatement() is not null)
        {
            publishedStatement = new StarkPackageTypedTemplateStatementManifest(
                Kind: "continue");
            return true;
        }

        return false;
    }

    private static IReadOnlyList<string> BuildLoopContracts(
        IEnumerable<StarkParser.LoopContractContext> contracts)
    {
        return contracts
            .Select(static contract => contract.GetText())
            .Where(static contract => !string.IsNullOrWhiteSpace(contract))
            .ToArray();
    }

    private static bool CanUseTypedTemplateStatementAsTerminal(
        StarkPackageTypedTemplateStatementManifest statement,
        StarkTypeSymbol returnType)
    {
        if (string.Equals(statement.Kind, "return", StringComparison.Ordinal))
        {
            return statement.Expression is not null || returnType.Kind == StarkTypeKind.Void;
        }

        if (string.Equals(statement.Kind, "block", StringComparison.Ordinal))
        {
            return statement.BodyStatements is { Count: > 0 }
                && CanUseTypedTemplateStatementListAsTerminal(statement.BodyStatements, returnType);
        }

        if (string.Equals(statement.Kind, "if", StringComparison.Ordinal))
        {
            return CanUseTypedTemplateIfAsTerminal(statement, returnType);
        }

        return CanUseTypedTemplateSwitchAsTerminal(statement, returnType);
    }

    private static bool CanUseTypedTemplateIfAsTerminal(
        StarkPackageTypedTemplateStatementManifest statement,
        StarkTypeSymbol returnType)
    {
        if (!string.Equals(statement.Kind, "if", StringComparison.Ordinal)
            || statement.ThenStatements is not { Count: > 0 }
            || statement.ElseStatements is not { Count: > 0 })
        {
            return false;
        }

        return CanUseTypedTemplateStatementListAsTerminal(statement.ThenStatements, returnType)
            && CanUseTypedTemplateStatementListAsTerminal(statement.ElseStatements, returnType);
    }

    private static bool CanUseTypedTemplateSwitchAsTerminal(
        StarkPackageTypedTemplateStatementManifest statement,
        StarkTypeSymbol returnType)
    {
        if (!string.Equals(statement.Kind, "switch", StringComparison.Ordinal)
            || statement.SwitchCases is not { Count: > 0 })
        {
            return false;
        }

        foreach (var switchCase in statement.SwitchCases)
        {
            if (switchCase.Statements is not { Count: > 0 }
                || !CanUseTypedTemplateStatementListAsTerminal(switchCase.Statements, returnType))
            {
                return false;
            }
        }

        return true;
    }

    private static bool CanUseTypedTemplateStatementListAsTerminal(
        IReadOnlyList<StarkPackageTypedTemplateStatementManifest> statements,
        StarkTypeSymbol returnType)
    {
        return statements.Count != 0
            && CanUseTypedTemplateStatementAsTerminal(statements[^1], returnType);
    }

    private static bool TryBuildPublishedTypedTemplateSwitchCaseList(
        LoadedModuleDocument module,
        IReadOnlyList<StarkParser.SwitchSectionContext> sections,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, LiteralTypingRecord> literalsByLocation,
        IReadOnlyDictionary<string, LocalDeclarationTypingRecord> localDeclarationsByLocation,
        IReadOnlyDictionary<string, ConversionTypingRecord> conversionsByLocation,
        IReadOnlyDictionary<StarkParser.ObjectCreationExpressionContext, PublishedTypedTemplateObjectCreationLookup> objectCreationOrdinals,
        IReadOnlyDictionary<StarkParser.EnumConstructorExpressionContext, int> enumConstructorOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> enumCallOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> enumValueOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> enumPatternOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> aggregatePatternOrdinals,
        IReadOnlyDictionary<string, LocalStorageCapacityTypingRecord> localStorageCapacitiesByLocation,
        IReadOnlyDictionary<ParserRuleContext, int> functionAddressOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> directCallOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> dynamicStorageOperationOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> memberCallOrdinals,
        IReadOnlyDictionary<StarkParser.PostfixPartContext, int> fieldAccessOrdinals,
        out IReadOnlyList<StarkPackageTypedTemplateSwitchCaseManifest> switchCases)
    {
        var builtCases = new List<StarkPackageTypedTemplateSwitchCaseManifest>(sections.Sum(static section => section.switchLabel().Length));
        foreach (var section in sections)
        {
            if (section.switchLabel().Length == 0
                || !TryBuildPublishedTypedTemplateStatementList(
                    module,
                    section.statement(),
                    namedTypes,
                    literalsByLocation,
                    localDeclarationsByLocation,
                    conversionsByLocation,
                    objectCreationOrdinals,
                    enumConstructorOrdinals,
                    enumCallOrdinals,
                    enumValueOrdinals,
                    enumPatternOrdinals,
                    aggregatePatternOrdinals,
                    localStorageCapacitiesByLocation,
                    functionAddressOrdinals,
                    directCallOrdinals,
                    dynamicStorageOperationOrdinals,
                    memberCallOrdinals,
                    fieldAccessOrdinals,
                    out var statements))
            {
                switchCases = [];
                return false;
            }

            foreach (var label in section.switchLabel())
            {
                if (!TryBuildPublishedTypedTemplateSwitchCase(
                        module,
                        label,
                        namedTypes,
                        literalsByLocation,
                        conversionsByLocation,
                        objectCreationOrdinals,
                        enumConstructorOrdinals,
                        enumCallOrdinals,
                        enumValueOrdinals,
                        enumPatternOrdinals,
                        aggregatePatternOrdinals,
                        localStorageCapacitiesByLocation,
                        functionAddressOrdinals,
                        directCallOrdinals,
                        dynamicStorageOperationOrdinals,
                        memberCallOrdinals,
                        fieldAccessOrdinals,
                        statements,
                        out var builtCase))
                {
                    switchCases = [];
                    return false;
                }

                builtCases.Add(builtCase);
            }
        }

        switchCases = builtCases;
        return builtCases.Count > 0;
    }

    private static bool TryBuildPublishedTypedTemplateSwitchCase(
        LoadedModuleDocument module,
        StarkParser.SwitchLabelContext label,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, LiteralTypingRecord> literalsByLocation,
        IReadOnlyDictionary<string, ConversionTypingRecord> conversionsByLocation,
        IReadOnlyDictionary<StarkParser.ObjectCreationExpressionContext, PublishedTypedTemplateObjectCreationLookup> objectCreationOrdinals,
        IReadOnlyDictionary<StarkParser.EnumConstructorExpressionContext, int> enumConstructorOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> enumCallOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> enumValueOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> enumPatternOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> aggregatePatternOrdinals,
        IReadOnlyDictionary<string, LocalStorageCapacityTypingRecord> localStorageCapacitiesByLocation,
        IReadOnlyDictionary<ParserRuleContext, int> functionAddressOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> directCallOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> dynamicStorageOperationOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> memberCallOrdinals,
        IReadOnlyDictionary<StarkParser.PostfixPartContext, int> fieldAccessOrdinals,
        IReadOnlyList<StarkPackageTypedTemplateStatementManifest> statements,
        out StarkPackageTypedTemplateSwitchCaseManifest switchCase)
    {
        switchCase = null!;

        StarkPackageTypedTemplateExpressionManifest? guardExpression = null;
        if (label.whenClause()?.expression() is { } guard
            && !TryBuildPublishedTypedTemplateExpression(
                module,
                guard,
                namedTypes,
                literalsByLocation,
                conversionsByLocation,
                objectCreationOrdinals,
                enumConstructorOrdinals,
                enumCallOrdinals,
                enumValueOrdinals,
                functionAddressOrdinals,
                directCallOrdinals,
                dynamicStorageOperationOrdinals,
                memberCallOrdinals,
                fieldAccessOrdinals,
                out guardExpression))
        {
            return false;
        }

        if (label.DEFAULT() is not null)
        {
            switchCase = new StarkPackageTypedTemplateSwitchCaseManifest(
                Kind: "default",
                Statements: statements);
            return true;
        }

        if (label.pattern() is not { } pattern)
        {
            return false;
        }

        if (pattern.DISCARD() is not null)
        {
            switchCase = new StarkPackageTypedTemplateSwitchCaseManifest(
                Kind: guardExpression is null ? "default" : "match-all",
                GuardExpression: guardExpression,
                Statements: statements);
            return true;
        }

        if (pattern.VAR() is not null)
        {
            var captureName = pattern.Identifier()?.GetText();
            if (captureName is null)
            {
                return false;
            }

            switchCase = new StarkPackageTypedTemplateSwitchCaseManifest(
                Kind: "match-all",
                Name: captureName,
                GuardExpression: guardExpression,
                Statements: statements);
            return true;
        }

        if (pattern.literal() is { } literal)
        {
            if (!TryBuildPublishedTypedTemplateLiteralExpression(module, literal, literalsByLocation, out var literalExpression))
            {
                return false;
            }

            switchCase = new StarkPackageTypedTemplateSwitchCaseManifest(
                Kind: "literal",
                Expression: literalExpression,
                GuardExpression: guardExpression,
                Statements: statements);
            return true;
        }

        if (pattern.enumNamedFieldPattern() is { } enumNamedFieldPattern)
        {
            if (!enumPatternOrdinals.TryGetValue(enumNamedFieldPattern, out var ordinal)
                || !TryBuildPublishedTypedTemplateSwitchFieldPatterns(
                    module,
                    literalsByLocation,
                    enumPatternOrdinals,
                    aggregatePatternOrdinals,
                    enumNamedFieldPattern.enumNamedFieldPatternPayload().namedPatternMember().Select(static member => member.pattern()).ToArray(),
                    out var members))
            {
                return false;
            }

            switchCase = new StarkPackageTypedTemplateSwitchCaseManifest(
                Kind: "enum-pattern",
                Ordinal: ordinal,
                Members: members,
                GuardExpression: guardExpression,
                Statements: statements);
            return true;
        }

        if (pattern.genericEnumAggregatePattern() is { } genericEnumAggregatePattern)
        {
            var wholeCaptureName = genericEnumAggregatePattern.aggregatePatternSuffix()?.Identifier()?.GetText();
            IReadOnlyList<StarkPackageTypedTemplatePatternManifest> members = [];
            if (!enumPatternOrdinals.TryGetValue(genericEnumAggregatePattern, out var ordinal)
                || (wholeCaptureName is null
                    && !TryBuildPublishedTypedTemplateSwitchFieldPatterns(
                        module,
                        literalsByLocation,
                        enumPatternOrdinals,
                        aggregatePatternOrdinals,
                        genericEnumAggregatePattern.aggregatePatternSuffix(),
                        out members)))
            {
                return false;
            }

            switchCase = new StarkPackageTypedTemplateSwitchCaseManifest(
                Kind: "enum-pattern",
                Ordinal: ordinal,
                Name: wholeCaptureName,
                Members: wholeCaptureName is null ? members : [],
                GuardExpression: guardExpression,
                Statements: statements);
            return true;
        }

        if (pattern.aggregatePattern() is { } aggregatePattern)
        {
            if (enumPatternOrdinals.TryGetValue(aggregatePattern, out var enumOrdinal))
            {
                var enumWholeCaptureName = aggregatePattern.aggregatePatternSuffix()?.Identifier()?.GetText();
                IReadOnlyList<StarkPackageTypedTemplatePatternManifest> enumMembers = [];
                if (enumWholeCaptureName is null
                    && !TryBuildPublishedTypedTemplateSwitchFieldPatterns(
                        module,
                        literalsByLocation,
                        enumPatternOrdinals,
                        aggregatePatternOrdinals,
                        aggregatePattern.aggregatePatternSuffix(),
                        out enumMembers))
                {
                    return false;
                }

                switchCase = new StarkPackageTypedTemplateSwitchCaseManifest(
                    Kind: "enum-pattern",
                    Ordinal: enumOrdinal,
                    Name: enumWholeCaptureName,
                    Members: enumWholeCaptureName is null ? enumMembers : [],
                    GuardExpression: guardExpression,
                    Statements: statements);
                return true;
            }

            var wholeCaptureName = aggregatePattern.aggregatePatternSuffix()?.Identifier()?.GetText();
            IReadOnlyList<StarkPackageTypedTemplatePatternManifest> aggregateMembers = [];
            if (!aggregatePatternOrdinals.TryGetValue(aggregatePattern, out var aggregateOrdinal)
                || (wholeCaptureName is null
                    && !TryBuildPublishedTypedTemplateSwitchFieldPatterns(
                        module,
                        literalsByLocation,
                        enumPatternOrdinals,
                        aggregatePatternOrdinals,
                        aggregatePattern.aggregatePatternSuffix(),
                        out aggregateMembers)))
            {
                return false;
            }

            switchCase = new StarkPackageTypedTemplateSwitchCaseManifest(
                Kind: "aggregate-pattern",
                Ordinal: aggregateOrdinal,
                Name: wholeCaptureName,
                Members: wholeCaptureName is null ? aggregateMembers : [],
                GuardExpression: guardExpression,
                Statements: statements);
            return true;
        }

        return false;
    }

    private static bool TryBuildPublishedTypedTemplateLiteralExpression(
        LoadedModuleDocument module,
        StarkParser.LiteralContext literal,
        IReadOnlyDictionary<string, LiteralTypingRecord> literalsByLocation,
        out StarkPackageTypedTemplateExpressionManifest publishedExpression)
    {
        publishedExpression = null!;

        if (!literalsByLocation.TryGetValue(
                BuildTemplateLiteralLookupKey(literal.Start.Line, literal.Start.Column + 1),
                out var literalRecord))
        {
            return false;
        }

        publishedExpression = new StarkPackageTypedTemplateExpressionManifest(
            Kind: "literal",
            LiteralText: literal.GetText(),
            Type: BuildPublishedAbiTypeReference(literalRecord.Type, module));
        return true;
    }

    private static bool TryBuildPublishedTypedTemplateSwitchFieldPatterns(
        LoadedModuleDocument module,
        IReadOnlyDictionary<string, LiteralTypingRecord> literalsByLocation,
        IReadOnlyDictionary<ParserRuleContext, int> enumPatternOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> aggregatePatternOrdinals,
        StarkParser.AggregatePatternSuffixContext? suffix,
        out IReadOnlyList<StarkPackageTypedTemplatePatternManifest> members)
    {
        if (suffix is null)
        {
            members = [];
            return true;
        }

        if (suffix.Identifier() is not null)
        {
            members = [];
            return false;
        }

        return TryBuildPublishedTypedTemplateSwitchFieldPatterns(
            module,
            literalsByLocation,
            enumPatternOrdinals,
            aggregatePatternOrdinals,
            suffix.pattern(),
            out members);
    }

    private static bool TryBuildPublishedTypedTemplateSwitchFieldPatterns(
        LoadedModuleDocument module,
        IReadOnlyDictionary<string, LiteralTypingRecord> literalsByLocation,
        IReadOnlyDictionary<ParserRuleContext, int> enumPatternOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> aggregatePatternOrdinals,
        IReadOnlyList<StarkParser.PatternContext> patterns,
        out IReadOnlyList<StarkPackageTypedTemplatePatternManifest> members)
    {
        var builtMembers = new List<StarkPackageTypedTemplatePatternManifest>(patterns.Count);
        foreach (var pattern in patterns)
        {
            if (!TryBuildPublishedTypedTemplateSwitchFieldPattern(
                    module,
                    pattern,
                    literalsByLocation,
                    enumPatternOrdinals,
                    aggregatePatternOrdinals,
                    out var builtMember))
            {
                members = [];
                return false;
            }

            builtMembers.Add(builtMember);
        }

        members = builtMembers;
        return true;
    }

    private static bool TryBuildPublishedTypedTemplateSwitchFieldPattern(
        LoadedModuleDocument module,
        StarkParser.PatternContext pattern,
        IReadOnlyDictionary<string, LiteralTypingRecord> literalsByLocation,
        IReadOnlyDictionary<ParserRuleContext, int> enumPatternOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> aggregatePatternOrdinals,
        out StarkPackageTypedTemplatePatternManifest member)
    {
        member = null!;

        if (pattern.DISCARD() is not null)
        {
            member = new StarkPackageTypedTemplatePatternManifest("discard");
            return true;
        }

        if (pattern.VAR() is not null && pattern.Identifier() is not null)
        {
            member = new StarkPackageTypedTemplatePatternManifest("capture", pattern.Identifier().GetText());
            return true;
        }

        if (pattern.literal() is { } literal)
        {
            if (!TryBuildPublishedTypedTemplateLiteralExpression(module, literal, literalsByLocation, out var literalExpression))
            {
                return false;
            }

            member = new StarkPackageTypedTemplatePatternManifest(
                "literal",
                Expression: literalExpression);
            return true;
        }

        if (pattern.enumNamedFieldPattern() is { } enumNamedFieldPattern)
        {
            if (!enumPatternOrdinals.TryGetValue(enumNamedFieldPattern, out var ordinal)
                || !TryBuildPublishedTypedTemplateSwitchFieldPatterns(
                    module,
                    literalsByLocation,
                    enumPatternOrdinals,
                    aggregatePatternOrdinals,
                    enumNamedFieldPattern.enumNamedFieldPatternPayload().namedPatternMember().Select(static nestedMember => nestedMember.pattern()).ToArray(),
                    out var members))
            {
                return false;
            }

            member = new StarkPackageTypedTemplatePatternManifest(
                "enum-pattern",
                Ordinal: ordinal,
                Members: members);
            return true;
        }

        if (pattern.genericEnumAggregatePattern() is { } genericEnumAggregatePattern)
        {
            var wholeCaptureName = genericEnumAggregatePattern.aggregatePatternSuffix()?.Identifier()?.GetText();
            IReadOnlyList<StarkPackageTypedTemplatePatternManifest> members = [];
            if (!enumPatternOrdinals.TryGetValue(genericEnumAggregatePattern, out var ordinal)
                || (wholeCaptureName is null
                    && !TryBuildPublishedTypedTemplateSwitchFieldPatterns(
                        module,
                        literalsByLocation,
                        enumPatternOrdinals,
                        aggregatePatternOrdinals,
                        genericEnumAggregatePattern.aggregatePatternSuffix(),
                        out members)))
            {
                return false;
            }

            member = new StarkPackageTypedTemplatePatternManifest(
                "enum-pattern",
                Ordinal: ordinal,
                Name: wholeCaptureName,
                Members: wholeCaptureName is null ? members : []);
            return true;
        }

        if (pattern.aggregatePattern() is { } aggregatePattern)
        {
            if (enumPatternOrdinals.TryGetValue(aggregatePattern, out var enumOrdinal))
            {
                var enumWholeCaptureName = aggregatePattern.aggregatePatternSuffix()?.Identifier()?.GetText();
                IReadOnlyList<StarkPackageTypedTemplatePatternManifest> enumMembers = [];
                if (enumWholeCaptureName is null
                    && !TryBuildPublishedTypedTemplateSwitchFieldPatterns(
                        module,
                        literalsByLocation,
                        enumPatternOrdinals,
                        aggregatePatternOrdinals,
                        aggregatePattern.aggregatePatternSuffix(),
                        out enumMembers))
                {
                    return false;
                }

                member = new StarkPackageTypedTemplatePatternManifest(
                    "enum-pattern",
                    Ordinal: enumOrdinal,
                    Name: enumWholeCaptureName,
                    Members: enumWholeCaptureName is null ? enumMembers : []);
                return true;
            }

            var wholeCaptureName = aggregatePattern.aggregatePatternSuffix()?.Identifier()?.GetText();
            IReadOnlyList<StarkPackageTypedTemplatePatternManifest> aggregateMembers = [];
            if (!aggregatePatternOrdinals.TryGetValue(aggregatePattern, out var aggregateOrdinal)
                || (wholeCaptureName is null
                    && !TryBuildPublishedTypedTemplateSwitchFieldPatterns(
                        module,
                        literalsByLocation,
                        enumPatternOrdinals,
                        aggregatePatternOrdinals,
                        aggregatePattern.aggregatePatternSuffix(),
                        out aggregateMembers)))
            {
                return false;
            }

            member = new StarkPackageTypedTemplatePatternManifest(
                "aggregate-pattern",
                Ordinal: aggregateOrdinal,
                Name: wholeCaptureName,
                Members: wholeCaptureName is null ? aggregateMembers : []);
            return true;
        }

        return false;
    }

    private static bool TryBuildPublishedTypedTemplateBranchStatement(
        LoadedModuleDocument module,
        StarkParser.StatementContext statement,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, LiteralTypingRecord> literalsByLocation,
        IReadOnlyDictionary<string, LocalDeclarationTypingRecord> localDeclarationsByLocation,
        IReadOnlyDictionary<string, ConversionTypingRecord> conversionsByLocation,
        IReadOnlyDictionary<StarkParser.ObjectCreationExpressionContext, PublishedTypedTemplateObjectCreationLookup> objectCreationOrdinals,
        IReadOnlyDictionary<StarkParser.EnumConstructorExpressionContext, int> enumConstructorOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> enumCallOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> enumValueOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> enumPatternOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> aggregatePatternOrdinals,
        IReadOnlyDictionary<string, LocalStorageCapacityTypingRecord> localStorageCapacitiesByLocation,
        IReadOnlyDictionary<ParserRuleContext, int> functionAddressOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> directCallOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> dynamicStorageOperationOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> memberCallOrdinals,
        IReadOnlyDictionary<StarkParser.PostfixPartContext, int> fieldAccessOrdinals,
        out IReadOnlyList<StarkPackageTypedTemplateStatementManifest> publishedStatements)
    {
        if (statement.block() is { } block)
        {
            return TryBuildPublishedTypedTemplateStatementList(
                module,
                block.statement(),
                namedTypes,
                literalsByLocation,
                localDeclarationsByLocation,
                conversionsByLocation,
                objectCreationOrdinals,
                enumConstructorOrdinals,
                enumCallOrdinals,
                enumValueOrdinals,
                enumPatternOrdinals,
                aggregatePatternOrdinals,
                localStorageCapacitiesByLocation,
                functionAddressOrdinals,
                directCallOrdinals,
                dynamicStorageOperationOrdinals,
                memberCallOrdinals,
                fieldAccessOrdinals,
                out publishedStatements);
        }

        if (TryBuildPublishedTypedTemplateStatements(
                module,
                statement,
                namedTypes,
                literalsByLocation,
                localDeclarationsByLocation,
                conversionsByLocation,
                objectCreationOrdinals,
                enumConstructorOrdinals,
                enumCallOrdinals,
                enumValueOrdinals,
                enumPatternOrdinals,
                aggregatePatternOrdinals,
                localStorageCapacitiesByLocation,
                functionAddressOrdinals,
                directCallOrdinals,
                dynamicStorageOperationOrdinals,
                memberCallOrdinals,
                fieldAccessOrdinals,
                out publishedStatements))
        {
            return true;
        }

        publishedStatements = [];
        return false;
    }

    private static bool TryBuildPublishedTypedTemplateForInitializerStatements(
        LoadedModuleDocument module,
        StarkParser.ForInitializerContext? initializer,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, LiteralTypingRecord> literalsByLocation,
        IReadOnlyDictionary<string, LocalDeclarationTypingRecord> localDeclarationsByLocation,
        IReadOnlyDictionary<string, ConversionTypingRecord> conversionsByLocation,
        IReadOnlyDictionary<StarkParser.ObjectCreationExpressionContext, PublishedTypedTemplateObjectCreationLookup> objectCreationOrdinals,
        IReadOnlyDictionary<StarkParser.EnumConstructorExpressionContext, int> enumConstructorOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> enumCallOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> enumValueOrdinals,
        IReadOnlyDictionary<string, LocalStorageCapacityTypingRecord> localStorageCapacitiesByLocation,
        IReadOnlyDictionary<ParserRuleContext, int> functionAddressOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> directCallOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> dynamicStorageOperationOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> memberCallOrdinals,
        IReadOnlyDictionary<StarkParser.PostfixPartContext, int> fieldAccessOrdinals,
        out IReadOnlyList<StarkPackageTypedTemplateStatementManifest> initializerStatements)
    {
        if (initializer is null)
        {
            initializerStatements = [];
            return true;
        }

        if (initializer.localForVariableDeclaration() is { } localForVariableDeclaration)
        {
            if (!localDeclarationsByLocation.TryGetValue(
                    TemplateLocalDeclarationFacts.BuildLookupKey(
                        TemplateLocalDeclarationFacts.ForVariableKind,
                        localForVariableDeclaration.Start.Line,
                        localForVariableDeclaration.Start.Column + 1),
                    out var localDeclaration))
            {
                initializerStatements = [];
                return false;
            }

            var builtStatements = new List<StarkPackageTypedTemplateStatementManifest>(
                localForVariableDeclaration.variableDeclarators().variableDeclarator().Length);
            foreach (var declarator in localForVariableDeclaration.variableDeclarators().variableDeclarator())
            {
                var declaratorName = declarator.Identifier().GetText();
                var constProvenance = GetPublishedDeclaratorConstProvenance(localDeclaration, declaratorName);
                var storageCapacity = TryGetPublishedStorageCapacity(
                        declarator,
                        localStorageCapacitiesByLocation,
                        out var parsedStorageCapacity)
                    ? parsedStorageCapacity
                    : (int?)null;
                StarkPackageTypedTemplateExpressionManifest? initializerValue = null;
                if (declarator.variableInitializer() is { } variableInitializer
                    && !TryBuildPublishedTypedTemplateVariableInitializer(
                        module,
                        variableInitializer,
                        localDeclaration.Type,
                        namedTypes,
                        literalsByLocation,
                        conversionsByLocation,
                        objectCreationOrdinals,
                        enumConstructorOrdinals,
                        enumCallOrdinals,
                        enumValueOrdinals,
                        functionAddressOrdinals,
                        directCallOrdinals,
                        dynamicStorageOperationOrdinals,
                        memberCallOrdinals,
                        fieldAccessOrdinals,
                        out initializerValue))
                {
                    initializerStatements = [];
                    return false;
                }

                builtStatements.Add(
                    initializerValue is null
                        ? new StarkPackageTypedTemplateStatementManifest(
                            Kind: "local-variable",
                            Name: declaratorName,
                            StorageClass: localForVariableDeclaration.storageClass().GetText(),
                            IsMutable: localForVariableDeclaration.MUT() is not null,
                            Type: BuildPublishedAbiTypeReference(localDeclaration.Type, module),
                            StorageCapacity: storageCapacity,
                            ConstProvenance: constProvenance)
                        : new StarkPackageTypedTemplateStatementManifest(
                            Kind: "local-variable",
                            Expression: initializerValue,
                            Name: declaratorName,
                            StorageClass: localForVariableDeclaration.storageClass().GetText(),
                            IsMutable: localForVariableDeclaration.MUT() is not null,
                            Type: BuildPublishedAbiTypeReference(localDeclaration.Type, module),
                            StorageCapacity: storageCapacity,
                            ConstProvenance: constProvenance));
            }

            initializerStatements = builtStatements;
            return true;
        }

        if (initializer.expressionList() is { } expressionList)
        {
            return TryBuildPublishedTypedTemplateAssignmentStatementList(
                module,
                expressionList.expression(),
                namedTypes,
                literalsByLocation,
                conversionsByLocation,
                objectCreationOrdinals,
                enumConstructorOrdinals,
                enumCallOrdinals,
                enumValueOrdinals,
                functionAddressOrdinals,
                directCallOrdinals,
                dynamicStorageOperationOrdinals,
                memberCallOrdinals,
                fieldAccessOrdinals,
                out initializerStatements);
        }

        initializerStatements = [];
        return false;
    }

    private static bool TryBuildPublishedTypedTemplateForIteratorStatements(
        LoadedModuleDocument module,
        StarkParser.ForIteratorContext? iterator,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, LiteralTypingRecord> literalsByLocation,
        IReadOnlyDictionary<string, ConversionTypingRecord> conversionsByLocation,
        IReadOnlyDictionary<StarkParser.ObjectCreationExpressionContext, PublishedTypedTemplateObjectCreationLookup> objectCreationOrdinals,
        IReadOnlyDictionary<StarkParser.EnumConstructorExpressionContext, int> enumConstructorOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> enumCallOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> enumValueOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> functionAddressOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> directCallOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> dynamicStorageOperationOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> memberCallOrdinals,
        IReadOnlyDictionary<StarkParser.PostfixPartContext, int> fieldAccessOrdinals,
        out IReadOnlyList<StarkPackageTypedTemplateStatementManifest> iteratorStatements)
    {
        if (iterator is null)
        {
            iteratorStatements = [];
            return true;
        }

        return TryBuildPublishedTypedTemplateAssignmentStatementList(
            module,
            iterator.expressionList().expression(),
            namedTypes,
            literalsByLocation,
            conversionsByLocation,
            objectCreationOrdinals,
            enumConstructorOrdinals,
            enumCallOrdinals,
            enumValueOrdinals,
            functionAddressOrdinals,
            directCallOrdinals,
            dynamicStorageOperationOrdinals,
            memberCallOrdinals,
            fieldAccessOrdinals,
            out iteratorStatements);
    }

    private static bool TryGetPublishedTypedTemplateForCondition(
        LoadedModuleDocument module,
        StarkParser.ForStatementContext forStatement,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, LiteralTypingRecord> literalsByLocation,
        IReadOnlyDictionary<string, ConversionTypingRecord> conversionsByLocation,
        IReadOnlyDictionary<StarkParser.ObjectCreationExpressionContext, PublishedTypedTemplateObjectCreationLookup> objectCreationOrdinals,
        IReadOnlyDictionary<StarkParser.EnumConstructorExpressionContext, int> enumConstructorOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> enumCallOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> enumValueOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> functionAddressOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> directCallOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> dynamicStorageOperationOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> memberCallOrdinals,
        IReadOnlyDictionary<StarkParser.PostfixPartContext, int> fieldAccessOrdinals,
        out StarkPackageTypedTemplateExpressionManifest? condition)
    {
        condition = null;

        if (forStatement.forCondition() is not { } forConditionClause)
        {
            return true;
        }

        return TryBuildPublishedTypedTemplateExpression(
            module,
            forConditionClause.expression(),
            namedTypes,
            literalsByLocation,
            conversionsByLocation,
            objectCreationOrdinals,
            enumConstructorOrdinals,
            enumCallOrdinals,
            enumValueOrdinals,
            functionAddressOrdinals,
            directCallOrdinals,
            dynamicStorageOperationOrdinals,
            memberCallOrdinals,
            fieldAccessOrdinals,
            out condition!);
    }

    private static bool TryBuildPublishedTypedTemplateAssignmentStatementList(
        LoadedModuleDocument module,
        IReadOnlyList<StarkParser.ExpressionContext> expressions,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, LiteralTypingRecord> literalsByLocation,
        IReadOnlyDictionary<string, ConversionTypingRecord> conversionsByLocation,
        IReadOnlyDictionary<StarkParser.ObjectCreationExpressionContext, PublishedTypedTemplateObjectCreationLookup> objectCreationOrdinals,
        IReadOnlyDictionary<StarkParser.EnumConstructorExpressionContext, int> enumConstructorOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> enumCallOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> enumValueOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> functionAddressOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> directCallOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> dynamicStorageOperationOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> memberCallOrdinals,
        IReadOnlyDictionary<StarkParser.PostfixPartContext, int> fieldAccessOrdinals,
        out IReadOnlyList<StarkPackageTypedTemplateStatementManifest> assignmentStatements)
    {
        var builtStatements = new List<StarkPackageTypedTemplateStatementManifest>(expressions.Count);
        foreach (var expression in expressions)
        {
            if (expression.assignmentExpression() is not { } assignmentExpression
                || assignmentExpression.assignmentOperator() is null
                || !TryBuildPublishedTypedTemplateAssignmentTarget(
                    module,
                    assignmentExpression.unaryExpression(),
                    namedTypes,
                    literalsByLocation,
                    conversionsByLocation,
                    objectCreationOrdinals,
                    enumConstructorOrdinals,
                    enumCallOrdinals,
                    enumValueOrdinals,
                    functionAddressOrdinals,
                    directCallOrdinals,
                    dynamicStorageOperationOrdinals,
                    memberCallOrdinals,
                    fieldAccessOrdinals,
                    out var assignmentTargetName,
                    out var assignmentTarget)
                || !TryBuildPublishedTypedTemplateAssignmentExpression(
                    module,
                    assignmentExpression.assignmentExpression(),
                    namedTypes,
                    literalsByLocation,
                    conversionsByLocation,
                    objectCreationOrdinals,
                    enumConstructorOrdinals,
                    enumCallOrdinals,
                    enumValueOrdinals,
                    functionAddressOrdinals,
                    directCallOrdinals,
                    dynamicStorageOperationOrdinals,
                    memberCallOrdinals,
                    fieldAccessOrdinals,
                    out var assignmentValue))
            {
                assignmentStatements = [];
                return false;
            }

            builtStatements.Add(new StarkPackageTypedTemplateStatementManifest(
                Kind: "assignment",
                Expression: assignmentValue,
                Name: assignmentTargetName,
                AssignmentOperator: assignmentExpression.assignmentOperator().GetText(),
                TargetExpression: assignmentTarget));
        }

        assignmentStatements = builtStatements;
        return true;
    }

    private static bool TryBuildPublishedTypedTemplateExpression(
        LoadedModuleDocument module,
        StarkParser.ExpressionContext expression,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, LiteralTypingRecord> literalsByLocation,
        IReadOnlyDictionary<string, ConversionTypingRecord> conversionsByLocation,
        IReadOnlyDictionary<StarkParser.ObjectCreationExpressionContext, PublishedTypedTemplateObjectCreationLookup> objectCreationOrdinals,
        IReadOnlyDictionary<StarkParser.EnumConstructorExpressionContext, int> enumConstructorOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> enumCallOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> enumValueOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> functionAddressOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> directCallOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> dynamicStorageOperationOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> memberCallOrdinals,
        IReadOnlyDictionary<StarkParser.PostfixPartContext, int> fieldAccessOrdinals,
        out StarkPackageTypedTemplateExpressionManifest publishedExpression)
    {
        publishedExpression = null!;

        if (expression.assignmentExpression() is not { } assignmentExpression)
        {
            return false;
        }

        return TryBuildPublishedTypedTemplateAssignmentExpression(
            module,
            assignmentExpression,
            namedTypes,
            literalsByLocation,
            conversionsByLocation,
            objectCreationOrdinals,
            enumConstructorOrdinals,
            enumCallOrdinals,
            enumValueOrdinals,
            functionAddressOrdinals,
            directCallOrdinals,
            dynamicStorageOperationOrdinals,
            memberCallOrdinals,
            fieldAccessOrdinals,
            out publishedExpression);
    }

    private static bool TryBuildPublishedTypedTemplateAssignmentExpression(
        LoadedModuleDocument module,
        StarkParser.AssignmentExpressionContext assignmentExpression,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, LiteralTypingRecord> literalsByLocation,
        IReadOnlyDictionary<string, ConversionTypingRecord> conversionsByLocation,
        IReadOnlyDictionary<StarkParser.ObjectCreationExpressionContext, PublishedTypedTemplateObjectCreationLookup> objectCreationOrdinals,
        IReadOnlyDictionary<StarkParser.EnumConstructorExpressionContext, int> enumConstructorOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> enumCallOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> enumValueOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> functionAddressOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> directCallOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> dynamicStorageOperationOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> memberCallOrdinals,
        IReadOnlyDictionary<StarkParser.PostfixPartContext, int> fieldAccessOrdinals,
        out StarkPackageTypedTemplateExpressionManifest publishedExpression)
    {
        publishedExpression = null!;

        if (assignmentExpression.INIT() is not null
            && assignmentExpression.ASSIGN() is not null
            && assignmentExpression.assignmentOperator() is null)
        {
            if (assignmentExpression.unaryExpression() is not { } initAssignmentTargetUnary
                || !TryBuildPublishedTypedTemplateAssignmentTarget(
                    module,
                    initAssignmentTargetUnary,
                    namedTypes,
                    literalsByLocation,
                    conversionsByLocation,
                    objectCreationOrdinals,
                    enumConstructorOrdinals,
                    enumCallOrdinals,
                    enumValueOrdinals,
                    functionAddressOrdinals,
                    directCallOrdinals,
                    dynamicStorageOperationOrdinals,
                    memberCallOrdinals,
                    fieldAccessOrdinals,
                    out var initAssignmentTargetName,
                    out var initAssignmentTarget)
                || !TryBuildPublishedTypedTemplateAssignmentExpression(
                    module,
                    assignmentExpression.assignmentExpression(),
                    namedTypes,
                    literalsByLocation,
                    conversionsByLocation,
                    objectCreationOrdinals,
                    enumConstructorOrdinals,
                    enumCallOrdinals,
                    enumValueOrdinals,
                    functionAddressOrdinals,
                    directCallOrdinals,
                    dynamicStorageOperationOrdinals,
                    memberCallOrdinals,
                    fieldAccessOrdinals,
                    out var initAssignmentValue))
            {
                return false;
            }

            publishedExpression = new StarkPackageTypedTemplateExpressionManifest(
                Kind: "assignment",
                Name: initAssignmentTargetName,
                AssignmentOperator: "init =",
                Arguments: [initAssignmentValue],
                TargetExpression: initAssignmentTarget);
            return true;
        }

        if (assignmentExpression.assignmentOperator() is { } assignmentOperator)
        {
            if (assignmentExpression.unaryExpression() is not { } assignmentTargetUnary
                || !TryBuildPublishedTypedTemplateAssignmentTarget(
                    module,
                    assignmentTargetUnary,
                    namedTypes,
                    literalsByLocation,
                    conversionsByLocation,
                    objectCreationOrdinals,
                    enumConstructorOrdinals,
                    enumCallOrdinals,
                    enumValueOrdinals,
                    functionAddressOrdinals,
                    directCallOrdinals,
                    dynamicStorageOperationOrdinals,
                    memberCallOrdinals,
                    fieldAccessOrdinals,
                    out var assignmentTargetName,
                    out var assignmentTarget)
                || !TryBuildPublishedTypedTemplateAssignmentExpression(
                    module,
                    assignmentExpression.assignmentExpression(),
                    namedTypes,
                    literalsByLocation,
                    conversionsByLocation,
                    objectCreationOrdinals,
                    enumConstructorOrdinals,
                    enumCallOrdinals,
                    enumValueOrdinals,
                    functionAddressOrdinals,
                    directCallOrdinals,
                    dynamicStorageOperationOrdinals,
                    memberCallOrdinals,
                    fieldAccessOrdinals,
                    out var assignmentValue))
            {
                return false;
            }

            publishedExpression = new StarkPackageTypedTemplateExpressionManifest(
                Kind: "assignment",
                Name: assignmentTargetName,
                AssignmentOperator: assignmentOperator.GetText(),
                Arguments: [assignmentValue],
                TargetExpression: assignmentTarget);
            return true;
        }

        if (assignmentExpression.conditionalExpression() is not { } conditionalExpression)
        {
            return false;
        }

        if (conditionalExpression.expression().Length == 2)
        {
            if (!TryBuildPublishedTypedTemplateConditionExpression(
                    module,
                    conditionalExpression.logicalOrExpression(),
                    namedTypes,
                    literalsByLocation,
                    conversionsByLocation,
                    objectCreationOrdinals,
                    enumConstructorOrdinals,
                    enumCallOrdinals,
                    enumValueOrdinals,
                    functionAddressOrdinals,
                    directCallOrdinals,
                    dynamicStorageOperationOrdinals,
                    memberCallOrdinals,
                    fieldAccessOrdinals,
                    out var condition)
                || !TryBuildPublishedTypedTemplateExpression(
                    module,
                    conditionalExpression.expression(0),
                    namedTypes,
                    literalsByLocation,
                    conversionsByLocation,
                    objectCreationOrdinals,
                    enumConstructorOrdinals,
                    enumCallOrdinals,
                    enumValueOrdinals,
                    functionAddressOrdinals,
                    directCallOrdinals,
                    dynamicStorageOperationOrdinals,
                    memberCallOrdinals,
                    fieldAccessOrdinals,
                    out var whenTrue)
                || !TryBuildPublishedTypedTemplateExpression(
                    module,
                    conditionalExpression.expression(1),
                    namedTypes,
                    literalsByLocation,
                    conversionsByLocation,
                    objectCreationOrdinals,
                    enumConstructorOrdinals,
                    enumCallOrdinals,
                    enumValueOrdinals,
                    functionAddressOrdinals,
                    directCallOrdinals,
                    dynamicStorageOperationOrdinals,
                    memberCallOrdinals,
                    fieldAccessOrdinals,
                    out var whenFalse))
            {
                return false;
            }

            publishedExpression = new StarkPackageTypedTemplateExpressionManifest(
                Kind: "conditional",
                Arguments: [condition, whenTrue, whenFalse]);
            return true;
        }

        return TryBuildPublishedTypedTemplateLogicalOrExpression(
            module,
            conditionalExpression.logicalOrExpression(),
            namedTypes,
            literalsByLocation,
            conversionsByLocation,
            objectCreationOrdinals,
            enumConstructorOrdinals,
            enumCallOrdinals,
            enumValueOrdinals,
            functionAddressOrdinals,
            directCallOrdinals,
            dynamicStorageOperationOrdinals,
            memberCallOrdinals,
            fieldAccessOrdinals,
            out publishedExpression);
    }

    private static bool TryBuildPublishedTypedTemplateVariableInitializer(
        LoadedModuleDocument module,
        StarkParser.VariableInitializerContext variableInitializer,
        StarkTypeSymbol targetType,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, LiteralTypingRecord> literalsByLocation,
        IReadOnlyDictionary<string, ConversionTypingRecord> conversionsByLocation,
        IReadOnlyDictionary<StarkParser.ObjectCreationExpressionContext, PublishedTypedTemplateObjectCreationLookup> objectCreationOrdinals,
        IReadOnlyDictionary<StarkParser.EnumConstructorExpressionContext, int> enumConstructorOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> enumCallOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> enumValueOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> functionAddressOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> directCallOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> dynamicStorageOperationOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> memberCallOrdinals,
        IReadOnlyDictionary<StarkParser.PostfixPartContext, int> fieldAccessOrdinals,
        out StarkPackageTypedTemplateExpressionManifest publishedExpression)
    {
        publishedExpression = null!;

        if (variableInitializer.expression() is { } expression)
        {
            if (IsTextBufferType(targetType)
                && TryBuildPublishedTypedTemplateTextInitializer(
                    module,
                    expression,
                    namedTypes,
                    literalsByLocation,
                    conversionsByLocation,
                    objectCreationOrdinals,
                    enumConstructorOrdinals,
                    enumCallOrdinals,
                    enumValueOrdinals,
                    functionAddressOrdinals,
                    directCallOrdinals,
                    dynamicStorageOperationOrdinals,
                    memberCallOrdinals,
                    fieldAccessOrdinals,
                    out publishedExpression))
            {
                return true;
            }

            return TryBuildPublishedTypedTemplateExpression(
                module,
                expression,
                namedTypes,
                literalsByLocation,
                conversionsByLocation,
                objectCreationOrdinals,
                enumConstructorOrdinals,
                enumCallOrdinals,
                enumValueOrdinals,
                functionAddressOrdinals,
                directCallOrdinals,
                dynamicStorageOperationOrdinals,
                memberCallOrdinals,
                fieldAccessOrdinals,
                out publishedExpression);
        }

        if (variableInitializer.objectInitializer() is { } objectInitializer)
        {
            return TryBuildPublishedTypedTemplateObjectInitializerExpression(
                module,
                objectInitializer,
                targetType,
                namedTypes,
                literalsByLocation,
                conversionsByLocation,
                objectCreationOrdinals,
                enumConstructorOrdinals,
                enumCallOrdinals,
                enumValueOrdinals,
                functionAddressOrdinals,
                directCallOrdinals,
                dynamicStorageOperationOrdinals,
                memberCallOrdinals,
                fieldAccessOrdinals,
                out publishedExpression);
        }

        if (variableInitializer.arrayInitializer() is { } arrayInitializer)
        {
            if (targetType.Kind != StarkTypeKind.FixedArray
                || targetType.ElementType is null
                || targetType.FixedLength is not int)
            {
                return false;
            }

            var arguments = new List<StarkPackageTypedTemplateExpressionManifest>(arrayInitializer.variableInitializer().Length);
            foreach (var elementInitializer in arrayInitializer.variableInitializer())
            {
                if (!TryBuildPublishedTypedTemplateVariableInitializer(
                        module,
                        elementInitializer,
                        targetType.ElementType,
                        namedTypes,
                        literalsByLocation,
                        conversionsByLocation,
                        objectCreationOrdinals,
                        enumConstructorOrdinals,
                        enumCallOrdinals,
                        enumValueOrdinals,
                        functionAddressOrdinals,
                        directCallOrdinals,
                        dynamicStorageOperationOrdinals,
                        memberCallOrdinals,
                        fieldAccessOrdinals,
                        out var publishedElement))
                {
                    return false;
                }

                arguments.Add(publishedElement);
            }

            publishedExpression = new StarkPackageTypedTemplateExpressionManifest(
                Kind: "array-initializer",
                Arguments: arguments,
                Type: BuildPublishedAbiTypeReference(targetType, module));
            return true;
        }

        return false;
    }

    private static bool TryBuildPublishedTypedTemplateTextInitializer(
        LoadedModuleDocument module,
        StarkParser.ExpressionContext expression,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, LiteralTypingRecord> literalsByLocation,
        IReadOnlyDictionary<string, ConversionTypingRecord> conversionsByLocation,
        IReadOnlyDictionary<StarkParser.ObjectCreationExpressionContext, PublishedTypedTemplateObjectCreationLookup> objectCreationOrdinals,
        IReadOnlyDictionary<StarkParser.EnumConstructorExpressionContext, int> enumConstructorOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> enumCallOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> enumValueOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> functionAddressOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> directCallOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> dynamicStorageOperationOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> memberCallOrdinals,
        IReadOnlyDictionary<StarkParser.PostfixPartContext, int> fieldAccessOrdinals,
        out StarkPackageTypedTemplateExpressionManifest publishedExpression)
    {
        publishedExpression = null!;

        if (TryGetStandaloneInterpolatedTextLiteral(expression) is { } interpolatedLiteral
            && interpolatedLiteral.StringLiteral() is { } interpolatedString
            && InterpolatedText.TryParse(interpolatedString.GetText(), out var segments, out _))
        {
            var arguments = new List<StarkPackageTypedTemplateExpressionManifest>();
            foreach (var hole in segments.OfType<InterpolatedTextHoleSegment>())
            {
                if (!TryBuildPublishedTypedTemplateExpression(
                        module,
                        hole.Expression,
                        namedTypes,
                        literalsByLocation,
                        conversionsByLocation,
                        objectCreationOrdinals,
                        enumConstructorOrdinals,
                        enumCallOrdinals,
                        enumValueOrdinals,
                        functionAddressOrdinals,
                        directCallOrdinals,
                        dynamicStorageOperationOrdinals,
                        memberCallOrdinals,
                        fieldAccessOrdinals,
                        out var argument))
                {
                    return false;
                }

                arguments.Add(argument);
            }

            publishedExpression = new StarkPackageTypedTemplateExpressionManifest(
                Kind: "text-interpolation",
                LiteralText: interpolatedString.GetText(),
                Arguments: arguments);
            return true;
        }

        if (TryGetStandaloneAdditiveExpression(expression) is { } additive)
        {
            var operands = additive.multiplicativeExpression();
            var operators = ExtractOperators<StarkParser.MultiplicativeExpressionContext>(additive);
            if (operands.Length < 2 || operators.Any(static op => op != "+"))
            {
                return false;
            }

            var arguments = new List<StarkPackageTypedTemplateExpressionManifest>(operands.Length);
            foreach (var operand in operands)
            {
                if (!TryBuildPublishedTypedTemplateMultiplicativeExpression(
                        module,
                        operand,
                        namedTypes,
                        literalsByLocation,
                        conversionsByLocation,
                        objectCreationOrdinals,
                        enumConstructorOrdinals,
                        enumCallOrdinals,
                        enumValueOrdinals,
                        functionAddressOrdinals,
                        directCallOrdinals,
                        dynamicStorageOperationOrdinals,
                        memberCallOrdinals,
                        fieldAccessOrdinals,
                        out var argument))
                {
                    return false;
                }

                arguments.Add(argument);
            }

            publishedExpression = new StarkPackageTypedTemplateExpressionManifest(
                Kind: "text-build",
                Arguments: arguments);
            return true;
        }

        return false;
    }

    private static bool TryBuildPublishedTypedTemplateObjectInitializerExpression(
        LoadedModuleDocument module,
        StarkParser.ObjectInitializerContext objectInitializer,
        StarkTypeSymbol targetType,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, LiteralTypingRecord> literalsByLocation,
        IReadOnlyDictionary<string, ConversionTypingRecord> conversionsByLocation,
        IReadOnlyDictionary<StarkParser.ObjectCreationExpressionContext, PublishedTypedTemplateObjectCreationLookup> objectCreationOrdinals,
        IReadOnlyDictionary<StarkParser.EnumConstructorExpressionContext, int> enumConstructorOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> enumCallOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> enumValueOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> functionAddressOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> directCallOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> dynamicStorageOperationOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> memberCallOrdinals,
        IReadOnlyDictionary<StarkParser.PostfixPartContext, int> fieldAccessOrdinals,
        out StarkPackageTypedTemplateExpressionManifest publishedExpression)
    {
        publishedExpression = null!;

        if (!TryResolvePublishedTypedTemplateNamedType(targetType, namedTypes, out var namedType, out var substitution))
        {
            return false;
        }

        var arguments = new List<StarkPackageTypedTemplateExpressionManifest>(objectInitializer.memberInitializer().Length);
        var memberNames = new List<string>(objectInitializer.memberInitializer().Length);
        foreach (var memberInitializer in objectInitializer.memberInitializer())
        {
            var memberName = memberInitializer.Identifier().GetText();
            if (!namedType.TryGetField(memberName, out var field, out _))
            {
                return false;
            }

            var fieldType = substitution.Count == 0
                ? field.Type
                : FunctionOverloadFacts.SubstituteType(field.Type, substitution);
            if (memberInitializer.variableInitializer() is not { } nestedInitializer
                || !TryBuildPublishedTypedTemplateVariableInitializer(
                    module,
                    nestedInitializer,
                    fieldType,
                    namedTypes,
                    literalsByLocation,
                    conversionsByLocation,
                    objectCreationOrdinals,
                    enumConstructorOrdinals,
                    enumCallOrdinals,
                    enumValueOrdinals,
                    functionAddressOrdinals,
                    directCallOrdinals,
                    dynamicStorageOperationOrdinals,
                    memberCallOrdinals,
                    fieldAccessOrdinals,
                    out var publishedMemberValue))
            {
                return false;
            }

            memberNames.Add(memberName);
            arguments.Add(publishedMemberValue);
        }

        publishedExpression = new StarkPackageTypedTemplateExpressionManifest(
            Kind: "object-initializer",
            Arguments: arguments,
            MemberNames: memberNames,
            Type: BuildPublishedAbiTypeReference(targetType, module));
        return true;
    }

    private static bool TryResolvePublishedTypedTemplateNamedType(
        StarkTypeSymbol targetType,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        out NamedTypeSymbol namedType,
        out IReadOnlyDictionary<string, StarkTypeSymbol> substitution)
    {
        namedType = null!;
        substitution = EmptyTypeSubstitution;

        if (targetType.Kind != StarkTypeKind.Named
            || targetType.NamedType is null)
        {
            return false;
        }

        if (!namedTypes.TryGetValue(targetType.NamedType, out namedType!))
        {
            var baseName = StarkTypeSymbols.GetGenericBaseName(targetType.NamedType);
            if (!namedTypes.TryGetValue(baseName, out namedType!))
            {
                return false;
            }
        }

        if (targetType.TypeArguments is not { Count: > 0 } || namedType.GenericParams.Count == 0)
        {
            substitution = EmptyTypeSubstitution;
            return true;
        }

        if (namedType.GenericParams.Count != targetType.TypeArguments.Count)
        {
            return false;
        }

        var builtSubstitution = new Dictionary<string, StarkTypeSymbol>(namedType.GenericParams.Count, StringComparer.Ordinal);
        for (var index = 0; index < namedType.GenericParams.Count; index++)
        {
            builtSubstitution[namedType.GenericParams[index]] = targetType.TypeArguments[index];
        }

        substitution = builtSubstitution;
        return true;
    }

    private static bool TryBuildPublishedTypedTemplateAssignmentTarget(
        LoadedModuleDocument module,
        StarkParser.UnaryExpressionContext unaryExpression,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, LiteralTypingRecord> literalsByLocation,
        IReadOnlyDictionary<string, ConversionTypingRecord> conversionsByLocation,
        IReadOnlyDictionary<StarkParser.ObjectCreationExpressionContext, PublishedTypedTemplateObjectCreationLookup> objectCreationOrdinals,
        IReadOnlyDictionary<StarkParser.EnumConstructorExpressionContext, int> enumConstructorOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> enumCallOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> enumValueOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> functionAddressOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> directCallOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> dynamicStorageOperationOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> memberCallOrdinals,
        IReadOnlyDictionary<StarkParser.PostfixPartContext, int> fieldAccessOrdinals,
        out string? targetName,
        out StarkPackageTypedTemplateExpressionManifest targetExpression)
    {
        targetName = null;
        targetExpression = null!;

        if (!TryBuildPublishedTypedTemplateUnaryExpression(
                module,
                unaryExpression,
                namedTypes,
                literalsByLocation,
                conversionsByLocation,
                objectCreationOrdinals,
                enumConstructorOrdinals,
                enumCallOrdinals,
                enumValueOrdinals,
                functionAddressOrdinals,
                directCallOrdinals,
                dynamicStorageOperationOrdinals,
                memberCallOrdinals,
                fieldAccessOrdinals,
                out var builtTargetExpression)
            || !CanPublishTypedTemplateAssignmentTarget(builtTargetExpression))
        {
            return false;
        }

        targetExpression = builtTargetExpression;
        targetName = string.Equals(builtTargetExpression.Kind, "name", StringComparison.Ordinal)
            ? builtTargetExpression.Name
            : null;
        return true;
    }

    private static bool CanPublishTypedTemplateAssignmentTarget(StarkPackageTypedTemplateExpressionManifest expression)
    {
        return expression.Kind switch
        {
            "name" => !string.IsNullOrWhiteSpace(expression.Name),
            "unary" => string.Equals(expression.Name, "*", StringComparison.Ordinal)
                && expression.Arguments?.Count == 1,
            "field-access" => expression.Arguments?.Count == 1
                && CanPublishTypedTemplateAssignmentTarget(expression.Arguments[0]),
            "index-access" => expression.Arguments?.Count >= 2
                && CanPublishTypedTemplateAssignmentTarget(expression.Arguments[0]),
            _ => false
        };
    }

    private delegate bool TryBuildPublishedTypedTemplateOperand<in TOperandContext>(
        LoadedModuleDocument module,
        TOperandContext operand,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, LiteralTypingRecord> literalsByLocation,
        IReadOnlyDictionary<string, ConversionTypingRecord> conversionsByLocation,
        IReadOnlyDictionary<StarkParser.ObjectCreationExpressionContext, PublishedTypedTemplateObjectCreationLookup> objectCreationOrdinals,
        IReadOnlyDictionary<StarkParser.EnumConstructorExpressionContext, int> enumConstructorOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> enumCallOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> enumValueOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> functionAddressOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> directCallOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> dynamicStorageOperationOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> memberCallOrdinals,
        IReadOnlyDictionary<StarkParser.PostfixPartContext, int> fieldAccessOrdinals,
        out StarkPackageTypedTemplateExpressionManifest publishedExpression)
        where TOperandContext : ParserRuleContext;

    private static bool TryBuildPublishedTypedTemplatePostfixExpression(
        LoadedModuleDocument module,
        StarkParser.PostfixExpressionContext postfixExpression,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, LiteralTypingRecord> literalsByLocation,
        IReadOnlyDictionary<string, ConversionTypingRecord> conversionsByLocation,
        IReadOnlyDictionary<StarkParser.ObjectCreationExpressionContext, PublishedTypedTemplateObjectCreationLookup> objectCreationOrdinals,
        IReadOnlyDictionary<StarkParser.EnumConstructorExpressionContext, int> enumConstructorOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> enumCallOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> enumValueOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> functionAddressOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> directCallOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> dynamicStorageOperationOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> memberCallOrdinals,
        IReadOnlyDictionary<StarkParser.PostfixPartContext, int> fieldAccessOrdinals,
        out StarkPackageTypedTemplateExpressionManifest publishedExpression)
    {
        publishedExpression = null!;
        var primaryExpression = postfixExpression.primaryExpression();
        var postfixParts = postfixExpression.postfixPart();
        StarkPackageTypedTemplateExpressionManifest? baseExpression = null;

        if (primaryExpression?.objectCreationExpression() is { } objectCreationExpression
            && objectCreationOrdinals.TryGetValue(objectCreationExpression, out var objectCreationLookup)
            && TryBuildPublishedTypedTemplateObjectCreationArguments(
                module,
                objectCreationExpression,
                objectCreationLookup,
                namedTypes,
                literalsByLocation,
                conversionsByLocation,
                objectCreationOrdinals,
                enumConstructorOrdinals,
                enumCallOrdinals,
                enumValueOrdinals,
                functionAddressOrdinals,
                directCallOrdinals,
                dynamicStorageOperationOrdinals,
                memberCallOrdinals,
                fieldAccessOrdinals,
                out var objectCreationArguments))
        {
            baseExpression = new StarkPackageTypedTemplateExpressionManifest(
                Kind: "object-creation",
                Ordinal: objectCreationLookup.Ordinal,
                Arguments: objectCreationArguments);
        }

        if (baseExpression is null
            && primaryExpression?.enumConstructorExpression() is { } enumConstructorExpression
            && enumConstructorOrdinals.TryGetValue(enumConstructorExpression, out var enumConstructorOrdinal))
        {
            var arguments = new List<StarkPackageTypedTemplateExpressionManifest>(
                enumConstructorExpression.enumConstructorInitializer().enumConstructorMember().Length);
            foreach (var member in enumConstructorExpression.enumConstructorInitializer().enumConstructorMember())
            {
                if (!TryBuildPublishedTypedTemplateExpression(module, member.expression(), namedTypes, literalsByLocation, conversionsByLocation, objectCreationOrdinals, enumConstructorOrdinals, enumCallOrdinals, enumValueOrdinals, functionAddressOrdinals, directCallOrdinals, dynamicStorageOperationOrdinals, memberCallOrdinals, fieldAccessOrdinals, out var publishedArgument))
                {
                    return false;
                }

                arguments.Add(publishedArgument);
            }

            baseExpression = new StarkPackageTypedTemplateExpressionManifest(
                Kind: "enum-constructor",
                Ordinal: enumConstructorOrdinal,
                Arguments: arguments);
        }

        if (postfixParts.Length == 0
            && primaryExpression?.literal() is { } literal
            && literalsByLocation.TryGetValue(
                BuildTemplateLiteralLookupKey(literal.Start.Line, literal.Start.Column + 1),
                out var literalRecord))
        {
            publishedExpression = new StarkPackageTypedTemplateExpressionManifest(
                Kind: "literal",
                LiteralText: literal.GetText(),
                Type: BuildPublishedAbiTypeReference(literalRecord.Type, module));
            return true;
        }

        if (postfixParts.Length == 0
            && primaryExpression is not null
            && TryBuildPublishedTypedTemplateTypeLayoutExpression(module, primaryExpression, out publishedExpression))
        {
            return true;
        }

        if (postfixParts.Length == 0
            && primaryExpression is not null
            && functionAddressOrdinals.TryGetValue(primaryExpression, out var functionAddressOrdinal))
        {
            publishedExpression = new StarkPackageTypedTemplateExpressionManifest(
                Kind: "function-address",
                Ordinal: functionAddressOrdinal);
            return true;
        }

        if (primaryExpression is not null
            && enumValueOrdinals.TryGetValue(primaryExpression, out var enumValueOrdinal)
            && (postfixParts.Length == 0
                || (postfixParts.Length == 1 && postfixParts[0].Identifier() is not null)))
        {
            publishedExpression = new StarkPackageTypedTemplateExpressionManifest(
                Kind: "enum-value",
                Ordinal: enumValueOrdinal);
            return true;
        }

        if (postfixParts.Length == 1
            && postfixParts[0].argumentList() is { } enumArgumentList
            && enumCallOrdinals.TryGetValue(enumArgumentList, out var enumCallOrdinal))
        {
            var arguments = new List<StarkPackageTypedTemplateExpressionManifest>(enumArgumentList.argument().Length);
            foreach (var argument in enumArgumentList.argument())
            {
                if (!TryBuildPublishedTypedTemplateExpression(module, argument.expression(), namedTypes, literalsByLocation, conversionsByLocation, objectCreationOrdinals, enumConstructorOrdinals, enumCallOrdinals, enumValueOrdinals, functionAddressOrdinals, directCallOrdinals, dynamicStorageOperationOrdinals, memberCallOrdinals, fieldAccessOrdinals, out var publishedArgument))
                {
                    return false;
                }

                arguments.Add(publishedArgument);
            }

            publishedExpression = new StarkPackageTypedTemplateExpressionManifest(
                Kind: "enum-call",
                Ordinal: enumCallOrdinal,
                Arguments: arguments);
            return true;
        }

        if (baseExpression is null)
        {
            if (primaryExpression?.expression() is { } groupedExpression
                && TryBuildPublishedTypedTemplateExpression(
                    module,
                    groupedExpression,
                    namedTypes,
                    literalsByLocation,
                    conversionsByLocation,
                    objectCreationOrdinals,
                    enumConstructorOrdinals,
                    enumCallOrdinals,
                    enumValueOrdinals,
                    functionAddressOrdinals,
                    directCallOrdinals,
                    dynamicStorageOperationOrdinals,
                    memberCallOrdinals,
                    fieldAccessOrdinals,
                    out var groupedPublishedExpression))
            {
                baseExpression = groupedPublishedExpression;
            }
        }

        if (baseExpression is null)
        {
            var name = primaryExpression?.Identifier()?.GetText()
                ?? primaryExpression?.qualifiedName()?.GetText();
            if (name is null)
            {
                return false;
            }

            baseExpression = new StarkPackageTypedTemplateExpressionManifest(
                Kind: "name",
                Name: name);
        }

        if (postfixParts.Length == 0)
        {
            publishedExpression = baseExpression;
            return true;
        }

        return TryBuildPublishedTypedTemplatePostfixChain(
            module,
            baseExpression,
            postfixParts,
            namedTypes,
            literalsByLocation,
            conversionsByLocation,
            objectCreationOrdinals,
            enumConstructorOrdinals,
            enumCallOrdinals,
            enumValueOrdinals,
            functionAddressOrdinals,
            directCallOrdinals,
            dynamicStorageOperationOrdinals,
            memberCallOrdinals,
            fieldAccessOrdinals,
            out publishedExpression);
    }

    private static bool TryBuildPublishedTypedTemplateObjectCreationArguments(
        LoadedModuleDocument module,
        StarkParser.ObjectCreationExpressionContext objectCreationExpression,
        PublishedTypedTemplateObjectCreationLookup objectCreationLookup,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, LiteralTypingRecord> literalsByLocation,
        IReadOnlyDictionary<string, ConversionTypingRecord> conversionsByLocation,
        IReadOnlyDictionary<StarkParser.ObjectCreationExpressionContext, PublishedTypedTemplateObjectCreationLookup> objectCreationOrdinals,
        IReadOnlyDictionary<StarkParser.EnumConstructorExpressionContext, int> enumConstructorOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> enumCallOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> enumValueOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> functionAddressOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> directCallOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> dynamicStorageOperationOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> memberCallOrdinals,
        IReadOnlyDictionary<StarkParser.PostfixPartContext, int> fieldAccessOrdinals,
        out List<StarkPackageTypedTemplateExpressionManifest> arguments)
    {
        arguments = [];

        if (objectCreationExpression.argumentList() is { } objectCreationArgumentList)
        {
            foreach (var argument in objectCreationArgumentList.argument())
            {
                if (!TryBuildPublishedTypedTemplateExpression(
                        module,
                        argument.expression(),
                        namedTypes,
                        literalsByLocation,
                        conversionsByLocation,
                        objectCreationOrdinals,
                        enumConstructorOrdinals,
                        enumCallOrdinals,
                        enumValueOrdinals,
                        functionAddressOrdinals,
                        directCallOrdinals,
                        dynamicStorageOperationOrdinals,
                        memberCallOrdinals,
                        fieldAccessOrdinals,
                        out var publishedArgument))
                {
                    return false;
                }

                arguments.Add(publishedArgument);
            }
        }

        if (objectCreationExpression.objectInitializer() is not { } objectInitializer)
        {
            return true;
        }

        if (objectCreationLookup.Record is not { } objectCreationRecord
            || objectCreationRecord.Members.Count != objectInitializer.memberInitializer().Length)
        {
            return false;
        }

        foreach (var memberInitializer in objectInitializer.memberInitializer())
        {
            var memberName = memberInitializer.Identifier().GetText();
            var member = objectCreationRecord.Members.FirstOrDefault(candidate =>
                string.Equals(candidate.FieldName, memberName, StringComparison.Ordinal));
            if (member is null
                || memberInitializer.variableInitializer() is not { } variableInitializer
                || !TryBuildPublishedTypedTemplateVariableInitializer(
                    module,
                    variableInitializer,
                    member.FieldType,
                    namedTypes,
                    literalsByLocation,
                    conversionsByLocation,
                    objectCreationOrdinals,
                    enumConstructorOrdinals,
                    enumCallOrdinals,
                    enumValueOrdinals,
                    functionAddressOrdinals,
                    directCallOrdinals,
                    dynamicStorageOperationOrdinals,
                    memberCallOrdinals,
                    fieldAccessOrdinals,
                    out var publishedArgument))
            {
                return false;
            }

            arguments.Add(publishedArgument);
        }

        return true;
    }

    private static bool TryBuildPublishedTypedTemplatePostfixChain(
        LoadedModuleDocument module,
        StarkPackageTypedTemplateExpressionManifest baseExpression,
        IReadOnlyList<StarkParser.PostfixPartContext> postfixParts,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, LiteralTypingRecord> literalsByLocation,
        IReadOnlyDictionary<string, ConversionTypingRecord> conversionsByLocation,
        IReadOnlyDictionary<StarkParser.ObjectCreationExpressionContext, PublishedTypedTemplateObjectCreationLookup> objectCreationOrdinals,
        IReadOnlyDictionary<StarkParser.EnumConstructorExpressionContext, int> enumConstructorOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> enumCallOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> enumValueOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> functionAddressOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> directCallOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> dynamicStorageOperationOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> memberCallOrdinals,
        IReadOnlyDictionary<StarkParser.PostfixPartContext, int> fieldAccessOrdinals,
        out StarkPackageTypedTemplateExpressionManifest publishedExpression)
    {
        publishedExpression = baseExpression;

        for (var index = 0; index < postfixParts.Count; index++)
        {
            var postfixPart = postfixParts[index];

            if (postfixPart.LBRACK() is not null)
            {
                var indexExpressions = postfixPart.expressionList()?.expression() ?? [];
                var arguments = new List<StarkPackageTypedTemplateExpressionManifest>(indexExpressions.Length + 1)
                {
                    publishedExpression
                };
                foreach (var indexExpression in indexExpressions)
                {
                    if (!TryBuildPublishedTypedTemplateExpression(
                        module,
                        indexExpression,
                        namedTypes,
                        literalsByLocation,
                        conversionsByLocation,
                        objectCreationOrdinals,
                        enumConstructorOrdinals,
                        enumCallOrdinals,
                        enumValueOrdinals,
                        functionAddressOrdinals,
                        directCallOrdinals,
                        dynamicStorageOperationOrdinals,
                        memberCallOrdinals,
                        fieldAccessOrdinals,
                        out var publishedIndex))
                    {
                        return false;
                    }

                    arguments.Add(publishedIndex);
                }

                publishedExpression = new StarkPackageTypedTemplateExpressionManifest(
                    Kind: "index-access",
                    Arguments: arguments);
                continue;
            }

            if (postfixPart.argumentList() is { } directArgumentList)
            {
                var arguments = new List<StarkPackageTypedTemplateExpressionManifest>(directArgumentList.argument().Length);
                foreach (var argument in directArgumentList.argument())
                {
                    if (!TryBuildPublishedTypedTemplateExpression(module, argument.expression(), namedTypes, literalsByLocation, conversionsByLocation, objectCreationOrdinals, enumConstructorOrdinals, enumCallOrdinals, enumValueOrdinals, functionAddressOrdinals, directCallOrdinals, dynamicStorageOperationOrdinals, memberCallOrdinals, fieldAccessOrdinals, out var publishedArgument))
                    {
                        return false;
                    }

                    arguments.Add(publishedArgument);
                }

                if (publishedExpression.Kind == "name"
                    && directCallOrdinals.TryGetValue(directArgumentList, out var directCallOrdinal))
                {
                    publishedExpression = new StarkPackageTypedTemplateExpressionManifest(
                        Kind: "direct-call",
                        Ordinal: directCallOrdinal,
                        Arguments: arguments);
                    continue;
                }

                publishedExpression = new StarkPackageTypedTemplateExpressionManifest(
                    Kind: "closure-call",
                    Arguments: arguments.Prepend(publishedExpression).ToArray());
                continue;
            }

            if (postfixPart.Identifier() is not null)
            {
                if (enumValueOrdinals.TryGetValue(postfixPart, out var postfixEnumValueOrdinal))
                {
                    publishedExpression = new StarkPackageTypedTemplateExpressionManifest(
                        Kind: "enum-value",
                        Ordinal: postfixEnumValueOrdinal);
                    continue;
                }

                if (index + 1 < postfixParts.Count
                    && postfixParts[index + 1].argumentList() is { } chainedDirectArgumentList
                    && publishedExpression.Kind == "name"
                    && (directCallOrdinals.TryGetValue(chainedDirectArgumentList, out var directCallOrdinal)
                        || enumCallOrdinals.TryGetValue(chainedDirectArgumentList, out _)))
                {
                    var arguments = new List<StarkPackageTypedTemplateExpressionManifest>(chainedDirectArgumentList.argument().Length);
                    foreach (var argument in chainedDirectArgumentList.argument())
                    {
                        if (!TryBuildPublishedTypedTemplateExpression(module, argument.expression(), namedTypes, literalsByLocation, conversionsByLocation, objectCreationOrdinals, enumConstructorOrdinals, enumCallOrdinals, enumValueOrdinals, functionAddressOrdinals, directCallOrdinals, dynamicStorageOperationOrdinals, memberCallOrdinals, fieldAccessOrdinals, out var publishedArgument))
                        {
                            return false;
                        }

                        arguments.Add(publishedArgument);
                    }

                    publishedExpression = enumCallOrdinals.TryGetValue(chainedDirectArgumentList, out var enumCallOrdinal)
                        ? new StarkPackageTypedTemplateExpressionManifest(
                            Kind: "enum-call",
                            Ordinal: enumCallOrdinal,
                            Arguments: arguments)
                        : new StarkPackageTypedTemplateExpressionManifest(
                            Kind: "direct-call",
                            Ordinal: directCallOrdinal,
                            Arguments: arguments);
                    index += 1;
                    continue;
                }

                if (index + 1 < postfixParts.Count
                    && postfixParts[index + 1].argumentList() is { } dynamicOperationArgumentList
                    && dynamicStorageOperationOrdinals.TryGetValue(dynamicOperationArgumentList, out var dynamicStorageOperationOrdinal))
                {
                    var arguments = new List<StarkPackageTypedTemplateExpressionManifest>(dynamicOperationArgumentList.argument().Length + 1)
                    {
                        publishedExpression
                    };

                    foreach (var argument in dynamicOperationArgumentList.argument())
                    {
                        if (!TryBuildPublishedTypedTemplateExpression(module, argument.expression(), namedTypes, literalsByLocation, conversionsByLocation, objectCreationOrdinals, enumConstructorOrdinals, enumCallOrdinals, enumValueOrdinals, functionAddressOrdinals, directCallOrdinals, dynamicStorageOperationOrdinals, memberCallOrdinals, fieldAccessOrdinals, out var publishedArgument))
                        {
                            return false;
                        }

                        arguments.Add(publishedArgument);
                    }

                    publishedExpression = new StarkPackageTypedTemplateExpressionManifest(
                        Kind: "dynamic-storage-operation",
                        Ordinal: dynamicStorageOperationOrdinal,
                        Arguments: arguments);
                    index += 1;
                    continue;
                }

                if (index + 1 < postfixParts.Count
                    && postfixParts[index + 1].argumentList() is { } memberArgumentList
                    && memberCallOrdinals.TryGetValue(memberArgumentList, out var memberCallOrdinal))
                {
                    var arguments = new List<StarkPackageTypedTemplateExpressionManifest>(memberArgumentList.argument().Length + 1)
                    {
                        publishedExpression
                    };

                    foreach (var argument in memberArgumentList.argument())
                    {
                        if (!TryBuildPublishedTypedTemplateExpression(module, argument.expression(), namedTypes, literalsByLocation, conversionsByLocation, objectCreationOrdinals, enumConstructorOrdinals, enumCallOrdinals, enumValueOrdinals, functionAddressOrdinals, directCallOrdinals, dynamicStorageOperationOrdinals, memberCallOrdinals, fieldAccessOrdinals, out var publishedArgument))
                        {
                            return false;
                        }

                        arguments.Add(publishedArgument);
                    }

                    publishedExpression = new StarkPackageTypedTemplateExpressionManifest(
                        Kind: "member-call",
                        Ordinal: memberCallOrdinal,
                        Arguments: arguments);
                    index += 1;
                    continue;
                }

                if (!fieldAccessOrdinals.TryGetValue(postfixPart, out var fieldAccessOrdinal))
                {
                    return false;
                }

                publishedExpression = new StarkPackageTypedTemplateExpressionManifest(
                    Kind: "field-access",
                    Ordinal: fieldAccessOrdinal,
                    Arguments:
                    [
                        publishedExpression
                    ]);
                continue;
            }

            return false;
        }

        return true;
    }

    private static bool TryBuildPublishedTypedTemplateConditionExpression(
        LoadedModuleDocument module,
        StarkParser.LogicalOrExpressionContext logicalOrExpression,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, LiteralTypingRecord> literalsByLocation,
        IReadOnlyDictionary<string, ConversionTypingRecord> conversionsByLocation,
        IReadOnlyDictionary<StarkParser.ObjectCreationExpressionContext, PublishedTypedTemplateObjectCreationLookup> objectCreationOrdinals,
        IReadOnlyDictionary<StarkParser.EnumConstructorExpressionContext, int> enumConstructorOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> enumCallOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> enumValueOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> functionAddressOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> directCallOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> dynamicStorageOperationOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> memberCallOrdinals,
        IReadOnlyDictionary<StarkParser.PostfixPartContext, int> fieldAccessOrdinals,
        out StarkPackageTypedTemplateExpressionManifest publishedExpression)
    {
        return TryBuildPublishedTypedTemplateLogicalOrExpression(
            module,
            logicalOrExpression,
            namedTypes,
            literalsByLocation,
            conversionsByLocation,
            objectCreationOrdinals,
            enumConstructorOrdinals,
            enumCallOrdinals,
            enumValueOrdinals,
            functionAddressOrdinals,
            directCallOrdinals,
            dynamicStorageOperationOrdinals,
            memberCallOrdinals,
            fieldAccessOrdinals,
            out publishedExpression);
    }

    private static bool TryBuildPublishedTypedTemplateLogicalOrExpression(
        LoadedModuleDocument module,
        StarkParser.LogicalOrExpressionContext logicalOrExpression,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, LiteralTypingRecord> literalsByLocation,
        IReadOnlyDictionary<string, ConversionTypingRecord> conversionsByLocation,
        IReadOnlyDictionary<StarkParser.ObjectCreationExpressionContext, PublishedTypedTemplateObjectCreationLookup> objectCreationOrdinals,
        IReadOnlyDictionary<StarkParser.EnumConstructorExpressionContext, int> enumConstructorOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> enumCallOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> enumValueOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> functionAddressOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> directCallOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> dynamicStorageOperationOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> memberCallOrdinals,
        IReadOnlyDictionary<StarkParser.PostfixPartContext, int> fieldAccessOrdinals,
        out StarkPackageTypedTemplateExpressionManifest publishedExpression)
    {
        return TryBuildPublishedTypedTemplateBinaryChain(
            module,
            logicalOrExpression.logicalAndExpression(),
            ExtractOperators<StarkParser.LogicalAndExpressionContext>(logicalOrExpression),
            namedTypes,
            literalsByLocation,
            conversionsByLocation,
            objectCreationOrdinals,
            enumConstructorOrdinals,
            enumCallOrdinals,
            enumValueOrdinals,
            functionAddressOrdinals,
            directCallOrdinals,
            dynamicStorageOperationOrdinals,
            memberCallOrdinals,
            fieldAccessOrdinals,
            TryBuildPublishedTypedTemplateLogicalAndExpression,
            out publishedExpression);
    }

    private static bool TryBuildPublishedTypedTemplateLogicalAndExpression(
        LoadedModuleDocument module,
        StarkParser.LogicalAndExpressionContext logicalAndExpression,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, LiteralTypingRecord> literalsByLocation,
        IReadOnlyDictionary<string, ConversionTypingRecord> conversionsByLocation,
        IReadOnlyDictionary<StarkParser.ObjectCreationExpressionContext, PublishedTypedTemplateObjectCreationLookup> objectCreationOrdinals,
        IReadOnlyDictionary<StarkParser.EnumConstructorExpressionContext, int> enumConstructorOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> enumCallOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> enumValueOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> functionAddressOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> directCallOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> dynamicStorageOperationOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> memberCallOrdinals,
        IReadOnlyDictionary<StarkParser.PostfixPartContext, int> fieldAccessOrdinals,
        out StarkPackageTypedTemplateExpressionManifest publishedExpression)
    {
        return TryBuildPublishedTypedTemplateBinaryChain(
            module,
            logicalAndExpression.bitwiseOrExpression(),
            ExtractOperators<StarkParser.BitwiseOrExpressionContext>(logicalAndExpression),
            namedTypes,
            literalsByLocation,
            conversionsByLocation,
            objectCreationOrdinals,
            enumConstructorOrdinals,
            enumCallOrdinals,
            enumValueOrdinals,
            functionAddressOrdinals,
            directCallOrdinals,
            dynamicStorageOperationOrdinals,
            memberCallOrdinals,
            fieldAccessOrdinals,
            TryBuildPublishedTypedTemplateBitwiseOrExpression,
            out publishedExpression);
    }

    private static bool TryBuildPublishedTypedTemplateBitwiseOrExpression(
        LoadedModuleDocument module,
        StarkParser.BitwiseOrExpressionContext bitwiseOrExpression,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, LiteralTypingRecord> literalsByLocation,
        IReadOnlyDictionary<string, ConversionTypingRecord> conversionsByLocation,
        IReadOnlyDictionary<StarkParser.ObjectCreationExpressionContext, PublishedTypedTemplateObjectCreationLookup> objectCreationOrdinals,
        IReadOnlyDictionary<StarkParser.EnumConstructorExpressionContext, int> enumConstructorOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> enumCallOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> enumValueOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> functionAddressOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> directCallOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> dynamicStorageOperationOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> memberCallOrdinals,
        IReadOnlyDictionary<StarkParser.PostfixPartContext, int> fieldAccessOrdinals,
        out StarkPackageTypedTemplateExpressionManifest publishedExpression)
    {
        return TryBuildPublishedTypedTemplateBinaryChain(
            module,
            bitwiseOrExpression.bitwiseXorExpression(),
            ExtractOperators<StarkParser.BitwiseXorExpressionContext>(bitwiseOrExpression),
            namedTypes,
            literalsByLocation,
            conversionsByLocation,
            objectCreationOrdinals,
            enumConstructorOrdinals,
            enumCallOrdinals,
            enumValueOrdinals,
            functionAddressOrdinals,
            directCallOrdinals,
            dynamicStorageOperationOrdinals,
            memberCallOrdinals,
            fieldAccessOrdinals,
            TryBuildPublishedTypedTemplateBitwiseXorExpression,
            out publishedExpression);
    }

    private static bool TryBuildPublishedTypedTemplateBitwiseXorExpression(
        LoadedModuleDocument module,
        StarkParser.BitwiseXorExpressionContext bitwiseXorExpression,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, LiteralTypingRecord> literalsByLocation,
        IReadOnlyDictionary<string, ConversionTypingRecord> conversionsByLocation,
        IReadOnlyDictionary<StarkParser.ObjectCreationExpressionContext, PublishedTypedTemplateObjectCreationLookup> objectCreationOrdinals,
        IReadOnlyDictionary<StarkParser.EnumConstructorExpressionContext, int> enumConstructorOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> enumCallOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> enumValueOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> functionAddressOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> directCallOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> dynamicStorageOperationOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> memberCallOrdinals,
        IReadOnlyDictionary<StarkParser.PostfixPartContext, int> fieldAccessOrdinals,
        out StarkPackageTypedTemplateExpressionManifest publishedExpression)
    {
        return TryBuildPublishedTypedTemplateBinaryChain(
            module,
            bitwiseXorExpression.bitwiseAndExpression(),
            ExtractOperators<StarkParser.BitwiseAndExpressionContext>(bitwiseXorExpression),
            namedTypes,
            literalsByLocation,
            conversionsByLocation,
            objectCreationOrdinals,
            enumConstructorOrdinals,
            enumCallOrdinals,
            enumValueOrdinals,
            functionAddressOrdinals,
            directCallOrdinals,
            dynamicStorageOperationOrdinals,
            memberCallOrdinals,
            fieldAccessOrdinals,
            TryBuildPublishedTypedTemplateBitwiseAndExpression,
            out publishedExpression);
    }

    private static bool TryBuildPublishedTypedTemplateBitwiseAndExpression(
        LoadedModuleDocument module,
        StarkParser.BitwiseAndExpressionContext bitwiseAndExpression,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, LiteralTypingRecord> literalsByLocation,
        IReadOnlyDictionary<string, ConversionTypingRecord> conversionsByLocation,
        IReadOnlyDictionary<StarkParser.ObjectCreationExpressionContext, PublishedTypedTemplateObjectCreationLookup> objectCreationOrdinals,
        IReadOnlyDictionary<StarkParser.EnumConstructorExpressionContext, int> enumConstructorOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> enumCallOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> enumValueOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> functionAddressOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> directCallOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> dynamicStorageOperationOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> memberCallOrdinals,
        IReadOnlyDictionary<StarkParser.PostfixPartContext, int> fieldAccessOrdinals,
        out StarkPackageTypedTemplateExpressionManifest publishedExpression)
    {
        return TryBuildPublishedTypedTemplateBinaryChain(
            module,
            bitwiseAndExpression.equalityExpression(),
            ExtractOperators<StarkParser.EqualityExpressionContext>(bitwiseAndExpression),
            namedTypes,
            literalsByLocation,
            conversionsByLocation,
            objectCreationOrdinals,
            enumConstructorOrdinals,
            enumCallOrdinals,
            enumValueOrdinals,
            functionAddressOrdinals,
            directCallOrdinals,
            dynamicStorageOperationOrdinals,
            memberCallOrdinals,
            fieldAccessOrdinals,
            TryBuildPublishedTypedTemplateEqualityExpression,
            out publishedExpression);
    }

    private static bool TryBuildPublishedTypedTemplateEqualityExpression(
        LoadedModuleDocument module,
        StarkParser.EqualityExpressionContext equalityExpression,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, LiteralTypingRecord> literalsByLocation,
        IReadOnlyDictionary<string, ConversionTypingRecord> conversionsByLocation,
        IReadOnlyDictionary<StarkParser.ObjectCreationExpressionContext, PublishedTypedTemplateObjectCreationLookup> objectCreationOrdinals,
        IReadOnlyDictionary<StarkParser.EnumConstructorExpressionContext, int> enumConstructorOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> enumCallOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> enumValueOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> functionAddressOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> directCallOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> dynamicStorageOperationOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> memberCallOrdinals,
        IReadOnlyDictionary<StarkParser.PostfixPartContext, int> fieldAccessOrdinals,
        out StarkPackageTypedTemplateExpressionManifest publishedExpression)
    {
        var operators = ExtractOperators<StarkParser.RelationalExpressionContext>(equalityExpression);
        if (operators.Count > 1)
        {
            return TryBuildPublishedTypedTemplateComparisonChain(
                module,
                equalityExpression.relationalExpression(),
                operators,
                namedTypes,
                literalsByLocation,
                conversionsByLocation,
                objectCreationOrdinals,
                enumConstructorOrdinals,
                enumCallOrdinals,
                enumValueOrdinals,
                functionAddressOrdinals,
                directCallOrdinals,
                dynamicStorageOperationOrdinals,
                memberCallOrdinals,
                fieldAccessOrdinals,
                TryBuildPublishedTypedTemplateRelationalExpression,
                out publishedExpression);
        }

        return TryBuildPublishedTypedTemplateBinaryChain(
            module,
            equalityExpression.relationalExpression(),
            operators,
            namedTypes,
            literalsByLocation,
            conversionsByLocation,
            objectCreationOrdinals,
            enumConstructorOrdinals,
            enumCallOrdinals,
            enumValueOrdinals,
            functionAddressOrdinals,
            directCallOrdinals,
            dynamicStorageOperationOrdinals,
            memberCallOrdinals,
            fieldAccessOrdinals,
            TryBuildPublishedTypedTemplateRelationalExpression,
            out publishedExpression);
    }

    private static bool TryBuildPublishedTypedTemplateRelationalExpression(
        LoadedModuleDocument module,
        StarkParser.RelationalExpressionContext relationalExpression,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, LiteralTypingRecord> literalsByLocation,
        IReadOnlyDictionary<string, ConversionTypingRecord> conversionsByLocation,
        IReadOnlyDictionary<StarkParser.ObjectCreationExpressionContext, PublishedTypedTemplateObjectCreationLookup> objectCreationOrdinals,
        IReadOnlyDictionary<StarkParser.EnumConstructorExpressionContext, int> enumConstructorOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> enumCallOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> enumValueOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> functionAddressOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> directCallOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> dynamicStorageOperationOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> memberCallOrdinals,
        IReadOnlyDictionary<StarkParser.PostfixPartContext, int> fieldAccessOrdinals,
        out StarkPackageTypedTemplateExpressionManifest publishedExpression)
    {
        var operators = ExtractOperators<StarkParser.ShiftExpressionContext>(relationalExpression);
        if (operators.Count > 1)
        {
            return TryBuildPublishedTypedTemplateComparisonChain(
                module,
                relationalExpression.shiftExpression(),
                operators,
                namedTypes,
                literalsByLocation,
                conversionsByLocation,
                objectCreationOrdinals,
                enumConstructorOrdinals,
                enumCallOrdinals,
                enumValueOrdinals,
                functionAddressOrdinals,
                directCallOrdinals,
                dynamicStorageOperationOrdinals,
                memberCallOrdinals,
                fieldAccessOrdinals,
                TryBuildPublishedTypedTemplateShiftExpression,
                out publishedExpression);
        }

        return TryBuildPublishedTypedTemplateBinaryChain(
            module,
            relationalExpression.shiftExpression(),
            operators,
            namedTypes,
            literalsByLocation,
            conversionsByLocation,
            objectCreationOrdinals,
            enumConstructorOrdinals,
            enumCallOrdinals,
            enumValueOrdinals,
            functionAddressOrdinals,
            directCallOrdinals,
            dynamicStorageOperationOrdinals,
            memberCallOrdinals,
            fieldAccessOrdinals,
            TryBuildPublishedTypedTemplateShiftExpression,
            out publishedExpression);
    }

    private static bool TryBuildPublishedTypedTemplateShiftExpression(
        LoadedModuleDocument module,
        StarkParser.ShiftExpressionContext shiftExpression,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, LiteralTypingRecord> literalsByLocation,
        IReadOnlyDictionary<string, ConversionTypingRecord> conversionsByLocation,
        IReadOnlyDictionary<StarkParser.ObjectCreationExpressionContext, PublishedTypedTemplateObjectCreationLookup> objectCreationOrdinals,
        IReadOnlyDictionary<StarkParser.EnumConstructorExpressionContext, int> enumConstructorOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> enumCallOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> enumValueOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> functionAddressOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> directCallOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> dynamicStorageOperationOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> memberCallOrdinals,
        IReadOnlyDictionary<StarkParser.PostfixPartContext, int> fieldAccessOrdinals,
        out StarkPackageTypedTemplateExpressionManifest publishedExpression)
    {
        return TryBuildPublishedTypedTemplateBinaryChain(
            module,
            shiftExpression.additiveExpression(),
            ExtractOperators<StarkParser.AdditiveExpressionContext>(shiftExpression),
            namedTypes,
            literalsByLocation,
            conversionsByLocation,
            objectCreationOrdinals,
            enumConstructorOrdinals,
            enumCallOrdinals,
            enumValueOrdinals,
            functionAddressOrdinals,
            directCallOrdinals,
            dynamicStorageOperationOrdinals,
            memberCallOrdinals,
            fieldAccessOrdinals,
            TryBuildPublishedTypedTemplateAdditiveExpression,
            out publishedExpression);
    }

    private static bool TryBuildPublishedTypedTemplateAdditiveExpression(
        LoadedModuleDocument module,
        StarkParser.AdditiveExpressionContext additiveExpression,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, LiteralTypingRecord> literalsByLocation,
        IReadOnlyDictionary<string, ConversionTypingRecord> conversionsByLocation,
        IReadOnlyDictionary<StarkParser.ObjectCreationExpressionContext, PublishedTypedTemplateObjectCreationLookup> objectCreationOrdinals,
        IReadOnlyDictionary<StarkParser.EnumConstructorExpressionContext, int> enumConstructorOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> enumCallOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> enumValueOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> functionAddressOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> directCallOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> dynamicStorageOperationOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> memberCallOrdinals,
        IReadOnlyDictionary<StarkParser.PostfixPartContext, int> fieldAccessOrdinals,
        out StarkPackageTypedTemplateExpressionManifest publishedExpression)
    {
        return TryBuildPublishedTypedTemplateBinaryChain(
            module,
            additiveExpression.multiplicativeExpression(),
            ExtractOperators<StarkParser.MultiplicativeExpressionContext>(additiveExpression),
            namedTypes,
            literalsByLocation,
            conversionsByLocation,
            objectCreationOrdinals,
            enumConstructorOrdinals,
            enumCallOrdinals,
            enumValueOrdinals,
            functionAddressOrdinals,
            directCallOrdinals,
            dynamicStorageOperationOrdinals,
            memberCallOrdinals,
            fieldAccessOrdinals,
            TryBuildPublishedTypedTemplateMultiplicativeExpression,
            out publishedExpression);
    }

    private static bool TryBuildPublishedTypedTemplateMultiplicativeExpression(
        LoadedModuleDocument module,
        StarkParser.MultiplicativeExpressionContext multiplicativeExpression,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, LiteralTypingRecord> literalsByLocation,
        IReadOnlyDictionary<string, ConversionTypingRecord> conversionsByLocation,
        IReadOnlyDictionary<StarkParser.ObjectCreationExpressionContext, PublishedTypedTemplateObjectCreationLookup> objectCreationOrdinals,
        IReadOnlyDictionary<StarkParser.EnumConstructorExpressionContext, int> enumConstructorOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> enumCallOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> enumValueOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> functionAddressOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> directCallOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> dynamicStorageOperationOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> memberCallOrdinals,
        IReadOnlyDictionary<StarkParser.PostfixPartContext, int> fieldAccessOrdinals,
        out StarkPackageTypedTemplateExpressionManifest publishedExpression)
    {
        return TryBuildPublishedTypedTemplateBinaryChain(
            module,
            multiplicativeExpression.unaryExpression(),
            ExtractOperators<StarkParser.UnaryExpressionContext>(multiplicativeExpression),
            namedTypes,
            literalsByLocation,
            conversionsByLocation,
            objectCreationOrdinals,
            enumConstructorOrdinals,
            enumCallOrdinals,
            enumValueOrdinals,
            functionAddressOrdinals,
            directCallOrdinals,
            dynamicStorageOperationOrdinals,
            memberCallOrdinals,
            fieldAccessOrdinals,
            TryBuildPublishedTypedTemplateUnaryExpression,
            out publishedExpression);
    }

    private static bool TryBuildPublishedTypedTemplateUnaryExpression(
        LoadedModuleDocument module,
        StarkParser.UnaryExpressionContext unaryExpression,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, LiteralTypingRecord> literalsByLocation,
        IReadOnlyDictionary<string, ConversionTypingRecord> conversionsByLocation,
        IReadOnlyDictionary<StarkParser.ObjectCreationExpressionContext, PublishedTypedTemplateObjectCreationLookup> objectCreationOrdinals,
        IReadOnlyDictionary<StarkParser.EnumConstructorExpressionContext, int> enumConstructorOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> enumCallOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> enumValueOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> functionAddressOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> directCallOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> dynamicStorageOperationOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> memberCallOrdinals,
        IReadOnlyDictionary<StarkParser.PostfixPartContext, int> fieldAccessOrdinals,
        out StarkPackageTypedTemplateExpressionManifest publishedExpression)
    {
        publishedExpression = null!;

        if (unaryExpression.powerExpression() is { } powerExpression)
        {
            return TryBuildPublishedTypedTemplatePowerExpression(
                module,
                powerExpression,
                namedTypes,
                literalsByLocation,
                conversionsByLocation,
                objectCreationOrdinals,
                enumConstructorOrdinals,
                enumCallOrdinals,
                enumValueOrdinals,
                functionAddressOrdinals,
                directCallOrdinals,
                dynamicStorageOperationOrdinals,
                memberCallOrdinals,
                fieldAccessOrdinals,
                out publishedExpression);
        }

        if (unaryExpression.unaryExpression() is not { } operandExpression)
        {
            return false;
        }

        if (unaryExpression.conversionType() is { } conversionType)
        {
            if (!conversionsByLocation.TryGetValue(
                    BuildTemplateConversionLookupKey(unaryExpression.Start.Line, unaryExpression.Start.Column + 1),
                    out var conversionRecord))
            {
                return false;
            }

            if (!TryBuildPublishedTypedTemplateUnaryExpression(
                    module,
                    operandExpression,
                    namedTypes,
                    literalsByLocation,
                    conversionsByLocation,
                    objectCreationOrdinals,
                    enumConstructorOrdinals,
                    enumCallOrdinals,
                    enumValueOrdinals,
                    functionAddressOrdinals,
                    directCallOrdinals,
                    dynamicStorageOperationOrdinals,
                    memberCallOrdinals,
                    fieldAccessOrdinals,
                    out var publishedOperand))
            {
                return false;
            }

            publishedExpression = new StarkPackageTypedTemplateExpressionManifest(
                Kind: "conversion",
                Arguments: [publishedOperand],
                Type: BuildPublishedAbiTypeReference(conversionRecord.TargetType, module));
            return true;
        }

        var operatorText = unaryExpression.unaryOperator()?.GetText() ?? unaryExpression.GetChild(0).GetText();
        if (operatorText == "&")
        {
            if (!TryBuildPublishedTypedTemplateAssignmentTarget(
                    module,
                    operandExpression,
                    namedTypes,
                    literalsByLocation,
                    conversionsByLocation,
                    objectCreationOrdinals,
                    enumConstructorOrdinals,
                    enumCallOrdinals,
                    enumValueOrdinals,
                    functionAddressOrdinals,
                    directCallOrdinals,
                    dynamicStorageOperationOrdinals,
                    memberCallOrdinals,
                    fieldAccessOrdinals,
                    out _,
                    out var publishedAddressTarget))
            {
                return false;
            }

            publishedExpression = new StarkPackageTypedTemplateExpressionManifest(
                Kind: "unary",
                Name: operatorText,
                Arguments: [publishedAddressTarget]);
            return true;
        }

        if (operatorText is not ("+" or "-" or "-%" or "!" or "~" or "*")
            || !TryBuildPublishedTypedTemplateUnaryExpression(
                module,
                operandExpression,
                namedTypes,
                literalsByLocation,
                conversionsByLocation,
                objectCreationOrdinals,
                enumConstructorOrdinals,
                enumCallOrdinals,
                enumValueOrdinals,
                functionAddressOrdinals,
                directCallOrdinals,
                dynamicStorageOperationOrdinals,
                memberCallOrdinals,
                fieldAccessOrdinals,
                out var publishedUnaryOperand))
        {
            return false;
        }

        publishedExpression = new StarkPackageTypedTemplateExpressionManifest(
            Kind: "unary",
            Name: operatorText,
            Arguments: [publishedUnaryOperand]);
        return true;
    }

    private static bool TryBuildPublishedTypedTemplatePowerExpression(
        LoadedModuleDocument module,
        StarkParser.PowerExpressionContext powerExpression,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, LiteralTypingRecord> literalsByLocation,
        IReadOnlyDictionary<string, ConversionTypingRecord> conversionsByLocation,
        IReadOnlyDictionary<StarkParser.ObjectCreationExpressionContext, PublishedTypedTemplateObjectCreationLookup> objectCreationOrdinals,
        IReadOnlyDictionary<StarkParser.EnumConstructorExpressionContext, int> enumConstructorOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> enumCallOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> enumValueOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> functionAddressOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> directCallOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> dynamicStorageOperationOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> memberCallOrdinals,
        IReadOnlyDictionary<StarkParser.PostfixPartContext, int> fieldAccessOrdinals,
        out StarkPackageTypedTemplateExpressionManifest publishedExpression)
    {
        publishedExpression = null!;

        if (powerExpression.postfixExpression() is not { } postfixExpression)
        {
            return false;
        }

        if (!TryBuildPublishedTypedTemplatePostfixExpression(
                module,
                postfixExpression,
                namedTypes,
                literalsByLocation,
                conversionsByLocation,
                objectCreationOrdinals,
                enumConstructorOrdinals,
                enumCallOrdinals,
                enumValueOrdinals,
                functionAddressOrdinals,
                directCallOrdinals,
                dynamicStorageOperationOrdinals,
                memberCallOrdinals,
                fieldAccessOrdinals,
                out var left))
        {
            return false;
        }

        if (powerExpression.unaryExpression() is not { } rightExpression)
        {
            publishedExpression = left;
            return true;
        }

        if (!TryBuildPublishedTypedTemplateUnaryExpression(
                module,
                rightExpression,
                namedTypes,
                literalsByLocation,
                conversionsByLocation,
                objectCreationOrdinals,
                enumConstructorOrdinals,
                enumCallOrdinals,
                enumValueOrdinals,
                functionAddressOrdinals,
                directCallOrdinals,
                dynamicStorageOperationOrdinals,
                memberCallOrdinals,
                fieldAccessOrdinals,
                out var right))
        {
            return false;
        }

        publishedExpression = new StarkPackageTypedTemplateExpressionManifest(
            Kind: "binary",
            Name: "**",
            Arguments: [left, right]);
        return true;
    }

    private static bool TryBuildPublishedTypedTemplateBinaryChain<TOperandContext>(
        LoadedModuleDocument module,
        IReadOnlyList<TOperandContext> operands,
        IReadOnlyList<string> operators,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, LiteralTypingRecord> literalsByLocation,
        IReadOnlyDictionary<string, ConversionTypingRecord> conversionsByLocation,
        IReadOnlyDictionary<StarkParser.ObjectCreationExpressionContext, PublishedTypedTemplateObjectCreationLookup> objectCreationOrdinals,
        IReadOnlyDictionary<StarkParser.EnumConstructorExpressionContext, int> enumConstructorOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> enumCallOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> enumValueOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> functionAddressOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> directCallOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> dynamicStorageOperationOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> memberCallOrdinals,
        IReadOnlyDictionary<StarkParser.PostfixPartContext, int> fieldAccessOrdinals,
        TryBuildPublishedTypedTemplateOperand<TOperandContext> buildOperand,
        out StarkPackageTypedTemplateExpressionManifest publishedExpression)
        where TOperandContext : ParserRuleContext
    {
        publishedExpression = null!;

        if (operands.Count == 0
            || operators.Count != operands.Count - 1
            || !buildOperand(
                module,
                operands[0],
                namedTypes,
                literalsByLocation,
                conversionsByLocation,
                objectCreationOrdinals,
                enumConstructorOrdinals,
                enumCallOrdinals,
                enumValueOrdinals,
                functionAddressOrdinals,
                directCallOrdinals,
                dynamicStorageOperationOrdinals,
                memberCallOrdinals,
                fieldAccessOrdinals,
                out var current))
        {
            return false;
        }

        for (var index = 1; index < operands.Count; index++)
        {
            if (!buildOperand(
                    module,
                    operands[index],
                    namedTypes,
                    literalsByLocation,
                    conversionsByLocation,
                    objectCreationOrdinals,
                    enumConstructorOrdinals,
                    enumCallOrdinals,
                    enumValueOrdinals,
                    functionAddressOrdinals,
                    directCallOrdinals,
                    dynamicStorageOperationOrdinals,
                    memberCallOrdinals,
                    fieldAccessOrdinals,
                    out var next))
            {
                return false;
            }

            current = new StarkPackageTypedTemplateExpressionManifest(
                Kind: "binary",
                Name: operators[index - 1],
                Arguments: [current, next]);
        }

        publishedExpression = current;
        return true;
    }

    private static bool TryBuildPublishedTypedTemplateComparisonChain<TOperandContext>(
        LoadedModuleDocument module,
        IReadOnlyList<TOperandContext> operands,
        IReadOnlyList<string> operators,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, LiteralTypingRecord> literalsByLocation,
        IReadOnlyDictionary<string, ConversionTypingRecord> conversionsByLocation,
        IReadOnlyDictionary<StarkParser.ObjectCreationExpressionContext, PublishedTypedTemplateObjectCreationLookup> objectCreationOrdinals,
        IReadOnlyDictionary<StarkParser.EnumConstructorExpressionContext, int> enumConstructorOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> enumCallOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> enumValueOrdinals,
        IReadOnlyDictionary<ParserRuleContext, int> functionAddressOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> directCallOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> dynamicStorageOperationOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> memberCallOrdinals,
        IReadOnlyDictionary<StarkParser.PostfixPartContext, int> fieldAccessOrdinals,
        TryBuildPublishedTypedTemplateOperand<TOperandContext> buildOperand,
        out StarkPackageTypedTemplateExpressionManifest publishedExpression)
        where TOperandContext : ParserRuleContext
    {
        publishedExpression = null!;

        if (operands.Count < 2 || operators.Count != operands.Count - 1)
        {
            return false;
        }

        var arguments = new List<StarkPackageTypedTemplateExpressionManifest>(operands.Count);
        foreach (var operand in operands)
        {
            if (!buildOperand(
                    module,
                    operand,
                    namedTypes,
                    literalsByLocation,
                    conversionsByLocation,
                    objectCreationOrdinals,
                    enumConstructorOrdinals,
                    enumCallOrdinals,
                    enumValueOrdinals,
                    functionAddressOrdinals,
                    directCallOrdinals,
                    dynamicStorageOperationOrdinals,
                    memberCallOrdinals,
                    fieldAccessOrdinals,
                    out var publishedOperand))
            {
                return false;
            }

            arguments.Add(publishedOperand);
        }

        publishedExpression = new StarkPackageTypedTemplateExpressionManifest(
            Kind: "comparison-chain",
            Arguments: arguments,
            OperatorNames: operators);
        return true;
    }

    private static IReadOnlyList<string> ExtractOperators<TOperand>(ParserRuleContext context)
        where TOperand : ParserRuleContext
    {
        var operators = new List<string>();
        var builder = new StringBuilder();

        for (var index = 0; index < context.ChildCount; index++)
        {
            var child = context.GetChild(index);
            if (child is TOperand)
            {
                if (builder.Length > 0)
                {
                    operators.Add(builder.ToString());
                    builder.Clear();
                }

                continue;
            }

            builder.Append(child.GetText());
        }

        return operators;
    }

    private static IReadOnlyList<StarkPackageTemplateObjectCreationManifest>? BuildPublishedTemplateObjectCreations(
        LoadedModuleDocument module,
        ParserRuleContext functionBody,
        IReadOnlyList<ObjectCreationTypingRecord>? objectCreations)
    {
        if (objectCreations is not { Count: > 0 })
        {
            return null;
        }

        var objectCreationsByKey = objectCreations
            .GroupBy(static record => BuildTemplateObjectCreationLookupKey(record.ExpressionText, record.Location.Line, record.Location.Column))
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.Ordinal);
        var published = CollectTrackedTemplateObjectCreations(functionBody)
            .Select(objectCreation => objectCreationsByKey.TryGetValue(
                    BuildTemplateObjectCreationLookupKey(
                        objectCreation.GetText(),
                        objectCreation.Start.Line,
                        objectCreation.Start.Column + 1),
                    out var record)
                ? new StarkPackageTemplateObjectCreationManifest(
                    BuildPublishedAbiTypeReference(record.CreatedType, module),
                    BuildPublishedConstructorShape(module, record.Constructor),
                    record.Members.Count == 0
                        ? null
                        : record.Members
                            .Select(member => new StarkPackageTemplateObjectInitializerMemberManifest(
                                member.FieldName,
                                member.FieldIndex,
                                BuildPublishedAbiTypeReference(member.FieldType, module)))
                            .ToArray())
                : new StarkPackageTemplateObjectCreationManifest(
                    CreatedType: BuildPublishedAbiTypeReference(StarkTypeSymbols.Error, module),
                    Constructor: null))
            .ToArray();

        return published.Length == 0 ? null : published;
    }

    private static IReadOnlyList<StarkPackageTemplateEnumConstructorManifest>? BuildPublishedTemplateEnumConstructors(
        LoadedModuleDocument module,
        ParserRuleContext functionBody,
        IReadOnlyList<EnumConstructorTypingRecord>? enumConstructors)
    {
        if (enumConstructors is not { Count: > 0 })
        {
            return null;
        }

        var enumConstructorsByLocation = enumConstructors
            .GroupBy(static record => BuildTemplateEnumConstructorLookupKey(record.Location.Line, record.Location.Column))
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.Ordinal);
        var published = CollectTemplateEnumConstructorExpressions(functionBody)
            .Select((enumConstructor, ordinal) => enumConstructorsByLocation.TryGetValue(
                    BuildTemplateEnumConstructorLookupKey(enumConstructor.Start.Line, enumConstructor.Start.Column + 1),
                    out var record)
                ? new StarkPackageTemplateEnumConstructorManifest(
                    ordinal,
                    BuildPublishedAbiTypeReference(record.EnumType, module),
                    record.VariantName,
                    record.Members.Count == 0
                        ? null
                        : record.Members
                            .Select(member => new StarkPackageTemplateEnumConstructorMemberManifest(
                                member.FieldName,
                                member.FieldIndex,
                                BuildPublishedAbiTypeReference(member.FieldType, module)))
                            .ToArray())
                : null)
            .Where(static enumConstructor => enumConstructor is not null)
            .Cast<StarkPackageTemplateEnumConstructorManifest>()
            .ToArray();

        return published.Length == 0 ? null : published;
    }

    private static IReadOnlyList<StarkPackageTemplateEnumCallManifest>? BuildPublishedTemplateEnumCalls(
        LoadedModuleDocument module,
        ParserRuleContext functionBody,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyList<EnumCallTypingRecord>? enumCalls)
    {
        var syntacticReferences = CollectPublishedTemplateEnumCallReferences(module, functionBody, namedTypes);
        if (syntacticReferences.Count > 0)
        {
            return syntacticReferences
                .Select((reference, ordinal) => new StarkPackageTemplateEnumCallManifest(
                    ordinal,
                    BuildPublishedAbiTypeReference(reference.EnumType, module),
                    reference.VariantName))
                .ToArray();
        }

        if (enumCalls is not { Count: > 0 })
        {
            return null;
        }

        var enumCallsByLocation = enumCalls
            .GroupBy(static record => TemplateDirectCallFacts.BuildLookupKey(record.Location))
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.Ordinal);
        var published = CollectTemplateDirectCallArgumentLists(functionBody)
            .Select((argumentList, ordinal) => enumCallsByLocation.TryGetValue(
                    TemplateDirectCallFacts.BuildLookupKey(argumentList.Start.Line, argumentList.Start.Column + 1),
                    out var record)
                ? new StarkPackageTemplateEnumCallManifest(
                    ordinal,
                    BuildPublishedAbiTypeReference(record.EnumType, module),
                    record.VariantName)
                : null)
            .Where(static enumCall => enumCall is not null)
            .Cast<StarkPackageTemplateEnumCallManifest>()
            .ToArray();

        return published.Length == 0 ? null : published;
    }

    private static IReadOnlyList<StarkPackageTemplateEnumValueManifest>? BuildPublishedTemplateEnumValues(
        LoadedModuleDocument module,
        ParserRuleContext functionBody,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyList<EnumValueTypingRecord>? enumValues)
    {
        var syntacticReferences = CollectPublishedTemplateEnumValueReferences(module, functionBody, namedTypes);
        if (syntacticReferences.Count > 0)
        {
            return syntacticReferences
                .Select((reference, ordinal) => new StarkPackageTemplateEnumValueManifest(
                    ordinal,
                    BuildPublishedAbiTypeReference(reference.EnumType, module),
                    reference.VariantName))
                .ToArray();
        }

        if (enumValues is not { Count: > 0 })
        {
            return null;
        }

        var enumValuesByLocation = enumValues
            .GroupBy(static record => TemplateDirectCallFacts.BuildLookupKey(record.Location))
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.Ordinal);
        var published = CollectTemplateEnumValueContexts(functionBody)
            .Select((context, ordinal) => enumValuesByLocation.TryGetValue(
                    BuildTemplateEnumValueLookupKey(context),
                    out var record)
                ? new StarkPackageTemplateEnumValueManifest(
                    ordinal,
                    BuildPublishedAbiTypeReference(record.EnumType, module),
                    record.VariantName)
                : null)
            .Where(static enumValue => enumValue is not null)
            .Cast<StarkPackageTemplateEnumValueManifest>()
            .ToArray();

        return published.Length == 0 ? null : published;
    }

    private static IReadOnlyList<StarkPackageTemplateEnumPatternManifest>? BuildPublishedTemplateEnumPatterns(
        LoadedModuleDocument module,
        ParserRuleContext functionBody,
        IReadOnlyList<EnumPatternTypingRecord>? enumPatterns)
    {
        if (enumPatterns is not { Count: > 0 })
        {
            return null;
        }

        var enumPatternsByLocation = enumPatterns
            .GroupBy(static record => TemplateDirectCallFacts.BuildLookupKey(record.Location))
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.Ordinal);
        var published = CollectTemplateEnumPatternContexts(functionBody)
            .Select((patternContext, ordinal) => enumPatternsByLocation.TryGetValue(
                    TemplateDirectCallFacts.BuildLookupKey(patternContext.Start.Line, patternContext.Start.Column + 1),
                    out var record)
                ? new StarkPackageTemplateEnumPatternManifest(
                    ordinal,
                    BuildPublishedAbiTypeReference(record.EnumType, module),
                    record.VariantName,
                    record.Members.Count == 0
                        ? null
                        : record.Members
                            .Select(member => new StarkPackageTemplateEnumPatternMemberManifest(
                                member.FieldName,
                                member.FieldIndex,
                                BuildPublishedAbiTypeReference(member.FieldType, module)))
                            .ToArray())
                : null)
            .Where(static enumPattern => enumPattern is not null)
            .Cast<StarkPackageTemplateEnumPatternManifest>()
            .ToArray();

        return published.Length == 0 ? null : published;
    }

    private static IReadOnlyList<StarkPackageTemplateAggregatePatternManifest>? BuildPublishedTemplateAggregatePatterns(
        LoadedModuleDocument module,
        ParserRuleContext functionBody,
        IReadOnlyList<AggregatePatternTypingRecord>? aggregatePatterns)
    {
        if (aggregatePatterns is not { Count: > 0 })
        {
            return null;
        }

        var aggregatePatternsByLocation = aggregatePatterns
            .GroupBy(static record => TemplateDirectCallFacts.BuildLookupKey(record.Location))
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.Ordinal);
        var published = CollectTemplateEnumPatternContexts(functionBody)
            .Select((patternContext, ordinal) => patternContext is StarkParser.AggregatePatternContext aggregatePattern
                    && aggregatePatternsByLocation.TryGetValue(
                        TemplateDirectCallFacts.BuildLookupKey(aggregatePattern.Start.Line, aggregatePattern.Start.Column + 1),
                        out var record)
                ? new StarkPackageTemplateAggregatePatternManifest(
                    ordinal,
                    BuildPublishedAbiTypeReference(record.Type, module))
                : null)
            .Where(static aggregatePattern => aggregatePattern is not null)
            .Cast<StarkPackageTemplateAggregatePatternManifest>()
            .ToArray();

        return published.Length == 0 ? null : published;
    }

    private static StarkPackagePublishedConstructorShapeManifest? BuildPublishedConstructorShape(
        LoadedModuleDocument module,
        TypedConstructorShape? constructor)
    {
        return constructor is null
            ? null
            : new StarkPackagePublishedConstructorShapeManifest(
                constructor.TypeName,
                constructor.Parameters
                    .Select(parameter => new StarkPackageTypedParameterManifest(
                        parameter.Name,
                        BuildPublishedAbiTypeReference(parameter.Type, module),
                        parameter.IsDisjoint,
                        parameter.IsConst,
                        parameter.RawPointerElementCountExpression))
                    .ToArray(),
                constructor.IsPrimaryShape);
    }

    private static IReadOnlyList<StarkPackageTemplateLocalDeclarationManifest>? BuildPublishedTemplateLocalDeclarations(
        LoadedModuleDocument module,
        IReadOnlyList<LocalDeclarationTypingRecord>? localDeclarations)
    {
        if (localDeclarations is not { Count: > 0 })
        {
            return null;
        }

        return localDeclarations
            .OrderBy(static record => record.Location.Line)
            .ThenBy(static record => record.Location.Column)
            .Select(record => new StarkPackageTemplateLocalDeclarationManifest(
                record.Kind,
                record.Location.Line,
                record.Location.Column,
                BuildPublishedAbiTypeReference(record.Type, module)))
            .ToArray();
    }

    private static string? GetPublishedDeclaratorConstProvenance(
        LocalDeclarationTypingRecord localDeclaration,
        string declaratorName)
    {
        return localDeclaration.ConstProvenanceByDeclarator.TryGetValue(declaratorName, out var constProvenance)
            ? ConstProvenanceFacts.ToManifestText(constProvenance)
            : null;
    }

    private static IReadOnlyList<StarkPackageTemplateConversionManifest>? BuildPublishedTemplateConversions(
        LoadedModuleDocument module,
        ParserRuleContext functionBody,
        IReadOnlyList<ConversionTypingRecord>? conversions)
    {
        if (conversions is not { Count: > 0 })
        {
            return null;
        }

        var conversionsByLocation = conversions
            .GroupBy(static record => BuildTemplateConversionLookupKey(record.Location.Line, record.Location.Column))
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.Ordinal);
        var published = CollectTemplateConversionExpressions(functionBody)
            .Select((unaryExpression, ordinal) => conversionsByLocation.TryGetValue(
                    BuildTemplateConversionLookupKey(unaryExpression.Start.Line, unaryExpression.Start.Column + 1),
                    out var record)
                ? new StarkPackageTemplateConversionManifest(
                    ordinal,
                    BuildPublishedAbiTypeReference(record.TargetType, module))
                : null)
            .Where(static conversion => conversion is not null)
            .Cast<StarkPackageTemplateConversionManifest>()
            .ToArray();

        return published.Length == 0 ? null : published;
    }

    private static IReadOnlyList<StarkPackageTemplateDirectCallManifest>? BuildPublishedTemplateDirectCalls(
        LoadedModuleDocument module,
        ParserRuleContext functionBody,
        IReadOnlyList<DirectCallTypingRecord>? directCalls)
    {
        if (directCalls is not { Count: > 0 })
        {
            return null;
        }

        var directCallsByLocation = directCalls
            .GroupBy(static record => TemplateDirectCallFacts.BuildLookupKey(record.Location))
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.Ordinal);
        var published = CollectTemplateArgumentListsByPublishedLocations(functionBody, directCallsByLocation.Keys)
            .Select((argumentList, ordinal) => directCallsByLocation.TryGetValue(
                    TemplateDirectCallFacts.BuildLookupKey(argumentList.Start.Line, argumentList.Start.Column + 1),
                    out var record)
                ? new StarkPackageTemplateDirectCallManifest(
                    ordinal,
                    QualifyPublishedCalledFunctionName(module, record.Signature.Name),
                    BuildPublishedAbiTypeReference(record.Signature.ReturnType, module),
                    record.Signature.Parameters
                        .Select(parameter => new StarkPackageTypedParameterManifest(
                            parameter.Name,
                            BuildPublishedAbiTypeReference(parameter.Type, module),
                            parameter.IsDisjoint,
                            parameter.IsConst,
                            parameter.RawPointerElementCountExpression))
                        .ToArray(),
                    QualifiedSourceName: record.Signature.SourceName is null
                        ? null
                        : QualifyPublishedCalledFunctionName(module, record.Signature.SourceName),
                    QualifiedTemplateName: record.Signature.TemplateName is null
                        ? null
                        : QualifyPublishedCalledFunctionName(module, record.Signature.TemplateName),
                    TypeArguments: record.Signature.TypeArguments is { Count: > 0 }
                        ? record.Signature.TypeArguments
                            .Select(typeArgument => BuildPublishedAbiTypeReference(typeArgument, module))
                            .ToArray()
                        : null,
                    DisjointParameterGroups: BuildParameterDisjointGroupManifests(record.Signature.DisjointGroups),
                    OverlapParameterGroups: BuildParameterOverlapGroupManifests(record.Signature.OverlapGroups),
                    SameParameterGroups: BuildParameterSameGroupManifests(record.Signature.SameGroups))
                : null)
            .Where(static directCall => directCall is not null)
            .Cast<StarkPackageTemplateDirectCallManifest>()
            .ToArray();

        return published.Length == 0 ? null : published;
    }

    private static IReadOnlyList<StarkPackageTemplateFieldAccessManifest>? BuildPublishedTemplateFieldAccesses(
        LoadedModuleDocument module,
        ParserRuleContext functionBody,
        IReadOnlyList<FieldAccessTypingRecord>? fieldAccesses)
    {
        if (fieldAccesses is not { Count: > 0 })
        {
            return null;
        }

        var fieldAccessesByLocation = fieldAccesses
            .GroupBy(static record => TemplateFieldAccessFacts.BuildLookupKey(record.Location))
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.Ordinal);
        var published = CollectTemplateMemberAccessParts(functionBody)
            .Select((postfixPart, ordinal) => fieldAccessesByLocation.TryGetValue(
                    TemplateFieldAccessFacts.BuildLookupKey(postfixPart.Start.Line, postfixPart.Start.Column + 1),
                    out var record)
                ? new StarkPackageTemplateFieldAccessManifest(
                    ordinal,
                    record.FieldName,
                    record.FieldIndex,
                    BuildPublishedAbiTypeReference(record.FieldType, module))
                : null)
            .Where(static fieldAccess => fieldAccess is not null)
            .Cast<StarkPackageTemplateFieldAccessManifest>()
            .ToArray();

        return published.Length == 0 ? null : published;
    }

    private static IReadOnlyList<StarkPackageTemplateMemberCallManifest>? BuildPublishedTemplateMemberCalls(
        LoadedModuleDocument module,
        ParserRuleContext functionBody,
        IReadOnlyList<MemberCallTypingRecord>? memberCalls)
    {
        if (memberCalls is not { Count: > 0 })
        {
            return null;
        }

        var memberCallsByLocation = memberCalls
            .GroupBy(static record => TemplateDirectCallFacts.BuildLookupKey(record.Location))
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.Ordinal);
        var published = CollectTemplateMemberCallArgumentLists(functionBody)
            .Select((argumentList, ordinal) => memberCallsByLocation.TryGetValue(
                    TemplateDirectCallFacts.BuildLookupKey(argumentList.Start.Line, argumentList.Start.Column + 1),
                    out var record)
                ? new StarkPackageTemplateMemberCallManifest(
                    ordinal,
                    QualifyPublishedCalledFunctionName(module, record.Signature.Name),
                    BuildPublishedAbiTypeReference(record.Signature.ReturnType, module),
                    record.Signature.Parameters
                        .Select(parameter => new StarkPackageTypedParameterManifest(
                            parameter.Name,
                            BuildPublishedAbiTypeReference(parameter.Type, module),
                            parameter.IsDisjoint,
                            parameter.IsConst,
                            parameter.RawPointerElementCountExpression))
                        .ToArray(),
                    QualifiedSourceName: record.Signature.SourceName is null
                        ? null
                        : QualifyPublishedCalledFunctionName(module, record.Signature.SourceName),
                    QualifiedTemplateName: record.Signature.TemplateName is null
                        ? null
                        : QualifyPublishedCalledFunctionName(module, record.Signature.TemplateName),
                    TypeArguments: record.Signature.TypeArguments is { Count: > 0 }
                        ? record.Signature.TypeArguments
                            .Select(typeArgument => BuildPublishedAbiTypeReference(typeArgument, module))
                            .ToArray()
                        : null,
                    DisjointParameterGroups: BuildParameterDisjointGroupManifests(record.Signature.DisjointGroups),
                    OverlapParameterGroups: BuildParameterOverlapGroupManifests(record.Signature.OverlapGroups),
                    SameParameterGroups: BuildParameterSameGroupManifests(record.Signature.SameGroups))
                : null)
            .Where(static memberCall => memberCall is not null)
            .Cast<StarkPackageTemplateMemberCallManifest>()
            .ToArray();

        return published.Length == 0 ? null : published;
    }

    private static IReadOnlyDictionary<string, int> BuildFunctionAddressPromotionsByLocation(
        IReadOnlyList<FunctionPointerPromotionTypingRecord>? functionPointerPromotions)
    {
        if (functionPointerPromotions is not { Count: > 0 })
        {
            return new Dictionary<string, int>(StringComparer.Ordinal);
        }

        return functionPointerPromotions
            .OrderBy(static promotion => promotion.Location.Line)
            .ThenBy(static promotion => promotion.Location.Column)
            .Select((promotion, ordinal) => (
                key: TemplateDirectCallFacts.BuildLookupKey(promotion.Location),
                ordinal))
            .GroupBy(static item => item.key, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Last().ordinal, StringComparer.Ordinal);
    }

    private static IReadOnlyList<StarkPackageTemplateFunctionAddressManifest>? BuildPublishedTemplateFunctionAddresses(
        LoadedModuleDocument module,
        IReadOnlyList<FunctionPointerPromotionTypingRecord>? functionPointerPromotions)
    {
        if (functionPointerPromotions is not { Count: > 0 })
        {
            return null;
        }

        var published = functionPointerPromotions
            .OrderBy(static promotion => promotion.Location.Line)
            .ThenBy(static promotion => promotion.Location.Column)
            .Select((promotion, ordinal) => new StarkPackageTemplateFunctionAddressManifest(
                ordinal,
                QualifyPublishedCalledFunctionName(module, promotion.Signature.Name),
                BuildPublishedAbiTypeReference(promotion.TargetType, module),
                BuildPublishedAbiTypeReference(promotion.Signature.ReturnType, module),
                BuildPublishedTemplateTypedParameters(module, promotion.Signature.Parameters),
                QualifiedSourceName: promotion.Signature.SourceName is null
                    ? null
                    : QualifyPublishedCalledFunctionName(module, promotion.Signature.SourceName),
                QualifiedTemplateName: promotion.Signature.TemplateName is null
                    ? null
                    : QualifyPublishedCalledFunctionName(module, promotion.Signature.TemplateName),
                TypeArguments: promotion.Signature.TypeArguments is { Count: > 0 }
                    ? promotion.Signature.TypeArguments
                        .Select(typeArgument => BuildPublishedAbiTypeReference(typeArgument, module))
                        .ToArray()
                    : null,
                DisjointParameterGroups: BuildParameterDisjointGroupManifests(promotion.Signature.DisjointGroups),
                OverlapParameterGroups: BuildParameterOverlapGroupManifests(promotion.Signature.OverlapGroups),
                SameParameterGroups: BuildParameterSameGroupManifests(promotion.Signature.SameGroups)))
            .ToArray();

        return published.Length == 0 ? null : published;
    }

    private static IReadOnlyList<StarkPackageTemplateBoundOperationManifest>? BuildPublishedTemplateBoundOperations(
        LoadedModuleDocument module,
        ParserRuleContext functionBody,
        IReadOnlyList<BoundOperation>? boundOperations)
    {
        if (boundOperations is not { Count: > 0 })
        {
            return null;
        }

        var ordinals = BuildPublishedTemplateBoundOperationOrdinals(functionBody, boundOperations);
        var published = boundOperations
            .OrderBy(static operation => operation.Location.Line)
            .ThenBy(static operation => operation.Location.Column)
            .ThenBy(static operation => operation.Kind.ToString(), StringComparer.Ordinal)
            .Select(operation => BuildPublishedTemplateBoundOperation(
                module,
                operation,
                ordinals.TryGetValue(
                    new PublishedBoundOperationOrdinalKey(operation.Kind, operation.Location.Line, operation.Location.Column),
                    out var ordinal)
                    ? ordinal
                    : null))
            .ToArray();

        return published.Length == 0 ? null : published;
    }

    private static IReadOnlyDictionary<PublishedBoundOperationOrdinalKey, int> BuildPublishedTemplateBoundOperationOrdinals(
        ParserRuleContext functionBody,
        IReadOnlyList<BoundOperation> boundOperations)
    {
        var ordinals = new Dictionary<PublishedBoundOperationOrdinalKey, int>();

        foreach (var item in CollectTemplateArgumentListsByPublishedLocations(
                     functionBody,
                     boundOperations
                         .Where(static operation => operation.Kind == BoundOperationKind.DirectCall)
                         .Select(static operation => TemplateDirectCallFacts.BuildLookupKey(operation.Location)))
                     .Select((argumentList, ordinal) => (argumentList, ordinal)))
        {
            AddOrdinal(BoundOperationKind.DirectCall, item.argumentList.Start.Line, item.argumentList.Start.Column + 1, item.ordinal);
        }

        foreach (var item in CollectTemplateMemberCallArgumentLists(functionBody).Select((argumentList, ordinal) => (argumentList, ordinal)))
        {
            AddOrdinal(BoundOperationKind.MemberCall, item.argumentList.Start.Line, item.argumentList.Start.Column + 1, item.ordinal);
        }

        foreach (var item in CollectTemplateArgumentListsByPublishedLocations(
                     functionBody,
                     boundOperations
                         .Where(static operation => operation.Kind is BoundOperationKind.FunctionPointerCall or BoundOperationKind.ClosureCall)
                         .Select(static operation => TemplateDirectCallFacts.BuildLookupKey(operation.Location)))
                     .Select((argumentList, ordinal) => (argumentList, ordinal)))
        {
            AddOrdinal(BoundOperationKind.FunctionPointerCall, item.argumentList.Start.Line, item.argumentList.Start.Column + 1, item.ordinal);
            AddOrdinal(BoundOperationKind.ClosureCall, item.argumentList.Start.Line, item.argumentList.Start.Column + 1, item.ordinal);
        }

        foreach (var item in CollectTemplateIndexAccessParts(functionBody).Select((postfixPart, ordinal) => (postfixPart, ordinal)))
        {
            AddOrdinal(BoundOperationKind.IndexAccess, item.postfixPart.Start.Line, item.postfixPart.Start.Column + 1, item.ordinal);
            AddOrdinal(BoundOperationKind.SliceAccess, item.postfixPart.Start.Line, item.postfixPart.Start.Column + 1, item.ordinal);
        }

        foreach (var item in CollectTrackedTemplateObjectCreations(functionBody).Select((objectCreation, ordinal) => (objectCreation, ordinal)))
        {
            AddOrdinal(BoundOperationKind.ObjectCreation, item.objectCreation.Start.Line, item.objectCreation.Start.Column + 1, item.ordinal);
        }

        foreach (var item in CollectTemplateEnumConstructorExpressions(functionBody).Select((enumConstructor, ordinal) => (enumConstructor, ordinal)))
        {
            AddOrdinal(BoundOperationKind.EnumConstruction, item.enumConstructor.Start.Line, item.enumConstructor.Start.Column + 1, item.ordinal);
        }

        foreach (var item in CollectTemplateDirectCallArgumentLists(functionBody).Select((argumentList, ordinal) => (argumentList, ordinal)))
        {
            AddOrdinal(BoundOperationKind.EnumCall, item.argumentList.Start.Line, item.argumentList.Start.Column + 1, item.ordinal);
        }

        foreach (var item in CollectTemplateEnumValueContexts(functionBody).Select((context, ordinal) => (context, ordinal)))
        {
            var (line, column) = GetTemplateEnumValueLineColumn(item.context);
            AddOrdinal(BoundOperationKind.EnumValue, line, column, item.ordinal);
        }

        foreach (var item in CollectTemplateArgumentListsByPublishedLocations(
                     functionBody,
                     boundOperations
                         .Where(static operation => operation.Kind == BoundOperationKind.DynamicStorageOperation)
                         .Select(static operation => TemplateDirectCallFacts.BuildLookupKey(operation.Location)))
                     .Select((argumentList, ordinal) => (argumentList, ordinal)))
        {
            AddOrdinal(BoundOperationKind.DynamicStorageOperation, item.argumentList.Start.Line, item.argumentList.Start.Column + 1, item.ordinal);
        }

        foreach (var item in CollectTemplateTypeLayoutPrimaryExpressions(functionBody).Select((primaryExpression, ordinal) => (primaryExpression, ordinal)))
        {
            AddOrdinal(BoundOperationKind.LayoutQuery, item.primaryExpression.Start.Line, item.primaryExpression.Start.Column + 1, item.ordinal);
        }

        foreach (var item in CollectTemplateSwitchStatements(functionBody).Select((switchStatement, ordinal) => (switchStatement, ordinal)))
        {
            AddOrdinal(BoundOperationKind.SwitchDispatch, item.switchStatement.Start.Line, item.switchStatement.Start.Column + 1, item.ordinal);
        }

        return ordinals;

        void AddOrdinal(BoundOperationKind kind, int line, int column, int ordinal)
        {
            var key = new PublishedBoundOperationOrdinalKey(kind, line, column);
            if (!ordinals.ContainsKey(key))
            {
                ordinals.Add(key, ordinal);
            }
        }
    }

    private static StarkPackageTemplateBoundOperationManifest BuildPublishedTemplateBoundOperation(
        LoadedModuleDocument module,
        BoundOperation operation,
        int? ordinal)
    {
        var location = operation.Location;
        var resultType = BuildPublishedAbiTypeReference(operation.ResultType, module);

        return operation switch
        {
            BoundDirectCallOperation directCall => BuildPublishedTemplateCallBoundOperation(
                module,
                directCall.Kind,
                location,
                resultType,
                ordinal,
                directCall.EnclosingFunctionName,
                directCall.Signature,
                directCall.Arguments),
            BoundMemberCallOperation memberCall => BuildPublishedTemplateCallBoundOperation(
                module,
                memberCall.Kind,
                location,
                resultType,
                ordinal,
                memberCall.EnclosingFunctionName,
                memberCall.Signature,
                memberCall.Arguments,
                ReceiverType: BuildPublishedAbiTypeReference(memberCall.ReceiverType, module),
                ReceiverIsAddressable: memberCall.ReceiverIsAddressable,
                ReceiverIsMutable: memberCall.ReceiverIsMutable),
            BoundFunctionPointerCallOperation functionPointerCall => new StarkPackageTemplateBoundOperationManifest(
                RenderBoundOperationKind(functionPointerCall.Kind),
                location.Line,
                location.Column,
                resultType,
                Ordinal: ordinal,
                EnclosingFunctionName: functionPointerCall.EnclosingFunctionName,
                CallArguments: BuildPublishedTemplateCallArguments(module, functionPointerCall.Arguments),
                FunctionPointerType: BuildPublishedAbiTypeReference(functionPointerCall.FunctionPointerType, module)),
            BoundClosureCallOperation closureCall => new StarkPackageTemplateBoundOperationManifest(
                RenderBoundOperationKind(closureCall.Kind),
                location.Line,
                location.Column,
                resultType,
                Ordinal: ordinal,
                EnclosingFunctionName: closureCall.EnclosingFunctionName,
                CallArguments: BuildPublishedTemplateCallArguments(module, closureCall.Arguments),
                ClosureType: BuildPublishedAbiTypeReference(closureCall.ClosureType, module)),
            BoundIndexAccessOperation indexAccess => new StarkPackageTemplateBoundOperationManifest(
                RenderBoundOperationKind(indexAccess.Kind),
                location.Line,
                location.Column,
                resultType,
                Ordinal: ordinal,
                EnclosingFunctionName: indexAccess.EnclosingFunctionName,
                AccessKind: RenderBoundIndexAccessKind(indexAccess.AccessKind),
                SourceKind: indexAccess.SourceKind,
                SourceType: BuildPublishedAbiTypeReference(indexAccess.SourceType, module),
                IndexCount: indexAccess.IndexCount),
            BoundDynamicStorageOperation dynamicStorage => new StarkPackageTemplateBoundOperationManifest(
                RenderBoundOperationKind(dynamicStorage.Kind),
                location.Line,
                location.Column,
                resultType,
                Ordinal: ordinal,
                EnclosingFunctionName: dynamicStorage.EnclosingFunctionName,
                OperationName: dynamicStorage.OperationName,
                ReceiverType: BuildPublishedAbiTypeReference(dynamicStorage.ReceiverType, module),
                ArgumentCount: dynamicStorage.ArgumentCount,
                ReceiverIsAddressable: dynamicStorage.ReceiverIsAddressable,
                ReceiverIsMutable: dynamicStorage.ReceiverIsMutable),
            BoundObjectCreationOperation objectCreation => new StarkPackageTemplateBoundOperationManifest(
                RenderBoundOperationKind(objectCreation.Kind),
                location.Line,
                location.Column,
                resultType,
                Ordinal: ordinal,
                EnclosingFunctionName: objectCreation.EnclosingFunctionName,
                ExpressionText: objectCreation.ExpressionText,
                CreatedType: BuildPublishedAbiTypeReference(objectCreation.CreatedType, module),
                Constructor: BuildPublishedConstructorShape(module, objectCreation.Constructor),
                InitializerMembers: objectCreation.Members.Count == 0
                    ? null
                    : objectCreation.Members
                        .Select(member => new StarkPackageTemplateObjectInitializerMemberManifest(
                            member.FieldName,
                            member.FieldIndex,
                            BuildPublishedAbiTypeReference(member.FieldType, module)))
                        .ToArray()),
            BoundEnumConstructionOperation enumConstruction => new StarkPackageTemplateBoundOperationManifest(
                RenderBoundOperationKind(enumConstruction.Kind),
                location.Line,
                location.Column,
                resultType,
                Ordinal: ordinal,
                EnclosingFunctionName: enumConstruction.EnclosingFunctionName,
                EnumType: BuildPublishedAbiTypeReference(enumConstruction.EnumType, module),
                VariantName: enumConstruction.VariantName,
                EnumMembers: enumConstruction.Members.Count == 0
                    ? null
                    : enumConstruction.Members
                        .Select(member => new StarkPackageTemplateEnumConstructorMemberManifest(
                            member.FieldName,
                            member.FieldIndex,
                            BuildPublishedAbiTypeReference(member.FieldType, module)))
                        .ToArray()),
            BoundEnumCallOperation enumCall => new StarkPackageTemplateBoundOperationManifest(
                RenderBoundOperationKind(enumCall.Kind),
                location.Line,
                location.Column,
                resultType,
                Ordinal: ordinal,
                EnclosingFunctionName: enumCall.EnclosingFunctionName,
                EnumType: BuildPublishedAbiTypeReference(enumCall.EnumType, module),
                VariantName: enumCall.VariantName),
            BoundEnumValueOperation enumValue => new StarkPackageTemplateBoundOperationManifest(
                RenderBoundOperationKind(enumValue.Kind),
                location.Line,
                location.Column,
                resultType,
                Ordinal: ordinal,
                EnclosingFunctionName: enumValue.EnclosingFunctionName,
                EnumType: BuildPublishedAbiTypeReference(enumValue.EnumType, module),
                VariantName: enumValue.VariantName),
            BoundTextInterpolationOperation interpolation => new StarkPackageTemplateBoundOperationManifest(
                RenderBoundOperationKind(interpolation.Kind),
                location.Line,
                location.Column,
                resultType,
                Ordinal: ordinal,
                EnclosingFunctionName: interpolation.EnclosingFunctionName,
                SegmentCount: interpolation.SegmentCount,
                HoleCount: interpolation.HoleCount,
                UsesFixedStorage: interpolation.UsesFixedStorage),
            BoundTextBuildOperation textBuild => new StarkPackageTemplateBoundOperationManifest(
                RenderBoundOperationKind(textBuild.Kind),
                location.Line,
                location.Column,
                resultType,
                Ordinal: ordinal,
                EnclosingFunctionName: textBuild.EnclosingFunctionName,
                BuildKind: textBuild.BuildKind,
                OperandCount: textBuild.OperandCount,
                UsesFixedStorage: textBuild.UsesFixedStorage),
            BoundLayoutQueryOperation layoutQuery => new StarkPackageTemplateBoundOperationManifest(
                RenderBoundOperationKind(layoutQuery.Kind),
                location.Line,
                location.Column,
                resultType,
                Ordinal: ordinal,
                EnclosingFunctionName: layoutQuery.EnclosingFunctionName,
                QueryKind: RenderBoundLayoutQueryKind(layoutQuery.QueryKind),
                TargetType: BuildPublishedAbiTypeReference(layoutQuery.TargetType, module)),
            BoundSwitchDispatchOperation switchDispatch => new StarkPackageTemplateBoundOperationManifest(
                RenderBoundOperationKind(switchDispatch.Kind),
                location.Line,
                location.Column,
                resultType,
                Ordinal: ordinal,
                EnclosingFunctionName: switchDispatch.EnclosingFunctionName,
                SwitchFamily: switchDispatch.Family,
                SwitchType: BuildPublishedAbiTypeReference(switchDispatch.SwitchType, module),
                SectionCount: switchDispatch.SectionCount,
                LabelCount: switchDispatch.LabelCount,
                ExplicitDefaultLabelCount: switchDispatch.ExplicitDefaultLabelCount,
                LoweredDefaultLabelCount: switchDispatch.LoweredDefaultLabelCount,
                LiteralLabelCount: switchDispatch.LiteralLabelCount,
                MatchAllLabelCount: switchDispatch.MatchAllLabelCount,
                CaptureLabelCount: switchDispatch.CaptureLabelCount,
                StructuredPatternLabelCount: switchDispatch.StructuredPatternLabelCount,
                GuardedLabelCount: switchDispatch.GuardedLabelCount),
            _ => new StarkPackageTemplateBoundOperationManifest(
                RenderBoundOperationKind(operation.Kind),
                location.Line,
                location.Column,
                resultType,
                Ordinal: ordinal,
                EnclosingFunctionName: operation.EnclosingFunctionName)
        };
    }

    private static StarkPackageTemplateBoundOperationManifest BuildPublishedTemplateCallBoundOperation(
        LoadedModuleDocument module,
        BoundOperationKind kind,
        SourceLocation location,
        StarkPackageTypeReference resultType,
        int? ordinal,
        string? enclosingFunctionName,
        TypedFunctionSignature signature,
        IReadOnlyList<CallArgumentTypingRecord> arguments,
        StarkPackageTypeReference? ReceiverType = null,
        bool? ReceiverIsAddressable = null,
        bool? ReceiverIsMutable = null)
    {
        return new StarkPackageTemplateBoundOperationManifest(
            RenderBoundOperationKind(kind),
            location.Line,
            location.Column,
            resultType,
            Ordinal: ordinal,
            EnclosingFunctionName: enclosingFunctionName,
            QualifiedResolvedName: QualifyPublishedCalledFunctionName(module, signature.Name),
            ReturnType: BuildPublishedAbiTypeReference(signature.ReturnType, module),
            Parameters: BuildPublishedTemplateTypedParameters(module, signature.Parameters),
            QualifiedSourceName: signature.SourceName is null
                ? null
                : QualifyPublishedCalledFunctionName(module, signature.SourceName),
            QualifiedTemplateName: signature.TemplateName is null
                ? null
                : QualifyPublishedCalledFunctionName(module, signature.TemplateName),
            TypeArguments: signature.TypeArguments is { Count: > 0 }
                ? signature.TypeArguments
                    .Select(typeArgument => BuildPublishedAbiTypeReference(typeArgument, module))
                    .ToArray()
                : null,
            DisjointParameterGroups: BuildParameterDisjointGroupManifests(signature.DisjointGroups),
            OverlapParameterGroups: BuildParameterOverlapGroupManifests(signature.OverlapGroups),
            SameParameterGroups: BuildParameterSameGroupManifests(signature.SameGroups),
            CallArguments: BuildPublishedTemplateCallArguments(module, arguments),
            ReceiverType: ReceiverType,
            ReceiverIsAddressable: ReceiverIsAddressable,
            ReceiverIsMutable: ReceiverIsMutable);
    }

    private static IReadOnlyList<StarkPackageTypedParameterManifest> BuildPublishedTemplateTypedParameters(
        LoadedModuleDocument module,
        IReadOnlyList<TypedParameterSymbol> parameters)
    {
        return parameters
            .Select(parameter => new StarkPackageTypedParameterManifest(
                parameter.Name,
                BuildPublishedAbiTypeReference(parameter.Type, module),
                parameter.IsDisjoint,
                parameter.IsConst,
                parameter.RawPointerElementCountExpression))
            .ToArray();
    }

    private static IReadOnlyList<StarkPackageTemplateCallArgumentManifest>? BuildPublishedTemplateCallArguments(
        LoadedModuleDocument module,
        IReadOnlyList<CallArgumentTypingRecord> arguments)
    {
        return arguments.Count == 0
            ? null
            : arguments
                .Select(argument => new StarkPackageTemplateCallArgumentManifest(
                    argument.ParameterIndex,
                    argument.SourceArgumentIndex,
                    BuildPublishedAbiTypeReference(argument.ParameterType, module),
                    BuildPublishedAbiTypeReference(argument.ArgumentType, module),
                    argument.IsReceiver,
                    argument.RequiresAddressable,
                    argument.RequiresMutable,
                    argument.RequiresConstProvenance,
                    argument.ArgumentIsAddressable,
                    argument.ArgumentIsMutable,
                    argument.ArgumentHasConstProvenance))
                .ToArray();
    }

    private static string RenderBoundOperationKind(BoundOperationKind kind)
    {
        return kind switch
        {
            BoundOperationKind.DirectCall => "direct-call",
            BoundOperationKind.MemberCall => "member-call",
            BoundOperationKind.FunctionPointerCall => "function-pointer-call",
            BoundOperationKind.ClosureCall => "closure-call",
            BoundOperationKind.IndexAccess => "index-access",
            BoundOperationKind.SliceAccess => "slice-access",
            BoundOperationKind.ObjectCreation => "object-creation",
            BoundOperationKind.EnumConstruction => "enum-construction",
            BoundOperationKind.EnumCall => "enum-call",
            BoundOperationKind.EnumValue => "enum-value",
            BoundOperationKind.DynamicStorageOperation => "dynamic-storage-operation",
            BoundOperationKind.TextInterpolation => "text-interpolation",
            BoundOperationKind.TextBuild => "text-build",
            BoundOperationKind.LayoutQuery => "layout-query",
            BoundOperationKind.SwitchDispatch => "switch-dispatch",
            _ => kind.ToString()
        };
    }

    private static string RenderBoundIndexAccessKind(BoundIndexAccessKind kind)
    {
        return kind switch
        {
            BoundIndexAccessKind.Element => "element",
            BoundIndexAccessKind.Slice => "slice",
            BoundIndexAccessKind.TextElement => "text-element",
            BoundIndexAccessKind.TextSlice => "text-slice",
            BoundIndexAccessKind.DynamicElement => "dynamic-element",
            BoundIndexAccessKind.DynamicSlice => "dynamic-slice",
            BoundIndexAccessKind.RawPointerRegion => "raw-pointer-region",
            _ => kind.ToString()
        };
    }

    private static string RenderBoundLayoutQueryKind(BoundLayoutQueryKind kind)
    {
        return kind switch
        {
            BoundLayoutQueryKind.AlignOf => "alignof",
            _ => "sizeof"
        };
    }

    private static IReadOnlyList<StarkParser.ObjectCreationExpressionContext> CollectTrackedTemplateObjectCreations(ParserRuleContext node)
    {
        var objectCreations = new List<StarkParser.ObjectCreationExpressionContext>();
        Collect(node, objectCreations);
        return objectCreations;

        static void Collect(
            Antlr4.Runtime.Tree.IParseTree current,
            List<StarkParser.ObjectCreationExpressionContext> accumulator)
        {
            if (current is StarkParser.ObjectCreationExpressionContext objectCreation
                && ShouldTrackObjectCreation(objectCreation))
            {
                accumulator.Add(objectCreation);
            }

            for (var index = 0; index < current.ChildCount; index++)
            {
                Collect(current.GetChild(index), accumulator);
            }
        }
    }

    private static bool ShouldTrackObjectCreation(StarkParser.ObjectCreationExpressionContext expression)
    {
        return expression.type_() is null
            || expression.objectInitializer() is not null
            || expression.argumentList() is { } argumentList && argumentList.argument().Length > 0;
    }

    private static bool TryBuildPublishedTypedTemplateTypeLayoutExpression(
        LoadedModuleDocument module,
        StarkParser.PrimaryExpressionContext primaryExpression,
        out StarkPackageTypedTemplateExpressionManifest publishedExpression)
    {
        publishedExpression = null!;

        var name = primaryExpression.SIZEOF() is not null
            ? "sizeof"
            : primaryExpression.ALIGNOF() is not null
                ? "alignof"
                : null;
        if (name is null
            || primaryExpression.type_() is not { } type
            || !TryBuildPublishedAbiTypeReferenceFromSyntax(module, type, out var typeReference))
        {
            return false;
        }

        publishedExpression = new StarkPackageTypedTemplateExpressionManifest(
            Kind: "type-layout",
            Name: name,
            Type: typeReference);
        return true;
    }

    private static bool TryBuildPublishedAbiTypeReferenceFromSyntax(
        LoadedModuleDocument module,
        StarkParser.Type_Context type,
        out StarkPackageTypeReference typeReference)
    {
        typeReference = null!;

        if (!TryBuildPublishedAbiNonArrayTypeReferenceFromSyntax(module, type.nonArrayType(), out var current))
        {
            return false;
        }

        foreach (var suffix in type.arraySuffix())
        {
            if (suffix.expression() is null)
            {
                current = new StarkPackageTypeReference(
                    "slice",
                    ElementType: current);
                continue;
            }

            if (!CompileTimeExpressionEvaluator.TryEvaluateInteger(suffix.expression(), out var length)
                || length < 0
                || length > int.MaxValue)
            {
                return false;
            }

            current = new StarkPackageTypeReference(
                "fixedarray",
                FixedLength: (int)length,
                ElementType: current);
        }

        foreach (var qualifier in type.typeQualifier())
        {
            current = qualifier.GetText() switch
            {
                "borrow" => current with { BorrowKind = "borrow" },
                "retborrow" => current with { BorrowKind = "retborrow" },
                "storeborrow" => current with { BorrowKind = "storeborrow" },
                "frozen" => current with { AccessKind = "frozen" },
                "shared" => current with { AccessKind = "shared" },
                "out" => current with { InitializationKind = "out" },
                "init" => current with { InitializationKind = "init" },
                "mut" => current with { IsMutableView = true },
                _ => current
            };
        }

        typeReference = current;
        return true;
    }

    private static bool TryBuildPublishedAbiNonArrayTypeReferenceFromSyntax(
        LoadedModuleDocument module,
        StarkParser.NonArrayTypeContext type,
        out StarkPackageTypeReference typeReference)
    {
        typeReference = null!;

        if (type.rawPointerType() is { } rawPointerType)
        {
            if (!TryBuildPublishedAbiTypeReferenceFromSyntax(module, rawPointerType.type_(), out var elementType))
            {
                return false;
            }

            typeReference = new StarkPackageTypeReference(
                "rawpointer",
                IsMutablePointer: rawPointerType.RAWMUTPTR() is not null,
                ElementType: elementType);
            return true;
        }

        if (type.dynamicType() is { } dynamicType)
        {
            if (!TryBuildPublishedAbiTypeReferenceFromSyntax(module, dynamicType.type_(), out var elementType))
            {
                return false;
            }

            typeReference = new StarkPackageTypeReference(
                "dynamic",
                ElementType: elementType);
            return true;
        }

        if (type.functionPointerType() is { } functionPointerType)
        {
            var signature = functionPointerType.functionPointerSignature();
            StarkPackageTypeReference returnType;
            if (signature.returnType().VOID() is not null)
            {
                returnType = new StarkPackageTypeReference("void");
            }
            else if (!TryBuildPublishedAbiTypeReferenceFromSyntax(module, signature.returnType().type_(), out returnType))
            {
                return false;
            }

            var parameterTypes = new List<StarkPackageTypeReference>();
            foreach (var parameter in signature.functionPointerParameterList().type_())
            {
                if (!TryBuildPublishedAbiTypeReferenceFromSyntax(module, parameter, out var parameterType))
                {
                    return false;
                }

                parameterTypes.Add(parameterType);
            }

            typeReference = new StarkPackageTypeReference(
                "functionpointer",
                FunctionKind: RenderPublishedFunctionPointerKind(signature.functionKind().GetText()),
                ReturnType: returnType,
                ParameterTypes: parameterTypes.Count == 0 ? null : parameterTypes.ToArray(),
                OverlapParameterGroups: BuildParameterOverlapGroupManifests(signature.parameterMemoryContractClause()),
                SameParameterGroups: BuildParameterSameGroupManifests(signature.parameterMemoryContractClause()));
            return true;
        }

        if (type.integerType() is { } integerType)
        {
            var text = integerType.INTEGER_TYPE().GetText();
            if (text.Length < 2 || !int.TryParse(text[1..], out var bitWidth))
            {
                return false;
            }

            typeReference = new StarkPackageTypeReference(
                "integer",
                BitWidth: bitWidth,
                IsUnsigned: text[0] == 'u' ? true : null);
            return true;
        }

        return type.simpleType() is { } simpleType
            && TryBuildPublishedAbiSimpleTypeReferenceFromSyntax(module, simpleType, out typeReference);
    }

    private static string RenderPublishedFunctionPointerKind(string kind)
    {
        return kind switch
        {
            "finite" => "finite",
            "law" => "law",
            "finitelaw" => "finite law",
            _ => "fn"
        };
    }

    private static bool TryBuildPublishedAbiSimpleTypeReferenceFromSyntax(
        LoadedModuleDocument module,
        StarkParser.SimpleTypeContext type,
        out StarkPackageTypeReference typeReference)
    {
        typeReference = null!;

        if (type.builtinType() is { } builtinType)
        {
            var builtinText = builtinType.GetText();
            if (builtinText.Length >= 2 && builtinText[0] == 'f' && int.TryParse(builtinText[1..], out var floatBitWidth))
            {
                typeReference = new StarkPackageTypeReference("float", BitWidth: floatBitWidth);
                return true;
            }

            typeReference = builtinText switch
            {
                "bool" => new StarkPackageTypeReference("bool"),
                "ascii" => new StarkPackageTypeReference("ascii"),
                "unicode" => new StarkPackageTypeReference("unicode"),
                "asciistring" => BuildPublishedAbiTypeReference(StarkTypeSymbols.OwnedAscii, module),
                "unicodestring" => BuildPublishedAbiTypeReference(StarkTypeSymbols.OwnedUnicode, module),
                _ => null!
            };
            return typeReference is not null;
        }

        var name = type.qualifiedName()?.GetText();
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var localNamedTypes = GetModuleLocalNamedTypes(module);
        var qualifiedName = name.Contains('.', StringComparison.Ordinal) || !localNamedTypes.Contains(name)
            ? name
            : $"{module.SyntaxModel.ModuleName}.{name}";
        var typeArguments = new List<StarkPackageTypeReference>();
        var typeArgumentSyntax = type.typeArgumentList()?.type_() ?? Array.Empty<StarkParser.Type_Context>();
        foreach (var typeArgument in typeArgumentSyntax)
        {
            if (!TryBuildPublishedAbiTypeReferenceFromSyntax(module, typeArgument, out var publishedTypeArgument))
            {
                return false;
            }

            typeArguments.Add(publishedTypeArgument);
        }

        typeReference = new StarkPackageTypeReference(
            "named",
            Name: qualifiedName,
            TypeArguments: typeArguments.Count == 0 ? null : typeArguments);
        return true;
    }

    private static IReadOnlyList<StarkParser.ArgumentListContext> CollectTemplateDirectCallArgumentLists(ParserRuleContext node)
    {
        var directCalls = new List<StarkParser.ArgumentListContext>();
        Collect(node, directCalls);
        return directCalls;

        static void Collect(
            Antlr4.Runtime.Tree.IParseTree current,
            List<StarkParser.ArgumentListContext> accumulator)
        {
            if (current is StarkParser.PostfixExpressionContext postfixExpression
                && postfixExpression.postfixPart().Length > 0
                && postfixExpression.postfixPart()[0].argumentList() is { } argumentList)
            {
                accumulator.Add(argumentList);
            }

            for (var index = 0; index < current.ChildCount; index++)
            {
                Collect(current.GetChild(index), accumulator);
            }
        }
    }

    private static IReadOnlyList<StarkParser.ArgumentListContext> CollectTemplateArgumentListsByPublishedLocations(
        ParserRuleContext node,
        IEnumerable<string> publishedLocations)
    {
        var locationSet = publishedLocations.ToHashSet(StringComparer.Ordinal);
        if (locationSet.Count == 0)
        {
            return [];
        }

        var argumentLists = new List<StarkParser.ArgumentListContext>();
        Collect(node, argumentLists, locationSet);
        return argumentLists;

        static void Collect(
            Antlr4.Runtime.Tree.IParseTree current,
            List<StarkParser.ArgumentListContext> accumulator,
            IReadOnlySet<string> locationSet)
        {
            if (current is StarkParser.ArgumentListContext argumentList
                && locationSet.Contains(TemplateDirectCallFacts.BuildLookupKey(argumentList.Start.Line, argumentList.Start.Column + 1)))
            {
                accumulator.Add(argumentList);
            }

            for (var index = 0; index < current.ChildCount; index++)
            {
                Collect(current.GetChild(index), accumulator, locationSet);
            }
        }
    }

    private static IReadOnlyList<StarkParser.PrimaryExpressionContext> CollectTemplateFunctionAddressPrimaryExpressions(ParserRuleContext node)
    {
        var expressions = new List<StarkParser.PrimaryExpressionContext>();
        Collect(node, expressions);
        return expressions;

        static void Collect(
            Antlr4.Runtime.Tree.IParseTree current,
            List<StarkParser.PrimaryExpressionContext> accumulator)
        {
            if (current is StarkParser.PrimaryExpressionContext primaryExpression
                && (primaryExpression.Identifier() is not null || primaryExpression.qualifiedName() is not null))
            {
                accumulator.Add(primaryExpression);
            }

            for (var index = 0; index < current.ChildCount; index++)
            {
                Collect(current.GetChild(index), accumulator);
            }
        }
    }

    private static bool IsTextBufferType(StarkTypeSymbol type)
    {
        return type.Kind == StarkTypeKind.Named
            && type.NamedType is StarkTypeSymbols.OwnedAsciiName or StarkTypeSymbols.OwnedUnicodeName;
    }

    private static bool TryGetPublishedStorageCapacity(
        StarkParser.VariableDeclaratorContext declarator,
        IReadOnlyDictionary<string, LocalStorageCapacityTypingRecord> storageCapacitiesByLocation,
        out int capacity)
    {
        capacity = 0;
        var key = TemplateDirectCallFacts.BuildLookupKey(declarator.Start.Line, declarator.Start.Column + 1);
        if (!storageCapacitiesByLocation.TryGetValue(key, out var record)
            || !string.Equals(record.Name, declarator.Identifier().GetText(), StringComparison.Ordinal)
            || record.Capacity <= 0)
        {
            return false;
        }

        capacity = record.Capacity;
        return true;
    }

    private static StarkParser.AdditiveExpressionContext? TryGetStandaloneAdditiveExpression(StarkParser.ExpressionContext expression)
    {
        var assignment = expression.assignmentExpression();
        if (assignment.assignmentOperator() is not null || assignment.conditionalExpression() is not { } conditional)
        {
            return null;
        }

        if (conditional.expression().Length != 0)
        {
            return null;
        }

        var logicalOr = conditional.logicalOrExpression();
        if (logicalOr.logicalAndExpression().Length != 1)
        {
            return null;
        }

        var logicalAnd = logicalOr.logicalAndExpression(0);
        if (logicalAnd.bitwiseOrExpression().Length != 1)
        {
            return null;
        }

        var bitwiseOr = logicalAnd.bitwiseOrExpression(0);
        if (bitwiseOr.bitwiseXorExpression().Length != 1)
        {
            return null;
        }

        var bitwiseXor = bitwiseOr.bitwiseXorExpression(0);
        if (bitwiseXor.bitwiseAndExpression().Length != 1)
        {
            return null;
        }

        var bitwiseAnd = bitwiseXor.bitwiseAndExpression(0);
        if (bitwiseAnd.equalityExpression().Length != 1)
        {
            return null;
        }

        var equality = bitwiseAnd.equalityExpression(0);
        if (equality.relationalExpression().Length != 1)
        {
            return null;
        }

        var relational = equality.relationalExpression(0);
        if (relational.shiftExpression().Length != 1)
        {
            return null;
        }

        var shift = relational.shiftExpression(0);
        return shift.additiveExpression().Length == 1
            ? shift.additiveExpression(0)
            : null;
    }

    private static StarkParser.LiteralContext? TryGetStandaloneInterpolatedTextLiteral(StarkParser.ExpressionContext expression)
    {
        var additive = TryGetStandaloneAdditiveExpression(expression);
        if (additive is null || additive.multiplicativeExpression().Length != 1)
        {
            return null;
        }

        var multiplicative = additive.multiplicativeExpression(0);
        if (multiplicative.unaryExpression().Length != 1)
        {
            return null;
        }

        var unary = multiplicative.unaryExpression(0);
        if (unary.powerExpression() is not { } power
            || power.unaryExpression() is not null
            || power.postfixExpression() is not { } postfix
            || postfix.postfixPart().Length != 0)
        {
            return null;
        }

        var literal = postfix.primaryExpression().literal();
        return literal?.DOLLAR() is not null && literal.StringLiteral() is not null
            ? literal
            : null;
    }

    private static StarkParser.PostfixExpressionContext? TryGetSimplePostfixExpression(StarkParser.ExpressionContext expression)
    {
        var assignment = expression.assignmentExpression();
        if (assignment.assignmentOperator() is not null || assignment.conditionalExpression() is not { } conditional)
        {
            return null;
        }

        if (conditional.expression().Length != 0)
        {
            return null;
        }

        var logicalOr = conditional.logicalOrExpression();
        if (logicalOr.logicalAndExpression().Length != 1)
        {
            return null;
        }

        var logicalAnd = logicalOr.logicalAndExpression(0);
        if (logicalAnd.bitwiseOrExpression().Length != 1)
        {
            return null;
        }

        var bitwiseOr = logicalAnd.bitwiseOrExpression(0);
        if (bitwiseOr.bitwiseXorExpression().Length != 1)
        {
            return null;
        }

        var bitwiseXor = bitwiseOr.bitwiseXorExpression(0);
        if (bitwiseXor.bitwiseAndExpression().Length != 1)
        {
            return null;
        }

        var bitwiseAnd = bitwiseXor.bitwiseAndExpression(0);
        if (bitwiseAnd.equalityExpression().Length != 1)
        {
            return null;
        }

        var equality = bitwiseAnd.equalityExpression(0);
        if (equality.relationalExpression().Length != 1)
        {
            return null;
        }

        var relational = equality.relationalExpression(0);
        if (relational.shiftExpression().Length != 1)
        {
            return null;
        }

        var shift = relational.shiftExpression(0);
        if (shift.additiveExpression().Length != 1)
        {
            return null;
        }

        var additive = shift.additiveExpression(0);
        if (additive.multiplicativeExpression().Length != 1)
        {
            return null;
        }

        var multiplicative = additive.multiplicativeExpression(0);
        if (multiplicative.unaryExpression().Length != 1)
        {
            return null;
        }

        return TryGetSimplePostfixExpression(multiplicative.unaryExpression(0));
    }

    private static StarkParser.PostfixExpressionContext? TryGetSimplePostfixExpression(StarkParser.UnaryExpressionContext expression)
    {
        if (expression.powerExpression() is not { } powerExpression
            || powerExpression.unaryExpression() is not null)
        {
            return null;
        }

        return powerExpression.postfixExpression();
    }

    private static string BuildTemplateEnumConstructorLookupKey(int line, int column)
    {
        return $"{line}:{column}";
    }

    private static string BuildTemplateLiteralLookupKey(int line, int column)
    {
        return $"{line}:{column}";
    }

    private static IReadOnlyList<StarkParser.EnumConstructorExpressionContext> CollectTemplateEnumConstructorExpressions(ParserRuleContext node)
    {
        var enumConstructors = new List<StarkParser.EnumConstructorExpressionContext>();
        Collect(node, enumConstructors);
        return enumConstructors;

        static void Collect(
            Antlr4.Runtime.Tree.IParseTree current,
            List<StarkParser.EnumConstructorExpressionContext> accumulator)
        {
            if (current is StarkParser.EnumConstructorExpressionContext enumConstructor)
            {
                accumulator.Add(enumConstructor);
            }

            for (var index = 0; index < current.ChildCount; index++)
            {
                Collect(current.GetChild(index), accumulator);
            }
        }
    }

    private static string BuildTemplateEnumValueLookupKey(ParserRuleContext context)
    {
        var (line, column) = GetTemplateEnumValueLineColumn(context);
        return TemplateDirectCallFacts.BuildLookupKey(line, column);
    }

    private static (int Line, int Column) GetTemplateEnumValueLineColumn(ParserRuleContext context)
    {
        if (context is StarkParser.PostfixPartContext postfixPart
            && postfixPart.Identifier()?.Symbol is { } identifier)
        {
            return (identifier.Line, identifier.Column + 1);
        }

        return (context.Start.Line, context.Start.Column + 1);
    }

    private static IReadOnlyList<PublishedTemplateEnumValueReference> CollectPublishedTemplateEnumValueReferences(
        LoadedModuleDocument module,
        ParserRuleContext node,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes)
    {
        var references = new List<PublishedTemplateEnumValueReference>();
        Collect(node, references);
        return references;

        void Collect(
            Antlr4.Runtime.Tree.IParseTree current,
            List<PublishedTemplateEnumValueReference> accumulator)
        {
            if (current is StarkParser.PostfixExpressionContext postfixExpression
                && TryResolvePublishedTemplateEnumValueReference(module, postfixExpression, namedTypes, out var reference))
            {
                accumulator.Add(reference);
            }

            for (var index = 0; index < current.ChildCount; index++)
            {
                Collect(current.GetChild(index), accumulator);
            }
        }
    }

    private static bool TryResolvePublishedTemplateEnumValueReference(
        LoadedModuleDocument module,
        StarkParser.PostfixExpressionContext postfixExpression,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        out PublishedTemplateEnumValueReference reference)
    {
        reference = null!;

        var primaryExpression = postfixExpression.primaryExpression();
        var postfixParts = postfixExpression.postfixPart();
        if (primaryExpression is null || postfixParts.Length != 1)
        {
            return false;
        }

        var variantPart = postfixParts[0];
        var enumTypeName = primaryExpression.Identifier()?.GetText()
            ?? primaryExpression.qualifiedName()?.GetText();
        var variantName = variantPart.Identifier()?.GetText();
        if (enumTypeName is null || variantName is null)
        {
            return false;
        }

        if (!TryResolvePublishedTemplateEnumType(module, namedTypes, enumTypeName, out var enumType))
        {
            return false;
        }

        var variant = enumType.EnumVariants?.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, variantName, StringComparison.Ordinal));
        if (variant is null || !variant.IsUnit)
        {
            return false;
        }

        reference = new PublishedTemplateEnumValueReference(
            variantPart,
            StarkTypeSymbols.Named(enumType.Name),
            variant.Name);
        return true;
    }

    private static IReadOnlyList<PublishedTemplateEnumCallReference> CollectPublishedTemplateEnumCallReferences(
        LoadedModuleDocument module,
        ParserRuleContext node,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes)
    {
        var references = new List<PublishedTemplateEnumCallReference>();
        Collect(node, references);
        return references;

        void Collect(
            Antlr4.Runtime.Tree.IParseTree current,
            List<PublishedTemplateEnumCallReference> accumulator)
        {
            if (current is StarkParser.PostfixExpressionContext postfixExpression
                && TryResolvePublishedTemplateEnumCallReference(module, postfixExpression, namedTypes, out var reference))
            {
                accumulator.Add(reference);
            }

            for (var index = 0; index < current.ChildCount; index++)
            {
                Collect(current.GetChild(index), accumulator);
            }
        }
    }

    private static bool TryResolvePublishedTemplateEnumCallReference(
        LoadedModuleDocument module,
        StarkParser.PostfixExpressionContext postfixExpression,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        out PublishedTemplateEnumCallReference reference)
    {
        reference = null!;

        var primaryExpression = postfixExpression.primaryExpression();
        var postfixParts = postfixExpression.postfixPart();
        if (primaryExpression is null || postfixParts.Length != 2)
        {
            return false;
        }

        var variantPart = postfixParts[0];
        var argumentList = postfixParts[1].argumentList();
        var enumTypeName = primaryExpression.Identifier()?.GetText()
            ?? primaryExpression.qualifiedName()?.GetText();
        var variantName = variantPart.Identifier()?.GetText();
        if (enumTypeName is null || variantName is null || argumentList is null)
        {
            return false;
        }

        if (!TryResolvePublishedTemplateEnumType(module, namedTypes, enumTypeName, out var enumType))
        {
            return false;
        }

        var variant = enumType.EnumVariants?.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, variantName, StringComparison.Ordinal));
        if (variant is null || variant.IsUnit)
        {
            return false;
        }

        reference = new PublishedTemplateEnumCallReference(
            argumentList,
            StarkTypeSymbols.Named(enumType.Name),
            variant.Name);
        return true;
    }

    private static bool TryResolvePublishedTemplateEnumType(
        LoadedModuleDocument module,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        string sourceName,
        out NamedTypeSymbol enumType)
    {
        if (namedTypes.TryGetValue(sourceName, out enumType!)
            && enumType.Kind == DeclarationKind.Enum)
        {
            return true;
        }

        var qualifiedName = sourceName.Contains('.', StringComparison.Ordinal)
            ? sourceName
            : $"{module.SyntaxModel.ModuleName}.{sourceName}";
        if (namedTypes.TryGetValue(qualifiedName, out enumType!)
            && enumType.Kind == DeclarationKind.Enum)
        {
            return true;
        }

        enumType = null!;
        return false;
    }

    private static IReadOnlyList<ParserRuleContext> CollectTemplateEnumValueContexts(ParserRuleContext node)
    {
        var enumValues = new List<ParserRuleContext>();
        Collect(node, enumValues);
        return enumValues;

        static void Collect(
            Antlr4.Runtime.Tree.IParseTree current,
            List<ParserRuleContext> accumulator)
        {
            if (current is StarkParser.PrimaryExpressionContext primaryExpression
                && (primaryExpression.genericEnumCaseReference() is not null
                    || primaryExpression.qualifiedName() is not null
                    || primaryExpression.Identifier() is not null))
            {
                accumulator.Add(primaryExpression);
            }
            else if (current is StarkParser.PostfixPartContext postfixPart
                     && postfixPart.Identifier() is not null)
            {
                accumulator.Add(postfixPart);
            }

            for (var index = 0; index < current.ChildCount; index++)
            {
                Collect(current.GetChild(index), accumulator);
            }
        }
    }

    private static IReadOnlyList<ParserRuleContext> CollectTemplateEnumPatternContexts(ParserRuleContext node)
    {
        var enumPatterns = new List<ParserRuleContext>();
        Collect(node, enumPatterns);
        return enumPatterns;

        static void Collect(
            Antlr4.Runtime.Tree.IParseTree current,
            List<ParserRuleContext> accumulator)
        {
            switch (current)
            {
                case StarkParser.EnumNamedFieldPatternContext enumNamedFieldPattern:
                    accumulator.Add(enumNamedFieldPattern);
                    break;
                case StarkParser.GenericEnumAggregatePatternContext genericEnumAggregatePattern:
                    accumulator.Add(genericEnumAggregatePattern);
                    break;
                case StarkParser.AggregatePatternContext aggregatePattern:
                    accumulator.Add(aggregatePattern);
                    break;
            }

            for (var index = 0; index < current.ChildCount; index++)
            {
                Collect(current.GetChild(index), accumulator);
            }
        }
    }

    private static string BuildTemplateConversionLookupKey(int line, int column)
    {
        return $"{line}:{column}";
    }

    private static IReadOnlyList<StarkParser.UnaryExpressionContext> CollectTemplateConversionExpressions(ParserRuleContext node)
    {
        var conversions = new List<StarkParser.UnaryExpressionContext>();
        Collect(node, conversions);
        return conversions;

        static void Collect(
            Antlr4.Runtime.Tree.IParseTree current,
            List<StarkParser.UnaryExpressionContext> accumulator)
        {
            if (current is StarkParser.UnaryExpressionContext unaryExpression
                && unaryExpression.conversionType() is not null)
            {
                accumulator.Add(unaryExpression);
            }

            for (var index = 0; index < current.ChildCount; index++)
            {
                Collect(current.GetChild(index), accumulator);
            }
        }
    }

    private static IReadOnlyList<StarkParser.PostfixPartContext> CollectTemplateMemberAccessParts(ParserRuleContext node)
    {
        var memberAccesses = new List<StarkParser.PostfixPartContext>();
        Collect(node, memberAccesses);
        return memberAccesses;

        static void Collect(
            Antlr4.Runtime.Tree.IParseTree current,
            List<StarkParser.PostfixPartContext> accumulator)
        {
            if (current is StarkParser.PostfixExpressionContext postfixExpression)
            {
                foreach (var postfixPart in postfixExpression.postfixPart())
                {
                    if (postfixPart.Identifier() is not null)
                    {
                        accumulator.Add(postfixPart);
                    }
                }
            }

            for (var index = 0; index < current.ChildCount; index++)
            {
                Collect(current.GetChild(index), accumulator);
            }
        }
    }

    private static IReadOnlyList<StarkParser.ArgumentListContext> CollectTemplateMemberCallArgumentLists(ParserRuleContext node)
    {
        var memberCalls = new List<StarkParser.ArgumentListContext>();
        Collect(node, memberCalls);
        return memberCalls;

        static void Collect(
            Antlr4.Runtime.Tree.IParseTree current,
            List<StarkParser.ArgumentListContext> accumulator)
        {
            if (current is StarkParser.PostfixExpressionContext postfixExpression)
            {
                var postfixParts = postfixExpression.postfixPart();
                for (var index = 0; index + 1 < postfixParts.Length; index++)
                {
                    if (postfixParts[index].Identifier() is not null
                        && postfixParts[index + 1].argumentList() is { } argumentList)
                    {
                        accumulator.Add(argumentList);
                    }
                }
            }

            for (var index = 0; index < current.ChildCount; index++)
            {
                Collect(current.GetChild(index), accumulator);
            }
        }
    }

    private static IReadOnlyList<StarkParser.PostfixPartContext> CollectTemplateIndexAccessParts(ParserRuleContext node)
    {
        var indexes = new List<StarkParser.PostfixPartContext>();
        Collect(node, indexes);
        return indexes;

        static void Collect(
            Antlr4.Runtime.Tree.IParseTree current,
            List<StarkParser.PostfixPartContext> accumulator)
        {
            if (current is StarkParser.PostfixPartContext postfixPart
                && postfixPart.LBRACK() is not null)
            {
                accumulator.Add(postfixPart);
            }

            for (var index = 0; index < current.ChildCount; index++)
            {
                Collect(current.GetChild(index), accumulator);
            }
        }
    }

    private static IReadOnlyList<StarkParser.PrimaryExpressionContext> CollectTemplateTypeLayoutPrimaryExpressions(ParserRuleContext node)
    {
        var expressions = new List<StarkParser.PrimaryExpressionContext>();
        Collect(node, expressions);
        return expressions;

        static void Collect(
            Antlr4.Runtime.Tree.IParseTree current,
            List<StarkParser.PrimaryExpressionContext> accumulator)
        {
            if (current is StarkParser.PrimaryExpressionContext primaryExpression
                && (primaryExpression.SIZEOF() is not null || primaryExpression.ALIGNOF() is not null))
            {
                accumulator.Add(primaryExpression);
            }

            for (var index = 0; index < current.ChildCount; index++)
            {
                Collect(current.GetChild(index), accumulator);
            }
        }
    }

    private static IReadOnlyList<StarkParser.SwitchStatementContext> CollectTemplateSwitchStatements(ParserRuleContext node)
    {
        var switches = new List<StarkParser.SwitchStatementContext>();
        Collect(node, switches);
        return switches;

        static void Collect(
            Antlr4.Runtime.Tree.IParseTree current,
            List<StarkParser.SwitchStatementContext> accumulator)
        {
            if (current is StarkParser.SwitchStatementContext switchStatement)
            {
                accumulator.Add(switchStatement);
            }

            for (var index = 0; index < current.ChildCount; index++)
            {
                Collect(current.GetChild(index), accumulator);
            }
        }
    }

    private static string BuildTemplateObjectCreationLookupKey(string expressionText, int line, int column)
    {
        return $"{line}:{column}:{expressionText}";
    }
}
