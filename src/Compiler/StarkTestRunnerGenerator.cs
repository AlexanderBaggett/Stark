using Antlr4.Runtime;
using System.Text;
using Stark.Parsing;

namespace Stark.Compiler;

internal static class StarkTestRunnerGenerator
{
    public static StarkTestRunnerGenerationResult Generate(
        string sourceText,
        IReadOnlyList<string> filters)
    {
        var parseResult = StarkSyntax.ParseCompilationUnit(sourceText);
        if (!parseResult.Succeeded)
        {
            return StarkTestRunnerGenerationResult.NotGenerated();
        }

        var facts = CollectFacts(parseResult.Root);
        if (facts.Count == 0)
        {
            return filters.Count == 0
                ? StarkTestRunnerGenerationResult.NotGenerated()
                : StarkTestRunnerGenerationResult.Fail(
                    $"No [Fact] tests were found, so --filter cannot be applied.");
        }

        var diagnostics = ValidateFacts(facts);
        if (HasExplicitMain(parseResult.Root))
        {
            diagnostics.Add(new StarkTestRunnerDiagnostic(
                "Test projects with [Fact] metadata use a generated runner. Remove the explicit 'main' function from the test root.",
                parseResult.Root.moduleDeclaration().Start.Line,
                parseResult.Root.moduleDeclaration().Start.Column + 1));
        }

        if (diagnostics.Count != 0)
        {
            return StarkTestRunnerGenerationResult.Fail(diagnostics);
        }

        var selectedFacts = SelectFacts(facts, filters);
        if (selectedFacts.Count == 0)
        {
            return StarkTestRunnerGenerationResult.Fail(
                $"No [Fact] tests matched filter(s): {string.Join(", ", filters)}.");
        }

        var runnerSource = BuildRunnerSource(parseResult, sourceText, selectedFacts);
        return StarkTestRunnerGenerationResult.Generated(runnerSource, selectedFacts, facts);
    }

    private static IReadOnlyList<StarkTestFact> CollectFacts(StarkParser.CompilationUnitContext root)
    {
        var facts = new List<StarkTestFact>();
        foreach (var declaration in root.topLevelDeclaration())
        {
            if (declaration.functionDeclaration() is { } function
                && HasFactAttribute(declaration.attributeList()))
            {
                facts.Add(CreateTopLevelFact(function));
                continue;
            }

            if (declaration.structDeclaration() is { } structDeclaration)
            {
                AddStructFacts(facts, structDeclaration);
                continue;
            }

            if (declaration.recordDeclaration() is { } recordDeclaration)
            {
                AddRecordFacts(facts, recordDeclaration);
            }
        }

        return facts;
    }

    private static void AddStructFacts(List<StarkTestFact> facts, StarkParser.StructDeclarationContext declaration)
    {
        var typeName = declaration.Identifier().GetText();
        foreach (var member in declaration.structBody().structMember())
        {
            var method = member.methodDeclaration();
            if (method is null || !HasFactAttribute(member.attributeList()))
            {
                continue;
            }

            facts.Add(CreateMethodFact(typeName, method));
        }
    }

    private static void AddRecordFacts(List<StarkTestFact> facts, StarkParser.RecordDeclarationContext declaration)
    {
        var typeName = declaration.Identifier().GetText();
        foreach (var member in declaration.recordBody().recordMember())
        {
            var method = member.methodDeclaration();
            if (method is null || !HasFactAttribute(member.attributeList()))
            {
                continue;
            }

            facts.Add(CreateMethodFact(typeName, method));
        }
    }

    private static StarkTestFact CreateTopLevelFact(StarkParser.FunctionDeclarationContext function)
    {
        var name = function.Identifier().GetText();
        return new StarkTestFact(
            DisplayName: name,
            CallExpression: $"{name}()",
            NameToken: function.Identifier().Symbol,
            ReturnTypeText: function.returnType().GetText(),
            ParameterCount: function.parameterList().parameter().Length,
            HasBody: function.functionBody().block() is not null,
            IsStaticMember: true,
            IsGeneric: function.typeParameterList() is not null);
    }

    private static StarkTestFact CreateMethodFact(string typeName, StarkParser.MethodDeclarationContext method)
    {
        var methodName = method.Identifier().GetText();
        var displayName = $"{typeName}.{methodName}";
        return new StarkTestFact(
            DisplayName: displayName,
            CallExpression: $"{typeName}.{methodName}()",
            NameToken: method.Identifier().Symbol,
            ReturnTypeText: method.returnType().GetText(),
            ParameterCount: method.parameterList().parameter().Length,
            HasBody: method.functionBody().block() is not null,
            IsStaticMember: method.functionModifier().Any(static modifier =>
                string.Equals(modifier.GetText(), "static", StringComparison.Ordinal)),
            IsGeneric: method.typeParameterList() is not null);
    }

    private static List<StarkTestRunnerDiagnostic> ValidateFacts(IReadOnlyList<StarkTestFact> facts)
    {
        var diagnostics = new List<StarkTestRunnerDiagnostic>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fact in facts)
        {
            if (!names.Add(fact.DisplayName))
            {
                diagnostics.Add(new StarkTestRunnerDiagnostic(
                    $"Duplicate [Fact] test name '{fact.DisplayName}'.",
                    fact.NameToken.Line,
                    fact.NameToken.Column + 1));
            }

            if (fact.IsGeneric)
            {
                diagnostics.Add(new StarkTestRunnerDiagnostic(
                    $"[Fact] test '{fact.DisplayName}' must not be generic.",
                    fact.NameToken.Line,
                    fact.NameToken.Column + 1));
            }

            if (!fact.HasBody)
            {
                diagnostics.Add(new StarkTestRunnerDiagnostic(
                    $"[Fact] test '{fact.DisplayName}' must have a body.",
                    fact.NameToken.Line,
                    fact.NameToken.Column + 1));
            }

            if (!fact.IsStaticMember)
            {
                diagnostics.Add(new StarkTestRunnerDiagnostic(
                    $"[Fact] method '{fact.DisplayName}' must be static so the generated runner can call it without constructing a receiver.",
                    fact.NameToken.Line,
                    fact.NameToken.Column + 1));
            }

            if (fact.ParameterCount != 0)
            {
                diagnostics.Add(new StarkTestRunnerDiagnostic(
                    $"[Fact] test '{fact.DisplayName}' must take no parameters.",
                    fact.NameToken.Line,
                    fact.NameToken.Column + 1));
            }

            if (!string.Equals(fact.ReturnTypeText, "bool", StringComparison.Ordinal))
            {
                diagnostics.Add(new StarkTestRunnerDiagnostic(
                    $"[Fact] test '{fact.DisplayName}' must return bool.",
                    fact.NameToken.Line,
                    fact.NameToken.Column + 1));
            }
        }

        return diagnostics;
    }

    private static bool HasExplicitMain(StarkParser.CompilationUnitContext root)
    {
        return root.topLevelDeclaration()
            .Select(static declaration => declaration.functionDeclaration())
            .Where(static function => function is not null)
            .Any(static function =>
                string.Equals(function!.Identifier().GetText(), "main", StringComparison.Ordinal)
                && function.functionBody().block() is not null);
    }

    private static IReadOnlyList<StarkTestFact> SelectFacts(
        IReadOnlyList<StarkTestFact> facts,
        IReadOnlyList<string> filters)
    {
        if (filters.Count == 0)
        {
            return facts;
        }

        return facts
            .Where(fact => filters.Any(filter =>
                fact.DisplayName.Contains(filter, StringComparison.Ordinal)))
            .ToArray();
    }

    private static string BuildRunnerSource(
        ParseResult parseResult,
        string sourceText,
        IReadOnlyList<StarkTestFact> facts)
    {
        var builder = new StringBuilder(EnsureTestingImport(parseResult, sourceText));
        if (builder.Length == 0 || builder[builder.Length - 1] != '\n')
        {
            builder.AppendLine();
        }

        builder.AppendLine();
        builder.AppendLine("// Generated by stark test from [Fact] metadata.");
        builder.AppendLine("export fn i32[min max] main()");
        builder.AppendLine("{");
        builder.AppendLine("    stack mut u8[0 1] failed = 0;");
        foreach (var fact in facts)
        {
            builder.Append("    if (System.Testing.RunFact(\"");
            builder.Append(EscapeAsciiString(fact.DisplayName));
            builder.Append("\", ");
            builder.Append(fact.CallExpression);
            builder.AppendLine(") != 0)");
            builder.AppendLine("    {");
            builder.AppendLine("        failed = 1;");
            builder.AppendLine("    }");
            builder.AppendLine();
        }

        builder.AppendLine("    return System.Testing.ExitCode(failed);");
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static string EnsureTestingImport(ParseResult parseResult, string sourceText)
    {
        if (parseResult.Root.importDeclaration().Any(static import =>
            string.Equals(import.qualifiedName().GetText(), "System.Testing", StringComparison.Ordinal)))
        {
            return sourceText;
        }

        var moduleStartIndex = parseResult.Root.moduleDeclaration().Start.StartIndex;
        return sourceText[..moduleStartIndex]
               + "import System.Testing\n"
               + sourceText[moduleStartIndex..];
    }

    private static bool HasFactAttribute(IEnumerable<StarkParser.AttributeListContext> attributeLists)
    {
        return attributeLists
            .SelectMany(static attributeList => attributeList.attribute())
            .Any(static attribute =>
                string.Equals(attribute.qualifiedName().GetText(), "Fact", StringComparison.Ordinal));
    }

    private static string EscapeAsciiString(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    builder.Append(ch);
                    break;
            }
        }

        return builder.ToString();
    }
}

internal sealed record StarkTestRunnerGenerationResult(
    bool Success,
    bool GeneratedRunner,
    string? SourceText,
    IReadOnlyList<StarkTestFact> SelectedFacts,
    IReadOnlyList<StarkTestFact> AllFacts,
    IReadOnlyList<StarkTestRunnerDiagnostic> Diagnostics)
{
    public static StarkTestRunnerGenerationResult NotGenerated() =>
        new(
            Success: true,
            GeneratedRunner: false,
            SourceText: null,
            SelectedFacts: [],
            AllFacts: [],
            Diagnostics: []);

    public static StarkTestRunnerGenerationResult Generated(
        string sourceText,
        IReadOnlyList<StarkTestFact> selectedFacts,
        IReadOnlyList<StarkTestFact> allFacts) =>
        new(
            Success: true,
            GeneratedRunner: true,
            SourceText: sourceText,
            SelectedFacts: selectedFacts,
            AllFacts: allFacts,
            Diagnostics: []);

    public static StarkTestRunnerGenerationResult Fail(string message) =>
        Fail([new StarkTestRunnerDiagnostic(message, Line: 1, Column: 1)]);

    public static StarkTestRunnerGenerationResult Fail(IReadOnlyList<StarkTestRunnerDiagnostic> diagnostics) =>
        new(
            Success: false,
            GeneratedRunner: false,
            SourceText: null,
            SelectedFacts: [],
            AllFacts: [],
            Diagnostics: diagnostics);
}

internal sealed record StarkTestFact(
    string DisplayName,
    string CallExpression,
    IToken NameToken,
    string ReturnTypeText,
    int ParameterCount,
    bool HasBody,
    bool IsStaticMember,
    bool IsGeneric);

internal sealed record StarkTestRunnerDiagnostic(
    string Message,
    int Line,
    int Column);
