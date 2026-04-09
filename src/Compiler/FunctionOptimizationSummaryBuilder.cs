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

        var wrapperKind = TryGetSingleReturnWrapperKind(block, out var kind)
            ? kind
            : SingleReturnWrapperKind.None;

        return new FunctionOptimizationSummary(
            accumulator.DirectCallCount,
            accumulator.MemberCallCount,
            accumulator.FieldAccessCount,
            accumulator.IndexAccessCount,
            accumulator.BranchStatementCount,
            accumulator.LoopStatementCount,
            accumulator.ObjectCreationCount,
            wrapperKind == SingleReturnWrapperKind.DirectCall,
            wrapperKind == SingleReturnWrapperKind.MemberCall,
            wrapperKind == SingleReturnWrapperKind.FieldAccess,
            wrapperKind == SingleReturnWrapperKind.IndexAccess,
            wrapperKind == SingleReturnWrapperKind.Conversion,
            wrapperKind == SingleReturnWrapperKind.AddressOf,
            wrapperKind == SingleReturnWrapperKind.Dereference);
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
                when TryClassifySimplePostfixExpression(
                    postfixExpression,
                    out var expressionKind,
                    out var fieldAccessCount,
                    out var indexAccessCount):
                if (expressionKind == SingleReturnWrapperKind.DirectCall)
                {
                    accumulator.DirectCallCount++;
                }
                else if (expressionKind == SingleReturnWrapperKind.MemberCall)
                {
                    accumulator.MemberCallCount++;
                }
                else if (expressionKind == SingleReturnWrapperKind.FieldAccess)
                {
                    accumulator.FieldAccessCount += fieldAccessCount;
                }
                else if (expressionKind == SingleReturnWrapperKind.IndexAccess)
                {
                    accumulator.FieldAccessCount += fieldAccessCount;
                    accumulator.IndexAccessCount += indexAccessCount;
                }

                break;
        }

        for (var index = 0; index < node.ChildCount; index++)
        {
            CountExpressionNode(node.GetChild(index), accumulator);
        }
    }

    private static bool TryGetSingleReturnWrapperKind(
        StarkParser.BlockContext block,
        out SingleReturnWrapperKind kind)
    {
        kind = SingleReturnWrapperKind.None;

        if (block.statement().Length != 1
            || block.statement(0).returnStatement()?.expression() is not { } returnExpression)
        {
            return false;
        }

        return TryClassifySimpleReturnWrapperExpression(returnExpression, out kind);
    }

    private static bool TryClassifySimpleReturnWrapperExpression(
        StarkParser.ExpressionContext expression,
        out SingleReturnWrapperKind kind)
    {
        kind = SingleReturnWrapperKind.None;
        return TryGetSimpleUnaryExpression(expression) is { } unaryExpression
            && TryClassifySimpleReturnWrapperUnary(unaryExpression, out kind);
    }

    private static bool TryClassifySimpleReturnWrapperUnary(
        StarkParser.UnaryExpressionContext expression,
        out SingleReturnWrapperKind kind)
    {
        kind = SingleReturnWrapperKind.None;

        if (TryGetSimplePostfixExpression(expression) is { } postfixExpression)
        {
            return TryClassifySimplePostfixExpression(postfixExpression, out kind, out _, out _);
        }

        if (expression.conversionType() is not null
            && expression.unaryExpression() is { } convertedOperand
            && (TryClassifySimpleReturnWrapperUnary(convertedOperand, out _)
                || TryIsSimpleAddressableOperand(convertedOperand)
                || TryIsSimpleIdentifierOrQualifiedNameOperand(convertedOperand)))
        {
            kind = SingleReturnWrapperKind.Conversion;
            return true;
        }

        if (expression.unaryOperator() is not { } unaryOperator
            || expression.unaryExpression() is not { } operand)
        {
            return false;
        }

        if (unaryOperator.GetText() == "&"
            && TryIsSimpleAddressableOperand(operand))
        {
            kind = SingleReturnWrapperKind.AddressOf;
            return true;
        }

        if (unaryOperator.GetText() == "*"
            && (TryClassifySimpleReturnWrapperUnary(operand, out _)
                || TryIsSimplePointerOperand(operand)))
        {
            kind = SingleReturnWrapperKind.Dereference;
            return true;
        }

        return false;
    }

    private static bool TryClassifySimplePostfixExpression(
        StarkParser.PostfixExpressionContext expression,
        out SingleReturnWrapperKind kind,
        out int fieldAccessCount,
        out int indexAccessCount)
    {
        kind = SingleReturnWrapperKind.None;
        fieldAccessCount = 0;
        indexAccessCount = 0;

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
                    ? SingleReturnWrapperKind.MemberCall
                    : SingleReturnWrapperKind.DirectCall;
                return true;
            }

            if (postfixPart.LBRACK() is not null)
            {
                indexAccessCount++;
                continue;
            }

            if (postfixPart.Identifier() is not null)
            {
                sawPostfixIdentifier = true;
                fieldAccessCount++;
                continue;
            }

            return false;
        }

        if (indexAccessCount > 0)
        {
            kind = SingleReturnWrapperKind.IndexAccess;
            return true;
        }

        if (fieldAccessCount > 0)
        {
            kind = SingleReturnWrapperKind.FieldAccess;
            return true;
        }

        return false;
    }

    private static bool TryIsSimpleIdentifierOrQualifiedNameOperand(StarkParser.UnaryExpressionContext expression)
    {
        return TryGetSimplePostfixExpression(expression) is { } postfixExpression
            && postfixExpression.postfixPart().Length == 0
            && TryIsSimpleIdentifierOrQualifiedNamePrimary(postfixExpression.primaryExpression());
    }

    private static bool TryIsSimplePointerOperand(StarkParser.UnaryExpressionContext expression)
    {
        return TryIsSimpleIdentifierOrQualifiedNameOperand(expression);
    }

    private static bool TryIsSimpleAddressableOperand(StarkParser.UnaryExpressionContext expression)
    {
        if (TryGetSimplePostfixExpression(expression) is { } postfixExpression)
        {
            return TryIsSimpleAddressablePostfixExpression(postfixExpression);
        }

        return expression.unaryOperator()?.GetText() == "*"
            && expression.unaryExpression() is { } pointerOperand
            && TryIsSimplePointerOperand(pointerOperand);
    }

    private static bool TryIsSimpleAddressablePostfixExpression(StarkParser.PostfixExpressionContext expression)
    {
        if (!TryIsSimpleIdentifierOrQualifiedNamePrimary(expression.primaryExpression()))
        {
            return false;
        }

        foreach (var postfixPart in expression.postfixPart())
        {
            if (postfixPart.argumentList() is not null)
            {
                return false;
            }

            if (postfixPart.Identifier() is null && postfixPart.LBRACK() is null)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryIsSimpleIdentifierOrQualifiedNamePrimary(StarkParser.PrimaryExpressionContext expression)
    {
        return expression.Identifier() is not null || expression.qualifiedName() is not null;
    }

    private static StarkParser.UnaryExpressionContext? TryGetSimpleUnaryExpression(StarkParser.ExpressionContext expression)
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

        return multiplicative.unaryExpression(0);
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

        public int FieldAccessCount { get; set; }

        public int IndexAccessCount { get; set; }

        public int BranchStatementCount { get; set; }

        public int LoopStatementCount { get; set; }

        public int ObjectCreationCount { get; set; }
    }

    private enum SingleReturnWrapperKind
    {
        None,
        DirectCall,
        MemberCall,
        FieldAccess,
        IndexAccess,
        Conversion,
        AddressOf,
        Dereference
    }
}
