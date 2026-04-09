using Antlr4.Runtime.Tree;
using Stark.Parsing;

namespace Stark.Compiler;

internal static class FunctionOptimizationSummaryBuilder
{
    public static FunctionOptimizationSummary? Build(StarkParser.FunctionBodyContext? functionBody)
    {
        return functionBody?.block() is { } block
            ? Build(block)
            : null;
    }

    public static FunctionOptimizationSummary? Build(StarkParser.BlockContext? block)
    {
        if (block is null)
        {
            return null;
        }

        var accumulator = new SummaryAccumulator();
        CountBlock(block, accumulator);

        var forwarderKind = TryGetSingleReturnForwarderKind(block, out var kind)
            ? kind
            : CallForwarderKind.None;

        return new FunctionOptimizationSummary(
            accumulator.DirectCallCount,
            accumulator.MemberCallCount,
            accumulator.BranchStatementCount,
            accumulator.LoopStatementCount,
            accumulator.ObjectCreationCount,
            forwarderKind == CallForwarderKind.Direct,
            forwarderKind == CallForwarderKind.Member);
    }

    private static void CountBlock(StarkParser.BlockContext block, SummaryAccumulator accumulator)
    {
        foreach (var statement in block.statement())
        {
            CountStatement(statement, accumulator);
        }
    }

    private static void CountStatement(StarkParser.StatementContext statement, SummaryAccumulator accumulator)
    {
        if (statement.block() is { } block)
        {
            CountBlock(block, accumulator);
            return;
        }

        if (statement.localConstantDeclaration() is { } localConstant)
        {
            foreach (var declarator in localConstant.constantDeclarators().constantDeclarator())
            {
                CountVariableInitializer(declarator.variableInitializer(), accumulator);
            }

            return;
        }

        if (statement.localVariableDeclaration() is { } localVariable)
        {
            foreach (var declarator in localVariable.variableDeclarators().variableDeclarator())
            {
                if (declarator.variableInitializer() is { } initializer)
                {
                    CountVariableInitializer(initializer, accumulator);
                }
            }

            return;
        }

        if (statement.ifStatement() is { } ifStatement)
        {
            accumulator.BranchStatementCount++;
            CountExpression(ifStatement.expression(), accumulator);
            CountStatement(ifStatement.statement(0), accumulator);
            if (ifStatement.statement().Length > 1)
            {
                CountStatement(ifStatement.statement(1), accumulator);
            }

            return;
        }

        if (statement.switchStatement() is { } switchStatement)
        {
            accumulator.BranchStatementCount++;
            CountExpression(switchStatement.expression(), accumulator);
            foreach (var section in switchStatement.switchSection())
            {
                foreach (var label in section.switchLabel())
                {
                    if (label.whenClause()?.expression() is { } whenExpression)
                    {
                        CountExpression(whenExpression, accumulator);
                    }
                }

                foreach (var nestedStatement in section.statement())
                {
                    CountStatement(nestedStatement, accumulator);
                }
            }

            return;
        }

        if (statement.whileStatement() is { } whileStatement)
        {
            accumulator.LoopStatementCount++;
            CountExpression(whileStatement.expression(), accumulator);
            CountStatement(whileStatement.statement(), accumulator);
            return;
        }

        if (statement.forStatement() is { } forStatement)
        {
            accumulator.LoopStatementCount++;
            CountForInitializer(forStatement.forInitializer(), accumulator);
            if (forStatement.forCondition()?.expression() is { } condition)
            {
                CountExpression(condition, accumulator);
            }

            if (forStatement.forIterator()?.expressionList() is { } iterator)
            {
                foreach (var expression in iterator.expression())
                {
                    CountExpression(expression, accumulator);
                }
            }

            CountStatement(forStatement.statement(), accumulator);
            return;
        }

        if (statement.returnStatement()?.expression() is { } returnExpression)
        {
            CountExpression(returnExpression, accumulator);
            return;
        }

        if (statement.expressionStatement()?.expression() is { } expressionStatement)
        {
            CountExpression(expressionStatement, accumulator);
        }
    }

    private static void CountForInitializer(StarkParser.ForInitializerContext? initializer, SummaryAccumulator accumulator)
    {
        if (initializer is null)
        {
            return;
        }

        if (initializer.localForVariableDeclaration() is { } localDeclaration)
        {
            foreach (var declarator in localDeclaration.variableDeclarators().variableDeclarator())
            {
                if (declarator.variableInitializer() is { } variableInitializer)
                {
                    CountVariableInitializer(variableInitializer, accumulator);
                }
            }

            return;
        }

        if (initializer.expressionList() is { } expressionList)
        {
            foreach (var expression in expressionList.expression())
            {
                CountExpression(expression, accumulator);
            }
        }
    }

    private static void CountVariableInitializer(
        StarkParser.VariableInitializerContext initializer,
        SummaryAccumulator accumulator)
    {
        if (initializer.expression() is { } expression)
        {
            CountExpression(expression, accumulator);
            return;
        }

        if (initializer.objectInitializer() is { } objectInitializer)
        {
            foreach (var member in objectInitializer.memberInitializer())
            {
                CountVariableInitializer(member.variableInitializer(), accumulator);
            }

            return;
        }

        if (initializer.arrayInitializer() is { } arrayInitializer)
        {
            foreach (var nestedInitializer in arrayInitializer.variableInitializer())
            {
                CountVariableInitializer(nestedInitializer, accumulator);
            }
        }
    }

    private static void CountExpression(StarkParser.ExpressionContext expression, SummaryAccumulator accumulator)
    {
        CountExpressionNode(expression, accumulator);
    }

    private static void CountExpressionNode(IParseTree node, SummaryAccumulator accumulator)
    {
        switch (node)
        {
            case StarkParser.ObjectCreationExpressionContext:
                accumulator.ObjectCreationCount++;
                break;

            case StarkParser.PostfixExpressionContext postfixExpression
                when TryClassifySimpleCall(postfixExpression, out var callKind):
                if (callKind == CallForwarderKind.Direct)
                {
                    accumulator.DirectCallCount++;
                }
                else if (callKind == CallForwarderKind.Member)
                {
                    accumulator.MemberCallCount++;
                }

                break;
        }

        for (var index = 0; index < node.ChildCount; index++)
        {
            CountExpressionNode(node.GetChild(index), accumulator);
        }
    }

    private static bool TryGetSingleReturnForwarderKind(
        StarkParser.BlockContext block,
        out CallForwarderKind kind)
    {
        kind = CallForwarderKind.None;

        if (block.statement().Length != 1
            || block.statement(0).returnStatement()?.expression() is not { } returnExpression
            || TryGetSimplePostfixExpression(returnExpression) is not { } postfixExpression)
        {
            return false;
        }

        return TryClassifySimpleCall(postfixExpression, out kind);
    }

    private static bool TryClassifySimpleCall(
        StarkParser.PostfixExpressionContext expression,
        out CallForwarderKind kind)
    {
        kind = CallForwarderKind.None;

        if (expression.primaryExpression() is not { } primaryExpression)
        {
            return false;
        }

        var currentName = primaryExpression.Identifier()?.GetText()
            ?? primaryExpression.qualifiedName()?.GetText();
        if (currentName is null)
        {
            return false;
        }

        var sawPostfixIdentifier = false;
        var postfixParts = expression.postfixPart();
        for (var index = 0; index < postfixParts.Length; index++)
        {
            var postfixPart = postfixParts[index];
            if (postfixPart.argumentList() is not null)
            {
                if (index != postfixParts.Length - 1)
                {
                    return false;
                }

                kind = sawPostfixIdentifier
                    ? CallForwarderKind.Member
                    : CallForwarderKind.Direct;
                return true;
            }

            if (postfixPart.Identifier() is not null)
            {
                sawPostfixIdentifier = true;
                continue;
            }

            return false;
        }

        return false;
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

    private sealed class SummaryAccumulator
    {
        public int DirectCallCount { get; set; }

        public int MemberCallCount { get; set; }

        public int BranchStatementCount { get; set; }

        public int LoopStatementCount { get; set; }

        public int ObjectCreationCount { get; set; }
    }

    private enum CallForwarderKind
    {
        None,
        Direct,
        Member
    }
}
