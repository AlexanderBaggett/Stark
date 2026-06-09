using Antlr4.Runtime;
using System.Globalization;
using System.Numerics;
using System.Text;
using Stark.Parsing;

namespace Stark.Compiler;

internal static class StarkTestRunnerGenerator
{
    public static StarkTestRunnerGenerationResult Generate(
        string sourceText,
        IReadOnlyList<string> filters,
        string? targetTriple = null)
    {
        var parseResult = StarkSyntax.ParseCompilationUnit(sourceText);
        if (!parseResult.Succeeded)
        {
            return StarkTestRunnerGenerationResult.NotGenerated();
        }

        var collectedFacts = CollectFacts(parseResult.Root);
        var facts = collectedFacts.Facts;
        if (facts.Count == 0)
        {
            return filters.Count == 0
                ? StarkTestRunnerGenerationResult.NotGenerated()
                : StarkTestRunnerGenerationResult.Fail(
                    $"No [Fact] or [Theory] tests were found, so --filter cannot be applied.");
        }

        var diagnostics = new List<StarkTestRunnerDiagnostic>(collectedFacts.Diagnostics);
        diagnostics.AddRange(ValidateFacts(facts));
        if (HasExplicitMain(parseResult.Root))
        {
            diagnostics.Add(new StarkTestRunnerDiagnostic(
                "Test projects with [Fact] or [Theory] metadata use a generated runner. Remove the explicit 'main' function from the test root.",
                parseResult.Root.moduleDeclaration().Start.Line,
                parseResult.Root.moduleDeclaration().Start.Column + 1));
        }

        if (diagnostics.Count != 0)
        {
            return StarkTestRunnerGenerationResult.Fail(diagnostics);
        }

        var targetPlatform = StarkTestTargetPlatform.FromTriple(targetTriple);
        var runnerEntries = CreateRunnerEntries(facts, targetPlatform);
        diagnostics.AddRange(ValidateRunnerEntries(runnerEntries));
        if (diagnostics.Count != 0)
        {
            return StarkTestRunnerGenerationResult.Fail(diagnostics);
        }

        var selectedRunnerEntries = SelectRunnerEntries(runnerEntries, filters);
        if (selectedRunnerEntries.Count == 0)
        {
            return StarkTestRunnerGenerationResult.Fail(
                $"No generated tests matched filter(s): {string.Join(", ", filters)}.");
        }

        var selectedFacts = selectedRunnerEntries
            .Select(static entry => entry.Fact)
            .Distinct()
            .ToArray();
        var runnerSource = BuildRunnerSource(parseResult, sourceText, selectedRunnerEntries);
        return StarkTestRunnerGenerationResult.Generated(runnerSource, selectedFacts, facts);
    }

    private static StarkTestFactCollection CollectFacts(StarkParser.CompilationUnitContext root)
    {
        var facts = new List<StarkTestFact>();
        var diagnostics = new List<StarkTestRunnerDiagnostic>();
        foreach (var declaration in root.topLevelDeclaration())
        {
            if (declaration.functionDeclaration() is { } function
                && TryGetTestKind(declaration.attributeList(), diagnostics, out var testKind))
            {
                var platformScope = CreatePlatformScope(declaration.attributeList(), diagnostics);
                var collectionScope = CreateCollectionScope(declaration.attributeList(), diagnostics);
                var dataSources = CreateDataSources(declaration.attributeList(), diagnostics);
                facts.Add(CreateTopLevelFact(
                    function,
                    testKind,
                    CompactPlatformScopes(platformScope),
                    collectionScope.CollectionName,
                    dataSources));
                continue;
            }

            if (declaration.structDeclaration() is { } structDeclaration)
            {
                AddStructFacts(facts, diagnostics, declaration.attributeList(), structDeclaration);
                continue;
            }

            if (declaration.recordDeclaration() is { } recordDeclaration)
            {
                AddRecordFacts(facts, diagnostics, declaration.attributeList(), recordDeclaration);
            }
        }

        return new StarkTestFactCollection(facts, diagnostics);
    }

    private static void AddStructFacts(
        List<StarkTestFact> facts,
        List<StarkTestRunnerDiagnostic> diagnostics,
        IEnumerable<StarkParser.AttributeListContext> typeAttributeLists,
        StarkParser.StructDeclarationContext declaration)
    {
        var typeName = declaration.Identifier().GetText();
        StarkTestPlatformScope? typePlatformScope = null;
        StarkTestCollectionScope? typeCollectionScope = null;
        foreach (var member in declaration.structBody().structMember())
        {
            var method = member.methodDeclaration();
            if (method is null
                || !TryGetTestKind(member.attributeList(), diagnostics, out var testKind))
            {
                continue;
            }

            typePlatformScope ??= CreatePlatformScope(typeAttributeLists, diagnostics);
            typeCollectionScope ??= CreateCollectionScope(typeAttributeLists, diagnostics);
            var memberPlatformScope = CreatePlatformScope(member.attributeList(), diagnostics);
            var memberCollectionScope = CreateCollectionScope(member.attributeList(), diagnostics);
            var dataSources = CreateDataSources(member.attributeList(), diagnostics);
            var displayName = $"{typeName}.{method.Identifier().GetText()}";
            facts.Add(CreateMethodFact(
                typeName,
                method,
                testKind,
                CombinePlatformScopes(typePlatformScope, memberPlatformScope),
                CombineCollectionScopes(typeCollectionScope, memberCollectionScope, displayName, diagnostics),
                dataSources));
        }
    }

    private static void AddRecordFacts(
        List<StarkTestFact> facts,
        List<StarkTestRunnerDiagnostic> diagnostics,
        IEnumerable<StarkParser.AttributeListContext> typeAttributeLists,
        StarkParser.RecordDeclarationContext declaration)
    {
        var typeName = declaration.Identifier().GetText();
        StarkTestPlatformScope? typePlatformScope = null;
        StarkTestCollectionScope? typeCollectionScope = null;
        foreach (var member in declaration.recordBody().recordMember())
        {
            var method = member.methodDeclaration();
            if (method is null
                || !TryGetTestKind(member.attributeList(), diagnostics, out var testKind))
            {
                continue;
            }

            typePlatformScope ??= CreatePlatformScope(typeAttributeLists, diagnostics);
            typeCollectionScope ??= CreateCollectionScope(typeAttributeLists, diagnostics);
            var memberPlatformScope = CreatePlatformScope(member.attributeList(), diagnostics);
            var memberCollectionScope = CreateCollectionScope(member.attributeList(), diagnostics);
            var dataSources = CreateDataSources(member.attributeList(), diagnostics);
            var displayName = $"{typeName}.{method.Identifier().GetText()}";
            facts.Add(CreateMethodFact(
                typeName,
                method,
                testKind,
                CombinePlatformScopes(typePlatformScope, memberPlatformScope),
                CombineCollectionScopes(typeCollectionScope, memberCollectionScope, displayName, diagnostics),
                dataSources));
        }
    }

    private static StarkTestFact CreateTopLevelFact(
        StarkParser.FunctionDeclarationContext function,
        StarkTestDeclarationKind testKind,
        IReadOnlyList<StarkTestPlatformScope> platformScopes,
        string? collectionName,
        IReadOnlyList<StarkTestDataSource> dataSources)
    {
        var name = function.Identifier().GetText();
        return new StarkTestFact(
            DisplayName: name,
            CallTarget: name,
            NameToken: function.Identifier().Symbol,
            ReturnTypeText: function.returnType().GetText(),
            Parameters: CreateParameters(function.parameterList()),
            HasBody: function.functionBody().block() is not null,
            IsStaticMember: true,
            IsGeneric: function.typeParameterList() is not null,
            PlatformScopes: platformScopes,
            CollectionName: collectionName,
            TestKind: testKind,
            DataSources: dataSources);
    }

    private static StarkTestFact CreateMethodFact(
        string typeName,
        StarkParser.MethodDeclarationContext method,
        StarkTestDeclarationKind testKind,
        IReadOnlyList<StarkTestPlatformScope> platformScopes,
        string? collectionName,
        IReadOnlyList<StarkTestDataSource> dataSources)
    {
        var methodName = method.Identifier().GetText();
        var displayName = $"{typeName}.{methodName}";
        return new StarkTestFact(
            DisplayName: displayName,
            CallTarget: $"{typeName}.{methodName}",
            NameToken: method.Identifier().Symbol,
            ReturnTypeText: method.returnType().GetText(),
            Parameters: CreateParameters(method.parameterList()),
            HasBody: method.functionBody().block() is not null,
            IsStaticMember: method.functionModifier().Any(static modifier =>
                string.Equals(modifier.GetText(), "static", StringComparison.Ordinal)),
            IsGeneric: method.typeParameterList() is not null,
            PlatformScopes: platformScopes,
            CollectionName: collectionName,
            TestKind: testKind,
            DataSources: dataSources);
    }

    private static List<StarkTestRunnerDiagnostic> ValidateFacts(IReadOnlyList<StarkTestFact> facts)
    {
        var diagnostics = new List<StarkTestRunnerDiagnostic>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fact in facts)
        {
            var testAttributeName = GetTestAttributeName(fact);
            if (!names.Add(fact.DisplayName))
            {
                diagnostics.Add(new StarkTestRunnerDiagnostic(
                    $"Duplicate generated test declaration name '{fact.DisplayName}'.",
                    fact.NameToken.Line,
                    fact.NameToken.Column + 1));
            }

            if (fact.IsGeneric)
            {
                diagnostics.Add(new StarkTestRunnerDiagnostic(
                    $"{testAttributeName} test '{fact.DisplayName}' must not be generic.",
                    fact.NameToken.Line,
                    fact.NameToken.Column + 1));
            }

            if (!fact.HasBody)
            {
                diagnostics.Add(new StarkTestRunnerDiagnostic(
                    $"{testAttributeName} test '{fact.DisplayName}' must have a body.",
                    fact.NameToken.Line,
                    fact.NameToken.Column + 1));
            }

            if (!fact.IsStaticMember)
            {
                diagnostics.Add(new StarkTestRunnerDiagnostic(
                    $"{testAttributeName} method '{fact.DisplayName}' must be static so the generated runner can call it without constructing a receiver.",
                    fact.NameToken.Line,
                    fact.NameToken.Column + 1));
            }

            if (fact.ParameterCount != 0)
            {
                if (fact.TestKind == StarkTestDeclarationKind.Fact)
                {
                    diagnostics.Add(new StarkTestRunnerDiagnostic(
                        $"[Fact] test '{fact.DisplayName}' must take no parameters.",
                        fact.NameToken.Line,
                        fact.NameToken.Column + 1));
                }
            }

            if (!string.Equals(fact.ReturnTypeText, "bool", StringComparison.Ordinal))
            {
                diagnostics.Add(new StarkTestRunnerDiagnostic(
                    $"{testAttributeName} test '{fact.DisplayName}' must return bool.",
                    fact.NameToken.Line,
                    fact.NameToken.Column + 1));
            }

            if (fact.TestKind == StarkTestDeclarationKind.Fact)
            {
                foreach (var dataSource in fact.DataSources)
                {
                    var attributeName = dataSource.Kind == StarkTestDataSourceKind.InlineData
                        ? "[InlineData]"
                        : "[MemberData]";
                    diagnostics.Add(new StarkTestRunnerDiagnostic(
                        $"[Fact] test '{fact.DisplayName}' cannot use {attributeName}; use [Theory] for parameterized tests.",
                        dataSource.AttributeToken.Line,
                        dataSource.AttributeToken.Column + 1));
                }

                continue;
            }

            if (!TryCountTheoryRows(fact, out var theoryRowCount))
            {
                diagnostics.Add(new StarkTestRunnerDiagnostic(
                    $"[Theory] test '{fact.DisplayName}' expands to too many generated rows.",
                    fact.NameToken.Line,
                    fact.NameToken.Column + 1));
                continue;
            }

            if (theoryRowCount == 0)
            {
                diagnostics.Add(new StarkTestRunnerDiagnostic(
                    $"[Theory] test '{fact.DisplayName}' must declare at least one data row through [InlineData(...)] or [MemberData(...)].",
                    fact.NameToken.Line,
                    fact.NameToken.Column + 1));
                continue;
            }

            foreach (var dataSource in fact.DataSources)
            {
                if (dataSource.InlineDataRow is { } row
                    && row.Values.Count != fact.ParameterCount)
                {
                    diagnostics.Add(new StarkTestRunnerDiagnostic(
                        $"[Theory] test '{fact.DisplayName}' inline data row {row.RowIndex + 1} has {row.Values.Count} value(s), but the test has {fact.ParameterCount} parameter(s).",
                        row.AttributeToken.Line,
                        row.AttributeToken.Column + 1));
                }

                if (dataSource.MemberDataSource is { } memberData
                    && memberData.FieldNames.Count != 0
                    && memberData.FieldNames.Count != fact.ParameterCount)
                {
                    diagnostics.Add(new StarkTestRunnerDiagnostic(
                        $"[Theory] test '{fact.DisplayName}' member data source '{memberData.ProviderName}' maps {memberData.FieldNames.Count} field(s), but the test has {fact.ParameterCount} parameter(s).",
                        memberData.AttributeToken.Line,
                        memberData.AttributeToken.Column + 1));
                }
            }
        }

        return diagnostics;
    }

    private static bool TryCountTheoryRows(StarkTestFact fact, out int count)
    {
        count = 0;
        foreach (var dataSource in fact.DataSources)
        {
            if (dataSource.InlineDataRow is not null)
            {
                if (count == int.MaxValue)
                {
                    return false;
                }

                count++;
                continue;
            }

            if (dataSource.MemberDataSource is { } memberData)
            {
                if (count > int.MaxValue - memberData.RowCount)
                {
                    return false;
                }

                count += memberData.RowCount;
            }
        }

        return true;
    }

    private static int CountTheoryRows(StarkTestFact fact)
    {
        if (!TryCountTheoryRows(fact, out var count))
        {
            throw new InvalidOperationException(
                $"Theory '{fact.DisplayName}' expands to too many generated rows.");
        }

        return count;
    }

    private static IReadOnlyList<StarkTestRunnerDiagnostic> ValidateRunnerEntries(
        IReadOnlyList<StarkTestRunnerEntry> entries)
    {
        var diagnostics = new List<StarkTestRunnerDiagnostic>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (names.Add(entry.DisplayName))
            {
                continue;
            }

            diagnostics.Add(new StarkTestRunnerDiagnostic(
                $"Duplicate generated test entry name '{entry.DisplayName}'. Make inline-data values or member-data provider row names unique.",
                entry.Fact.NameToken.Line,
                entry.Fact.NameToken.Column + 1));
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

    private static IReadOnlyList<StarkTestRunnerEntry> SelectRunnerEntries(
        IReadOnlyList<StarkTestRunnerEntry> entries,
        IReadOnlyList<string> filters)
    {
        if (filters.Count == 0)
        {
            return entries;
        }

        return entries
            .Where(entry => filters.Any(filter =>
                entry.DisplayName.Contains(filter, StringComparison.Ordinal)))
            .ToArray();
    }

    private static string BuildRunnerSource(
        ParseResult parseResult,
        string sourceText,
        IReadOnlyList<StarkTestRunnerEntry> entries)
    {
        var builder = new StringBuilder(EnsureTestingImport(parseResult, sourceText));
        if (builder.Length == 0 || builder[builder.Length - 1] != '\n')
        {
            builder.AppendLine();
        }

        builder.AppendLine();
        builder.AppendLine("// Generated by stark test from [Fact] / [Theory] metadata.");
        builder.AppendLine("export fn i32[min max] main()");
        builder.AppendLine("{");
        builder.AppendLine("    stack mut u8[0 1] failed = 0;");
        string? currentCollectionName = null;
        foreach (var entry in entries)
        {
            var fact = entry.Fact;
            if (!string.Equals(currentCollectionName, fact.CollectionName, StringComparison.Ordinal))
            {
                currentCollectionName = fact.CollectionName;
                if (currentCollectionName is not null)
                {
                    builder.Append("    // Test collection: ");
                    builder.AppendLine(EscapeGeneratedComment(currentCollectionName));
                }
            }

            if (entry.SkipReason is { } skipReason)
            {
                builder.Append("    System.Testing.SkipFact(\"");
                builder.Append(EscapeAsciiString(entry.DisplayName));
                builder.Append("\", \"");
                builder.Append(EscapeAsciiString(skipReason));
                builder.AppendLine("\");");
                builder.AppendLine();
                continue;
            }

            if (entry.MemberDataRow is { } memberDataRow)
            {
                builder.Append("    stack ");
                builder.Append(memberDataRow.Source.RowTypeName);
                builder.Append(' ');
                builder.Append(memberDataRow.LocalName);
                builder.Append(" = ");
                builder.Append(memberDataRow.Source.ProviderName);
                builder.Append('(');
                builder.Append(memberDataRow.RowIndex.ToString(CultureInfo.InvariantCulture));
                builder.AppendLine(");");
            }

            builder.Append("    if (System.Testing.RunFact(\"");
            builder.Append(EscapeAsciiString(entry.DisplayName));
            builder.Append("\", ");
            builder.Append(entry.CallExpression);
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

    private static IReadOnlyList<StarkTestRunnerEntry> CreateRunnerEntries(
        IReadOnlyList<StarkTestFact> facts,
        StarkTestTargetPlatform targetPlatform)
    {
        var entries = new List<StarkTestRunnerEntry>(CountRunnerEntries(facts));
        foreach (var fact in facts)
        {
            var skipReason = GetPlatformSkipReason(fact, targetPlatform);
            if (fact.TestKind == StarkTestDeclarationKind.Fact)
            {
                entries.Add(new StarkTestRunnerEntry(
                    fact,
                    fact.DisplayName,
                    $"{fact.CallTarget}()",
                    skipReason,
                    MemberDataRow: null));
                continue;
            }

            foreach (var dataSource in fact.DataSources)
            {
                if (dataSource.InlineDataRow is { } inlineDataRow)
                {
                    entries.Add(new StarkTestRunnerEntry(
                        fact,
                        CreateTheoryDisplayName(fact, inlineDataRow),
                        CreateTheoryCallExpression(fact, inlineDataRow),
                        skipReason,
                        MemberDataRow: null));
                    continue;
                }

                if (dataSource.MemberDataSource is { } memberDataSource)
                {
                    for (var rowIndex = 0; rowIndex < memberDataSource.RowCount; rowIndex++)
                    {
                        var localName = $"__stark_member_data_{entries.Count.ToString(CultureInfo.InvariantCulture)}";
                        var memberDataRow = new StarkTestMemberDataRow(
                            memberDataSource,
                            rowIndex,
                            localName);
                        entries.Add(new StarkTestRunnerEntry(
                            fact,
                            CreateMemberDataDisplayName(fact, memberDataSource, rowIndex),
                            CreateMemberDataCallExpression(fact, memberDataSource, localName),
                            skipReason,
                            memberDataRow));
                    }
                }
            }
        }

        return OrderRunnerEntriesByCollection(entries);
    }

    private static int CountRunnerEntries(IReadOnlyList<StarkTestFact> facts)
    {
        var count = 0;
        foreach (var fact in facts)
        {
            count += fact.TestKind == StarkTestDeclarationKind.Fact
                ? 1
                : CountTheoryRows(fact);
        }

        return count;
    }

    private static string CreateMemberDataDisplayName(
        StarkTestFact fact,
        StarkTestMemberDataSource memberDataSource,
        int rowIndex)
    {
        var builder = new StringBuilder(fact.DisplayName.Length + memberDataSource.ProviderName.Length + 6);
        builder.Append(fact.DisplayName);
        builder.Append('[');
        builder.Append(memberDataSource.ProviderName);
        builder.Append(':');
        builder.Append(rowIndex.ToString(CultureInfo.InvariantCulture));
        builder.Append(']');
        return builder.ToString();
    }

    private static string CreateMemberDataCallExpression(
        StarkTestFact fact,
        StarkTestMemberDataSource memberDataSource,
        string localName)
    {
        var builder = new StringBuilder(fact.CallTarget.Length + 2 + (fact.Parameters.Count * (localName.Length + 2)));
        builder.Append(fact.CallTarget);
        builder.Append('(');
        for (var index = 0; index < fact.Parameters.Count; index++)
        {
            if (index != 0)
            {
                builder.Append(", ");
            }

            builder.Append(localName);
            builder.Append('.');
            builder.Append(GetMemberDataFieldName(fact, memberDataSource, index));
        }

        builder.Append(')');
        return builder.ToString();
    }

    private static string GetMemberDataFieldName(
        StarkTestFact fact,
        StarkTestMemberDataSource memberDataSource,
        int parameterIndex)
    {
        return memberDataSource.FieldNames.Count == 0
            ? fact.Parameters[parameterIndex].Name
            : memberDataSource.FieldNames[parameterIndex];
    }

    private static string GetTestAttributeName(StarkTestFact fact)
    {
        return fact.TestKind == StarkTestDeclarationKind.Fact
            ? "[Fact]"
            : "[Theory]";
    }

    private static string CreateTheoryDisplayName(
        StarkTestFact fact,
        StarkTestInlineDataRow row)
    {
        var builder = new StringBuilder(fact.DisplayName.Length + 2);
        builder.Append(fact.DisplayName);
        builder.Append('(');
        for (var index = 0; index < row.Values.Count; index++)
        {
            if (index != 0)
            {
                builder.Append(", ");
            }

            builder.Append(row.Values[index].DisplayText);
        }

        builder.Append(')');
        return builder.ToString();
    }

    private static string CreateTheoryCallExpression(
        StarkTestFact fact,
        StarkTestInlineDataRow row)
    {
        var builder = new StringBuilder(fact.CallTarget.Length + 2);
        builder.Append(fact.CallTarget);
        builder.Append('(');
        for (var index = 0; index < row.Values.Count; index++)
        {
            if (index != 0)
            {
                builder.Append(", ");
            }

            builder.Append(row.Values[index].SourceText);
        }

        builder.Append(')');
        return builder.ToString();
    }

    private static IReadOnlyList<StarkTestRunnerEntry> OrderRunnerEntriesByCollection(
        IReadOnlyList<StarkTestRunnerEntry> entries)
    {
        var groups = new List<List<StarkTestRunnerEntry>>(entries.Count);
        var namedGroups = new Dictionary<string, List<StarkTestRunnerEntry>>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var collectionName = entry.Fact.CollectionName;
            if (collectionName is null)
            {
                groups.Add([entry]);
                continue;
            }

            if (!namedGroups.TryGetValue(collectionName, out var group))
            {
                group = [];
                namedGroups.Add(collectionName, group);
                groups.Add(group);
            }

            group.Add(entry);
        }

        var orderedEntries = new List<StarkTestRunnerEntry>(entries.Count);
        foreach (var group in groups)
        {
            orderedEntries.AddRange(group);
        }

        return orderedEntries;
    }

    private static string? GetPlatformSkipReason(
        StarkTestFact fact,
        StarkTestTargetPlatform targetPlatform)
    {
        foreach (var scope in fact.PlatformScopes)
        {
            foreach (var selector in scope.SkipSelectors)
            {
                if (targetPlatform.Matches(selector))
                {
                    return "excluded by [SkipPlatform]";
                }
            }
        }

        foreach (var scope in fact.PlatformScopes)
        {
            if (scope.IncludeSelectors.Count != 0
                && !scope.IncludeSelectors.Any(targetPlatform.Matches))
            {
                return "target does not match [Platform]";
            }
        }

        return null;
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

    private static bool TryGetTestKind(
        IEnumerable<StarkParser.AttributeListContext> attributeLists,
        List<StarkTestRunnerDiagnostic> diagnostics,
        out StarkTestDeclarationKind testKind)
    {
        testKind = StarkTestDeclarationKind.Fact;
        StarkParser.AttributeContext? factAttribute = null;
        StarkParser.AttributeContext? theoryAttribute = null;
        foreach (var attribute in attributeLists.SelectMany(static attributeList => attributeList.attribute()))
        {
            var attributeName = attribute.qualifiedName().GetText();
            if (string.Equals(attributeName, "Fact", StringComparison.Ordinal))
            {
                factAttribute = attribute;
                continue;
            }

            if (string.Equals(attributeName, "Theory", StringComparison.Ordinal))
            {
                theoryAttribute = attribute;
            }
        }

        if (factAttribute is null && theoryAttribute is null)
        {
            return false;
        }

        if (factAttribute is not null && theoryAttribute is not null)
        {
            diagnostics.Add(new StarkTestRunnerDiagnostic(
                "A test declaration cannot use both [Fact] and [Theory].",
                theoryAttribute.Start.Line,
                theoryAttribute.Start.Column + 1));
        }

        testKind = theoryAttribute is null
            ? StarkTestDeclarationKind.Fact
            : StarkTestDeclarationKind.Theory;
        return true;
    }

    private static IReadOnlyList<StarkTestParameter> CreateParameters(
        StarkParser.ParameterListContext parameterList)
    {
        return parameterList.parameter()
            .Select(static parameter => new StarkTestParameter(
                parameter.Identifier().GetText(),
                parameter.type_().GetText()))
            .ToArray();
    }

    private static IReadOnlyList<StarkTestDataSource> CreateDataSources(
        IEnumerable<StarkParser.AttributeListContext> attributeLists,
        List<StarkTestRunnerDiagnostic> diagnostics)
    {
        List<StarkTestDataSource>? dataSources = null;
        var rowIndex = 0;
        foreach (var attribute in attributeLists.SelectMany(static attributeList => attributeList.attribute()))
        {
            var attributeName = attribute.qualifiedName().GetText();
            if (string.Equals(attributeName, "InlineData", StringComparison.Ordinal))
            {
                var inlineDataRow = CreateInlineDataRow(attribute, rowIndex, diagnostics);
                dataSources ??= [];
                dataSources.Add(StarkTestDataSource.InlineData(inlineDataRow));
                rowIndex++;
                continue;
            }

            if (string.Equals(attributeName, "MemberData", StringComparison.Ordinal))
            {
                if (TryCreateMemberDataSource(attribute, diagnostics, out var memberDataSource))
                {
                    dataSources ??= [];
                    dataSources.Add(StarkTestDataSource.MemberData(memberDataSource));
                }
            }
        }

        return dataSources ?? [];
    }

    private static StarkTestInlineDataRow CreateInlineDataRow(
        StarkParser.AttributeContext attribute,
        int rowIndex,
        List<StarkTestRunnerDiagnostic> diagnostics)
    {
        if (attribute.attributeCondition() is not null)
        {
            diagnostics.Add(new StarkTestRunnerDiagnostic(
                "[InlineData] on [Theory] tests cannot use an attribute condition.",
                attribute.Start.Line,
                attribute.Start.Column + 1));
        }

        var values = new List<StarkTestInlineDataValue>();
        foreach (var argument in attribute.attributeArgument())
        {
            if (TryCreateInlineDataValue(argument, diagnostics, out var value))
            {
                values.Add(value);
            }
        }

        return new StarkTestInlineDataRow(values, attribute.Start, rowIndex);
    }

    private static bool TryCreateMemberDataSource(
        StarkParser.AttributeContext attribute,
        List<StarkTestRunnerDiagnostic> diagnostics,
        out StarkTestMemberDataSource memberDataSource)
    {
        memberDataSource = default!;
        if (attribute.attributeCondition() is not null)
        {
            diagnostics.Add(new StarkTestRunnerDiagnostic(
                "[MemberData] on [Theory] tests cannot use an attribute condition.",
                attribute.Start.Line,
                attribute.Start.Column + 1));
        }

        var arguments = attribute.attributeArgument();
        if (arguments.Length < 3)
        {
            diagnostics.Add(new StarkTestRunnerDiagnostic(
                "[MemberData] on [Theory] tests must provide a provider function, row type, positive row count, and optional row field names.",
                attribute.Start.Line,
                attribute.Start.Column + 1));
            return false;
        }

        var valid = TryCreateQualifiedNameArgument(
            arguments[0],
            "[MemberData] provider function",
            diagnostics,
            out var providerName);
        valid &= TryCreateQualifiedNameArgument(
            arguments[1],
            "[MemberData] row type",
            diagnostics,
            out var rowTypeName);
        valid &= TryCreateMemberDataRowCount(arguments[2], diagnostics, out var rowCount);

        var fieldNames = new List<string>(Math.Max(0, arguments.Length - 3));
        for (var index = 3; index < arguments.Length; index++)
        {
            if (!TryCreateMemberDataFieldName(arguments[index], diagnostics, out var fieldName))
            {
                valid = false;
                continue;
            }

            fieldNames.Add(fieldName);
        }

        if (!valid)
        {
            return false;
        }

        memberDataSource = new StarkTestMemberDataSource(
            providerName,
            rowTypeName,
            rowCount,
            fieldNames,
            attribute.Start);
        return true;
    }

    private static bool TryCreateQualifiedNameArgument(
        StarkParser.AttributeArgumentContext argument,
        string description,
        List<StarkTestRunnerDiagnostic> diagnostics,
        out string value)
    {
        value = string.Empty;
        if (argument.qualifiedName() is not { } qualifiedName)
        {
            diagnostics.Add(new StarkTestRunnerDiagnostic(
                $"{description} must be a qualified identifier.",
                argument.Start.Line,
                argument.Start.Column + 1));
            return false;
        }

        value = qualifiedName.GetText();
        return true;
    }

    private static bool TryCreateMemberDataRowCount(
        StarkParser.AttributeArgumentContext argument,
        List<StarkTestRunnerDiagnostic> diagnostics,
        out int rowCount)
    {
        rowCount = 0;
        if (argument.signedIntegerLiteral() is not { } literal)
        {
            diagnostics.Add(new StarkTestRunnerDiagnostic(
                "[MemberData] row count must be a positive integer literal.",
                argument.Start.Line,
                argument.Start.Column + 1));
            return false;
        }

        var value = ParseSignedIntegerLiteral(literal);
        if (value <= BigInteger.Zero || value > new BigInteger(int.MaxValue))
        {
            diagnostics.Add(new StarkTestRunnerDiagnostic(
                "[MemberData] row count must be between 1 and 2147483647.",
                literal.Start.Line,
                literal.Start.Column + 1));
            return false;
        }

        rowCount = (int)value;
        return true;
    }

    private static bool TryCreateMemberDataFieldName(
        StarkParser.AttributeArgumentContext argument,
        List<StarkTestRunnerDiagnostic> diagnostics,
        out string fieldName)
    {
        fieldName = string.Empty;
        if (argument.qualifiedName() is not { } qualifiedName)
        {
            diagnostics.Add(new StarkTestRunnerDiagnostic(
                "[MemberData] row field names must be identifiers.",
                argument.Start.Line,
                argument.Start.Column + 1));
            return false;
        }

        fieldName = qualifiedName.GetText();
        if (fieldName.Contains('.', StringComparison.Ordinal))
        {
            diagnostics.Add(new StarkTestRunnerDiagnostic(
                "[MemberData] row field names must be simple identifiers.",
                argument.Start.Line,
                argument.Start.Column + 1));
            return false;
        }

        return true;
    }

    private static BigInteger ParseSignedIntegerLiteral(StarkParser.SignedIntegerLiteralContext literal)
    {
        var value = BigInteger.Parse(literal.IntegerLiteral().GetText(), CultureInfo.InvariantCulture);
        return literal.MINUS() is null ? value : -value;
    }

    private static bool TryCreateInlineDataValue(
        StarkParser.AttributeArgumentContext argument,
        List<StarkTestRunnerDiagnostic> diagnostics,
        out StarkTestInlineDataValue value)
    {
        value = default!;
        if (argument.signedIntegerLiteral() is { } integerLiteral)
        {
            var sourceText = integerLiteral.GetText();
            value = new StarkTestInlineDataValue(
                sourceText,
                sourceText,
                StarkTestInlineDataValueKind.Integer);
            return true;
        }

        if (argument.TRUE() is not null || argument.FALSE() is not null)
        {
            var sourceText = argument.GetText();
            value = new StarkTestInlineDataValue(
                sourceText,
                sourceText,
                StarkTestInlineDataValueKind.Boolean);
            return true;
        }

        if (argument.StringLiteral() is { } stringLiteral)
        {
            if (!TextLiteralDecoder.TryDecode(
                    stringLiteral.GetText(),
                    TextLiteralKind.String,
                    out var decoded,
                    out var diagnostic))
            {
                diagnostics.Add(new StarkTestRunnerDiagnostic(
                    $"[InlineData] string could not be decoded: {diagnostic.Message}",
                    stringLiteral.Symbol.Line,
                    stringLiteral.Symbol.Column + 1));
                return false;
            }

            value = new StarkTestInlineDataValue(
                stringLiteral.GetText(),
                $"\"{EscapeDisplayValue(decoded.Value)}\"",
                StarkTestInlineDataValueKind.String);
            return true;
        }

        if (argument.qualifiedName() is { } qualifiedName)
        {
            var sourceText = qualifiedName.GetText();
            value = new StarkTestInlineDataValue(
                sourceText,
                sourceText,
                StarkTestInlineDataValueKind.QualifiedName);
            return true;
        }

        diagnostics.Add(new StarkTestRunnerDiagnostic(
            "[InlineData] values must be strings, booleans, integers, or qualified names.",
            argument.Start.Line,
            argument.Start.Column + 1));
        return false;
    }

    private static StarkTestPlatformScope CreatePlatformScope(
        IEnumerable<StarkParser.AttributeListContext> attributeLists,
        List<StarkTestRunnerDiagnostic> diagnostics)
    {
        var includeSelectors = new List<StarkTestPlatformSelector>();
        var skipSelectors = new List<StarkTestPlatformSelector>();
        foreach (var attribute in attributeLists.SelectMany(static attributeList => attributeList.attribute()))
        {
            var attributeName = attribute.qualifiedName().GetText();
            var isPlatform = string.Equals(attributeName, "Platform", StringComparison.Ordinal);
            var isSkipPlatform = string.Equals(attributeName, "SkipPlatform", StringComparison.Ordinal);
            if (!isPlatform && !isSkipPlatform)
            {
                continue;
            }

            if (attribute.attributeCondition() is not null)
            {
                diagnostics.Add(new StarkTestRunnerDiagnostic(
                    $"[{attributeName}] on generated tests cannot use an attribute condition.",
                    attribute.Start.Line,
                    attribute.Start.Column + 1));
            }

            var arguments = attribute.attributeArgument();
            if (arguments.Length == 0)
            {
                diagnostics.Add(new StarkTestRunnerDiagnostic(
                    $"[{attributeName}] on generated tests must name at least one platform selector.",
                    attribute.Start.Line,
                    attribute.Start.Column + 1));
                continue;
            }

            foreach (var argument in arguments)
            {
                if (!TryCreatePlatformSelector(argument, attributeName, diagnostics, out var selector))
                {
                    continue;
                }

                if (isPlatform)
                {
                    includeSelectors.Add(selector);
                }
                else
                {
                    skipSelectors.Add(selector);
                }
            }
        }

        return new StarkTestPlatformScope(includeSelectors, skipSelectors);
    }

    private static bool TryCreatePlatformSelector(
        StarkParser.AttributeArgumentContext argument,
        string attributeName,
        List<StarkTestRunnerDiagnostic> diagnostics,
        out StarkTestPlatformSelector selector)
    {
        selector = default!;
        string text;
        if (argument.StringLiteral() is { } stringLiteral)
        {
            if (!TextLiteralDecoder.TryDecode(
                    stringLiteral.GetText(),
                    TextLiteralKind.String,
                    out var decoded,
                    out var diagnostic))
            {
                diagnostics.Add(new StarkTestRunnerDiagnostic(
                    $"[{attributeName}] selector string could not be decoded: {diagnostic.Message}",
                    stringLiteral.Symbol.Line,
                    stringLiteral.Symbol.Column + 1));
                return false;
            }

            text = decoded.Value;
        }
        else if (argument.qualifiedName() is { } qualifiedName)
        {
            text = qualifiedName.GetText();
        }
        else
        {
            diagnostics.Add(new StarkTestRunnerDiagnostic(
                $"[{attributeName}] selector must be an OS, architecture, os.arch pair, or exact target triple string.",
                argument.Start.Line,
                argument.Start.Column + 1));
            return false;
        }

        if (!TryParsePlatformSelector(text, out selector))
        {
            diagnostics.Add(new StarkTestRunnerDiagnostic(
                $"Unsupported [{attributeName}] selector '{text}'. Use an OS (linux/windows/macos), architecture (x64/arm64), os.arch pair, or exact target triple string.",
                argument.Start.Line,
                argument.Start.Column + 1));
            return false;
        }

        return true;
    }

    private static StarkTestCollectionScope CreateCollectionScope(
        IEnumerable<StarkParser.AttributeListContext> attributeLists,
        List<StarkTestRunnerDiagnostic> diagnostics)
    {
        var scope = StarkTestCollectionScope.Empty;
        foreach (var attribute in attributeLists.SelectMany(static attributeList => attributeList.attribute()))
        {
            var attributeName = attribute.qualifiedName().GetText();
            if (!string.Equals(attributeName, "Collection", StringComparison.Ordinal)
                && !string.Equals(attributeName, "Serial", StringComparison.Ordinal))
            {
                continue;
            }

            if (attribute.attributeCondition() is not null)
            {
                diagnostics.Add(new StarkTestRunnerDiagnostic(
                    $"[{attributeName}] on generated tests cannot use an attribute condition.",
                    attribute.Start.Line,
                    attribute.Start.Column + 1));
            }

            if (string.Equals(attributeName, "Serial", StringComparison.Ordinal))
            {
                if (attribute.attributeArgument().Length != 0)
                {
                    diagnostics.Add(new StarkTestRunnerDiagnostic(
                        "[Serial] on generated tests does not take arguments.",
                        attribute.Start.Line,
                        attribute.Start.Column + 1));
                    continue;
                }

                AssignCollectionName(ref scope, "Serial", attribute, diagnostics);
                continue;
            }

            var arguments = attribute.attributeArgument();
            if (arguments.Length != 1)
            {
                diagnostics.Add(new StarkTestRunnerDiagnostic(
                    "[Collection] on generated tests must name exactly one collection.",
                    attribute.Start.Line,
                    attribute.Start.Column + 1));
                continue;
            }

            if (!TryCreateCollectionName(arguments[0], diagnostics, out var collectionName))
            {
                continue;
            }

            AssignCollectionName(ref scope, collectionName, attribute, diagnostics);
        }

        return scope;
    }

    private static bool TryCreateCollectionName(
        StarkParser.AttributeArgumentContext argument,
        List<StarkTestRunnerDiagnostic> diagnostics,
        out string collectionName)
    {
        collectionName = string.Empty;
        if (argument.StringLiteral() is { } stringLiteral)
        {
            if (!TextLiteralDecoder.TryDecode(
                    stringLiteral.GetText(),
                    TextLiteralKind.String,
                    out var decoded,
                    out var diagnostic))
            {
                diagnostics.Add(new StarkTestRunnerDiagnostic(
                    $"[Collection] name string could not be decoded: {diagnostic.Message}",
                    stringLiteral.Symbol.Line,
                    stringLiteral.Symbol.Column + 1));
                return false;
            }

            collectionName = decoded.Value;
        }
        else if (argument.qualifiedName() is { } qualifiedName)
        {
            collectionName = qualifiedName.GetText();
        }
        else
        {
            diagnostics.Add(new StarkTestRunnerDiagnostic(
                "[Collection] name must be a string literal or qualified identifier.",
                argument.Start.Line,
                argument.Start.Column + 1));
            return false;
        }

        if (!IsValidCollectionName(collectionName))
        {
            diagnostics.Add(new StarkTestRunnerDiagnostic(
                $"Invalid [Collection] name '{collectionName}'. Collection names must be non-empty, trimmed, and cannot contain control characters.",
                argument.Start.Line,
                argument.Start.Column + 1));
            return false;
        }

        return true;
    }

    private static bool TryParsePlatformSelector(string text, out StarkTestPlatformSelector selector)
    {
        selector = default!;
        if (text.Length == 0
            || !string.Equals(text, text.Trim(), StringComparison.Ordinal)
            || text.IndexOfAny([' ', '\t', '\r', '\n']) >= 0)
        {
            return false;
        }

        var normalized = text.ToLowerInvariant();
        if (TryParseOperatingSystemName(normalized, out var operatingSystem))
        {
            selector = StarkTestPlatformSelector.ForOperatingSystem(text, operatingSystem);
            return true;
        }

        if (TryParseArchitectureName(normalized, out var architecture))
        {
            selector = StarkTestPlatformSelector.ForArchitecture(text, architecture);
            return true;
        }

        var dotIndex = normalized.IndexOf('.');
        if (dotIndex > 0
            && dotIndex == normalized.LastIndexOf('.')
            && dotIndex < normalized.Length - 1)
        {
            var left = normalized[..dotIndex];
            var right = normalized[(dotIndex + 1)..];
            if (TryParseOperatingSystemName(left, out operatingSystem)
                && TryParseArchitectureName(right, out architecture))
            {
                selector = StarkTestPlatformSelector.ForOperatingSystemAndArchitecture(
                    text,
                    operatingSystem,
                    architecture);
                return true;
            }

            if (TryParseArchitectureName(left, out architecture)
                && TryParseOperatingSystemName(right, out operatingSystem))
            {
                selector = StarkTestPlatformSelector.ForOperatingSystemAndArchitecture(
                    text,
                    operatingSystem,
                    architecture);
                return true;
            }
        }

        if (normalized.Contains('-', StringComparison.Ordinal))
        {
            selector = StarkTestPlatformSelector.ForTargetTriple(text, normalized);
            return true;
        }

        return false;
    }

    private static bool TryParseOperatingSystemName(string text, out string operatingSystem)
    {
        operatingSystem = text switch
        {
            "linux" => "linux",
            "windows" or "win" or "win32" => "windows",
            "macos" or "darwin" or "osx" => "macos",
            "android" => "android",
            "ios" => "ios",
            "freebsd" => "freebsd",
            "openbsd" => "openbsd",
            "netbsd" => "netbsd",
            "wasi" => "wasi",
            _ => string.Empty
        };

        return operatingSystem.Length != 0;
    }

    internal static bool TryParseArchitectureName(string text, out string architecture)
    {
        architecture = text switch
        {
            "x64" or "x86_64" or "amd64" => "x64",
            "x86" or "i386" or "i486" or "i586" or "i686" => "x86",
            "arm64" or "aarch64" => "arm64",
            "arm" or "armv6" or "armv7" or "armv7a" => "arm",
            "riscv64" => "riscv64",
            "riscv32" => "riscv32",
            "wasm32" => "wasm32",
            "wasm64" => "wasm64",
            _ => string.Empty
        };

        if (architecture.Length != 0)
        {
            return true;
        }

        if (text.StartsWith("armv", StringComparison.Ordinal))
        {
            architecture = "arm";
            return true;
        }

        return false;
    }

    private static IReadOnlyList<StarkTestPlatformScope> CompactPlatformScopes(StarkTestPlatformScope scope)
    {
        return scope.IsEmpty ? [] : [scope];
    }

    private static IReadOnlyList<StarkTestPlatformScope> CombinePlatformScopes(
        StarkTestPlatformScope typeScope,
        StarkTestPlatformScope memberScope)
    {
        if (typeScope.IsEmpty)
        {
            return CompactPlatformScopes(memberScope);
        }

        return memberScope.IsEmpty
            ? [typeScope]
            : [typeScope, memberScope];
    }

    private static string? CombineCollectionScopes(
        StarkTestCollectionScope typeScope,
        StarkTestCollectionScope memberScope,
        string displayName,
        List<StarkTestRunnerDiagnostic> diagnostics)
    {
        if (typeScope.CollectionName is null)
        {
            return memberScope.CollectionName;
        }

        if (memberScope.CollectionName is null
            || string.Equals(typeScope.CollectionName, memberScope.CollectionName, StringComparison.Ordinal))
        {
            return typeScope.CollectionName;
        }

        var token = memberScope.AttributeToken ?? typeScope.AttributeToken;
        diagnostics.Add(new StarkTestRunnerDiagnostic(
            $"Generated test '{displayName}' cannot combine collection '{typeScope.CollectionName}' from its containing type with collection '{memberScope.CollectionName}' on the test.",
            token?.Line ?? 1,
            (token?.Column ?? 0) + 1));
        return memberScope.CollectionName;
    }

    private static void AssignCollectionName(
        ref StarkTestCollectionScope scope,
        string collectionName,
        StarkParser.AttributeContext attribute,
        List<StarkTestRunnerDiagnostic> diagnostics)
    {
        if (scope.CollectionName is null)
        {
            scope = new StarkTestCollectionScope(collectionName, attribute.Start);
            return;
        }

        if (string.Equals(scope.CollectionName, collectionName, StringComparison.Ordinal))
        {
            return;
        }

        diagnostics.Add(new StarkTestRunnerDiagnostic(
            $"Generated test collection attributes conflict: '{scope.CollectionName}' and '{collectionName}'. Use one collection name.",
            attribute.Start.Line,
            attribute.Start.Column + 1));
    }

    private static bool IsValidCollectionName(string collectionName)
    {
        if (collectionName.Length == 0
            || !string.Equals(collectionName, collectionName.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var ch in collectionName)
        {
            if (char.IsControl(ch))
            {
                return false;
            }
        }

        return true;
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

    private static string EscapeGeneratedComment(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\n':
                case '\r':
                    builder.Append(' ');
                    break;
                default:
                    builder.Append(ch);
                    break;
            }
        }

        return builder.ToString();
    }

    private static string EscapeDisplayValue(string value)
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
    string CallTarget,
    IToken NameToken,
    string ReturnTypeText,
    IReadOnlyList<StarkTestParameter> Parameters,
    bool HasBody,
    bool IsStaticMember,
    bool IsGeneric,
    IReadOnlyList<StarkTestPlatformScope> PlatformScopes,
    string? CollectionName,
    StarkTestDeclarationKind TestKind,
    IReadOnlyList<StarkTestDataSource> DataSources)
{
    public int ParameterCount => Parameters.Count;
}

internal enum StarkTestDeclarationKind
{
    Fact,
    Theory
}

internal sealed record StarkTestParameter(
    string Name,
    string TypeText);

internal enum StarkTestInlineDataValueKind
{
    Integer,
    String,
    Boolean,
    QualifiedName
}

internal sealed record StarkTestInlineDataValue(
    string SourceText,
    string DisplayText,
    StarkTestInlineDataValueKind Kind);

internal sealed record StarkTestInlineDataRow(
    IReadOnlyList<StarkTestInlineDataValue> Values,
    IToken AttributeToken,
    int RowIndex);

internal enum StarkTestDataSourceKind
{
    InlineData,
    MemberData
}

internal sealed record StarkTestDataSource(
    StarkTestDataSourceKind Kind,
    StarkTestInlineDataRow? InlineDataRow,
    StarkTestMemberDataSource? MemberDataSource,
    IToken AttributeToken)
{
    public static StarkTestDataSource InlineData(StarkTestInlineDataRow row) =>
        new(
            StarkTestDataSourceKind.InlineData,
            row,
            null,
            row.AttributeToken);

    public static StarkTestDataSource MemberData(StarkTestMemberDataSource source) =>
        new(
            StarkTestDataSourceKind.MemberData,
            null,
            source,
            source.AttributeToken);
}

internal sealed record StarkTestMemberDataSource(
    string ProviderName,
    string RowTypeName,
    int RowCount,
    IReadOnlyList<string> FieldNames,
    IToken AttributeToken);

internal sealed record StarkTestMemberDataRow(
    StarkTestMemberDataSource Source,
    int RowIndex,
    string LocalName);

internal sealed record StarkTestFactCollection(
    IReadOnlyList<StarkTestFact> Facts,
    IReadOnlyList<StarkTestRunnerDiagnostic> Diagnostics);

internal sealed record StarkTestRunnerEntry(
    StarkTestFact Fact,
    string DisplayName,
    string CallExpression,
    string? SkipReason,
    StarkTestMemberDataRow? MemberDataRow);

internal sealed record StarkTestPlatformScope(
    IReadOnlyList<StarkTestPlatformSelector> IncludeSelectors,
    IReadOnlyList<StarkTestPlatformSelector> SkipSelectors)
{
    public bool IsEmpty => IncludeSelectors.Count == 0 && SkipSelectors.Count == 0;
}

internal sealed record StarkTestCollectionScope(
    string? CollectionName,
    IToken? AttributeToken)
{
    public static StarkTestCollectionScope Empty { get; } = new(null, null);
}

internal enum StarkTestPlatformSelectorKind
{
    OperatingSystem,
    Architecture,
    OperatingSystemAndArchitecture,
    TargetTriple
}

internal sealed record StarkTestPlatformSelector(
    string SourceText,
    StarkTestPlatformSelectorKind Kind,
    string? OperatingSystem,
    string? Architecture,
    string? Triple)
{
    public static StarkTestPlatformSelector ForOperatingSystem(string sourceText, string operatingSystem) =>
        new(sourceText, StarkTestPlatformSelectorKind.OperatingSystem, operatingSystem, null, null);

    public static StarkTestPlatformSelector ForArchitecture(string sourceText, string architecture) =>
        new(sourceText, StarkTestPlatformSelectorKind.Architecture, null, architecture, null);

    public static StarkTestPlatformSelector ForOperatingSystemAndArchitecture(
        string sourceText,
        string operatingSystem,
        string architecture) =>
        new(
            sourceText,
            StarkTestPlatformSelectorKind.OperatingSystemAndArchitecture,
            operatingSystem,
            architecture,
            null);

    public static StarkTestPlatformSelector ForTargetTriple(string sourceText, string triple) =>
        new(sourceText, StarkTestPlatformSelectorKind.TargetTriple, null, null, triple);
}

internal sealed record StarkTestTargetPlatform(
    string Triple,
    string? OperatingSystem,
    string? Architecture)
{
    public static StarkTestTargetPlatform FromTriple(string? triple)
    {
        var normalizedTriple = string.IsNullOrWhiteSpace(triple)
            ? string.Empty
            : triple.Trim().ToLowerInvariant();
        return new StarkTestTargetPlatform(
            normalizedTriple,
            ResolveOperatingSystem(normalizedTriple),
            ResolveArchitecture(normalizedTriple));
    }

    public bool Matches(StarkTestPlatformSelector selector)
    {
        return selector.Kind switch
        {
            StarkTestPlatformSelectorKind.OperatingSystem =>
                string.Equals(OperatingSystem, selector.OperatingSystem, StringComparison.Ordinal),
            StarkTestPlatformSelectorKind.Architecture =>
                string.Equals(Architecture, selector.Architecture, StringComparison.Ordinal),
            StarkTestPlatformSelectorKind.OperatingSystemAndArchitecture =>
                string.Equals(OperatingSystem, selector.OperatingSystem, StringComparison.Ordinal)
                && string.Equals(Architecture, selector.Architecture, StringComparison.Ordinal),
            StarkTestPlatformSelectorKind.TargetTriple =>
                string.Equals(Triple, selector.Triple, StringComparison.Ordinal),
            _ => false
        };
    }

    private static string? ResolveOperatingSystem(string triple)
    {
        if (triple.Contains("android", StringComparison.Ordinal))
        {
            return "android";
        }

        if (triple.Contains("ios", StringComparison.Ordinal))
        {
            return "ios";
        }

        if (triple.Contains("windows", StringComparison.Ordinal)
            || triple.Contains("win32", StringComparison.Ordinal)
            || triple.Contains("mingw", StringComparison.Ordinal))
        {
            return "windows";
        }

        if (triple.Contains("darwin", StringComparison.Ordinal)
            || triple.Contains("macos", StringComparison.Ordinal))
        {
            return "macos";
        }

        if (triple.Contains("linux", StringComparison.Ordinal))
        {
            return "linux";
        }

        if (triple.Contains("freebsd", StringComparison.Ordinal))
        {
            return "freebsd";
        }

        if (triple.Contains("openbsd", StringComparison.Ordinal))
        {
            return "openbsd";
        }

        if (triple.Contains("netbsd", StringComparison.Ordinal))
        {
            return "netbsd";
        }

        if (triple.Contains("wasi", StringComparison.Ordinal))
        {
            return "wasi";
        }

        return null;
    }

    private static string? ResolveArchitecture(string triple)
    {
        var archText = triple;
        var dashIndex = triple.IndexOf('-');
        if (dashIndex > 0)
        {
            archText = triple[..dashIndex];
        }

        return StarkTestRunnerGenerator.TryParseArchitectureName(archText, out var architecture)
            ? architecture
            : null;
    }
}

internal sealed record StarkTestRunnerDiagnostic(
    string Message,
    int Line,
    int Column);
