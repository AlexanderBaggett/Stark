using Stark.Parsing;

namespace Stark.Compiler;

internal static partial class PackageImageBuilder
{
    private static StarkPackageSourceSurfaceSection BuildSourceSurfaceSection(
        LoadedModuleDocument module,
        IReadOnlyList<StarkPackageImportManifest> imports,
        IReadOnlyList<StarkPackageReExportManifest> reExports,
        IReadOnlyList<StarkPackageFunctionManifest> functions,
        IReadOnlyList<StarkPackageTypeManifest> types,
        IReadOnlyList<StarkPackageGlobalManifest> globals,
        IReadOnlyList<StarkPackageTypeAliasManifest> typeAliases)
    {
        var functionQueues = functions
            .GroupBy(static function => function.Name, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => new Queue<StarkPackageFunctionManifest>(group),
                StringComparer.Ordinal);
        var functionSyntaxQueues = BuildFunctionDeclarationSyntaxLookup(module);
        var typeLookup = types.ToDictionary(static type => type.Name, StringComparer.Ordinal);
        var globalSyntaxLookup = BuildGlobalDeclarationSyntaxLookup(module);
        var globalLookup = globals.ToDictionary(static global => global.Name, StringComparer.Ordinal);
        var typeAliasLookup = typeAliases.ToDictionary(static alias => alias.Name, StringComparer.Ordinal);
        var typeAliasSyntaxLookup = BuildTypeAliasDeclarationSyntaxLookup(module);

        var sourceFunctions = new List<StarkPackageFunctionManifest>();
        var sourceTypes = new List<StarkPackageTypeManifest>();
        var sourceGlobals = new List<StarkPackageGlobalManifest>();
        var sourceTypeAliases = new List<StarkPackageTypeAliasManifest>();

        foreach (var declaration in module.SyntaxModel.Declarations
                     .Where(static declaration => ShouldIncludeInPackageImageSurface(declaration.Visibility))
                     .OrderBy(static declaration => declaration.Name, StringComparer.Ordinal))
        {
            switch (declaration.Kind)
            {
                case DeclarationKind.Function when declaration.Function is not null && !declaration.Name.Contains('.', StringComparison.Ordinal):
                    if (functionQueues.TryGetValue(declaration.Name, out var functionQueue)
                        && functionSyntaxQueues.TryGetValue(declaration.Name, out var functionSyntaxQueue)
                        && functionSyntaxQueue.Count != 0
                        && functionQueue.Count != 0)
                    {
                        sourceFunctions.Add(BuildSourceSurfaceFunctionManifest(module, functionQueue.Dequeue(), functionSyntaxQueue.Dequeue()));
                    }

                    break;

                case DeclarationKind.Struct:
                case DeclarationKind.Record:
                case DeclarationKind.Enum:
                case DeclarationKind.Trait:
                case DeclarationKind.Doctrine:
                    if (typeLookup.TryGetValue(declaration.Name, out var typeManifest))
                    {
                        sourceTypes.Add(BuildSourceSurfaceTypeManifest(module, declaration.Name, typeManifest));
                    }

                    break;

                case DeclarationKind.GlobalConstant:
                case DeclarationKind.GlobalVariable:
                    if (globalLookup.TryGetValue(declaration.Name, out var globalManifest))
                    {
                        sourceGlobals.Add(globalSyntaxLookup.TryGetValue(declaration.Name, out var globalTypeSyntax) && globalTypeSyntax is not null
                            ? globalManifest with
                        {
                            Type = GetContextSourceText(module.ParseResult, globalTypeSyntax)
                        }
                            : globalManifest);
                    }

                    break;

                case DeclarationKind.TypeAlias when declaration.TypeAlias is not null:
                    if (GenericTemplatePublicationPolicy.HasPublishedApiVisibility(declaration.Visibility)
                        && typeAliasLookup.TryGetValue(declaration.Name, out var typeAliasManifest)
                        && typeAliasSyntaxLookup.TryGetValue(declaration.Name, out var typeAliasSyntax))
                    {
                        sourceTypeAliases.Add(typeAliasManifest with
                        {
                            TargetType = GetContextSourceText(module.ParseResult, typeAliasSyntax.type_()),
                            GenericParameters = typeAliasSyntax.typeParameterList() is { } typeParameterList
                                ? typeParameterList.typeParameter()
                                    .Where(static parameter => parameter.COMPTIME() is null)
                                    .Select(static parameter => parameter.Identifier().GetText())
                                    .ToArray()
                                : null
                        });
                    }

                    break;
            }
        }

        return new StarkPackageSourceSurfaceSection(
            Imports: imports.Count == 0 ? null : imports,
            ReExports: reExports.Count == 0 ? null : reExports,
            Functions: sourceFunctions.Count == 0 ? null : sourceFunctions,
            Types: sourceTypes.Count == 0 ? null : sourceTypes,
            Globals: sourceGlobals.Count == 0 ? null : sourceGlobals,
            TypeAliases: sourceTypeAliases.Count == 0 ? null : sourceTypeAliases);
    }

    private static StarkPackageFunctionManifest BuildSourceSurfaceFunctionManifest(
        LoadedModuleDocument module,
        StarkPackageFunctionManifest manifest,
        StarkParser.FunctionDeclarationContext functionDeclaration)
    {
        return manifest with
        {
            ReturnType = GetContextSourceText(module.ParseResult, functionDeclaration.returnType()),
            Parameters = functionDeclaration.parameterList().parameter()
                .Select(parameter => new StarkPackageParameterManifest(
                    parameter.Identifier().GetText(),
                    GetContextSourceText(module.ParseResult, parameter.type_()),
                    ParameterHasPrefix(parameter, StarkParser.DISJOINT),
                    ParameterHasPrefix(parameter, StarkParser.CONST)))
                .ToArray(),
            GenericParameters = functionDeclaration.typeParameterList() is { } typeParameterList
                ? typeParameterList.typeParameter()
                    .Where(static parameter => parameter.COMPTIME() is null)
                    .Select(static parameter => parameter.Identifier().GetText())
                    .ToArray()
                : null,
            DisjointParameterGroups = BuildParameterDisjointGroupManifests(
                functionDeclaration.parameterList(),
                functionDeclaration.parameterMemoryContractClause()),
            OverlapParameterGroups = BuildParameterOverlapGroupManifests(functionDeclaration.parameterMemoryContractClause()),
            SameParameterGroups = BuildParameterSameGroupManifests(functionDeclaration.parameterMemoryContractClause()),
            TypeParameterConstraints = BuildSourceSurfaceTypeParameterConstraints(
                module,
                functionDeclaration.typeParameterConstraints()),
            ThreadSafetyLawPredicates = BuildSourceSurfaceThreadSafetyLawPredicates(
                module,
                functionDeclaration.parameterMemoryContractClause())
        };
    }

    private static StarkPackageTypeManifest BuildSourceSurfaceTypeManifest(
        LoadedModuleDocument module,
        string typeName,
        StarkPackageTypeManifest manifest)
    {
        foreach (var declaration in module.ParseResult.Root.topLevelDeclaration())
        {
            if (declaration.structDeclaration() is { } structDeclaration
                && string.Equals(structDeclaration.Identifier().GetText(), typeName, StringComparison.Ordinal))
            {
                return manifest with
                {
                    Fields = BuildSourceSurfaceFields(module, structDeclaration.structBody().structMember()),
                    StructLayout = TryGetAttributeArgument(declaration.attributeList(), "StructLayout"),
                    PackBytes = TryGetIntegerAttributeArgument(declaration.attributeList(), "Pack"),
                    AlignBytes = TryGetIntegerAttributeArgument(declaration.attributeList(), "Align"),
                    ThreadSafetyLawAttributes = BuildSourceSurfaceThreadSafetyLawAttributes(module, declaration.attributeList()),
                    ImplementedTraits = BuildSourceSurfaceImplementedTraits(module, structDeclaration.baseTraitList()),
                    Constructors = BuildSourceSurfaceConstructorManifests(
                        module,
                        structDeclaration.Identifier().GetText(),
                        structDeclaration.structBody().structMember()
                            .Select(static member => member.constructorDeclaration())
                            .Where(static constructor => constructor is not null)!
                            .Cast<StarkParser.ConstructorDeclarationContext>()
                            .ToArray()),
                    Methods = BuildSourceSurfaceMethodManifests(
                        module,
                        manifest.Methods,
                        structDeclaration.structBody().structMember()
                            .Select(static member => member.methodDeclaration())
                            .Where(static method => method is not null)!
                            .Cast<StarkParser.MethodDeclarationContext>()
                            .ToArray())
                };
            }

            if (declaration.recordDeclaration() is { } recordDeclaration
                && string.Equals(recordDeclaration.Identifier().GetText(), typeName, StringComparison.Ordinal))
            {
                return manifest with
                {
                    Fields = BuildSourceSurfaceFields(module, recordDeclaration.recordBody().recordMember()),
                    PrimaryConstructorParameters = recordDeclaration.primaryConstructorParameters()?.parameterList().parameter()
                        .Select(parameter => new StarkPackageParameterManifest(
                            parameter.Identifier().GetText(),
                            GetContextSourceText(module.ParseResult, parameter.type_()),
                            ParameterHasPrefix(parameter, StarkParser.DISJOINT),
                            ParameterHasPrefix(parameter, StarkParser.CONST)))
                        .ToArray(),
                    ThreadSafetyLawAttributes = BuildSourceSurfaceThreadSafetyLawAttributes(module, declaration.attributeList()),
                    ImplementedTraits = BuildSourceSurfaceImplementedTraits(module, recordDeclaration.baseTraitList()),
                    Constructors = BuildSourceSurfaceConstructorManifests(
                        module,
                        recordDeclaration.Identifier().GetText(),
                        recordDeclaration.recordBody().recordMember()
                            .Select(static member => member.constructorDeclaration())
                            .Where(static constructor => constructor is not null)!
                            .Cast<StarkParser.ConstructorDeclarationContext>()
                            .ToArray()),
                    Methods = BuildSourceSurfaceMethodManifests(
                        module,
                        manifest.Methods,
                        recordDeclaration.recordBody().recordMember()
                            .Select(static member => member.methodDeclaration())
                            .Where(static method => method is not null)!
                            .Cast<StarkParser.MethodDeclarationContext>()
                            .ToArray())
                };
            }

            if (declaration.enumDeclaration() is { } enumDeclaration
                && string.Equals(enumDeclaration.Identifier().GetText(), typeName, StringComparison.Ordinal))
            {
                return manifest with
                {
                    ThreadSafetyLawAttributes = BuildSourceSurfaceThreadSafetyLawAttributes(module, declaration.attributeList()),
                    Variants = enumDeclaration.enumBody().enumVariantDeclaration()
                        .Select(variant => BuildSourceSurfaceEnumVariantManifest(module, variant))
                        .ToArray()
                };
            }

            if (declaration.traitDeclaration() is { } traitDeclaration
                && string.Equals(traitDeclaration.Identifier().GetText(), typeName, StringComparison.Ordinal))
            {
                return manifest with
                {
                    Methods = BuildSourceSurfaceMethodManifests(
                        module,
                        manifest.Methods,
                        traitDeclaration.traitBody().traitMember()
                            .Select(static member => member.traitMethodDeclaration())
                            .Where(static method => method is not null)!
                            .Cast<StarkParser.TraitMethodDeclarationContext>()
                            .ToArray())
                };
            }

            if (declaration.doctrineDeclaration() is { } doctrineDeclaration
                && string.Equals(doctrineDeclaration.Identifier().GetText(), typeName, StringComparison.Ordinal))
            {
                return manifest with
                {
                    Methods = BuildSourceSurfaceMethodManifests(
                        module,
                        manifest.Methods,
                        doctrineDeclaration.doctrineBody().doctrineMember()
                            .Select(static member => member.doctrineMethodDeclaration())
                            .Where(static method => method is not null)!
                            .Cast<StarkParser.DoctrineMethodDeclarationContext>()
                            .ToArray())
                };
            }
        }

        return manifest;
    }

    private static IReadOnlyList<string>? BuildSourceSurfaceImplementedTraits(
        LoadedModuleDocument module,
        StarkParser.BaseTraitListContext? baseTraitList)
    {
        return baseTraitList is null
            ? null
            : baseTraitList.type_()
                .Select(type => GetContextSourceText(module.ParseResult, type))
                .ToArray();
    }

    private static IReadOnlyList<StarkPackageFieldManifest> BuildSourceSurfaceFields(
        LoadedModuleDocument module,
        IEnumerable<StarkParser.StructMemberContext> members)
    {
        return members
            .Where(static member => member.fieldDeclaration() is not null)
            .SelectMany(fieldDeclaration =>
                fieldDeclaration.fieldDeclaration()!.variableDeclarators().variableDeclarator()
                    .Select(variable => new StarkPackageFieldManifest(
                        variable.Identifier().GetText(),
                        GetContextSourceText(module.ParseResult, fieldDeclaration.fieldDeclaration()!.type_()),
                        fieldDeclaration.fieldDeclaration()!.visibilityModifier()?.GetText(),
                        TryGetIntegerAttributeArgument(fieldDeclaration.attributeList(), "FieldOffset"),
                        BuildSourceSurfaceThreadSafetyLawAttributes(module, fieldDeclaration.attributeList()))))
            .ToArray();
    }

    private static IReadOnlyList<StarkPackageFieldManifest> BuildSourceSurfaceFields(
        LoadedModuleDocument module,
        IEnumerable<StarkParser.RecordMemberContext> members)
    {
        return members
            .Where(static member => member.fieldDeclaration() is not null)
            .SelectMany(fieldDeclaration =>
                fieldDeclaration.fieldDeclaration()!.variableDeclarators().variableDeclarator()
                    .Select(variable => new StarkPackageFieldManifest(
                        variable.Identifier().GetText(),
                        GetContextSourceText(module.ParseResult, fieldDeclaration.fieldDeclaration()!.type_()),
                        fieldDeclaration.fieldDeclaration()!.visibilityModifier()?.GetText(),
                        TryGetIntegerAttributeArgument(fieldDeclaration.attributeList(), "FieldOffset"),
                        BuildSourceSurfaceThreadSafetyLawAttributes(module, fieldDeclaration.attributeList()))))
            .ToArray();
    }

    private static IReadOnlyList<StarkPackageThreadSafetyLawAttributeManifest>? BuildSourceSurfaceThreadSafetyLawAttributes(
        LoadedModuleDocument module,
        IEnumerable<StarkParser.AttributeListContext> attributeLists)
    {
        List<StarkPackageThreadSafetyLawAttributeManifest>? attributes = null;
        foreach (var attribute in attributeLists.SelectMany(static list => list.attribute()))
        {
            var attributeName = attribute.qualifiedName().GetText();
            if (!TryRenderThreadSafetyLawAttributeKind(attributeName, out var kind)
                || attribute.attributeArgument() is not [var lawArgument])
            {
                continue;
            }

            StarkPackageThreadSafetyLawPredicateManifest? condition = null;
            if (attribute.attributeCondition()?.lawPredicateContract() is { } predicate)
            {
                condition = new StarkPackageThreadSafetyLawPredicateManifest(
                    predicate.Identifier().GetText(),
                    GetContextSourceText(module.ParseResult, predicate.type_()));
            }

            attributes ??= [];
            attributes.Add(new StarkPackageThreadSafetyLawAttributeManifest(
                kind,
                lawArgument.GetText(),
                condition));
        }

        return attributes;
    }

    private static bool TryRenderThreadSafetyLawAttributeKind(string attributeName, out string kind)
    {
        if (string.Equals(attributeName, "Grant", StringComparison.Ordinal))
        {
            kind = "grant";
            return true;
        }

        if (string.Equals(attributeName, "Deny", StringComparison.Ordinal))
        {
            kind = "deny";
            return true;
        }

        kind = string.Empty;
        return false;
    }

    private static IReadOnlyList<StarkPackageThreadSafetyLawPredicateManifest>? BuildSourceSurfaceThreadSafetyLawPredicates(
        LoadedModuleDocument module,
        IReadOnlyList<StarkParser.ParameterMemoryContractClauseContext> memoryContractClauses)
    {
        List<StarkPackageThreadSafetyLawPredicateManifest>? predicates = null;
        foreach (var clause in memoryContractClauses)
        {
            foreach (var contract in clause.parameterMemoryContract())
            {
                if (contract.lawPredicateContract() is not { } predicate)
                {
                    continue;
                }

                predicates ??= [];
                predicates.Add(new StarkPackageThreadSafetyLawPredicateManifest(
                    predicate.Identifier().GetText(),
                    GetContextSourceText(module.ParseResult, predicate.type_())));
            }
        }

        return predicates;
    }

    private static IReadOnlyList<StarkPackageTypeParameterConstraintManifest>? BuildSourceSurfaceTypeParameterConstraints(
        LoadedModuleDocument module,
        IReadOnlyList<StarkParser.TypeParameterConstraintsContext> constraintContexts)
    {
        List<StarkPackageTypeParameterConstraintManifest>? constraints = null;
        foreach (var constraint in constraintContexts)
        {
            var bounds = constraint.type_()
                .Select(type => GetContextSourceText(module.ParseResult, type))
                .ToArray();
            if (bounds.Length == 0)
            {
                continue;
            }

            constraints ??= [];
            constraints.Add(new StarkPackageTypeParameterConstraintManifest(
                constraint.Identifier().GetText(),
                bounds));
        }

        return constraints;
    }

    private static string? TryGetAttributeArgument(
        IEnumerable<StarkParser.AttributeListContext> attributeLists,
        string attributeName)
    {
        foreach (var attribute in attributeLists.SelectMany(static list => list.attribute()))
        {
            if (string.Equals(attribute.qualifiedName().GetText(), attributeName, StringComparison.Ordinal)
                && attribute.attributeArgument() is [var argument])
            {
                return argument.GetText();
            }
        }

        return null;
    }

    private static int? TryGetIntegerAttributeArgument(
        IEnumerable<StarkParser.AttributeListContext> attributeLists,
        string attributeName)
    {
        return int.TryParse(TryGetAttributeArgument(attributeLists, attributeName), out var value)
            ? value
            : null;
    }

    private static IReadOnlyList<StarkPackageConstructorManifest>? BuildSourceSurfaceConstructorManifests(
        LoadedModuleDocument module,
        string typeName,
        IReadOnlyList<StarkParser.ConstructorDeclarationContext> constructors)
    {
        var manifests = constructors
            .Where(constructor => string.Equals(constructor.Identifier().GetText(), typeName, StringComparison.Ordinal))
            .Select(constructor => new StarkPackageConstructorManifest(
                constructor.parameterList().parameter()
                    .Select(parameter => new StarkPackageParameterManifest(
                        parameter.Identifier().GetText(),
                        GetContextSourceText(module.ParseResult, parameter.type_()),
                        ParameterHasPrefix(parameter, StarkParser.DISJOINT),
                        ParameterHasPrefix(parameter, StarkParser.CONST)))
                    .ToArray(),
                GetContextSourceText(module.ParseResult, constructor.block())))
            .ToArray();

        return manifests.Length == 0 ? null : manifests;
    }

    private static IReadOnlyList<StarkPackageMethodManifest>? BuildSourceSurfaceMethodManifests(
        LoadedModuleDocument module,
        IReadOnlyList<StarkPackageMethodManifest>? normalizedMethods,
        IReadOnlyList<StarkParser.MethodDeclarationContext> methods)
    {
        if (normalizedMethods is not { Count: > 0 })
        {
            return null;
        }

        var methodQueues = normalizedMethods
            .GroupBy(static method => method.Name, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => new Queue<StarkPackageMethodManifest>(group),
                StringComparer.Ordinal);

        var manifests = new List<StarkPackageMethodManifest>();
        foreach (var method in methods
                     .OrderBy(static method => method.Identifier().GetText(), StringComparer.Ordinal))
        {
            var methodName = method.Identifier().GetText();
            if (!methodQueues.TryGetValue(methodName, out var methodQueue)
                || methodQueue.Count == 0)
            {
                continue;
            }

            var normalizedMethod = methodQueue.Dequeue();
            manifests.Add(normalizedMethod with
            {
                ReturnType = GetContextSourceText(module.ParseResult, method.returnType()),
                Parameters = method.parameterList().parameter()
                    .Select(parameter => new StarkPackageParameterManifest(
                        parameter.Identifier().GetText(),
                        GetContextSourceText(module.ParseResult, parameter.type_()),
                        ParameterHasPrefix(parameter, StarkParser.DISJOINT),
                        ParameterHasPrefix(parameter, StarkParser.CONST)))
                    .ToArray(),
                GenericParameters = method.typeParameterList() is { } typeParameterList
                    ? typeParameterList.typeParameter()
                        .Where(static parameter => parameter.COMPTIME() is null)
                        .Select(static parameter => parameter.Identifier().GetText())
                        .ToArray()
                    : null,
                DisjointParameterGroups = BuildParameterDisjointGroupManifests(
                    method.parameterList(),
                    method.parameterMemoryContractClause()),
                OverlapParameterGroups = BuildParameterOverlapGroupManifests(method.parameterMemoryContractClause()),
                SameParameterGroups = BuildParameterSameGroupManifests(method.parameterMemoryContractClause()),
                TypeParameterConstraints = BuildSourceSurfaceTypeParameterConstraints(
                    module,
                    method.typeParameterConstraints()),
                ThreadSafetyLawPredicates = BuildSourceSurfaceThreadSafetyLawPredicates(
                    module,
                    method.parameterMemoryContractClause())
            });
        }

        return manifests.Count == 0 ? null : manifests;
    }

    private static IReadOnlyList<StarkPackageMethodManifest>? BuildSourceSurfaceMethodManifests(
        LoadedModuleDocument module,
        IReadOnlyList<StarkPackageMethodManifest>? normalizedMethods,
        IReadOnlyList<StarkParser.TraitMethodDeclarationContext> methods)
    {
        if (normalizedMethods is not { Count: > 0 })
        {
            return null;
        }

        var methodQueues = normalizedMethods
            .GroupBy(static method => method.Name, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => new Queue<StarkPackageMethodManifest>(group),
                StringComparer.Ordinal);

        var manifests = new List<StarkPackageMethodManifest>();
        foreach (var method in methods
                     .OrderBy(static method => method.Identifier().GetText(), StringComparer.Ordinal))
        {
            var methodName = method.Identifier().GetText();
            if (!methodQueues.TryGetValue(methodName, out var methodQueue)
                || methodQueue.Count == 0)
            {
                continue;
            }

            var normalizedMethod = methodQueue.Dequeue();
            manifests.Add(normalizedMethod with
            {
                ReturnType = GetContextSourceText(module.ParseResult, method.returnType()),
                Parameters = method.parameterList().parameter()
                    .Select(parameter => new StarkPackageParameterManifest(
                        parameter.Identifier().GetText(),
                        GetContextSourceText(module.ParseResult, parameter.type_()),
                        ParameterHasPrefix(parameter, StarkParser.DISJOINT),
                        ParameterHasPrefix(parameter, StarkParser.CONST)))
                    .ToArray(),
                GenericParameters = method.typeParameterList() is { } typeParameterList
                    ? typeParameterList.typeParameter()
                        .Where(static parameter => parameter.COMPTIME() is null)
                        .Select(static parameter => parameter.Identifier().GetText())
                        .ToArray()
                    : null,
                DisjointParameterGroups = BuildParameterDisjointGroupManifests(
                    method.parameterList(),
                    method.parameterMemoryContractClause()),
                OverlapParameterGroups = BuildParameterOverlapGroupManifests(method.parameterMemoryContractClause()),
                SameParameterGroups = BuildParameterSameGroupManifests(method.parameterMemoryContractClause()),
                TypeParameterConstraints = BuildSourceSurfaceTypeParameterConstraints(
                    module,
                    method.typeParameterConstraints()),
                ThreadSafetyLawPredicates = BuildSourceSurfaceThreadSafetyLawPredicates(
                    module,
                    method.parameterMemoryContractClause())
            });
        }

        return manifests.Count == 0 ? null : manifests;
    }

    private static IReadOnlyList<StarkPackageMethodManifest>? BuildSourceSurfaceMethodManifests(
        LoadedModuleDocument module,
        IReadOnlyList<StarkPackageMethodManifest>? normalizedMethods,
        IReadOnlyList<StarkParser.DoctrineMethodDeclarationContext> methods)
    {
        if (normalizedMethods is not { Count: > 0 })
        {
            return null;
        }

        var methodQueues = normalizedMethods
            .GroupBy(static method => method.Name, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => new Queue<StarkPackageMethodManifest>(group),
                StringComparer.Ordinal);

        var manifests = new List<StarkPackageMethodManifest>();
        foreach (var method in methods
                     .OrderBy(static method => method.Identifier().GetText(), StringComparer.Ordinal))
        {
            var methodName = method.Identifier().GetText();
            if (!methodQueues.TryGetValue(methodName, out var methodQueue)
                || methodQueue.Count == 0)
            {
                continue;
            }

            var normalizedMethod = methodQueue.Dequeue();
            manifests.Add(normalizedMethod with
            {
                ReturnType = GetContextSourceText(module.ParseResult, method.returnType()),
                Parameters = method.parameterList().parameter()
                    .Select(parameter => new StarkPackageParameterManifest(
                        parameter.Identifier().GetText(),
                        GetContextSourceText(module.ParseResult, parameter.type_()),
                        ParameterHasPrefix(parameter, StarkParser.DISJOINT),
                        ParameterHasPrefix(parameter, StarkParser.CONST)))
                    .ToArray(),
                GenericParameters = method.typeParameterList() is { } typeParameterList
                    ? typeParameterList.typeParameter()
                        .Where(static parameter => parameter.COMPTIME() is null)
                        .Select(static parameter => parameter.Identifier().GetText())
                        .ToArray()
                    : null,
                DisjointParameterGroups = BuildParameterDisjointGroupManifests(
                    method.parameterList(),
                    method.parameterMemoryContractClause()),
                OverlapParameterGroups = BuildParameterOverlapGroupManifests(method.parameterMemoryContractClause()),
                SameParameterGroups = BuildParameterSameGroupManifests(method.parameterMemoryContractClause()),
                TypeParameterConstraints = BuildSourceSurfaceTypeParameterConstraints(
                    module,
                    method.typeParameterConstraints()),
                ThreadSafetyLawPredicates = BuildSourceSurfaceThreadSafetyLawPredicates(
                    module,
                    method.parameterMemoryContractClause())
            });
        }

        return manifests.Count == 0 ? null : manifests;
    }

    private static StarkPackageEnumVariantManifest BuildSourceSurfaceEnumVariantManifest(
        LoadedModuleDocument module,
        StarkParser.EnumVariantDeclarationContext variant)
    {
        // The authored [Ok]/[Err] role attribute is part of the published surface: it is
        // what keeps the enum `try`-propagatable for downstream packages.
        var role = GetSourceSurfaceEnumVariantRole(variant);

        if (variant.enumVariantPayload() is not { } payload)
        {
            return new StarkPackageEnumVariantManifest(
                variant.Identifier().GetText(),
                UsesNamedFields: false,
                Fields: [],
                Role: role);
        }

        if (payload.FROM() is not null)
        {
            // `Name from Type`: a single positional payload plus the `try` conversion funnel.
            var absorbedErrorTypeText = GetContextSourceText(module.ParseResult, payload.type_(0));
            return new StarkPackageEnumVariantManifest(
                variant.Identifier().GetText(),
                UsesNamedFields: false,
                Fields: [new StarkPackageFieldManifest("Item0", absorbedErrorTypeText)],
                Role: role,
                AbsorbsErrorType: absorbedErrorTypeText);
        }

        if (payload.enumVariantFieldDeclaration().Length != 0)
        {
            return new StarkPackageEnumVariantManifest(
                variant.Identifier().GetText(),
                UsesNamedFields: true,
                Fields: payload.enumVariantFieldDeclaration()
                    .Select(field => new StarkPackageFieldManifest(
                        field.Identifier().GetText(),
                        GetContextSourceText(module.ParseResult, field.type_())))
                    .ToArray(),
                Role: role);
        }

        return new StarkPackageEnumVariantManifest(
            variant.Identifier().GetText(),
            UsesNamedFields: false,
            Fields: payload.type_()
                .Select((fieldType, index) => new StarkPackageFieldManifest(
                    $"Item{index}",
                    GetContextSourceText(module.ParseResult, fieldType)))
                .ToArray(),
            Role: role);
    }

    private static string? GetSourceSurfaceEnumVariantRole(StarkParser.EnumVariantDeclarationContext variant)
    {
        foreach (var attributeList in variant.attributeList())
        {
            foreach (var attribute in attributeList.attribute())
            {
                var role = attribute.qualifiedName().GetText() switch
                {
                    "Ok" => "ok",
                    "Err" => "err",
                    _ => null
                };

                if (role is not null)
                {
                    return role;
                }
            }
        }

        return null;
    }

    private static Dictionary<string, Queue<StarkParser.FunctionDeclarationContext>> BuildFunctionDeclarationSyntaxLookup(
        LoadedModuleDocument module)
    {
        return module.ParseResult.Root.topLevelDeclaration()
            .Select(static declaration => declaration.functionDeclaration())
            .Where(static function => function is not null)!
            .Cast<StarkParser.FunctionDeclarationContext>()
            .GroupBy(static function => function.Identifier().GetText(), StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => new Queue<StarkParser.FunctionDeclarationContext>(group),
                StringComparer.Ordinal);
    }

    private static Dictionary<string, StarkParser.TypeAliasDeclarationContext> BuildTypeAliasDeclarationSyntaxLookup(
        LoadedModuleDocument module)
    {
        return module.ParseResult.Root.topLevelDeclaration()
            .Select(static declaration => declaration.typeAliasDeclaration())
            .Where(static typeAlias => typeAlias is not null)!
            .Cast<StarkParser.TypeAliasDeclarationContext>()
            .ToDictionary(static typeAlias => typeAlias.Identifier().GetText(), StringComparer.Ordinal);
    }

    private static Dictionary<string, StarkParser.Type_Context?> BuildGlobalDeclarationSyntaxLookup(LoadedModuleDocument module)
    {
        var lookup = new Dictionary<string, StarkParser.Type_Context?>(StringComparer.Ordinal);

        foreach (var declaration in module.ParseResult.Root.topLevelDeclaration())
        {
            if (declaration.globalConstantDeclaration() is { } constantDeclaration)
            {
                if (constantDeclaration.type_() is { } typeContext)
                {
                    lookup[constantDeclaration.constantDeclarators().constantDeclarator(0).Identifier().GetText()] =
                        typeContext;
                }
                else
                {
                    lookup[constantDeclaration.constantDeclarators().constantDeclarator(0).Identifier().GetText()] =
                        null;
                }

                continue;
            }

            if (declaration.globalVariableDeclaration() is { } variableDeclaration)
            {
                lookup[variableDeclaration.variableDeclarators().variableDeclarator(0).Identifier().GetText()] =
                    variableDeclaration.type_();
            }
        }

        return lookup;
    }
}
