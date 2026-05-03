using Stark.Parsing;

namespace Stark.Compiler;

internal sealed record SyntaxModelDiagnostic(string Code, string Message, int Line, int Column);

internal sealed record SyntaxModelBuildResult(
    SyntaxModel Model,
    IReadOnlyList<SyntaxModelDiagnostic> Diagnostics);

internal static class SyntaxModelFactory
{
    public static SyntaxModel Create(ParseResult parseResult)
    {
        return CreateWithDiagnostics(parseResult, targetInfo: null).Model;
    }

    public static SyntaxModelBuildResult CreateWithDiagnostics(ParseResult parseResult, LlvmTargetInfo? targetInfo)
    {
        var root = parseResult.Root;
        var declarations = new List<TopLevelDeclarationModel>();
        var diagnostics = new List<SyntaxModelDiagnostic>();
        var moduleAttributes = CreateModuleAttributes(root.moduleDeclaration());
        var backendOptimizationMode = ResolveBackendOptimizationMode(root.moduleDeclaration(), moduleAttributes, diagnostics);

        foreach (var declaration in root.topLevelDeclaration())
        {
            AddDeclarationModels(declarations, declaration, diagnostics);
        }

        declarations = ApplyAsmSelection(
            root.topLevelDeclaration(),
            declarations,
            StarkAsmArchitectureFacts.ResolveActiveArchitecture(targetInfo),
            diagnostics);

        return new SyntaxModelBuildResult(
            new SyntaxModel(
                ModuleName: root.moduleDeclaration().qualifiedName().GetText(),
                Imports: root.importDeclaration().Select(CreateImportModel).ToArray(),
                Declarations: declarations,
                ModuleAttributes: moduleAttributes,
                BackendOptimizationMode: backendOptimizationMode),
            diagnostics);
    }

    private static IReadOnlyList<ModuleAttributeModel> CreateModuleAttributes(
        StarkParser.ModuleDeclarationContext moduleDeclaration)
    {
        var attributes = new List<ModuleAttributeModel>();
        foreach (var attributeList in moduleDeclaration.attributeList())
        {
            foreach (var attribute in attributeList.attribute())
            {
                var name = attribute.qualifiedName().GetText();
                var arguments = attribute.attributeArgument()
                    .Select(static argument => argument.GetText())
                    .ToArray();
                attributes.Add(new ModuleAttributeModel(name, arguments));
            }
        }

        return attributes;
    }

    private static ModuleBackendOptimizationMode ResolveBackendOptimizationMode(
        StarkParser.ModuleDeclarationContext moduleDeclaration,
        IReadOnlyList<ModuleAttributeModel> attributes,
        List<SyntaxModelDiagnostic> diagnostics)
    {
        var backendOptimizationMode = ModuleBackendOptimizationMode.Default;
        var backendAttributeCount = 0;
        var attributeContexts = moduleDeclaration.attributeList()
            .SelectMany(static attributeList => attributeList.attribute())
            .ToArray();

        for (var index = 0; index < attributes.Count; index++)
        {
            var attribute = attributes[index];
            var attributeContext = attributeContexts[index];
            if (!string.Equals(attribute.Name, "Backend", StringComparison.Ordinal))
            {
                diagnostics.Add(new SyntaxModelDiagnostic(
                    "STK2110",
                    $"Unsupported module attribute '[{attribute.Name}]'. v1 module attributes only support '[Backend(Opaque)]'.",
                    attributeContext.Start.Line,
                    attributeContext.Start.Column + 1));
                continue;
            }

            backendAttributeCount++;
            if (backendAttributeCount > 1)
            {
                diagnostics.Add(new SyntaxModelDiagnostic(
                    "STK2111",
                    "Module attribute '[Backend(...)]' can only be specified once.",
                    attributeContext.Start.Line,
                    attributeContext.Start.Column + 1));
                continue;
            }

            if (attribute.Arguments.Count != 1
                || !string.Equals(attribute.Arguments[0], "Opaque", StringComparison.Ordinal))
            {
                diagnostics.Add(new SyntaxModelDiagnostic(
                    "STK2112",
                    "Unsupported Backend attribute argument. Use '[Backend(Opaque)]' to mark a module as a backend optimization boundary.",
                    attributeContext.Start.Line,
                    attributeContext.Start.Column + 1));
                continue;
            }

            backendOptimizationMode = ModuleBackendOptimizationMode.Opaque;
        }

        return backendOptimizationMode;
    }

    private static List<TopLevelDeclarationModel> ApplyAsmSelection(
        IReadOnlyList<StarkParser.TopLevelDeclarationContext> declarationContexts,
        IReadOnlyList<TopLevelDeclarationModel> declarations,
        StarkAsmArchitecture activeArchitecture,
        List<SyntaxModelDiagnostic> diagnostics)
    {
        var asmContextsByName = declarationContexts
            .Select(static declaration => declaration.functionDeclaration())
            .Where(static function => function?.asmSpecifier() is not null)
            .Select(static function => function!)
            .GroupBy(static function => function.Identifier().GetText(), StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.Ordinal);
        var selectedNames = new HashSet<string>(StringComparer.Ordinal);
        var asmGroups = declarations
            .Where(static declaration => declaration.Function?.Asm is not null)
            .GroupBy(static declaration => declaration.Name, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.Ordinal);

        if (asmGroups.Count == 0)
        {
            return declarations.ToList();
        }

        if (activeArchitecture == StarkAsmArchitecture.Unknown)
        {
            foreach (var asmDeclaration in asmGroups.Values.SelectMany(static group => group))
            {
                diagnostics.Add(new SyntaxModelDiagnostic(
                    "STK2100",
                    $"Unable to resolve the active build target architecture for asm declaration '{asmDeclaration.Name}'.",
                    GetLine(asmContextsByName, asmDeclaration.Name),
                    GetColumn(asmContextsByName, asmDeclaration.Name)));
            }

            return declarations.Where(static declaration => declaration.Function?.Asm is null).ToList();
        }

        var selectedAsmDeclarations = new Dictionary<string, TopLevelDeclarationModel>(StringComparer.Ordinal);
        foreach (var group in asmGroups)
        {
            var contexts = asmContextsByName[group.Key];
            var validDeclarations = new List<TopLevelDeclarationModel>(group.Value.Length);
            for (var index = 0; index < group.Value.Length; index++)
            {
                if (ValidateAsmDeclarationShape(group.Value[index], contexts[index], diagnostics))
                {
                    validDeclarations.Add(group.Value[index]);
                }
            }

            var nonAsmDeclarations = declarations
                .Where(declaration => declaration.Name == group.Key && declaration.Function?.Asm is null)
                .ToArray();
            if (nonAsmDeclarations.Length != 0)
            {
                diagnostics.Add(new SyntaxModelDiagnostic(
                    "STK2101",
                    $"Function '{group.Key}' mixes asm-targeted and non-asm declarations. v1 asm functions must own the full declaration name.",
                    contexts[0].Identifier().Symbol.Line,
                    contexts[0].Identifier().Symbol.Column + 1));
                continue;
            }

            var matches = validDeclarations
                .Where(declaration => declaration.Function!.Asm!.Architecture == activeArchitecture)
                .ToArray();

            if (matches.Length == 0 && validDeclarations.Count != 0)
            {
                diagnostics.Add(new SyntaxModelDiagnostic(
                    "STK2102",
                    $"No asm declaration for '{group.Key}' matches the active target architecture '{Describe(activeArchitecture)}'.",
                    contexts[0].Identifier().Symbol.Line,
                    contexts[0].Identifier().Symbol.Column + 1));
                continue;
            }

            if (matches.Length > 1)
            {
                diagnostics.Add(new SyntaxModelDiagnostic(
                    "STK2103",
                    $"Multiple asm declarations for '{group.Key}' match the active target architecture '{Describe(activeArchitecture)}'.",
                    contexts[0].Identifier().Symbol.Line,
                    contexts[0].Identifier().Symbol.Column + 1));
                continue;
            }

            if (matches.Length == 1)
            {
                var selected = matches[0];
                var selectedIndex = Array.FindIndex(group.Value, candidate => ReferenceEquals(candidate, selected));
                if (selectedIndex >= 0
                    && ValidateAsmOperandBindings(selected, contexts[selectedIndex], diagnostics))
                {
                    selectedAsmDeclarations[group.Key] = selected;
                }
            }
        }

        var filtered = new List<TopLevelDeclarationModel>(declarations.Count);
        foreach (var declaration in declarations)
        {
            if (declaration.Function?.Asm is null)
            {
                filtered.Add(declaration);
                continue;
            }

            if (selectedNames.Contains(declaration.Name))
            {
                continue;
            }

            if (selectedAsmDeclarations.TryGetValue(declaration.Name, out var selected)
                && ReferenceEquals(selected, declaration))
            {
                filtered.Add(declaration);
                selectedNames.Add(declaration.Name);
            }
        }

        return filtered;
    }

    private static bool ValidateAsmDeclarationShape(
        TopLevelDeclarationModel declaration,
        StarkParser.FunctionDeclarationContext context,
        List<SyntaxModelDiagnostic> diagnostics)
    {
        var function = declaration.Function!;
        var nameToken = context.Identifier().Symbol;
        var modifiers = context.functionModifier().Select(static modifier => modifier.GetText()).ToHashSet(StringComparer.Ordinal);
        var hasUnsupportedModifier = modifiers.Any(static modifier => modifier is not "ffi" and not "unsafe");

        if (function.Asm!.Architecture == StarkAsmArchitecture.Unknown)
        {
            diagnostics.Add(new SyntaxModelDiagnostic(
                "STK2104",
                $"Asm declaration '{declaration.Name}' uses unsupported target architecture '{function.Asm.ArchitectureText}'.",
                nameToken.Line,
                nameToken.Column + 1));
            return false;
        }

        if (!function.Modifiers.IsFfi
            || function.Kind != StarkFunctionKind.Fn
            || context.typeParameterList() is not null
            || context.typeParameterConstraints().Length != 0
            || hasUnsupportedModifier
            || context.functionBody().asmFunctionBody() is null)
        {
            diagnostics.Add(new SyntaxModelDiagnostic(
                "STK2105",
                $"Asm declaration '{declaration.Name}' must use the v1 surface 'unsafe ffi asm(arch) fn' with no generics or extra modifiers except 'unsafe', and must use an asm template body.",
                nameToken.Line,
                nameToken.Column + 1));
            return false;
        }

        return true;
    }

    private static bool ValidateAsmOperandBindings(
        TopLevelDeclarationModel declaration,
        StarkParser.FunctionDeclarationContext context,
        List<SyntaxModelDiagnostic> diagnostics)
    {
        var asm = declaration.Function!.Asm!;
        var parameters = context.parameterList().parameter()
            .ToDictionary(static parameter => parameter.Identifier().GetText(), StringComparer.Ordinal);
        var usedInputBindings = new HashSet<string>(StringComparer.Ordinal);
        var usedOutputBindings = new HashSet<string>(StringComparer.Ordinal);
        var inputRegisters = new HashSet<string>(StringComparer.Ordinal);
        var outputRegisters = new HashSet<string>(StringComparer.Ordinal);
        var returnOutputRegisters = new HashSet<string>(StringComparer.Ordinal);
        var clobberRegisters = new HashSet<string>(StringComparer.Ordinal);
        var returnBindingCount = 0;
        var valid = true;

        if (context.asmClauseList() is not { } clauseList)
        {
            if (context.returnType().VOID() is null)
            {
                diagnostics.Add(new SyntaxModelDiagnostic(
                    "STK2107",
                    $"Asm declaration '{declaration.Name}' must bind the function return value with exactly one 'out(\"reg\") return' operand.",
                    context.Identifier().Symbol.Line,
                    context.Identifier().Symbol.Column + 1));
                return false;
            }

            return true;
        }

        foreach (var clause in clauseList.asmClause())
        {
            if (clause.asmInputClause() is { } input)
            {
                var registerName = DecodeAsmString(input.StringLiteral().GetText());
                valid &= ValidateOperandRegister(declaration.Name, asm.Architecture, registerName, input.StringLiteral().Symbol, diagnostics);
                valid &= ValidateInputRegisterBinding(
                    declaration.Name,
                    registerName,
                    inputRegisters,
                    outputRegisters,
                    returnOutputRegisters,
                    input.StringLiteral().Symbol,
                    diagnostics);

                var valueName = input.Identifier().GetText();
                if (!parameters.TryGetValue(valueName, out var parameter))
                {
                    diagnostics.Add(new SyntaxModelDiagnostic(
                        "STK2107",
                        $"Asm declaration '{declaration.Name}' binds input operand '{valueName}', but no parameter with that name exists.",
                        input.Identifier().Symbol.Line,
                        input.Identifier().Symbol.Column + 1));
                    valid = false;
                    continue;
                }

                if (IsOutputOnlyParameter(parameter))
                {
                    diagnostics.Add(new SyntaxModelDiagnostic(
                        "STK2107",
                        $"Asm declaration '{declaration.Name}' cannot bind input operand '{valueName}' to an 'out' or 'init' parameter.",
                        input.Identifier().Symbol.Line,
                        input.Identifier().Symbol.Column + 1));
                    valid = false;
                }

                if (!usedInputBindings.Add(valueName))
                {
                    diagnostics.Add(new SyntaxModelDiagnostic(
                        "STK2107",
                        $"Asm declaration '{declaration.Name}' binds input operand '{valueName}' more than once.",
                        input.Identifier().Symbol.Line,
                        input.Identifier().Symbol.Column + 1));
                    valid = false;
                }

                if (usedOutputBindings.Contains(valueName))
                {
                    diagnostics.Add(new SyntaxModelDiagnostic(
                        "STK2107",
                        $"Asm declaration '{declaration.Name}' cannot bind '{valueName}' as both an input and an output operand in v1. Use separate shim declarations until explicit inout support exists.",
                        input.Identifier().Symbol.Line,
                        input.Identifier().Symbol.Column + 1));
                    valid = false;
                }

                continue;
            }

            if (clause.asmOutputClause() is { } output)
            {
                var registerName = DecodeAsmString(output.StringLiteral().GetText());
                valid &= ValidateOperandRegister(declaration.Name, asm.Architecture, registerName, output.StringLiteral().Symbol, diagnostics);

                if (output.RETURN() is not null)
                {
                    returnBindingCount++;
                    valid &= ValidateOutputRegisterBinding(
                        declaration.Name,
                        registerName,
                        bindsReturnValue: true,
                        inputRegisters,
                        outputRegisters,
                        returnOutputRegisters,
                        output.StringLiteral().Symbol,
                        diagnostics);
                    if (context.returnType().VOID() is not null)
                    {
                        diagnostics.Add(new SyntaxModelDiagnostic(
                            "STK2107",
                            $"Asm declaration '{declaration.Name}' cannot bind 'return' because the function return type is 'void'.",
                            output.RETURN().Symbol.Line,
                            output.RETURN().Symbol.Column + 1));
                        valid = false;
                    }
                    else if (returnBindingCount > 1)
                    {
                        diagnostics.Add(new SyntaxModelDiagnostic(
                            "STK2107",
                            $"Asm declaration '{declaration.Name}' may bind the function return value at most once.",
                            output.RETURN().Symbol.Line,
                            output.RETURN().Symbol.Column + 1));
                        valid = false;
                    }

                    continue;
                }

                var valueName = output.Identifier()!.GetText();
                valid &= ValidateOutputRegisterBinding(
                    declaration.Name,
                    registerName,
                    bindsReturnValue: false,
                    inputRegisters,
                    outputRegisters,
                    returnOutputRegisters,
                    output.StringLiteral().Symbol,
                    diagnostics);
                if (!parameters.TryGetValue(valueName, out var parameter))
                {
                    diagnostics.Add(new SyntaxModelDiagnostic(
                        "STK2107",
                        $"Asm declaration '{declaration.Name}' binds output operand '{valueName}', but no parameter with that name exists.",
                        output.Identifier()!.Symbol.Line,
                        output.Identifier()!.Symbol.Column + 1));
                    valid = false;
                    continue;
                }

                if (!IsOutputOnlyParameter(parameter))
                {
                    diagnostics.Add(new SyntaxModelDiagnostic(
                        "STK2107",
                        $"Asm declaration '{declaration.Name}' may only bind non-return outputs to 'out' or 'init' parameters, but '{valueName}' is not one of those.",
                        output.Identifier()!.Symbol.Line,
                        output.Identifier()!.Symbol.Column + 1));
                    valid = false;
                }

                if (!usedOutputBindings.Add(valueName))
                {
                    diagnostics.Add(new SyntaxModelDiagnostic(
                        "STK2107",
                        $"Asm declaration '{declaration.Name}' binds output operand '{valueName}' more than once.",
                        output.Identifier()!.Symbol.Line,
                        output.Identifier()!.Symbol.Column + 1));
                    valid = false;
                }

                if (usedInputBindings.Contains(valueName))
                {
                    diagnostics.Add(new SyntaxModelDiagnostic(
                        "STK2107",
                        $"Asm declaration '{declaration.Name}' cannot bind '{valueName}' as both an input and an output operand in v1. Use separate shim declarations until explicit inout support exists.",
                        output.Identifier()!.Symbol.Line,
                        output.Identifier()!.Symbol.Column + 1));
                    valid = false;
                }

                continue;
            }

            if (clause.asmClobberClause() is not { } clobber)
            {
                continue;
            }

            foreach (var registerLiteral in clobber.StringLiteral())
            {
                var registerName = DecodeAsmString(registerLiteral.GetText());
                valid &= ValidateOperandRegister(declaration.Name, asm.Architecture, registerName, registerLiteral.Symbol, diagnostics);

                var normalized = StarkAsmRegisterFacts.Normalize(registerName);
                if (!clobberRegisters.Add(normalized))
                {
                    diagnostics.Add(new SyntaxModelDiagnostic(
                        "STK2108",
                        $"Asm declaration '{declaration.Name}' lists clobber register '{registerName}' more than once.",
                        registerLiteral.Symbol.Line,
                        registerLiteral.Symbol.Column + 1));
                    valid = false;
                }

                if (inputRegisters.Contains(normalized) || outputRegisters.Contains(normalized))
                {
                    diagnostics.Add(new SyntaxModelDiagnostic(
                        "STK2108",
                        $"Asm declaration '{declaration.Name}' cannot clobber register '{registerName}' because it is already bound as an input or output operand.",
                        registerLiteral.Symbol.Line,
                        registerLiteral.Symbol.Column + 1));
                    valid = false;
                }
            }
        }

        if (context.returnType().VOID() is null && returnBindingCount != 1)
        {
            diagnostics.Add(new SyntaxModelDiagnostic(
                "STK2107",
                $"Asm declaration '{declaration.Name}' must bind the function return value with exactly one 'out(\"reg\") return' operand.",
                context.Identifier().Symbol.Line,
                context.Identifier().Symbol.Column + 1));
            valid = false;
        }

        return valid;
    }

    private static bool ValidateOperandRegister(
        string declarationName,
        StarkAsmArchitecture architecture,
        string registerName,
        Antlr4.Runtime.IToken token,
        List<SyntaxModelDiagnostic> diagnostics)
    {
        if (StarkAsmRegisterFacts.IsValidRegister(architecture, registerName))
        {
            return true;
        }

        diagnostics.Add(new SyntaxModelDiagnostic(
            "STK2106",
            $"Asm declaration '{declarationName}' uses register '{registerName}', which is not valid for target architecture '{Describe(architecture)}'.",
            token.Line,
            token.Column + 1));
        return false;
    }

    private static bool ValidateUniqueOperandRegister(
        string declarationName,
        string registerName,
        string message,
        ISet<string> registers,
        Antlr4.Runtime.IToken token,
        List<SyntaxModelDiagnostic> diagnostics)
    {
        var normalized = StarkAsmRegisterFacts.Normalize(registerName);
        if (registers.Add(normalized))
        {
            return true;
        }

        diagnostics.Add(new SyntaxModelDiagnostic(
            "STK2107",
            $"Asm declaration '{declarationName}' {message}",
            token.Line,
            token.Column + 1));
        return false;
    }

    private static bool ValidateInputRegisterBinding(
        string declarationName,
        string registerName,
        ISet<string> inputRegisters,
        ISet<string> outputRegisters,
        ISet<string> returnOutputRegisters,
        Antlr4.Runtime.IToken token,
        List<SyntaxModelDiagnostic> diagnostics)
    {
        var valid = ValidateUniqueOperandRegister(
            declarationName,
            registerName,
            $"binds input register '{registerName}' more than once.",
            inputRegisters,
            token,
            diagnostics);
        var normalized = StarkAsmRegisterFacts.Normalize(registerName);
        if (outputRegisters.Contains(normalized) && !returnOutputRegisters.Contains(normalized))
        {
            diagnostics.Add(new SyntaxModelDiagnostic(
                "STK2107",
                $"Asm declaration '{declarationName}' cannot reuse register '{registerName}' across input/output operands in v1 unless the output binds 'return'.",
                token.Line,
                token.Column + 1));
            valid = false;
        }

        return valid;
    }

    private static bool ValidateOutputRegisterBinding(
        string declarationName,
        string registerName,
        bool bindsReturnValue,
        ISet<string> inputRegisters,
        ISet<string> outputRegisters,
        ISet<string> returnOutputRegisters,
        Antlr4.Runtime.IToken token,
        List<SyntaxModelDiagnostic> diagnostics)
    {
        var valid = ValidateUniqueOperandRegister(
            declarationName,
            registerName,
            $"binds output register '{registerName}' more than once.",
            outputRegisters,
            token,
            diagnostics);
        var normalized = StarkAsmRegisterFacts.Normalize(registerName);

        if (bindsReturnValue)
        {
            returnOutputRegisters.Add(normalized);
            return valid;
        }

        if (inputRegisters.Contains(normalized))
        {
            diagnostics.Add(new SyntaxModelDiagnostic(
                "STK2107",
                $"Asm declaration '{declarationName}' cannot reuse register '{registerName}' across input/output operands in v1 unless the output binds 'return'.",
                token.Line,
                token.Column + 1));
            valid = false;
        }

        return valid;
    }

    private static bool IsOutputOnlyParameter(StarkParser.ParameterContext parameter)
    {
        return parameter.type_().typeQualifier().Any(static qualifier =>
        {
            var text = qualifier.GetText();
            return string.Equals(text, "out", StringComparison.Ordinal)
                || string.Equals(text, "init", StringComparison.Ordinal);
        });
    }

    private static int GetLine(
        IReadOnlyDictionary<string, StarkParser.FunctionDeclarationContext[]> asmContextsByName,
        string name)
    {
        return asmContextsByName.TryGetValue(name, out var contexts)
            ? contexts[0].Identifier().Symbol.Line
            : 1;
    }

    private static int GetColumn(
        IReadOnlyDictionary<string, StarkParser.FunctionDeclarationContext[]> asmContextsByName,
        string name)
    {
        return asmContextsByName.TryGetValue(name, out var contexts)
            ? contexts[0].Identifier().Symbol.Column + 1
            : 1;
    }

    private static string Describe(StarkAsmArchitecture architecture)
    {
        return architecture switch
        {
            StarkAsmArchitecture.X86_64 => "x86_64",
            StarkAsmArchitecture.AArch64 => "aarch64",
            StarkAsmArchitecture.RiscV64 => "riscv64",
            StarkAsmArchitecture.X86 => "x86",
            StarkAsmArchitecture.Arm32 => "arm",
            _ => "unknown"
        };
    }

    private static ImportDeclarationModel CreateImportModel(StarkParser.ImportDeclarationContext importDeclaration)
    {
        return new ImportDeclarationModel(
            importDeclaration.qualifiedName().GetText(),
            importDeclaration.EXPORT() is not null);
    }

    private static IReadOnlyList<ModuleAttributeModel> CreateAttributes(IEnumerable<StarkParser.AttributeListContext> attributeLists)
    {
        var attributes = new List<ModuleAttributeModel>();
        foreach (var attributeList in attributeLists)
        {
            foreach (var attribute in attributeList.attribute())
            {
                var name = attribute.qualifiedName().GetText();
                var arguments = attribute.attributeArgument()
                    .Select(static argument => argument.GetText())
                    .ToArray();
                attributes.Add(new ModuleAttributeModel(name, arguments));
            }
        }

        return attributes;
    }

    private static ModuleBackendOptimizationMode ResolveBackendOptimizationMode(
        IReadOnlyList<ModuleAttributeModel> attributes,
        IReadOnlyList<StarkParser.AttributeContext> attributeContexts,
        string targetDescription,
        List<SyntaxModelDiagnostic> diagnostics)
    {
        var backendOptimizationMode = ModuleBackendOptimizationMode.Default;
        var backendAttributeCount = 0;

        for (var index = 0; index < attributes.Count; index++)
        {
            var attribute = attributes[index];
            var attributeContext = attributeContexts[index];
            if (!string.Equals(attribute.Name, "Backend", StringComparison.Ordinal))
            {
                diagnostics.Add(new SyntaxModelDiagnostic(
                    "STK2110",
                    $"Unsupported attribute '[{attribute.Name}]' on {targetDescription}. v1 attributes only support '[Backend(Opaque)]'.",
                    attributeContext.Start.Line,
                    attributeContext.Start.Column + 1));
                continue;
            }

            backendAttributeCount++;
            if (backendAttributeCount > 1)
            {
                diagnostics.Add(new SyntaxModelDiagnostic(
                    "STK2111",
                    $"Attribute '[Backend(...)]' can only be specified once on {targetDescription}.",
                    attributeContext.Start.Line,
                    attributeContext.Start.Column + 1));
                continue;
            }

            if (attribute.Arguments.Count != 1
                || !string.Equals(attribute.Arguments[0], "Opaque", StringComparison.Ordinal))
            {
                diagnostics.Add(new SyntaxModelDiagnostic(
                    "STK2112",
                    "Unsupported Backend attribute argument. Use '[Backend(Opaque)]' to mark a backend optimization boundary.",
                    attributeContext.Start.Line,
                    attributeContext.Start.Column + 1));
                continue;
            }

            backendOptimizationMode = ModuleBackendOptimizationMode.Opaque;
        }

        return backendOptimizationMode;
    }

    private static void AddUnsupportedAttributeDiagnostics(
        IReadOnlyList<ModuleAttributeModel> attributes,
        IReadOnlyList<StarkParser.AttributeContext> attributeContexts,
        string targetDescription,
        List<SyntaxModelDiagnostic> diagnostics)
    {
        for (var index = 0; index < attributes.Count; index++)
        {
            var attribute = attributes[index];
            var attributeContext = attributeContexts[index];
            diagnostics.Add(new SyntaxModelDiagnostic(
                "STK2110",
                $"Unsupported attribute '[{attribute.Name}]' on {targetDescription}. v1 attributes only support modules, callables, structs, records, and doctrines.",
                attributeContext.Start.Line,
                attributeContext.Start.Column + 1));
        }
    }

    private static void AddDeclarationModels(
        List<TopLevelDeclarationModel> declarations,
        StarkParser.TopLevelDeclarationContext declaration,
        List<SyntaxModelDiagnostic> diagnostics)
    {
        var visibility = ParseVisibility(declaration.visibilityModifier());
        var declarationAttributes = CreateAttributes(declaration.attributeList());
        var declarationAttributeContexts = declaration.attributeList()
            .SelectMany(static attributeList => attributeList.attribute())
            .ToArray();

        if (declaration.functionDeclaration() is { } function)
        {
            var backendOptimizationMode = ResolveBackendOptimizationMode(
                declarationAttributes,
                declarationAttributeContexts,
                $"function '{function.Identifier().GetText()}'",
                diagnostics);
            declarations.Add(new TopLevelDeclarationModel(
                function.Identifier().GetText(),
                DeclarationKind.Function,
                visibility,
                CreateFunctionModel(
                    function.Identifier().GetText(),
                    ParseFunctionKind(function.functionKind()),
                    function.returnType(),
                    function.typeParameterList(),
                    function.parameterList(),
                    function.parameterMemoryContractClause(),
                    function.asmSpecifier(),
                    function.asmClauseList(),
                    function.functionModifier(),
                    function.functionBody(),
                    declarationAttributes,
                    backendOptimizationMode,
                    $"function '{function.Identifier().GetText()}'",
                    diagnostics),
                Attributes: declarationAttributes,
                BackendOptimizationMode: backendOptimizationMode));
            return;
        }

        if (declaration.structDeclaration() is { } structDeclaration)
        {
            var backendOptimizationMode = ResolveBackendOptimizationMode(
                declarationAttributes,
                declarationAttributeContexts,
                $"struct '{structDeclaration.Identifier().GetText()}'",
                diagnostics);
            declarations.Add(new TopLevelDeclarationModel(
                structDeclaration.Identifier().GetText(),
                DeclarationKind.Struct,
                visibility,
                null,
                Destructor: CreateDestructorModel(
                    structDeclaration.structBody().structMember()
                        .FirstOrDefault(static member => member.destructorDeclaration() is not null),
                    backendOptimizationMode,
                    diagnostics),
                Attributes: declarationAttributes,
                BackendOptimizationMode: backendOptimizationMode));

            foreach (var member in structDeclaration.structBody().structMember())
            {
                var method = member.methodDeclaration();
                if (method is null)
                {
                    ValidateNonMethodMemberAttributes(member, diagnostics);
                    continue;
                }

                var memberAttributes = CreateAttributes(member.attributeList());
                var memberAttributeContexts = member.attributeList()
                    .SelectMany(static attributeList => attributeList.attribute())
                    .ToArray();
                var methodBackendOptimizationMode = ResolveBackendOptimizationMode(
                    memberAttributes,
                    memberAttributeContexts,
                    $"method '{structDeclaration.Identifier().GetText()}.{method.Identifier().GetText()}'",
                    diagnostics);
                if (methodBackendOptimizationMode == ModuleBackendOptimizationMode.Default)
                {
                    methodBackendOptimizationMode = backendOptimizationMode;
                }

                var methodVisibility = ResolveMemberVisibility(visibility, method.visibilityModifier());
                declarations.Add(new TopLevelDeclarationModel(
                    $"{structDeclaration.Identifier().GetText()}.{method.Identifier().GetText()}",
                    DeclarationKind.Function,
                    methodVisibility,
                    CreateFunctionModel(
                        $"{structDeclaration.Identifier().GetText()}.{method.Identifier().GetText()}",
                        ParseFunctionKind(method.functionKind()),
                        method.returnType(),
                        method.typeParameterList(),
                        method.parameterList(),
                        method.parameterMemoryContractClause(),
                        null,
                        null,
                        method.functionModifier(),
                        method.functionBody(),
                        memberAttributes,
                        methodBackendOptimizationMode,
                        $"method '{structDeclaration.Identifier().GetText()}.{method.Identifier().GetText()}'",
                        diagnostics),
                    Attributes: memberAttributes,
                    BackendOptimizationMode: methodBackendOptimizationMode));
            }

            return;
        }

        if (declaration.recordDeclaration() is { } recordDeclaration)
        {
            var backendOptimizationMode = ResolveBackendOptimizationMode(
                declarationAttributes,
                declarationAttributeContexts,
                $"record '{recordDeclaration.Identifier().GetText()}'",
                diagnostics);
            declarations.Add(new TopLevelDeclarationModel(
                recordDeclaration.Identifier().GetText(),
                DeclarationKind.Record,
                visibility,
                null,
                Destructor: CreateDestructorModel(
                    recordDeclaration.recordBody().recordMember()
                        .FirstOrDefault(static member => member.destructorDeclaration() is not null),
                    backendOptimizationMode,
                    diagnostics),
                Attributes: declarationAttributes,
                BackendOptimizationMode: backendOptimizationMode));

            foreach (var member in recordDeclaration.recordBody().recordMember())
            {
                var method = member.methodDeclaration();
                if (method is null)
                {
                    ValidateNonMethodMemberAttributes(member, diagnostics);
                    continue;
                }

                var memberAttributes = CreateAttributes(member.attributeList());
                var memberAttributeContexts = member.attributeList()
                    .SelectMany(static attributeList => attributeList.attribute())
                    .ToArray();
                var methodBackendOptimizationMode = ResolveBackendOptimizationMode(
                    memberAttributes,
                    memberAttributeContexts,
                    $"method '{recordDeclaration.Identifier().GetText()}.{method.Identifier().GetText()}'",
                    diagnostics);
                if (methodBackendOptimizationMode == ModuleBackendOptimizationMode.Default)
                {
                    methodBackendOptimizationMode = backendOptimizationMode;
                }

                var methodVisibility = ResolveMemberVisibility(visibility, method.visibilityModifier());
                declarations.Add(new TopLevelDeclarationModel(
                    $"{recordDeclaration.Identifier().GetText()}.{method.Identifier().GetText()}",
                    DeclarationKind.Function,
                    methodVisibility,
                    CreateFunctionModel(
                        $"{recordDeclaration.Identifier().GetText()}.{method.Identifier().GetText()}",
                        ParseFunctionKind(method.functionKind()),
                        method.returnType(),
                        method.typeParameterList(),
                        method.parameterList(),
                        method.parameterMemoryContractClause(),
                        null,
                        null,
                        method.functionModifier(),
                        method.functionBody(),
                        memberAttributes,
                        methodBackendOptimizationMode,
                        $"method '{recordDeclaration.Identifier().GetText()}.{method.Identifier().GetText()}'",
                        diagnostics),
                    Attributes: memberAttributes,
                    BackendOptimizationMode: methodBackendOptimizationMode));
            }

            return;
        }

        if (declaration.enumDeclaration() is { } enumDeclaration)
        {
            AddUnsupportedAttributeDiagnostics(
                declarationAttributes,
                declarationAttributeContexts,
                $"enum '{enumDeclaration.Identifier().GetText()}'",
                diagnostics);
            declarations.Add(new TopLevelDeclarationModel(
                enumDeclaration.Identifier().GetText(),
                DeclarationKind.Enum,
                visibility,
                null));
            return;
        }

        if (declaration.traitDeclaration() is { } traitDeclaration)
        {
            AddUnsupportedAttributeDiagnostics(
                declarationAttributes,
                declarationAttributeContexts,
                $"trait '{traitDeclaration.Identifier().GetText()}'",
                diagnostics);
            declarations.Add(new TopLevelDeclarationModel(
                traitDeclaration.Identifier().GetText(),
                DeclarationKind.Trait,
                visibility,
                null));

            foreach (var member in traitDeclaration.traitBody().traitMember())
            {
                var method = member.traitMethodDeclaration();
                var memberAttributes = CreateAttributes(member.attributeList());
                var memberAttributeContexts = member.attributeList()
                    .SelectMany(static attributeList => attributeList.attribute())
                    .ToArray();
                var methodBackendOptimizationMode = ResolveBackendOptimizationMode(
                    memberAttributes,
                    memberAttributeContexts,
                    $"trait method '{traitDeclaration.Identifier().GetText()}.{method.Identifier().GetText()}'",
                    diagnostics);
                declarations.Add(new TopLevelDeclarationModel(
                    $"{traitDeclaration.Identifier().GetText()}.{method.Identifier().GetText()}",
                    DeclarationKind.Function,
                    visibility,
                    CreateFunctionModel(
                        $"{traitDeclaration.Identifier().GetText()}.{method.Identifier().GetText()}",
                        ParseFunctionKind(method.functionKind()),
                        method.returnType(),
                        method.typeParameterList(),
                        method.parameterList(),
                        method.parameterMemoryContractClause(),
                        null,
                        null,
                        method.functionModifier(),
                        method.functionBody(),
                        memberAttributes,
                        methodBackendOptimizationMode,
                        $"trait method '{traitDeclaration.Identifier().GetText()}.{method.Identifier().GetText()}'",
                        diagnostics),
                    Attributes: memberAttributes,
                    BackendOptimizationMode: methodBackendOptimizationMode));
            }

            return;
        }

        if (declaration.doctrineDeclaration() is { } doctrineDeclaration)
        {
            var backendOptimizationMode = ResolveBackendOptimizationMode(
                declarationAttributes,
                declarationAttributeContexts,
                $"doctrine '{doctrineDeclaration.Identifier().GetText()}'",
                diagnostics);
            declarations.Add(new TopLevelDeclarationModel(
                doctrineDeclaration.Identifier().GetText(),
                DeclarationKind.Doctrine,
                visibility,
                null,
                Attributes: declarationAttributes,
                BackendOptimizationMode: backendOptimizationMode));

            foreach (var member in doctrineDeclaration.doctrineBody().doctrineMember())
            {
                var method = member.doctrineMethodDeclaration();
                var memberAttributes = CreateAttributes(member.attributeList());
                var memberAttributeContexts = member.attributeList()
                    .SelectMany(static attributeList => attributeList.attribute())
                    .ToArray();
                var methodBackendOptimizationMode = ResolveBackendOptimizationMode(
                    memberAttributes,
                    memberAttributeContexts,
                    $"doctrine method '{doctrineDeclaration.Identifier().GetText()}.{method.Identifier().GetText()}'",
                    diagnostics);
                if (methodBackendOptimizationMode == ModuleBackendOptimizationMode.Default)
                {
                    methodBackendOptimizationMode = backendOptimizationMode;
                }

                declarations.Add(new TopLevelDeclarationModel(
                    $"{doctrineDeclaration.Identifier().GetText()}.{method.Identifier().GetText()}",
                    DeclarationKind.Function,
                    visibility,
                    CreateFunctionModel(
                        $"{doctrineDeclaration.Identifier().GetText()}.{method.Identifier().GetText()}",
                        ParseDoctrineFunctionKind(method.doctrineFunctionKind()),
                        method.returnType(),
                        method.typeParameterList(),
                        method.parameterList(),
                        method.parameterMemoryContractClause(),
                        null,
                        null,
                        method.functionModifier(),
                        method.functionBody(),
                        memberAttributes,
                        methodBackendOptimizationMode,
                        $"doctrine method '{doctrineDeclaration.Identifier().GetText()}.{method.Identifier().GetText()}'",
                        diagnostics),
                    Attributes: memberAttributes,
                    BackendOptimizationMode: methodBackendOptimizationMode));
            }

            return;
        }

        if (declaration.typeAliasDeclaration() is { } typeAliasDeclaration)
        {
            AddUnsupportedAttributeDiagnostics(
                declarationAttributes,
                declarationAttributeContexts,
                $"type alias '{typeAliasDeclaration.Identifier().GetText()}'",
                diagnostics);
            declarations.Add(new TopLevelDeclarationModel(
                typeAliasDeclaration.Identifier().GetText(),
                DeclarationKind.TypeAlias,
                visibility,
                null,
                TypeAlias: CreateTypeAliasModel(typeAliasDeclaration)));
            return;
        }

        if (declaration.globalConstantDeclaration() is { } constantDeclaration)
        {
            AddUnsupportedAttributeDiagnostics(
                declarationAttributes,
                declarationAttributeContexts,
                $"global constant '{constantDeclaration.constantDeclarators().constantDeclarator(0).Identifier().GetText()}'",
                diagnostics);
            declarations.Add(new TopLevelDeclarationModel(
                constantDeclaration.constantDeclarators().constantDeclarator(0).Identifier().GetText(),
                DeclarationKind.GlobalConstant,
                visibility,
                null));
            return;
        }

        var variableDeclaration = declaration.globalVariableDeclaration()
            ?? throw new InvalidOperationException("Unsupported top-level declaration shape.");

        AddUnsupportedAttributeDiagnostics(
            declarationAttributes,
            declarationAttributeContexts,
            $"global variable '{variableDeclaration.variableDeclarators().variableDeclarator(0).Identifier().GetText()}'",
            diagnostics);
        declarations.Add(new TopLevelDeclarationModel(
            variableDeclaration.variableDeclarators().variableDeclarator(0).Identifier().GetText(),
            DeclarationKind.GlobalVariable,
            visibility,
            null));
    }

    private static void ValidateNonMethodMemberAttributes(
        StarkParser.StructMemberContext member,
        List<SyntaxModelDiagnostic> diagnostics)
    {
        var attributes = CreateAttributes(member.attributeList());
        var attributeContexts = member.attributeList()
            .SelectMany(static attributeList => attributeList.attribute())
            .ToArray();
        if (member.destructorDeclaration() is { } destructor)
        {
            _ = ResolveBackendOptimizationMode(
                attributes,
                attributeContexts,
                destructor.MUT() is null ? "destructor" : "mutable destructor",
                diagnostics);
            return;
        }

        AddUnsupportedAttributeDiagnostics(attributes, attributeContexts, "struct member", diagnostics);
    }

    private static void ValidateNonMethodMemberAttributes(
        StarkParser.RecordMemberContext member,
        List<SyntaxModelDiagnostic> diagnostics)
    {
        var attributes = CreateAttributes(member.attributeList());
        var attributeContexts = member.attributeList()
            .SelectMany(static attributeList => attributeList.attribute())
            .ToArray();
        if (member.destructorDeclaration() is { } destructor)
        {
            _ = ResolveBackendOptimizationMode(
                attributes,
                attributeContexts,
                destructor.MUT() is null ? "destructor" : "mutable destructor",
                diagnostics);
            return;
        }

        AddUnsupportedAttributeDiagnostics(attributes, attributeContexts, "record member", diagnostics);
    }

    private static TypeAliasDeclarationModel CreateTypeAliasModel(StarkParser.TypeAliasDeclarationContext typeAliasDeclaration)
    {
        var genericParameters = typeAliasDeclaration.typeParameterList() is { } typeParameterList
            ? typeParameterList.typeParameter()
                .Select(static parameter => parameter.Identifier().GetText())
                .ToArray()
            : [];

        return new TypeAliasDeclarationModel(
            typeAliasDeclaration.Identifier().GetText(),
            typeAliasDeclaration.type_().GetText(),
            genericParameters);
    }

    private static FunctionDeclarationModel CreateFunctionModel(
        string name,
        StarkFunctionKind functionKind,
        StarkParser.ReturnTypeContext returnType,
        StarkParser.TypeParameterListContext? typeParameterList,
        StarkParser.ParameterListContext parameterList,
        IReadOnlyList<StarkParser.ParameterMemoryContractClauseContext> memoryContractClauses,
        StarkParser.AsmSpecifierContext? asmSpecifier,
        StarkParser.AsmClauseListContext? asmClauseList,
        IReadOnlyList<StarkParser.FunctionModifierContext> modifiersList,
        StarkParser.FunctionBodyContext functionBody,
        IReadOnlyList<ModuleAttributeModel>? attributes = null,
        ModuleBackendOptimizationMode backendOptimizationMode = ModuleBackendOptimizationMode.Default,
        string? targetDescription = null,
        List<SyntaxModelDiagnostic>? diagnostics = null)
    {
        var modifiers = modifiersList.Select(static modifier => modifier.GetText()).ToHashSet(StringComparer.Ordinal);
        AddBackendOpaqueModifierDiagnostics(
            backendOptimizationMode,
            modifiersList,
            targetDescription ?? $"function '{name}'",
            diagnostics);
        var hasExplicitInlinePreference = modifiers.Contains("inline")
            || modifiers.Contains("noinline")
            || modifiers.Contains("inlinehint");
        var inlinePreference = modifiers.Contains("inline")
            ? InlinePreference.Inline
            : modifiers.Contains("noinline")
                ? InlinePreference.NoInline
                : InlinePreference.InlineHint;
        var genericParameters = typeParameterList is null
            ? []
            : typeParameterList.typeParameter()
                .Select(static parameter => parameter.Identifier().GetText())
                .ToArray();

        return new FunctionDeclarationModel(
            Name: name,
            Kind: functionKind,
            ReturnType: returnType.GetText(),
            Parameters: parameterList.parameter()
                .Select(CreateParameterModel)
                .ToArray(),
            Modifiers: new FunctionModifierSet(
                inlinePreference,
                hasExplicitInlinePreference,
                modifiers.Contains("hot"),
                modifiers.Contains("cold"),
                modifiers.Contains("ffi"),
                modifiers.Contains("varargs"),
                modifiers.Contains("strictfp"),
                modifiers.Contains("unsafe")),
            HasBody: functionBody.block() is not null,
            Asm: CreateAsmModel(asmSpecifier, asmClauseList, functionBody),
            GenericParameterNames: genericParameters,
            IsStatic: modifiers.Contains("static"),
            Attributes: attributes,
            BackendOptimizationMode: backendOptimizationMode,
            DisjointParameterGroups: CreateDisjointParameterGroups(parameterList, memoryContractClauses));
    }

    private static ParameterModel CreateParameterModel(StarkParser.ParameterContext parameter)
    {
        return new ParameterModel(
            parameter.Identifier().GetText(),
            parameter.type_().GetText(),
            IsDisjoint: ParameterHasPrefix(parameter, StarkParser.DISJOINT),
            IsConst: ParameterHasPrefix(parameter, StarkParser.CONST),
            RawPointerElementCountExpression: TryGetBoundedRawPointerElementCount(parameter.type_()));
    }

    private static string? TryGetBoundedRawPointerElementCount(StarkParser.Type_Context type)
    {
        return type.nonArrayType().rawPointerType() is not null
            && type.arraySuffix() is [var suffix]
            && suffix.expression() is { } expression
                ? expression.GetText()
                : null;
    }

    private static bool ParameterHasPrefix(StarkParser.ParameterContext parameter, int tokenType)
    {
        return parameter.parameterContractPrefix()
            .Any(prefix => prefix.Start.Type == tokenType);
    }

    private static IReadOnlyList<ParameterDisjointGroup> CreateDisjointParameterGroups(
        StarkParser.ParameterListContext parameterList,
        IReadOnlyList<StarkParser.ParameterMemoryContractClauseContext> memoryContractClauses)
    {
        var groups = new List<ParameterDisjointGroup>();
        var prefixedParameters = parameterList.parameter()
            .Where(static parameter => ParameterHasPrefix(parameter, StarkParser.DISJOINT))
            .Select(static parameter => parameter.Identifier().GetText())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (prefixedParameters.Length > 1)
        {
            groups.Add(new ParameterDisjointGroup(prefixedParameters));
        }

        foreach (var clause in memoryContractClauses)
        {
            foreach (var contract in clause.disjointContract())
            {
                var names = contract.expressionList()
                    .expression()
                    .Select(static expression => TryGetDisjointContractParameterName(expression.GetText()))
                    .Where(static name => !string.IsNullOrWhiteSpace(name))
                    .Select(static name => name!)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (names.Length > 1)
                {
                    groups.Add(new ParameterDisjointGroup(names));
                }
            }
        }

        return groups;
    }

    private static string? TryGetDisjointContractParameterName(string operandText)
    {
        operandText = TrimOuterParentheses(operandText);
        if (!TryReadIdentifier(operandText, 0, out var identifier, out var position))
        {
            return null;
        }

        return position == operandText.Length || operandText[position] == '['
            ? identifier
            : null;
    }

    private static string TrimOuterParentheses(string text)
    {
        while (text.Length >= 2 && text[0] == '(' && text[^1] == ')')
        {
            text = text[1..^1];
        }

        return text;
    }

    private static bool TryReadIdentifier(string text, int start, out string identifier, out int end)
    {
        identifier = string.Empty;
        end = start;
        if (start >= text.Length || !(char.IsLetter(text[start]) || text[start] == '_'))
        {
            return false;
        }

        end = start + 1;
        while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_'))
        {
            end++;
        }

        identifier = text[start..end];
        return true;
    }

    private static void AddBackendOpaqueModifierDiagnostics(
        ModuleBackendOptimizationMode backendOptimizationMode,
        IReadOnlyList<StarkParser.FunctionModifierContext> modifiers,
        string targetDescription,
        List<SyntaxModelDiagnostic>? diagnostics)
    {
        if (backendOptimizationMode != ModuleBackendOptimizationMode.Opaque || diagnostics is null)
        {
            return;
        }

        foreach (var modifier in modifiers)
        {
            var modifierText = modifier.GetText();
            var message = modifierText switch
            {
                "inline" or "inlinehint" =>
                    $"[Backend(Opaque)] conflicts with '{modifierText}' on {targetDescription}; opaque callables are backend boundaries and are forced noinline.",
                "noinline" =>
                    $"[Backend(Opaque)] conflicts with 'noinline' on {targetDescription}; opaque callables are already forced noinline.",
                "hot" =>
                    $"[Backend(Opaque)] conflicts with 'hot' on {targetDescription}; opaque callables emit an unoptimized backend boundary, so hot-path optimization cannot apply.",
                "cold" =>
                    $"[Backend(Opaque)] conflicts with 'cold' on {targetDescription}; opaque callables are backend boundaries rather than layout-tuned cold paths.",
                "ffi" =>
                    $"[Backend(Opaque)] conflicts with 'ffi' on {targetDescription}; FFI callables are ABI boundaries rather than source bodies for backend optimization containment.",
                _ => null
            };

            if (message is null)
            {
                continue;
            }

            diagnostics.Add(new SyntaxModelDiagnostic(
                "STK2113",
                message,
                modifier.Start.Line,
                modifier.Start.Column + 1));
        }
    }

    private static DestructorDeclarationModel? CreateDestructorModel(
        StarkParser.StructMemberContext? member,
        ModuleBackendOptimizationMode containingBackendOptimizationMode,
        List<SyntaxModelDiagnostic> diagnostics)
    {
        var destructor = member?.destructorDeclaration();
        if (destructor is null)
        {
            return null;
        }

        return CreateDestructorModel(
            destructor,
            CreateAttributes(member!.attributeList()),
            member.attributeList()
                .SelectMany(static attributeList => attributeList.attribute())
                .ToArray(),
            containingBackendOptimizationMode,
            diagnostics);
    }

    private static DestructorDeclarationModel? CreateDestructorModel(
        StarkParser.RecordMemberContext? member,
        ModuleBackendOptimizationMode containingBackendOptimizationMode,
        List<SyntaxModelDiagnostic> diagnostics)
    {
        var destructor = member?.destructorDeclaration();
        if (destructor is null)
        {
            return null;
        }

        return CreateDestructorModel(
            destructor,
            CreateAttributes(member!.attributeList()),
            member.attributeList()
                .SelectMany(static attributeList => attributeList.attribute())
                .ToArray(),
            containingBackendOptimizationMode,
            diagnostics);
    }

    private static DestructorDeclarationModel CreateDestructorModel(
        StarkParser.DestructorDeclarationContext destructor,
        IReadOnlyList<ModuleAttributeModel> attributes,
        IReadOnlyList<StarkParser.AttributeContext> attributeContexts,
        ModuleBackendOptimizationMode containingBackendOptimizationMode,
        List<SyntaxModelDiagnostic> diagnostics)
    {
        var backendOptimizationMode = ResolveBackendOptimizationMode(
            attributes,
            attributeContexts,
            destructor.MUT() is null ? "destructor" : "mutable destructor",
            diagnostics);
        if (backendOptimizationMode == ModuleBackendOptimizationMode.Default)
        {
            backendOptimizationMode = containingBackendOptimizationMode;
        }

        return new DestructorDeclarationModel(
            destructor.MUT() is not null,
            attributes,
            backendOptimizationMode);
    }

    private static AsmFunctionModel? CreateAsmModel(
        StarkParser.AsmSpecifierContext? asmSpecifier,
        StarkParser.AsmClauseListContext? asmClauseList,
        StarkParser.FunctionBodyContext functionBody)
    {
        if (asmSpecifier is null)
        {
            return null;
        }

        var architectureText = asmSpecifier.Identifier().GetText();
        StarkAsmArchitectureFacts.TryParseArchitectureName(architectureText, out var architecture);

        var inputs = new List<AsmInputOperandModel>();
        var outputs = new List<AsmOutputOperandModel>();
        var clobbers = new List<string>();

        if (asmClauseList is not null)
        {
            foreach (var clause in asmClauseList.asmClause())
            {
                if (clause.asmInputClause() is { } input)
                {
                    inputs.Add(new AsmInputOperandModel(
                        RegisterName: DecodeAsmString(input.StringLiteral().GetText()),
                        ValueName: input.Identifier().GetText()));
                    continue;
                }

                if (clause.asmOutputClause() is { } output)
                {
                    var valueName = output.Identifier()?.GetText() ?? "return";
                    outputs.Add(new AsmOutputOperandModel(
                        RegisterName: DecodeAsmString(output.StringLiteral().GetText()),
                        ValueName: valueName,
                        BindsReturnValue: output.RETURN() is not null));
                    continue;
                }

                if (clause.asmClobberClause() is { } clobber)
                {
                    foreach (var registerLiteral in clobber.StringLiteral())
                    {
                        clobbers.Add(DecodeAsmString(registerLiteral.GetText()));
                    }
                }
            }
        }

        var templateText = functionBody.asmFunctionBody() is { } asmBody
            ? DecodeAsmString(asmBody.StringLiteral().GetText())
            : string.Empty;

        return new AsmFunctionModel(
            architecture,
            architectureText,
            templateText,
            inputs,
            outputs,
            clobbers);
    }

    private static string DecodeAsmString(string literalText)
    {
        return TextLiteralDecoder.TryDecode(literalText, TextLiteralKind.String, out var decoded, out _)
            ? decoded.Value
            : literalText.Length >= 2
                ? literalText[1..^1]
                : literalText;
    }

    private static StarkVisibility ParseVisibility(StarkParser.VisibilityModifierContext? visibilityModifier)
    {
        if (visibilityModifier is null)
        {
            return StarkVisibility.Module;
        }

        return visibilityModifier.GetText() switch
        {
            "internal" => StarkVisibility.Internal,
            "public" => StarkVisibility.Public,
            "export" => StarkVisibility.Export,
            _ => StarkVisibility.Module
        };
    }

    private static StarkVisibility ResolveMemberVisibility(
        StarkVisibility enclosingVisibility,
        StarkParser.VisibilityModifierContext? explicitVisibility)
    {
        if (explicitVisibility is not null)
        {
            return ParseVisibility(explicitVisibility);
        }

        return enclosingVisibility == StarkVisibility.Export
            ? StarkVisibility.Public
            : enclosingVisibility;
    }

    private static StarkFunctionKind ParseFunctionKind(StarkParser.FunctionKindContext functionKind)
    {
        return functionKind.GetText() switch
        {
            "fn" => StarkFunctionKind.Fn,
            "finite" => StarkFunctionKind.Finite,
            "law" => StarkFunctionKind.Law,
            "finitelaw" => StarkFunctionKind.FiniteLaw,
            _ => throw new InvalidOperationException($"Unsupported function kind '{functionKind.GetText()}'.")
        };
    }

    private static StarkFunctionKind ParseDoctrineFunctionKind(StarkParser.DoctrineFunctionKindContext functionKind)
    {
        return functionKind.GetText() switch
        {
            "law" => StarkFunctionKind.Law,
            "finitelaw" => StarkFunctionKind.FiniteLaw,
            _ => throw new InvalidOperationException($"Unsupported doctrine function kind '{functionKind.GetText()}'.")
        };
    }
}
