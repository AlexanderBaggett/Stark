using System.Text;
using Stark.Parsing;

namespace Stark.Compiler.LlvmIrEmission;

internal sealed record ImportedGlobalDeclarationPlan(
    string QualifiedName,
    string ModuleName,
    string SourceName,
    StarkVisibility Visibility,
    TypedGlobalSymbol Global,
    StarkParser.VariableInitializerContext? Initializer);

internal sealed class LlvmModuleSurfaceEmitter
{
    private readonly IReadOnlySet<string> _globalsEligibleForLocalUnnamedAddr;
    private readonly IReadOnlyDictionary<string, ImportedGlobalDeclarationPlan> _importedCloneReferencedGlobals;
    private readonly LlvmEmissionContext _context;
    private readonly LlvmGlobalInitializerPlanner _globalInitializerPlanner;

    public LlvmModuleSurfaceEmitter(
        LlvmEmissionContext context,
        IReadOnlySet<string> globalsEligibleForLocalUnnamedAddr,
        IReadOnlyDictionary<string, ImportedGlobalDeclarationPlan> importedCloneReferencedGlobals,
        LlvmGlobalInitializerPlanner globalInitializerPlanner)
    {
        _context = context;
        _globalsEligibleForLocalUnnamedAddr = globalsEligibleForLocalUnnamedAddr;
        _importedCloneReferencedGlobals = importedCloneReferencedGlobals;
        _globalInitializerPlanner = globalInitializerPlanner;
    }

    public void Emit(StringBuilder builder)
    {
        EmitBuiltinTypeDefinitions(builder);
        EmitNamedTypeDefinitions(builder);
        EmitStringConstants(builder);
        EmitVTableGlobals(builder);
        EmitGlobals(builder);
    }

    // Emits one read-only vtable per (implementing type, `dyn trait`) pair. The
    // table holds a function pointer for each object-safe trait method, in the
    // shared slot order from DynTraitFacts, followed by a drop slot (`null` for a
    // borrowed trait object; the implementing type's drop thunk for `heap dyn`).
    // A `dyn Trait` fat pointer's second word points at one of these tables, and
    // a dynamic call loads slot i with `getelementptr ptr, ptr <vtable>, i32 i`.
    private void EmitVTableGlobals(StringBuilder builder)
    {
        var emittedAny = false;
        foreach (var concreteType in _context.TypeModel.NamedTypes.Values
                     .Where(static type => type.Kind is DeclarationKind.Struct or DeclarationKind.Record
                                           && type.ImplementedTraits.Count > 0)
                     .OrderBy(static type => type.Name, StringComparer.Ordinal))
        {
            foreach (var traitName in concreteType.ImplementedTraits
                         .Distinct()
                         .OrderBy(static name => name, StringComparer.Ordinal))
            {
                if (!_context.TypeModel.NamedTypes.TryGetValue(traitName, out var traitType)
                    || traitType.Kind != DeclarationKind.Trait
                    || !traitType.IsDynTrait)
                {
                    continue;
                }

                if (!TryBuildVTableInitializer(concreteType.Name, traitName, out var slotCount, out var initializer))
                {
                    continue;
                }

                var vtableType = $"{{ {string.Join(", ", Enumerable.Repeat("ptr", slotCount + 1))} }}";
                var symbolName = DynTraitFacts.BuildVtableGlobalName(concreteType.Name, traitName);
                builder.AppendLine($"@{EscapeIdentifier(symbolName)} = private unnamed_addr constant {vtableType} {initializer}");
                emittedAny = true;
            }
        }

        if (emittedAny)
        {
            builder.AppendLine();
        }
    }

    private bool TryBuildVTableInitializer(string concreteTypeName, string traitName, out int slotCount, out string initializer)
    {
        initializer = string.Empty;
        var layout = DynTraitFacts.GetVtableLayout(traitName, _context.TypeModel.Functions);
        slotCount = layout.Count;
        var elements = new List<string>(slotCount + 1);
        foreach (var slot in layout)
        {
            if (!TryResolveSlotFunctionSymbol(concreteTypeName, slot.MethodName, out var symbol))
            {
                // A non-overridden default method has no concrete `Type.Method`
                // symbol; dispatching it through a trait object is not supported in
                // this version (the implementing type must override it). Skip the
                // table; the coercion site is rejected during semantic validation.
                return false;
            }

            elements.Add($"ptr @{EscapeIdentifier(symbol)}");
        }

        // Drop slot: the implementing type's drop thunk (`<Type>.__dyn_drop`), which an
        // owning `heap dyn` calls at scope exit to drop the boxed value and free the
        // box. A borrowed trait object never reads this slot. Generic templates have no
        // synthesized thunk, so their slot stays null (owned generic dyn is unsupported).
        var dropSlot = _context.TypeModel.NamedTypes.TryGetValue(concreteTypeName, out var concreteType)
                       && !concreteType.IsGeneric
            ? $"ptr @{EscapeIdentifier(DynTraitFacts.BuildDropThunkName(concreteTypeName))}"
            : "ptr null";
        elements.Add(dropSlot);
        initializer = $"{{ {string.Join(", ", elements)} }}";
        return true;
    }

    private bool TryResolveSlotFunctionSymbol(string concreteTypeName, string methodName, out string symbol)
    {
        symbol = string.Empty;
        var dot = concreteTypeName.LastIndexOf('.');
        var simpleType = dot < 0 ? concreteTypeName : concreteTypeName[(dot + 1)..];
        foreach (var key in new[] { $"{concreteTypeName}.{methodName}", $"{simpleType}.{methodName}" })
        {
            if (_context.TypeModel.Functions.TryGetValue(key, out var signature) && !signature.IsStatic)
            {
                symbol = signature.Name;
                return true;
            }
        }

        return false;
    }

    private void EmitBuiltinTypeDefinitions(StringBuilder builder)
    {
        builder.AppendLine($"%{_context.AsciiStringTypeName} = type {{ ptr, i64 }}");
        builder.AppendLine($"%{_context.UnicodeStringTypeName} = type {{ ptr, i64 }}");
        builder.AppendLine();
    }

    private void EmitNamedTypeDefinitions(StringBuilder builder)
    {
        var emittedAny = false;

        foreach (var namedType in _context.TypeModel.NamedTypes.Values
                     .Where(type => type.Kind is DeclarationKind.Struct or DeclarationKind.Record
                                    || (type.Kind == DeclarationKind.Enum && _context.EnumLayouts.ContainsKey(type.Name)))
                     .OrderBy(static type => type.Name, StringComparer.Ordinal))
        {
            emittedAny = true;
            var fieldsSource = namedType.Kind == DeclarationKind.Enum
                ? _context.EnumLayouts[namedType.Name].OrderedFields
                : namedType.OrderedFields;
            if (namedType.Kind is DeclarationKind.Struct or DeclarationKind.Record
                && TryBuildLayoutControlledTypeDefinition(namedType, out var layoutControlledDefinition))
            {
                builder.AppendLine($"%{EscapeIdentifier(namedType.Name)} = type {layoutControlledDefinition}");
                continue;
            }

            var fields = fieldsSource.Count == 0
                ? string.Empty
                : string.Join(", ", fieldsSource.Select(field => _context.MapType(field.Type)));
            builder.AppendLine($"%{EscapeIdentifier(namedType.Name)} = type {{ {fields} }}");
        }

        if (emittedAny)
        {
            builder.AppendLine();
        }
    }

    private bool TryBuildLayoutControlledTypeDefinition(NamedTypeSymbol namedType, out string definition)
    {
        definition = string.Empty;
        if (!LlvmLayoutControlledAggregateFacts.RequiresPhysicalLayout(namedType)
            || _context.TryGetConcreteTypeLayout(StarkTypeSymbols.Named(namedType.Name)) is not { } layout)
        {
            return false;
        }

        if (!LlvmLayoutControlledAggregateFacts.TryBuildPhysicalElements(
                namedType,
                layout,
                out var elements,
                out var hasOverlappingFields)
            || hasOverlappingFields)
        {
            definition = $"{{ [{layout.SizeBytes} x i8] }}";
            return true;
        }

        var fields = elements
            .Where(static element => element.SizeBytes > 0)
            .Select(element => element.FieldType is { } fieldType
                ? _context.MapType(fieldType)
                : $"[{element.SizeBytes} x i8]")
            .ToArray();
        definition = $"<{{ {string.Join(", ", fields)} }}>";
        return true;
    }

    private void EmitStringConstants(StringBuilder builder)
    {
        foreach (var constant in _context.StringConstants.OrderBy(static item => item.SymbolName, StringComparer.Ordinal))
        {
            builder.Append($"@{constant.SymbolName} = private unnamed_addr constant {constant.ArrayType} {constant.Initializer}");
            if (constant.AlignmentBytes > 1)
            {
                builder.Append($", align {constant.AlignmentBytes}");
            }

            builder.AppendLine();
        }

        if (_context.StringConstants.Count != 0)
        {
            builder.AppendLine();
        }
    }

    private void EmitGlobals(StringBuilder builder)
    {
        foreach (var declaration in _context.ParseResult.Root.topLevelDeclaration())
        {
            var visibility = ParseVisibility(declaration.visibilityModifier());

            if (declaration.globalConstantDeclaration() is { } constantDeclaration)
            {
                foreach (var declarator in constantDeclaration.constantDeclarators().constantDeclarator())
                {
                    var name = declarator.Identifier().GetText();
                    if (!_context.TypeModel.Globals.TryGetValue(name, out var global))
                    {
                        continue;
                    }

                    var symbolName = ResolveGlobalSymbolName(name);
                    if (_globalInitializerPlanner.ShouldEmitExternalConstPlaceholder(global, declarator.variableInitializer()))
                    {
                        builder.AppendLine($"; visibility: {visibility.ToString().ToLowerInvariant()}");
                        builder.AppendLine($"@{EscapeIdentifier(symbolName)} = external constant {_context.MapType(global.Type)}");
                        builder.AppendLine();
                        continue;
                    }

                    if (!_globalInitializerPlanner.TryPlanVariableInitializer(
                            declarator.variableInitializer(),
                            global.Type,
                            true,
                            out var initializerPlan))
                    {
                        builder.AppendLine($"; visibility: {visibility.ToString().ToLowerInvariant()}");
                        builder.AppendLine($"@{EscapeIdentifier(symbolName)} = external constant {_context.MapType(global.Type)}");
                        builder.AppendLine();
                        continue;
                    }

                    EmitGlobalInitializerPrelude(builder, initializerPlan.PreludeDefinitions);
                    builder.AppendLine($"; visibility: {visibility.ToString().ToLowerInvariant()}");
                    builder.AppendLine(BuildGlobalDefinition(name, symbolName, visibility, global, initializerPlan.Rendered));
                    builder.AppendLine();
                }

                continue;
            }

            if (declaration.globalVariableDeclaration() is not { } variableDeclaration)
            {
                continue;
            }

            foreach (var declarator in variableDeclaration.variableDeclarators().variableDeclarator())
            {
                var name = declarator.Identifier().GetText();
                if (!_context.TypeModel.Globals.TryGetValue(name, out var global))
                {
                    continue;
                }

                var symbolName = ResolveGlobalSymbolName(name);
                if (declarator.variableInitializer() is null)
                {
                    builder.AppendLine($"; visibility: {visibility.ToString().ToLowerInvariant()}");
                    var storage = global.IsMutable ? "global" : "constant";
                    builder.AppendLine($"@{EscapeIdentifier(symbolName)} = external {storage} {_context.MapType(global.Type)}");
                    builder.AppendLine();
                    continue;
                }

                if (!_globalInitializerPlanner.TryPlanVariableInitializer(
                        declarator.variableInitializer(),
                        global.Type,
                        false,
                        out var initializerPlan))
                {
                    builder.AppendLine($"; visibility: {visibility.ToString().ToLowerInvariant()}");
                    var storage = global.IsMutable ? "global" : "constant";
                    builder.AppendLine($"@{EscapeIdentifier(symbolName)} = external {storage} {_context.MapType(global.Type)}");
                    builder.AppendLine();
                    continue;
                }

                EmitGlobalInitializerPrelude(builder, initializerPlan.PreludeDefinitions);
                builder.AppendLine($"; visibility: {visibility.ToString().ToLowerInvariant()}");
                builder.AppendLine(BuildGlobalDefinition(name, symbolName, visibility, global, initializerPlan.Rendered));
                builder.AppendLine();
            }
        }

        EmitImportedGlobalDeclarations(builder);
    }

    private void EmitImportedGlobalDeclarations(StringBuilder builder)
    {
        var emitted = new HashSet<string>(StringComparer.Ordinal);

        foreach (var module in _context.LoadedModules.ImportedModules.OrderBy(static module => module.SyntaxModel.ModuleName, StringComparer.Ordinal))
        {
            foreach (var declaration in module.ParseResult.Root.topLevelDeclaration())
            {
                var visibility = ParseVisibility(declaration.visibilityModifier());

                if (declaration.globalConstantDeclaration() is { } constantDeclaration)
                {
                    foreach (var declarator in constantDeclaration.constantDeclarators().constantDeclarator())
                    {
                        EmitImportedGlobalDeclaration(builder, module, visibility, declarator.Identifier().GetText(), emitted);
                    }

                    continue;
                }

                if (declaration.globalVariableDeclaration() is not { } variableDeclaration)
                {
                    continue;
                }

                foreach (var declarator in variableDeclaration.variableDeclarators().variableDeclarator())
                {
                    EmitImportedGlobalDeclaration(builder, module, visibility, declarator.Identifier().GetText(), emitted);
                }
            }
        }

        foreach (var global in _importedCloneReferencedGlobals.Values.OrderBy(static global => global.QualifiedName, StringComparer.Ordinal))
        {
            if (emitted.Contains(global.QualifiedName))
            {
                continue;
            }

            EmitImportedGlobalDeclaration(builder, global, emitted);
        }
    }

    private void EmitImportedGlobalDeclaration(
        StringBuilder builder,
        LoadedModuleDocument module,
        StarkVisibility visibility,
        string sourceName,
        ISet<string> emitted)
    {
        var qualifiedName = $"{module.SyntaxModel.ModuleName}.{sourceName}";
        if (!_context.TypeModel.Globals.TryGetValue(qualifiedName, out var global))
        {
            if (!_importedCloneReferencedGlobals.TryGetValue(qualifiedName, out var importedGlobal))
            {
                return;
            }

            EmitImportedGlobalDeclaration(builder, importedGlobal, emitted);
            return;
        }

        EmitImportedGlobalDeclaration(
            builder,
            new ImportedGlobalDeclarationPlan(
                qualifiedName,
                module.SyntaxModel.ModuleName,
                sourceName,
                visibility,
                global,
                null),
            emitted);
    }

    private void EmitImportedGlobalDeclaration(
        StringBuilder builder,
        ImportedGlobalDeclarationPlan importedGlobal,
        ISet<string> emitted)
    {
        var qualifiedName = importedGlobal.QualifiedName;
        var global = importedGlobal.Global;
        if (!emitted.Add(qualifiedName))
        {
            return;
        }

        if (!_context.TypeModel.Globals.ContainsKey(qualifiedName)
            && global.IsConst
            && importedGlobal.Initializer is not null
            && _globalInitializerPlanner.TryPlanVariableInitializer(
                importedGlobal.Initializer,
                global.Type,
                true,
                out var initializerPlan))
        {
            EmitGlobalInitializerPrelude(builder, initializerPlan.PreludeDefinitions);
            builder.AppendLine($"; imported inline const definition: {qualifiedName}");
            builder.AppendLine($"; visibility: {importedGlobal.Visibility.ToString().ToLowerInvariant()}");
            builder.AppendLine(BuildImportedInlineConstDefinition(
                qualifiedName,
                ResolveGlobalSymbolName(qualifiedName),
                global,
                initializerPlan.Rendered));
            builder.AppendLine();
            return;
        }

        var symbolName = ResolveGlobalSymbolName(qualifiedName);
        var storage = global.IsMutable ? "global" : "constant";
        var addressAttribute = GetImportedGlobalAddressAttribute(qualifiedName, global);
        builder.AppendLine($"; imported declaration: {qualifiedName}");
        builder.AppendLine($"; visibility: {importedGlobal.Visibility.ToString().ToLowerInvariant()}");
        var segments = new List<string> { $"@{EscapeIdentifier(symbolName)}", "=", "external" };
        if (addressAttribute is not null)
        {
            segments.Add(addressAttribute);
        }

        segments.Add(storage);
        segments.Add(_context.MapType(global.Type));
        var declaration = string.Join(" ", segments);
        if (GetStarkOwnedGlobalAlignmentBytes(global) is { } alignmentBytes)
        {
            declaration += $", align {alignmentBytes}";
        }

        builder.AppendLine(declaration);
        builder.AppendLine();
    }

    private string BuildImportedInlineConstDefinition(
        string qualifiedName,
        string symbolName,
        TypedGlobalSymbol global,
        string initializer)
    {
        var segments = new List<string> { $"@{EscapeIdentifier(symbolName)}", "=", "internal" };
        if (!global.IsMutable
            && _globalsEligibleForLocalUnnamedAddr.Contains(qualifiedName))
        {
            segments.Add("unnamed_addr");
        }

        segments.Add("constant");
        segments.Add(_context.MapType(global.Type));
        segments.Add(initializer);
        var definition = string.Join(" ", segments);
        if (GetStarkOwnedGlobalAlignmentBytes(global) is { } alignmentBytes)
        {
            definition += $", align {alignmentBytes}";
        }

        return definition;
    }

    private string BuildGlobalDefinition(
        string globalName,
        string symbolName,
        StarkVisibility visibility,
        TypedGlobalSymbol global,
        string initializer)
    {
        var segments = new List<string> { $"@{EscapeIdentifier(symbolName)}", "=" };

        if (_context.ShouldInternalize(visibility))
        {
            segments.Add("internal");
        }

        if (GetGlobalAddressAttribute(globalName, visibility, global) is { } addressAttribute)
        {
            segments.Add(addressAttribute);
        }

        segments.Add(global.IsMutable ? "global" : "constant");
        segments.Add(_context.MapType(global.Type));
        segments.Add(initializer);
        var definition = string.Join(" ", segments);
        if (GetStarkOwnedGlobalAlignmentBytes(global) is { } alignmentBytes)
        {
            definition += $", align {alignmentBytes}";
        }

        return definition;
    }

    private int? GetStarkOwnedGlobalAlignmentBytes(TypedGlobalSymbol global)
    {
        var alignmentBytes = _context.TryGetGlobalAlignmentBytes(global.Type) ?? 1;
        if (!global.IsMutable
            && LlvmAggregateEmissionSupport.TryGetReadonlyVectorizationFriendlyAlignmentBytes(
                global.Type,
                _context.TryGetConcreteTypeLayout(global.Type)) is int preferredReadonlyAlignmentBytes)
        {
            alignmentBytes = Math.Max(alignmentBytes, preferredReadonlyAlignmentBytes);
        }

        return alignmentBytes > 1 ? alignmentBytes : null;
    }

    private string? GetGlobalAddressAttribute(string globalName, StarkVisibility visibility, TypedGlobalSymbol global)
    {
        if (global.IsMutable
            || !_globalsEligibleForLocalUnnamedAddr.Contains(globalName))
        {
            return null;
        }

        return _context.ShouldInternalize(visibility)
            ? "unnamed_addr"
            : "local_unnamed_addr";
    }

    private string? GetImportedGlobalAddressAttribute(string qualifiedName, TypedGlobalSymbol global)
    {
        if (global.IsMutable
            || !_globalsEligibleForLocalUnnamedAddr.Contains(qualifiedName))
        {
            return null;
        }

        return "local_unnamed_addr";
    }

    private void EmitGlobalInitializerPrelude(StringBuilder builder, IReadOnlyList<string> preludeDefinitions)
    {
        foreach (var prelude in preludeDefinitions)
        {
            builder.AppendLine(prelude);
            builder.AppendLine();
        }
    }

    private string ResolveGlobalSymbolName(string globalName)
    {
        return _context.ResolveGlobalSymbolName(globalName);
    }

    private static StarkVisibility ParseVisibility(StarkParser.VisibilityModifierContext? visibilityModifier)
    {
        return visibilityModifier?.GetText() switch
        {
            "internal" => StarkVisibility.Internal,
            "public" => StarkVisibility.Public,
            "export" => StarkVisibility.Export,
            _ => StarkVisibility.Module
        };
    }

    private static string EscapeIdentifier(string identifier)
    {
        var builder = new StringBuilder(identifier.Length);
        foreach (var ch in identifier)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
        }

        return builder.ToString();
    }
}
