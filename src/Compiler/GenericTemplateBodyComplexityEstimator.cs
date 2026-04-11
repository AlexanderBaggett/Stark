using Stark.Parsing;

namespace Stark.Compiler;

internal static class GenericTemplateBodyComplexityEstimator
{
    public static int? Estimate(StarkParser.FunctionBodyContext? functionBody)
    {
        return functionBody?.block() is { } block
            ? Estimate(block)
            : null;
    }

    public static int? Estimate(StarkParser.BlockContext? block)
    {
        return block is null
            ? null
            : EstimateStatementList(block.statement());
    }

    private static int EstimateStatementList(IEnumerable<StarkParser.StatementContext> statements)
    {
        var total = 0;
        foreach (var statement in statements)
        {
            total += EstimateStatement(statement);
        }

        return total;
    }

    private static int EstimateStatement(StarkParser.StatementContext statement)
    {
        if (statement.block() is { } block)
        {
            return EstimateStatementList(block.statement());
        }

        if (statement.localConstantDeclaration() is { } localConstant)
        {
            return EstimateConstantDeclaration(localConstant);
        }

        if (statement.localVariableDeclaration() is { } localVariable)
        {
            return EstimateVariableDeclaration(localVariable);
        }

        if (statement.ifStatement() is { } ifStatement)
        {
            var elseCost = ifStatement.statement().Length > 1
                ? EstimateStatement(ifStatement.statement(1))
                : 0;
            return 2
                + EstimateExpression(ifStatement.expression())
                + EstimateStatement(ifStatement.statement(0))
                + elseCost;
        }

        if (statement.switchStatement() is { } switchStatement)
        {
            return 3
                + EstimateExpression(switchStatement.expression())
                + switchStatement.switchSection().Sum(EstimateSwitchSection);
        }

        if (statement.whileStatement() is { } whileStatement)
        {
            return 4
                + EstimateExpression(whileStatement.expression())
                + EstimateStatement(whileStatement.statement());
        }

        if (statement.forStatement() is { } forStatement)
        {
            return 4
                + EstimateForInitializer(forStatement.forInitializer())
                + EstimateForCondition(forStatement.forCondition())
                + EstimateForIterator(forStatement.forIterator())
                + EstimateStatement(forStatement.statement());
        }

        if (statement.returnStatement() is { } returnStatement)
        {
            return 1 + (returnStatement.expression() is { } expression ? EstimateExpression(expression) : 0);
        }

        if (statement.breakStatement() is not null || statement.continueStatement() is not null)
        {
            return 1;
        }

        if (statement.expressionStatement() is { } expressionStatement)
        {
            return 1 + EstimateExpression(expressionStatement.expression());
        }

        return 1;
    }

    private static int EstimateSwitchSection(StarkParser.SwitchSectionContext switchSection)
    {
        var labelCost = switchSection.switchLabel().Sum(EstimateSwitchLabel);
        var statementCost = EstimateStatementList(switchSection.statement());
        return labelCost + statementCost;
    }

    private static int EstimateSwitchLabel(StarkParser.SwitchLabelContext switchLabel)
    {
        return 1 + (switchLabel.whenClause() is { } whenClause ? EstimateExpression(whenClause.expression()) : 0);
    }

    private static int EstimateConstantDeclaration(StarkParser.LocalConstantDeclarationContext declaration)
    {
        var total = 0;
        foreach (var declarator in declaration.constantDeclarators().constantDeclarator())
        {
            total += 1 + EstimateVariableInitializer(declarator.variableInitializer());
        }

        return total;
    }

    private static int EstimateVariableDeclaration(StarkParser.LocalVariableDeclarationContext declaration)
    {
        var total = 0;
        foreach (var declarator in declaration.variableDeclarators().variableDeclarator())
        {
            total += 1 + (declarator.variableInitializer() is { } initializer ? EstimateVariableInitializer(initializer) : 0);
        }

        return total;
    }

    private static int EstimateLocalForVariableDeclaration(StarkParser.LocalForVariableDeclarationContext declaration)
    {
        var total = 0;
        foreach (var declarator in declaration.variableDeclarators().variableDeclarator())
        {
            total += 1 + (declarator.variableInitializer() is { } initializer ? EstimateVariableInitializer(initializer) : 0);
        }

        return total;
    }

    private static int EstimateForInitializer(StarkParser.ForInitializerContext? initializer)
    {
        if (initializer is null)
        {
            return 0;
        }

        if (initializer.localForVariableDeclaration() is { } localDeclaration)
        {
            return EstimateLocalForVariableDeclaration(localDeclaration);
        }

        return initializer.expressionList() is { } expressionList
            ? EstimateExpressionList(expressionList)
            : 0;
    }

    private static int EstimateForCondition(StarkParser.ForConditionContext? condition)
    {
        return condition?.expression() is { } expression
            ? EstimateExpression(expression)
            : 0;
    }

    private static int EstimateForIterator(StarkParser.ForIteratorContext? iterator)
    {
        return iterator?.expressionList() is { } expressionList
            ? EstimateExpressionList(expressionList)
            : 0;
    }

    private static int EstimateExpressionList(StarkParser.ExpressionListContext expressionList)
    {
        return expressionList.expression().Sum(EstimateExpression);
    }

    private static int EstimateVariableInitializer(StarkParser.VariableInitializerContext initializer)
    {
        if (initializer.expression() is { } expression)
        {
            return EstimateExpression(expression);
        }

        if (initializer.objectInitializer() is { } objectInitializer)
        {
            return 1 + objectInitializer.memberInitializer().Sum(member => EstimateVariableInitializer(member.variableInitializer()));
        }

        if (initializer.arrayInitializer() is { } arrayInitializer)
        {
            return 1 + arrayInitializer.variableInitializer().Sum(EstimateVariableInitializer);
        }

        return 0;
    }

    private static int EstimateExpression(StarkParser.ExpressionContext expression)
    {
        return EstimateAssignmentExpression(expression.assignmentExpression());
    }

    private static int EstimateAssignmentExpression(StarkParser.AssignmentExpressionContext assignmentExpression)
    {
        if (assignmentExpression.assignmentOperator() is not null)
        {
            return 1
                + EstimateUnaryExpression(assignmentExpression.unaryExpression())
                + EstimateAssignmentExpression(assignmentExpression.assignmentExpression());
        }

        return EstimateConditionalExpression(assignmentExpression.conditionalExpression());
    }

    private static int EstimateConditionalExpression(StarkParser.ConditionalExpressionContext conditionalExpression)
    {
        var cost = CountStructuralExpressionCost(conditionalExpression.logicalOrExpression());
        if (conditionalExpression.expression().Length == 2)
        {
            cost += 2
                + EstimateExpression(conditionalExpression.expression(0))
                + EstimateExpression(conditionalExpression.expression(1));
        }

        return cost;
    }

    private static int EstimateUnaryExpression(StarkParser.UnaryExpressionContext? unaryExpression)
    {
        return unaryExpression is null
            ? 0
            : CountStructuralExpressionCost(unaryExpression);
    }

    private static int CountStructuralExpressionCost(Antlr4.Runtime.Tree.IParseTree node)
    {
        var total = 0;
        CountStructuralExpressionCost(node, ref total);
        return total;
    }

    private static void CountStructuralExpressionCost(Antlr4.Runtime.Tree.IParseTree node, ref int total)
    {
        switch (node)
        {
            case StarkParser.ObjectCreationExpressionContext:
            case StarkParser.EnumConstructorExpressionContext:
            case StarkParser.ObjectInitializerContext:
            case StarkParser.ArrayInitializerContext:
                total += 1;
                break;

            case StarkParser.PostfixPartContext postfixPart:
                total += postfixPart.argumentList() is not null ? 2 : 1;
                break;

            case StarkParser.ConditionalExpressionContext conditionalExpression when conditionalExpression.expression().Length == 2:
                total += 2;
                break;

            case StarkParser.AssignmentExpressionContext assignmentExpression when assignmentExpression.assignmentOperator() is not null:
                total += 1;
                break;
        }

        for (var index = 0; index < node.ChildCount; index++)
        {
            if (node.GetChild(index) is { } child)
            {
                CountStructuralExpressionCost(child, ref total);
            }
        }
    }
}
