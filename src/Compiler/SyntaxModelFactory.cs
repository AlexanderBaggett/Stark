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

        foreach (var declaration in root.topLevelDeclaration())
        {
            AddDeclarationModels(declarations, declaration);
        }

        var diagnostics = new List<SyntaxModelDiagnostic>();
        declarations = ApplyAsmSelection(
            root.topLevelDeclaration(),
            declarations,
            StarkAsmArchitectureFacts.ResolveActiveArchitecture(targetInfo),
            diagnostics);

        return new SyntaxModelBuildResult(
            new SyntaxModel(
                ModuleName: root.moduleDeclaration().qualifiedName().GetText(),
                Imports: root.importDeclaration().Select(CreateImportModel).ToArray(),
                Declarations: declarations),
            diagnostics);
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
        var hasUnsupportedModifier = modifiers.Any(static modifier => modifier is not "ffi");

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
                $"Asm declaration '{declaration.Name}' must use the v1 surface 'ffi asm(arch) fn' with no generics or extra modifiers, and must use an asm template body.",
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

    private static void AddDeclarationModels(List<TopLevelDeclarationModel> declarations, StarkParser.TopLevelDeclarationContext declaration)
    {
        var visibility = ParseVisibility(declaration.visibilityModifier());

        if (declaration.functionDeclaration() is { } function)
        {
            declarations.Add(new TopLevelDeclarationModel(
                function.Identifier().GetText(),
                DeclarationKind.Function,
                visibility,
                CreateFunctionModel(
                    function.Identifier().GetText(),
                    ParseFunctionKind(function.functionKind()),
                    function.returnType(),
                    function.parameterList(),
                    function.asmSpecifier(),
                    function.asmClauseList(),
                    function.functionModifier(),
                    function.functionBody())));
            return;
        }

        if (declaration.structDeclaration() is { } structDeclaration)
        {
            declarations.Add(new TopLevelDeclarationModel(
                structDeclaration.Identifier().GetText(),
                DeclarationKind.Struct,
                visibility,
                null,
                Destructor: CreateDestructorModel(
                    structDeclaration.structBody().structMember()
                        .Select(static member => member.destructorDeclaration())
                        .FirstOrDefault(static destructor => destructor is not null))));

            foreach (var method in structDeclaration.structBody().structMember()
                         .Select(static member => member.methodDeclaration())
                         .Where(static method => method is not null)!)
            {
                declarations.Add(new TopLevelDeclarationModel(
                    $"{structDeclaration.Identifier().GetText()}.{method.Identifier().GetText()}",
                    DeclarationKind.Function,
                    visibility,
                    CreateFunctionModel(
                        $"{structDeclaration.Identifier().GetText()}.{method.Identifier().GetText()}",
                        ParseFunctionKind(method.functionKind()),
                        method.returnType(),
                        method.parameterList(),
                        null,
                        null,
                        method.functionModifier(),
                        method.functionBody())));
            }

            return;
        }

        if (declaration.recordDeclaration() is { } recordDeclaration)
        {
            declarations.Add(new TopLevelDeclarationModel(
                recordDeclaration.Identifier().GetText(),
                DeclarationKind.Record,
                visibility,
                null,
                Destructor: CreateDestructorModel(
                    recordDeclaration.recordBody().recordMember()
                        .Select(static member => member.destructorDeclaration())
                        .FirstOrDefault(static destructor => destructor is not null))));

            foreach (var method in recordDeclaration.recordBody().recordMember()
                         .Select(static member => member.methodDeclaration())
                         .Where(static method => method is not null)!)
            {
                declarations.Add(new TopLevelDeclarationModel(
                    $"{recordDeclaration.Identifier().GetText()}.{method.Identifier().GetText()}",
                    DeclarationKind.Function,
                    visibility,
                    CreateFunctionModel(
                        $"{recordDeclaration.Identifier().GetText()}.{method.Identifier().GetText()}",
                        ParseFunctionKind(method.functionKind()),
                        method.returnType(),
                        method.parameterList(),
                        null,
                        null,
                        method.functionModifier(),
                        method.functionBody())));
            }

            return;
        }

        if (declaration.enumDeclaration() is { } enumDeclaration)
        {
            declarations.Add(new TopLevelDeclarationModel(
                enumDeclaration.Identifier().GetText(),
                DeclarationKind.Enum,
                visibility,
                null));
            return;
        }

        if (declaration.traitDeclaration() is { } traitDeclaration)
        {
            declarations.Add(new TopLevelDeclarationModel(
                traitDeclaration.Identifier().GetText(),
                DeclarationKind.Trait,
                visibility,
                null));

            foreach (var method in traitDeclaration.traitBody().traitMember()
                         .Select(static member => member.traitMethodDeclaration())
                         .Where(static method => method is not null)!)
            {
                declarations.Add(new TopLevelDeclarationModel(
                    $"{traitDeclaration.Identifier().GetText()}.{method.Identifier().GetText()}",
                    DeclarationKind.Function,
                    visibility,
                    CreateFunctionModel(
                        $"{traitDeclaration.Identifier().GetText()}.{method.Identifier().GetText()}",
                        ParseFunctionKind(method.functionKind()),
                        method.returnType(),
                        method.parameterList(),
                        null,
                        null,
                        method.functionModifier(),
                        method.functionBody())));
            }

            return;
        }

        if (declaration.doctrineDeclaration() is { } doctrineDeclaration)
        {
            declarations.Add(new TopLevelDeclarationModel(
                doctrineDeclaration.Identifier().GetText(),
                DeclarationKind.Doctrine,
                visibility,
                null));

            foreach (var method in doctrineDeclaration.doctrineBody().doctrineMember()
                         .Select(static member => member.doctrineMethodDeclaration())
                         .Where(static method => method is not null)!)
            {
                declarations.Add(new TopLevelDeclarationModel(
                    $"{doctrineDeclaration.Identifier().GetText()}.{method.Identifier().GetText()}",
                    DeclarationKind.Function,
                    visibility,
                    CreateFunctionModel(
                        $"{doctrineDeclaration.Identifier().GetText()}.{method.Identifier().GetText()}",
                        ParseDoctrineFunctionKind(method.doctrineFunctionKind()),
                        method.returnType(),
                        method.parameterList(),
                        null,
                        null,
                        method.functionModifier(),
                        method.functionBody())));
            }

            return;
        }

        if (declaration.globalConstantDeclaration() is { } constantDeclaration)
        {
            declarations.Add(new TopLevelDeclarationModel(
                constantDeclaration.constantDeclarators().constantDeclarator(0).Identifier().GetText(),
                DeclarationKind.GlobalConstant,
                visibility,
                null));
            return;
        }

        var variableDeclaration = declaration.globalVariableDeclaration()
            ?? throw new InvalidOperationException("Unsupported top-level declaration shape.");

        declarations.Add(new TopLevelDeclarationModel(
            variableDeclaration.variableDeclarators().variableDeclarator(0).Identifier().GetText(),
            DeclarationKind.GlobalVariable,
            visibility,
            null));
    }

    private static FunctionDeclarationModel CreateFunctionModel(
        string name,
        StarkFunctionKind functionKind,
        StarkParser.ReturnTypeContext returnType,
        StarkParser.ParameterListContext parameterList,
        StarkParser.AsmSpecifierContext? asmSpecifier,
        StarkParser.AsmClauseListContext? asmClauseList,
        IReadOnlyList<StarkParser.FunctionModifierContext> modifiersList,
        StarkParser.FunctionBodyContext functionBody)
    {
        var modifiers = modifiersList.Select(static modifier => modifier.GetText()).ToHashSet(StringComparer.Ordinal);
        var hasExplicitInlinePreference = modifiers.Contains("inline")
            || modifiers.Contains("noinline")
            || modifiers.Contains("inlinehint");
        var inlinePreference = modifiers.Contains("inline")
            ? InlinePreference.Inline
            : modifiers.Contains("noinline")
                ? InlinePreference.NoInline
                : InlinePreference.InlineHint;

        return new FunctionDeclarationModel(
            Name: name,
            Kind: functionKind,
            ReturnType: returnType.GetText(),
            Parameters: parameterList.parameter()
                .Select(static parameter => new ParameterModel(
                    parameter.Identifier().GetText(),
                    parameter.type_().GetText()))
                .ToArray(),
            Modifiers: new FunctionModifierSet(
                inlinePreference,
                hasExplicitInlinePreference,
                modifiers.Contains("hot"),
                modifiers.Contains("cold"),
                modifiers.Contains("ffi")),
            HasBody: functionBody.block() is not null,
            Asm: CreateAsmModel(asmSpecifier, asmClauseList, functionBody));
    }

    private static DestructorDeclarationModel? CreateDestructorModel(
        StarkParser.DestructorDeclarationContext? destructor)
    {
        return destructor is null
            ? null
            : new DestructorDeclarationModel(destructor.MUT() is not null);
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
