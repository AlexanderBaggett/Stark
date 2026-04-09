namespace Stark.Compiler;

internal static partial class PackageImageLoader
{
    private static bool TryBuildImportedTypedTemplateBody(
        StarkPackageTypedTemplateBodyManifest? manifest,
        out ImportedTemplateTypedBodySummary summary)
    {
        summary = null!;

        if (manifest is null)
        {
            return false;
        }

        var statements = new List<ImportedTemplateTypedBodyStatementSummary>(manifest.Statements.Count);
        foreach (var statement in manifest.Statements)
        {
            if (!TryBuildImportedTypedTemplateStatement(statement, out var builtStatement))
            {
                return false;
            }

            statements.Add(builtStatement);
        }

        summary = new ImportedTemplateTypedBodySummary(statements);
        return true;
    }

    private static bool TryBuildImportedTypedTemplateStatement(
        StarkPackageTypedTemplateStatementManifest manifest,
        out ImportedTemplateTypedBodyStatementSummary summary)
    {
        summary = null!;

        ImportedTemplateTypedBodyExpressionSummary? expression = null;
        if (manifest.Expression is not null
            && !TryBuildImportedTypedTemplateExpression(manifest.Expression, out expression))
        {
            return false;
        }

        ImportedTemplateTypedBodyExpressionSummary? targetExpression = null;
        if (manifest.TargetExpression is not null
            && !TryBuildImportedTypedTemplateExpression(manifest.TargetExpression, out targetExpression))
        {
            return false;
        }

        if (string.Equals(manifest.Kind, "local-variable", StringComparison.Ordinal))
        {
            if (manifest.Name is null || manifest.StorageClass is null || manifest.Type is null)
            {
                return false;
            }

            summary = new ImportedTemplateTypedBodyStatementSummary(
                ImportedTemplateTypedBodyStatementKind.LocalVariableDeclaration,
                Expression: expression!,
                Name: manifest.Name,
                StorageClass: manifest.StorageClass,
                IsMutable: manifest.IsMutable,
                IsConstant: manifest.IsConstant,
                Type: BuildTypeSymbol(manifest.Type));
            return true;
        }

        if (string.Equals(manifest.Kind, "expression", StringComparison.Ordinal))
        {
            if (expression is null)
            {
                return false;
            }

            summary = new ImportedTemplateTypedBodyStatementSummary(
                ImportedTemplateTypedBodyStatementKind.ExpressionStatement,
                expression);
            return true;
        }

        if (string.Equals(manifest.Kind, "assignment", StringComparison.Ordinal))
        {
            if ((manifest.Name is null && targetExpression is null) || expression is null)
            {
                return false;
            }

            summary = new ImportedTemplateTypedBodyStatementSummary(
                ImportedTemplateTypedBodyStatementKind.Assignment,
                expression,
                Name: manifest.Name,
                AssignmentOperator: manifest.AssignmentOperator,
                TargetExpression: targetExpression);
            return true;
        }

        if (string.Equals(manifest.Kind, "switch", StringComparison.Ordinal))
        {
            if (expression is null || manifest.SwitchCases is not { Count: > 0 })
            {
                return false;
            }

            var switchCases = new List<ImportedTemplateTypedSwitchCaseSummary>(manifest.SwitchCases.Count);
            foreach (var switchCase in manifest.SwitchCases)
            {
                if (!TryBuildImportedTypedTemplateSwitchCase(switchCase, out var builtSwitchCase))
                {
                    return false;
                }

                switchCases.Add(builtSwitchCase);
            }

            summary = new ImportedTemplateTypedBodyStatementSummary(
                ImportedTemplateTypedBodyStatementKind.Switch,
                expression,
                SwitchCaseSummaries: switchCases);
            return true;
        }

        if (string.Equals(manifest.Kind, "for", StringComparison.Ordinal))
        {
            if (expression is null)
            {
                return false;
            }

            var initializerStatements = new List<ImportedTemplateTypedBodyStatementSummary>((manifest.InitializerStatements ?? []).Count);
            foreach (var initializerStatement in manifest.InitializerStatements ?? [])
            {
                if (!TryBuildImportedTypedTemplateStatement(initializerStatement, out var builtInitializerStatement))
                {
                    return false;
                }

                initializerStatements.Add(builtInitializerStatement);
            }

            var iteratorStatements = new List<ImportedTemplateTypedBodyStatementSummary>((manifest.IteratorStatements ?? []).Count);
            foreach (var iteratorStatement in manifest.IteratorStatements ?? [])
            {
                if (!TryBuildImportedTypedTemplateStatement(iteratorStatement, out var builtIteratorStatement))
                {
                    return false;
                }

                iteratorStatements.Add(builtIteratorStatement);
            }

            var bodyStatements = new List<ImportedTemplateTypedBodyStatementSummary>((manifest.BodyStatements ?? []).Count);
            foreach (var bodyStatement in manifest.BodyStatements ?? [])
            {
                if (!TryBuildImportedTypedTemplateStatement(bodyStatement, out var builtBodyStatement))
                {
                    return false;
                }

                bodyStatements.Add(builtBodyStatement);
            }

            summary = new ImportedTemplateTypedBodyStatementSummary(
                ImportedTemplateTypedBodyStatementKind.For,
                expression,
                LoopBehavior: manifest.LoopBehavior,
                InitializerStatements: initializerStatements,
                IteratorStatements: iteratorStatements,
                BodyStatements: bodyStatements);
            return true;
        }

        if (string.Equals(manifest.Kind, "while", StringComparison.Ordinal))
        {
            if (expression is null)
            {
                return false;
            }

            var bodyStatements = new List<ImportedTemplateTypedBodyStatementSummary>((manifest.BodyStatements ?? []).Count);
            foreach (var bodyStatement in manifest.BodyStatements ?? [])
            {
                if (!TryBuildImportedTypedTemplateStatement(bodyStatement, out var builtBodyStatement))
                {
                    return false;
                }

                bodyStatements.Add(builtBodyStatement);
            }

            summary = new ImportedTemplateTypedBodyStatementSummary(
                ImportedTemplateTypedBodyStatementKind.While,
                expression,
                LoopBehavior: manifest.LoopBehavior,
                BodyStatements: bodyStatements);
            return true;
        }

        if (string.Equals(manifest.Kind, "if", StringComparison.Ordinal))
        {
            if (expression is null)
            {
                return false;
            }

            var thenStatements = new List<ImportedTemplateTypedBodyStatementSummary>((manifest.ThenStatements ?? []).Count);
            foreach (var thenStatement in manifest.ThenStatements ?? [])
            {
                if (!TryBuildImportedTypedTemplateStatement(thenStatement, out var builtThenStatement))
                {
                    return false;
                }

                thenStatements.Add(builtThenStatement);
            }

            List<ImportedTemplateTypedBodyStatementSummary>? elseStatements = null;
            if (manifest.ElseStatements is { Count: > 0 })
            {
                elseStatements = new List<ImportedTemplateTypedBodyStatementSummary>(manifest.ElseStatements.Count);
                foreach (var elseStatement in manifest.ElseStatements)
                {
                    if (!TryBuildImportedTypedTemplateStatement(elseStatement, out var builtElseStatement))
                    {
                        return false;
                    }

                    elseStatements.Add(builtElseStatement);
                }
            }

            summary = new ImportedTemplateTypedBodyStatementSummary(
                ImportedTemplateTypedBodyStatementKind.If,
                expression,
                ThenStatements: thenStatements,
                ElseStatements: elseStatements);
            return true;
        }

        if (string.Equals(manifest.Kind, "return", StringComparison.Ordinal))
        {
            if (expression is null)
            {
                summary = new ImportedTemplateTypedBodyStatementSummary(
                    ImportedTemplateTypedBodyStatementKind.Return);
                return true;
            }

            summary = new ImportedTemplateTypedBodyStatementSummary(
                ImportedTemplateTypedBodyStatementKind.Return,
                expression);
            return true;
        }

        if (string.Equals(manifest.Kind, "break", StringComparison.Ordinal))
        {
            summary = new ImportedTemplateTypedBodyStatementSummary(
                ImportedTemplateTypedBodyStatementKind.Break);
            return true;
        }

        if (string.Equals(manifest.Kind, "continue", StringComparison.Ordinal))
        {
            summary = new ImportedTemplateTypedBodyStatementSummary(
                ImportedTemplateTypedBodyStatementKind.Continue);
            return true;
        }

        return false;
    }

    private static bool TryBuildImportedTypedTemplateSwitchCase(
        StarkPackageTypedTemplateSwitchCaseManifest manifest,
        out ImportedTemplateTypedSwitchCaseSummary summary)
    {
        summary = null!;

        ImportedTemplateTypedSwitchCaseKind kind;
        if (string.Equals(manifest.Kind, "literal", StringComparison.Ordinal))
        {
            kind = ImportedTemplateTypedSwitchCaseKind.Literal;
        }
        else if (string.Equals(manifest.Kind, "match-all", StringComparison.Ordinal))
        {
            kind = ImportedTemplateTypedSwitchCaseKind.MatchAll;
        }
        else if (string.Equals(manifest.Kind, "default", StringComparison.Ordinal))
        {
            kind = ImportedTemplateTypedSwitchCaseKind.Default;
        }
        else if (string.Equals(manifest.Kind, "enum-pattern", StringComparison.Ordinal))
        {
            kind = ImportedTemplateTypedSwitchCaseKind.EnumPattern;
        }
        else if (string.Equals(manifest.Kind, "aggregate-pattern", StringComparison.Ordinal))
        {
            kind = ImportedTemplateTypedSwitchCaseKind.AggregatePattern;
        }
        else
        {
            return false;
        }

        if (manifest.Statements is not { Count: > 0 })
        {
            return false;
        }

        ImportedTemplateTypedBodyExpressionSummary? expression = null;
        if (manifest.Expression is not null
            && !TryBuildImportedTypedTemplateExpression(manifest.Expression, out expression))
        {
            return false;
        }

        ImportedTemplateTypedBodyExpressionSummary? guardExpression = null;
        if (manifest.GuardExpression is not null
            && !TryBuildImportedTypedTemplateExpression(manifest.GuardExpression, out guardExpression))
        {
            return false;
        }

        if (kind is ImportedTemplateTypedSwitchCaseKind.EnumPattern or ImportedTemplateTypedSwitchCaseKind.AggregatePattern
            && manifest.Ordinal is not { })
        {
            return false;
        }

        if (kind == ImportedTemplateTypedSwitchCaseKind.Literal && expression is null)
        {
            return false;
        }

        var members = new List<ImportedTemplateTypedSwitchFieldPatternSummary>((manifest.Members ?? []).Count);
        foreach (var member in manifest.Members ?? [])
        {
            if (!TryBuildImportedTypedTemplateSwitchFieldPattern(member, out var builtMember))
            {
                return false;
            }

            members.Add(builtMember);
        }

        var statements = new List<ImportedTemplateTypedBodyStatementSummary>(manifest.Statements.Count);
        foreach (var statement in manifest.Statements)
        {
            if (!TryBuildImportedTypedTemplateStatement(statement, out var builtStatement))
            {
                return false;
            }

            statements.Add(builtStatement);
        }

        summary = new ImportedTemplateTypedSwitchCaseSummary(
            kind,
            manifest.Ordinal,
            manifest.Name,
            expression,
            guardExpression,
            MemberPatterns: members,
            StatementSummaries: statements);
        return true;
    }

    private static bool TryBuildImportedTypedTemplateSwitchFieldPattern(
        StarkPackageTypedTemplatePatternManifest manifest,
        out ImportedTemplateTypedSwitchFieldPatternSummary summary)
    {
        summary = null!;

        if (string.Equals(manifest.Kind, "discard", StringComparison.Ordinal))
        {
            summary = new ImportedTemplateTypedSwitchFieldPatternSummary(
                ImportedTemplateTypedSwitchFieldPatternKind.Discard);
            return true;
        }

        if (string.Equals(manifest.Kind, "capture", StringComparison.Ordinal) && manifest.Name is not null)
        {
            summary = new ImportedTemplateTypedSwitchFieldPatternSummary(
                ImportedTemplateTypedSwitchFieldPatternKind.Capture,
                manifest.Name);
            return true;
        }

        return false;
    }

    private static bool TryBuildImportedTypedTemplateExpression(
        StarkPackageTypedTemplateExpressionManifest manifest,
        out ImportedTemplateTypedBodyExpressionSummary summary)
    {
        summary = null!;

        if (string.Equals(manifest.Kind, "name", StringComparison.Ordinal))
        {
            if (manifest.Name is null)
            {
                return false;
            }

            summary = new ImportedTemplateTypedBodyExpressionSummary(
                ImportedTemplateTypedBodyExpressionKind.NameReference,
                Name: manifest.Name);
            return true;
        }

        if (string.Equals(manifest.Kind, "literal", StringComparison.Ordinal))
        {
            if (manifest.LiteralText is null || manifest.Type is null)
            {
                return false;
            }

            summary = new ImportedTemplateTypedBodyExpressionSummary(
                ImportedTemplateTypedBodyExpressionKind.Literal,
                LiteralText: manifest.LiteralText,
                Type: BuildTypeSymbol(manifest.Type));
            return true;
        }

        if (string.Equals(manifest.Kind, "array-initializer", StringComparison.Ordinal))
        {
            if (manifest.Type is null || manifest.Arguments is null)
            {
                return false;
            }

            var arguments = new List<ImportedTemplateTypedBodyExpressionSummary>(manifest.Arguments.Count);
            foreach (var argument in manifest.Arguments)
            {
                if (!TryBuildImportedTypedTemplateExpression(argument, out var builtArgument))
                {
                    return false;
                }

                arguments.Add(builtArgument);
            }

            summary = new ImportedTemplateTypedBodyExpressionSummary(
                ImportedTemplateTypedBodyExpressionKind.ArrayInitializer,
                Arguments: arguments,
                Type: BuildTypeSymbol(manifest.Type));
            return true;
        }

        if (string.Equals(manifest.Kind, "conversion", StringComparison.Ordinal))
        {
            if (manifest.Type is null || manifest.Arguments is not { Count: 1 })
            {
                return false;
            }

            if (!TryBuildImportedTypedTemplateExpression(manifest.Arguments[0], out var operand))
            {
                return false;
            }

            summary = new ImportedTemplateTypedBodyExpressionSummary(
                ImportedTemplateTypedBodyExpressionKind.Conversion,
                Arguments: [operand],
                Type: BuildTypeSymbol(manifest.Type));
            return true;
        }

        if (string.Equals(manifest.Kind, "unary", StringComparison.Ordinal))
        {
            if (manifest.Name is null || manifest.Arguments is not { Count: 1 })
            {
                return false;
            }

            if (!TryBuildImportedTypedTemplateExpression(manifest.Arguments[0], out var operand))
            {
                return false;
            }

            summary = new ImportedTemplateTypedBodyExpressionSummary(
                ImportedTemplateTypedBodyExpressionKind.UnaryOperation,
                Name: manifest.Name,
                Arguments: [operand]);
            return true;
        }

        if (string.Equals(manifest.Kind, "binary", StringComparison.Ordinal))
        {
            if (manifest.Name is null || manifest.Arguments is not { Count: 2 })
            {
                return false;
            }

            var arguments = new List<ImportedTemplateTypedBodyExpressionSummary>(2);
            foreach (var argument in manifest.Arguments)
            {
                if (!TryBuildImportedTypedTemplateExpression(argument, out var builtArgument))
                {
                    return false;
                }

                arguments.Add(builtArgument);
            }

            summary = new ImportedTemplateTypedBodyExpressionSummary(
                ImportedTemplateTypedBodyExpressionKind.BinaryOperation,
                Name: manifest.Name,
                Arguments: arguments);
            return true;
        }

        if (string.Equals(manifest.Kind, "conditional", StringComparison.Ordinal))
        {
            if (manifest.Arguments is not { Count: 3 })
            {
                return false;
            }

            var arguments = new List<ImportedTemplateTypedBodyExpressionSummary>(3);
            foreach (var argument in manifest.Arguments)
            {
                if (!TryBuildImportedTypedTemplateExpression(argument, out var builtArgument))
                {
                    return false;
                }

                arguments.Add(builtArgument);
            }

            summary = new ImportedTemplateTypedBodyExpressionSummary(
                ImportedTemplateTypedBodyExpressionKind.Conditional,
                Arguments: arguments);
            return true;
        }

        if (string.Equals(manifest.Kind, "object-creation", StringComparison.Ordinal))
        {
            if (manifest.Ordinal is null)
            {
                return false;
            }

            var arguments = new List<ImportedTemplateTypedBodyExpressionSummary>((manifest.Arguments ?? []).Count);
            foreach (var argument in manifest.Arguments ?? [])
            {
                if (!TryBuildImportedTypedTemplateExpression(argument, out var builtArgument))
                {
                    return false;
                }

                arguments.Add(builtArgument);
            }

            summary = new ImportedTemplateTypedBodyExpressionSummary(
                ImportedTemplateTypedBodyExpressionKind.ObjectCreation,
                Ordinal: manifest.Ordinal,
                Arguments: arguments);
            return true;
        }

        if (string.Equals(manifest.Kind, "enum-constructor", StringComparison.Ordinal))
        {
            if (manifest.Ordinal is null)
            {
                return false;
            }

            var arguments = new List<ImportedTemplateTypedBodyExpressionSummary>((manifest.Arguments ?? []).Count);
            foreach (var argument in manifest.Arguments ?? [])
            {
                if (!TryBuildImportedTypedTemplateExpression(argument, out var builtArgument))
                {
                    return false;
                }

                arguments.Add(builtArgument);
            }

            summary = new ImportedTemplateTypedBodyExpressionSummary(
                ImportedTemplateTypedBodyExpressionKind.EnumConstructor,
                Ordinal: manifest.Ordinal,
                Arguments: arguments);
            return true;
        }

        if (string.Equals(manifest.Kind, "enum-call", StringComparison.Ordinal))
        {
            if (manifest.Ordinal is null)
            {
                return false;
            }

            var arguments = new List<ImportedTemplateTypedBodyExpressionSummary>((manifest.Arguments ?? []).Count);
            foreach (var argument in manifest.Arguments ?? [])
            {
                if (!TryBuildImportedTypedTemplateExpression(argument, out var builtArgument))
                {
                    return false;
                }

                arguments.Add(builtArgument);
            }

            summary = new ImportedTemplateTypedBodyExpressionSummary(
                ImportedTemplateTypedBodyExpressionKind.EnumCall,
                Ordinal: manifest.Ordinal,
                Arguments: arguments);
            return true;
        }

        if (string.Equals(manifest.Kind, "enum-value", StringComparison.Ordinal))
        {
            if (manifest.Ordinal is null)
            {
                return false;
            }

            summary = new ImportedTemplateTypedBodyExpressionSummary(
                ImportedTemplateTypedBodyExpressionKind.EnumValue,
                Ordinal: manifest.Ordinal);
            return true;
        }

        if (string.Equals(manifest.Kind, "direct-call", StringComparison.Ordinal))
        {
            if (manifest.Ordinal is null)
            {
                return false;
            }

            var arguments = new List<ImportedTemplateTypedBodyExpressionSummary>((manifest.Arguments ?? []).Count);
            foreach (var argument in manifest.Arguments ?? [])
            {
                if (!TryBuildImportedTypedTemplateExpression(argument, out var builtArgument))
                {
                    return false;
                }

                arguments.Add(builtArgument);
            }

            summary = new ImportedTemplateTypedBodyExpressionSummary(
                ImportedTemplateTypedBodyExpressionKind.DirectCall,
                Ordinal: manifest.Ordinal,
                Arguments: arguments);
            return true;
        }

        if (string.Equals(manifest.Kind, "index-access", StringComparison.Ordinal))
        {
            if (manifest.Arguments is not { Count: >= 1 })
            {
                return false;
            }

            var arguments = new List<ImportedTemplateTypedBodyExpressionSummary>(manifest.Arguments.Count);
            foreach (var argument in manifest.Arguments)
            {
                if (!TryBuildImportedTypedTemplateExpression(argument, out var builtArgument))
                {
                    return false;
                }

                arguments.Add(builtArgument);
            }

            summary = new ImportedTemplateTypedBodyExpressionSummary(
                ImportedTemplateTypedBodyExpressionKind.IndexAccess,
                Arguments: arguments);
            return true;
        }

        if (string.Equals(manifest.Kind, "field-access", StringComparison.Ordinal))
        {
            if (manifest.Ordinal is null || manifest.Arguments is not { Count: 1 })
            {
                return false;
            }

            if (!TryBuildImportedTypedTemplateExpression(manifest.Arguments[0], out var receiver))
            {
                return false;
            }

            summary = new ImportedTemplateTypedBodyExpressionSummary(
                ImportedTemplateTypedBodyExpressionKind.FieldAccess,
                Ordinal: manifest.Ordinal,
                Arguments: [receiver]);
            return true;
        }

        if (string.Equals(manifest.Kind, "member-call", StringComparison.Ordinal))
        {
            if (manifest.Ordinal is null || manifest.Arguments is not { Count: > 0 })
            {
                return false;
            }

            var arguments = new List<ImportedTemplateTypedBodyExpressionSummary>(manifest.Arguments.Count);
            foreach (var argument in manifest.Arguments)
            {
                if (!TryBuildImportedTypedTemplateExpression(argument, out var builtArgument))
                {
                    return false;
                }

                arguments.Add(builtArgument);
            }

            summary = new ImportedTemplateTypedBodyExpressionSummary(
                ImportedTemplateTypedBodyExpressionKind.MemberCall,
                Ordinal: manifest.Ordinal,
                Arguments: arguments);
            return true;
        }

        return false;
    }
}
