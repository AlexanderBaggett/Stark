using Stark.Compiler;

namespace compiler.Tests;

public sealed class BorrowLivenessValidationTests
{
    private static readonly StarkTypeSymbol BoxType = StarkTypeSymbols.Named("Box");
    private static readonly StarkTypeSymbol BorrowBoxType = StarkTypeSymbols.ApplyQualifiers(
        BoxType,
        borrowKind: StarkBorrowKind.Borrow);

    [Fact]
    public void MoveConflictIsReportedWhenBorrowRemainsLive()
    {
        var result = Validate(BuildLinearModule([
            StorageLive("box", BoxType),
            StorageLive("alias", BorrowBoxType),
            Assign("alias", BorrowBoxType, new MidLevelIrUseRValue(new MidLevelIrLocalOperand("box", BoxType)), "alias = box"),
            Evaluate(new MidLevelIrCallRValue("Consume", [new MidLevelIrLocalOperand("box", BoxType)], StarkTypeSymbols.Void, "Consume(box)"), "Consume(box)"),
            Evaluate(new MidLevelIrUseRValue(new MidLevelIrLocalOperand("alias", BorrowBoxType)), "alias"),
            ReturnZero()
        ]));

        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "STK4201"
                && diagnostic.Message.Contains("cannot move 'box' into call 'Consume'", StringComparison.Ordinal));
        Assert.False(result.Model.Functions["Main"].OwnershipValid);
    }

    [Fact]
    public void MoveAfterLastUseIsAllowed()
    {
        var result = Validate(BuildLinearModule([
            StorageLive("box", BoxType),
            StorageLive("alias", BorrowBoxType),
            Assign("alias", BorrowBoxType, new MidLevelIrUseRValue(new MidLevelIrLocalOperand("box", BoxType)), "alias = box"),
            Evaluate(new MidLevelIrUseRValue(new MidLevelIrLocalOperand("alias", BorrowBoxType)), "alias"),
            Evaluate(new MidLevelIrCallRValue("Consume", [new MidLevelIrLocalOperand("box", BoxType)], StarkTypeSymbols.Void, "Consume(box)"), "Consume(box)"),
            ReturnZero()
        ]));

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "STK4201");
        Assert.True(result.Model.Functions["Main"].OwnershipValid);
    }

    [Fact]
    public void OverwriteConflictIsReportedWhenBorrowRemainsLive()
    {
        var result = Validate(BuildLinearModule([
            StorageLive("box", BoxType),
            StorageLive("alias", BorrowBoxType),
            Assign("alias", BorrowBoxType, new MidLevelIrUseRValue(new MidLevelIrLocalOperand("box", BoxType)), "alias = box"),
            Assign("box", BoxType, new MidLevelIrUseRValue(new MidLevelIrZeroInitializerOperand(BoxType)), "box = zeroinitializer"),
            Evaluate(new MidLevelIrUseRValue(new MidLevelIrLocalOperand("alias", BorrowBoxType)), "alias"),
            ReturnZero()
        ]));

        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "STK4201"
                && diagnostic.Message.Contains("cannot overwrite 'box'", StringComparison.Ordinal));
        Assert.False(result.Model.Functions["Main"].OwnershipValid);
    }

    [Fact]
    public void BranchLastUseAllowsMoveAtMerge()
    {
        var module = BuildBranchModule(useAliasAfterMerge: false);
        var result = Validate(module);

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "STK4201");
        Assert.True(result.Model.Functions["Main"].OwnershipValid);
    }

    [Fact]
    public void BranchLiveBorrowBlocksMoveAtMerge()
    {
        var module = BuildBranchModule(useAliasAfterMerge: true);
        var result = Validate(module);

        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "STK4201"
                && diagnostic.Message.Contains("cannot move 'box' into call 'Consume'", StringComparison.Ordinal));
        Assert.False(result.Model.Functions["Main"].OwnershipValid);
    }

    private static ValidationResult Validate(MidLevelIrModule module)
    {
        var state = new CompilationState(new CompilationInput("module Demo"), new CompilerOptions());
        var context = new CompilerPassContext(state);
        var typeModel = new TypeCheckModel(
            "Demo",
            NamedTypes: new Dictionary<string, NamedTypeSymbol>(StringComparer.Ordinal),
            Functions: new Dictionary<string, TypedFunctionSignature>(StringComparer.Ordinal)
            {
                ["Main"] = new TypedFunctionSignature("Main", StarkTypeSymbols.Integer(32), [new TypedParameterSymbol("choose", StarkTypeSymbols.Bool)]),
                ["Consume"] = new TypedFunctionSignature("Consume", StarkTypeSymbols.Void, [new TypedParameterSymbol("value", BoxType)])
            },
            Globals: new Dictionary<string, StarkTypeSymbol>(StringComparer.Ordinal),
            Literals: []);
        var ownership = new OwnershipValidationModel(
            "Demo",
            new Dictionary<string, FunctionOwnershipSummary>(StringComparer.Ordinal)
            {
                ["Main"] = new FunctionOwnershipSummary("Main", OwnershipValid: true, ImplicitDrops: [], Moves: [])
            });

        var model = new NonLexicalBorrowLifetimeValidator(context, module, typeModel, ownership).Validate();
        return new ValidationResult(model, context.Diagnostics.Items);
    }

    private static MidLevelIrModule BuildLinearModule(IReadOnlyList<MidLevelIrStatement> statements)
    {
        var function = new MidLevelIrFunction(
            "Main",
            "i32 Main()",
            StarkTypeSymbols.Integer(32),
            Parameters: [new TypedParameterSymbol("choose", StarkTypeSymbols.Bool)],
            HasBody: true,
            SupportsDirectCodeGeneration: true,
            EntryBlockId: 0,
            Locals:
            [
                new MidLevelIrLocal("box", BoxType, "stack", IsMutable: true, IsConstant: false),
                new MidLevelIrLocal("alias", BorrowBoxType, "stack", IsMutable: false, IsConstant: false)
            ],
            Blocks:
            [
                new MidLevelIrBasicBlock(
                    0,
                    "bb0_entry",
                    statements,
                    new MidLevelIrTerminator(MidLevelIrTerminatorKind.Return, [], Value: new MidLevelIrIntegerConstantOperand(0, StarkTypeSymbols.Integer(32))))
            ]);

        return new MidLevelIrModule("Demo", [function]);
    }

    private static MidLevelIrModule BuildBranchModule(bool useAliasAfterMerge)
    {
        var branchValue = new MidLevelIrParameterOperand("choose", StarkTypeSymbols.Bool);
        var thenStatements = new List<MidLevelIrStatement>
        {
            Assign("alias", BorrowBoxType, new MidLevelIrUseRValue(new MidLevelIrLocalOperand("box", BoxType)), "alias = box"),
            Evaluate(new MidLevelIrUseRValue(new MidLevelIrLocalOperand("alias", BorrowBoxType)), "alias")
        };
        var elseStatements = new List<MidLevelIrStatement>
        {
            Assign("alias", BorrowBoxType, new MidLevelIrUseRValue(new MidLevelIrLocalOperand("box", BoxType)), "alias = box"),
            Evaluate(new MidLevelIrUseRValue(new MidLevelIrLocalOperand("alias", BorrowBoxType)), "alias")
        };
        var mergeStatements = new List<MidLevelIrStatement>
        {
            Evaluate(new MidLevelIrCallRValue("Consume", [new MidLevelIrLocalOperand("box", BoxType)], StarkTypeSymbols.Void, "Consume(box)"), "Consume(box)")
        };

        if (useAliasAfterMerge)
        {
            mergeStatements.Add(Evaluate(new MidLevelIrUseRValue(new MidLevelIrLocalOperand("alias", BorrowBoxType)), "alias"));
        }

        var function = new MidLevelIrFunction(
            "Main",
            "i32 Main(bool choose)",
            StarkTypeSymbols.Integer(32),
            Parameters: [new TypedParameterSymbol("choose", StarkTypeSymbols.Bool)],
            HasBody: true,
            SupportsDirectCodeGeneration: true,
            EntryBlockId: 0,
            Locals:
            [
                new MidLevelIrLocal("box", BoxType, "stack", IsMutable: true, IsConstant: false),
                new MidLevelIrLocal("alias", BorrowBoxType, "stack", IsMutable: false, IsConstant: false)
            ],
            Blocks:
            [
                new MidLevelIrBasicBlock(
                    0,
                    "bb0_entry",
                    [StorageLive("box", BoxType), StorageLive("alias", BorrowBoxType)],
                    new MidLevelIrTerminator(
                        MidLevelIrTerminatorKind.Branch,
                        Targets: [1, 2],
                        Condition: branchValue,
                        ConditionText: "choose")),
                new MidLevelIrBasicBlock(
                    1,
                    "bb1_then",
                    thenStatements,
                    new MidLevelIrTerminator(MidLevelIrTerminatorKind.Goto, [3])),
                new MidLevelIrBasicBlock(
                    2,
                    "bb2_else",
                    elseStatements,
                    new MidLevelIrTerminator(MidLevelIrTerminatorKind.Goto, [3])),
                new MidLevelIrBasicBlock(
                    3,
                    "bb3_merge",
                    mergeStatements,
                    new MidLevelIrTerminator(MidLevelIrTerminatorKind.Return, [], Value: new MidLevelIrIntegerConstantOperand(0, StarkTypeSymbols.Integer(32))))
            ]);

        return new MidLevelIrModule("Demo", [function]);
    }

    private static MidLevelIrStatement StorageLive(string name, StarkTypeSymbol type)
    {
        return new MidLevelIrStatement(MidLevelIrStatementKind.StorageLive, name, name, type);
    }

    private static MidLevelIrStatement Assign(string targetName, StarkTypeSymbol targetType, MidLevelIrRValue value, string text)
    {
        return new MidLevelIrStatement(MidLevelIrStatementKind.Assign, text, targetName, targetType, Value: value);
    }

    private static MidLevelIrStatement Evaluate(MidLevelIrRValue value, string text)
    {
        return new MidLevelIrStatement(MidLevelIrStatementKind.Evaluate, text, Value: value);
    }

    private static MidLevelIrStatement ReturnZero()
    {
        return new MidLevelIrStatement(MidLevelIrStatementKind.Evaluate, "0", Value: new MidLevelIrUseRValue(new MidLevelIrIntegerConstantOperand(0, StarkTypeSymbols.Integer(32))));
    }

    private sealed record ValidationResult(
        OwnershipValidationModel Model,
        IReadOnlyList<CompilerDiagnostic> Diagnostics);
}
