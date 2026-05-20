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
        EmitGlobals(builder);
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
