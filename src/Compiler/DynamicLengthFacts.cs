using Antlr4.Runtime.Tree;
using Stark.Parsing;

namespace Stark.Compiler;

/// <summary>
/// Branch-dominance length facts for dynamic storage reads: a comparison that
/// proves `index &lt; root.Length` on a control-flow path lets reads of
/// `root[index]` on that path pass the dense-prefix initialization gate.
/// Facts are matched by source text (whitespace-free ANTLR text), so the
/// guard and the read must spell the index and the storage path the same way.
/// </summary>
internal static class DynamicLengthFacts
{
    internal readonly record struct DynamicLengthFact(string RootText, string IndexText);

    internal readonly record struct ComparisonFact(string LeftText, string OperatorText, string RightText);

    /// <summary>
    /// Collects facts implied by a condition: <paramref name="thenFacts"/>
    /// hold when the condition is true, <paramref name="elseFacts"/> when it
    /// is false. Conjunctions contribute then-facts per conjunct; else-facts
    /// only come from a single comparison (the negation of a conjunction is
    /// a disjunction and proves nothing).
    /// </summary>
    public static void Collect(
        StarkParser.ExpressionContext condition,
        List<DynamicLengthFact> thenFacts,
        List<DynamicLengthFact> elseFacts)
    {
        Collect(condition, thenFacts, elseFacts, thenComparisons: null, elseComparisons: null);
    }

    public static void Collect(
        StarkParser.ExpressionContext condition,
        List<DynamicLengthFact> thenFacts,
        List<DynamicLengthFact> elseFacts,
        List<ComparisonFact>? thenComparisons,
        List<ComparisonFact>? elseComparisons)
    {
        var logicalAnd = DescendToLogicalAnd(condition);
        if (logicalAnd is null)
        {
            return;
        }

        var conjuncts = logicalAnd.bitwiseOrExpression();
        if (conjuncts.Length > 1)
        {
            foreach (var conjunct in conjuncts)
            {
                if (DescendToRelational(conjunct) is { } relational)
                {
                    CollectFromRelational(relational, thenFacts, elseFacts: null, thenComparisons, elseComparisons: null);
                }
            }

            return;
        }

        if (DescendToRelational(conjuncts[0]) is { } single)
        {
            CollectFromRelational(single, thenFacts, elseFacts, thenComparisons, elseComparisons);
        }
    }

    /// <summary>
    /// True when the statement always leaves the enclosing flow (return,
    /// break, or continue on every path through it), so the code after an
    /// `if` whose then-branch terminates only runs when the condition was
    /// false.
    /// </summary>
    public static bool TerminatesAlways(StarkParser.StatementContext? statement)
    {
        if (statement is null)
        {
            return false;
        }

        if (statement.returnStatement() is not null
            || statement.breakStatement() is not null
            || statement.continueStatement() is not null)
        {
            return true;
        }

        if (statement.block() is { } block)
        {
            foreach (var nested in block.statement())
            {
                if (TerminatesAlways(nested))
                {
                    return true;
                }
            }

            return false;
        }

        if (statement.ifStatement() is { } ifStatement)
        {
            var branches = ifStatement.statement();
            return branches.Length > 1
                && TerminatesAlways(branches[0])
                && TerminatesAlways(branches[1]);
        }

        return false;
    }

    /// <summary>
    /// True when the statement returns from the enclosing function on every
    /// path through it. Stricter than <see cref="TerminatesAlways"/>: break
    /// and continue also leave the branch, but their end-states still reach
    /// the enclosing loop's join, so only returning paths may be dropped
    /// from a branch-statement join.
    /// </summary>
    public static bool ReturnsAlways(StarkParser.StatementContext? statement)
    {
        if (statement is null)
        {
            return false;
        }

        if (statement.returnStatement() is not null)
        {
            return true;
        }

        if (statement.block() is { } block)
        {
            foreach (var nested in block.statement())
            {
                if (ReturnsAlways(nested))
                {
                    return true;
                }
            }

            return false;
        }

        if (statement.ifStatement() is { } ifStatement)
        {
            var branches = ifStatement.statement();
            return branches.Length > 1
                && ReturnsAlways(branches[0])
                && ReturnsAlways(branches[1]);
        }

        return false;
    }

    private static void CollectFromRelational(
        StarkParser.RelationalExpressionContext relational,
        List<DynamicLengthFact> thenFacts,
        List<DynamicLengthFact>? elseFacts,
        List<ComparisonFact>? thenComparisons = null,
        List<ComparisonFact>? elseComparisons = null)
    {
        var operands = relational.shiftExpression();
        if (operands.Length < 2)
        {
            return;
        }

        // Children alternate operand, operator, operand, ... — walk adjacent
        // pairs so comparison chains contribute every link in then-polarity.
        var isSingleComparison = operands.Length == 2;
        var operandIndex = 0;
        for (var childIndex = 1; childIndex < relational.ChildCount - 1 && operandIndex + 1 < operands.Length; childIndex += 2)
        {
            if (relational.GetChild(childIndex) is not ITerminalNode operatorNode)
            {
                break;
            }

            var left = operands[operandIndex];
            var right = operands[operandIndex + 1];
            operandIndex++;

            var operatorText = operatorNode.GetText();
            thenComparisons?.Add(new ComparisonFact(left.GetText(), operatorText, right.GetText()));
            if (isSingleComparison && elseComparisons is not null
                && NegateComparisonOperator(operatorText) is { } negated)
            {
                elseComparisons.Add(new ComparisonFact(left.GetText(), negated, right.GetText()));
            }

            switch (operatorText)
            {
                case "<" when TryRootFromLengthText(right.GetText()) is { } rootBelow:
                    thenFacts.Add(new DynamicLengthFact(rootBelow, left.GetText()));
                    break;
                case ">" when TryRootFromLengthText(left.GetText()) is { } rootAbove:
                    thenFacts.Add(new DynamicLengthFact(rootAbove, right.GetText()));
                    break;
                case ">=" when isSingleComparison && elseFacts is not null
                    && TryRootFromLengthText(right.GetText()) is { } rootGte:
                    elseFacts.Add(new DynamicLengthFact(rootGte, left.GetText()));
                    break;
                case "<=" when isSingleComparison && elseFacts is not null
                    && TryRootFromLengthText(left.GetText()) is { } rootLte:
                    elseFacts.Add(new DynamicLengthFact(rootLte, right.GetText()));
                    break;
            }
        }
    }

    private static string? NegateComparisonOperator(string operatorText) => operatorText switch
    {
        "<" => ">=",
        ">" => "<=",
        "<=" => ">",
        ">=" => "<",
        _ => null
    };

    private static string? TryRootFromLengthText(string text)
    {
        const string suffix = ".Length";
        return text.EndsWith(suffix, StringComparison.Ordinal) && text.Length > suffix.Length
            ? text[..^suffix.Length]
            : null;
    }

    private static StarkParser.LogicalAndExpressionContext? DescendToLogicalAnd(StarkParser.ExpressionContext condition)
    {
        var assignment = condition.assignmentExpression();
        var conditional = assignment?.conditionalExpression();
        if (conditional is null || conditional.QUESTION() is not null)
        {
            return null;
        }

        var logicalOr = conditional.logicalOrExpression();
        var logicalAnds = logicalOr.logicalAndExpression();
        return logicalAnds.Length == 1 ? logicalAnds[0] : null;
    }

    private static StarkParser.RelationalExpressionContext? DescendToRelational(StarkParser.BitwiseOrExpressionContext conjunct)
    {
        var xors = conjunct.bitwiseXorExpression();
        if (xors.Length != 1)
        {
            return null;
        }

        var ands = xors[0].bitwiseAndExpression();
        if (ands.Length != 1)
        {
            return null;
        }

        var equalities = ands[0].equalityExpression();
        if (equalities.Length != 1)
        {
            return null;
        }

        var relationals = equalities[0].relationalExpression();
        return relationals.Length == 1 ? relationals[0] : null;
    }
}
